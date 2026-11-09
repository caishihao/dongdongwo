using System.Diagnostics;
using System.Threading.Channels;
using BongoCat.Core.Config;
using BongoCat.Core.Input;
using BongoCat.Platform.Native;

namespace BongoCat.Platform.Input;

/// <summary>
/// 全局键盘轮询服务（ADR-002 决定 B：GetAsyncKeyState 轮询，非低级钩子）。
/// 不受 UIPI 限制（能读到管理员进程按键）、无钩子回调超时问题、杀软启发式友好。
/// </summary>
/// <remarks>
/// 实现要点（ADR-002 具体参数）：
/// - 后台线程循环轮询 VK 0x08~0xFE（跳过 0x01~0x06 鼠标键与 0x07 未定义键，GDD-01：鼠标点击默认不触发敲击）；
/// - 边沿检测：_wasPressed 状态数组与上一帧比较，仅上升沿写入 Channel；
/// - 双频自适应：连续 IdleAfterSeconds 无键 → Sleep 空闲档；任一键按下立即回活动档；
/// - 热路径零分配：轮询循环内无 LINQ、无装箱、无事件分配（struct 进 Channel）；
/// - 看门狗：连续异常指数退避重启循环体并回调日志（ADR-002 后果节）。
/// </remarks>
public sealed class KeyPollingService : IKeyHookService
{
    // 轮询范围：跳过鼠标键（0x01~0x06）与未定义键（0x07）
    private const int MinVirtualKey = 0x08;
    private const int MaxVirtualKey = 0xFE;
    private const int MaxConsecutiveErrors = 3;

    private readonly Channel<KeyPressEvent> _channel = Channel.CreateUnbounded<KeyPressEvent>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    private readonly GameConfig _config;
    private readonly Action<string>? _log;

    private readonly bool[] _wasPressed = new bool[MaxVirtualKey + 1];
    private Thread? _pollThread;
    private volatile bool _running;

    // ── 空闲 CPU 自证（epics E1-S3 后置项，E2 补齐）─────────────────────
    // 轮询线程单写者，无需原子操作；每 60s 输出一行统计到日志，
    // 用真实数据证明"空闲档 50ms 轮询"的 CPU 占用量级（解读方法见 docs/smoke-test-e2.md §5）。
    private const int StatsWindowMs = 60_000;

    private long _statsWindowStart = Stopwatch.GetTimestamp();
    private int _statPolls;        // 统计窗口内总轮询次数
    private int _statActivePolls;  // 统计窗口内活动档（8ms）轮询次数
    private int _statEdgeEvents;   // 统计窗口内上升沿事件数

    /// <summary>看门狗：连续异常计数（指数退避用）。</summary>
    private int _consecutiveErrors;

    public bool IsRunning => _running;

    public ChannelReader<KeyPressEvent> Reader => _channel.Reader;

    /// <param name="config">轮询参数（活动/空闲间隔、空闲判定）。</param>
    /// <param name="log">可选日志回调（异常/降级上报，Platform 层不直接依赖日志库）。</param>
    public KeyPollingService(GameConfig config, Action<string>? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
    }

    public void Start()
    {
        if (_running)
        {
            return; // 幂等
        }

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "BongoCat.KeyPoll",
            Priority = ThreadPriority.AboveNormal,
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_pollThread is { IsAlive: true })
        {
            _pollThread.Join(TimeSpan.FromSeconds(1));
        }

        _pollThread = null;
    }

    public void Dispose() => Stop();

    /// <summary>轮询主循环（独立后台线程，ADR-002 自适应休眠）。</summary>
    private void PollLoop()
    {
        long lastActivityTicks = Stopwatch.GetTimestamp();

        while (_running)
        {
            try
            {
                bool anyKeyDown = PollOnce();
                if (anyKeyDown)
                {
                    lastActivityTicks = Stopwatch.GetTimestamp();
                    _consecutiveErrors = 0;
                }

                // 双频自适应：连续无键超过阈值 → 空闲档（ADR-002）
                var sinceActivity = Stopwatch.GetElapsedTime(lastActivityTicks);
                int intervalMs = sinceActivity.TotalSeconds >= _config.IdleAfterSeconds
                    ? _config.IdlePollIntervalMs
                    : _config.ActivePollIntervalMs;

                // CPU 自证统计（E1-S3 后置项）：仅计数与到点输出，热路径零分配
                _statPolls++;
                if (intervalMs == _config.ActivePollIntervalMs)
                {
                    _statActivePolls++;
                }

                if (Stopwatch.GetElapsedTime(_statsWindowStart).TotalMilliseconds >= StatsWindowMs)
                {
                    LogStats();
                }

                Thread.Sleep(intervalMs);
            }
            catch (Exception ex)
            {
                // 看门狗：连续异常 → 指数退避（ADR-002 后果节）
                _consecutiveErrors++;
                _log?.Invoke($"[KeyPolling] 轮询异常 #{_consecutiveErrors}: {ex.Message}");
                if (_consecutiveErrors >= MaxConsecutiveErrors)
                {
                    int backoffMs = (int)Math.Min(1000 * Math.Pow(2, _consecutiveErrors - MaxConsecutiveErrors), 30_000);
                    _log?.Invoke($"[KeyPolling] 连续 {_consecutiveErrors} 次异常，退避 {backoffMs}ms");
                    Thread.Sleep(backoffMs);
                }
            }
        }
    }

    /// <summary>扫描一轮虚拟键状态，上升沿写入 Channel。返回本轮是否有键按下。</summary>
    private bool PollOnce()
    {
        bool anyKeyDown = false;

        for (int vk = MinVirtualKey; vk <= MaxVirtualKey; vk++)
        {
            short state = NativeMethods.GetAsyncKeyState(vk);
            bool down = (state & NativeMethods.KeyPressedBit) != 0;

            if (down)
            {
                anyKeyDown = true;
                if (!_wasPressed[vk])
                {
                    // 上升沿：仅此处产生事件（零分配 struct 投递，ADR-002）
                    if (_channel.Writer.TryWrite(new KeyPressEvent(vk, Stopwatch.GetTimestamp())))
                    {
                        _statEdgeEvents++;
                    }
                }
            }

            _wasPressed[vk] = down;
        }

        return anyKeyDown;
    }

    /// <summary>输出一个统计窗口（默认 60s）的轮询/CPU 自证数据并重置窗口（epics E1-S3 后置项）。</summary>
    private void LogStats()
    {
        int polls = _statPolls;
        int active = _statActivePolls;
        int edges = _statEdgeEvents;
        var window = Stopwatch.GetElapsedTime(_statsWindowStart);

        _statPolls = 0;
        _statActivePolls = 0;
        _statEdgeEvents = 0;
        _statsWindowStart = Stopwatch.GetTimestamp();

        int idle = polls - active;
        double avgIntervalMs = polls > 0 ? window.TotalMilliseconds / polls : 0;
        // 单核占用估算：轮询本身 ~1µs/键扫描可忽略，占用量级 ≈ Sleep 无法精确折叠的唤醒开销，
        // 这里给出可核查的原始数据（次数/平均间隔），解读方法见 docs/smoke-test-e2.md §5
        _log?.Invoke(
            $"[KeyPolling][CPU] 近{window.TotalSeconds:F0}s：轮询 {polls} 次" +
            $"（活动档 {active} / 空闲档 {idle}），上升沿事件 {edges} 个，平均间隔 {avgIntervalMs:F2}ms");
    }
}
