using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using BongoCat.Core.Audio;
using BongoCat.Core.Util;

namespace BongoCat.Platform.Audio;

/// <summary>
/// NAudio 音频服务（ADR-006 决定 B）：WaveOutEvent 输出 + 自写 BongoMixer 混音 + VolumeSampleProvider 主音量。
/// Play 线程安全、非阻塞——从任意线程投递即可，混音由音频设备回调线程消费，
/// 绝不卡轮询/UI 线程。
/// </summary>
/// <remarks>
/// 教学注：NAudio 2.2.1 的分发包（Core/WinMM/Wasapi）中不含 MixingSampleProvider
/// （该类型随主程序集仅在 .NET Framework 下提供），故此处自写 ~40 行混音器；
/// 这也让我们能干净地实现 ADR-006 规则 2 的"播放不新建对象"：音色实例播放完毕后
/// 归还对象池复用，而非每次 Play 分配。
/// </remarks>
public sealed class NAudioAudioService : IAudioService, IDisposable
{
    private const int MaxPooledVoices = 24;

    /// <summary>设备重建的最大连续尝试次数（指数退避到 30s 封顶）。</summary>
    private const int MaxRebuildAttempts = 6;

    private WaveOutEvent _output;
    private readonly BongoMixer _mixer;
    private readonly VolumeSampleProvider _volume;

    private readonly Dictionary<string, ShuffleBag<float[]>> _pools = [];
    private readonly ConcurrentQueue<CachedSoundSampleProvider> _voicePool = new();
    private int _voicesCreated;

    private readonly Action<string>? _log;

    // 设备重建串行化：RebuildOutput 只在锁内换 _output；Play 不触碰 _output，重建期间照常混音入队
    private readonly object _deviceLock = new();
    private int _rebuildAttempts;

    /// <param name="random">洗牌袋随机源（种子可复现）。</param>
    /// <param name="log">可选日志回调（设备故障/重建轨迹上报，E2-S4 烟测证据用）。</param>
    public NAudioAudioService(Random random, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(random);
        _log = log;

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(BongoSynthesizer.SampleRate, channels: 1);

        _mixer = new BongoMixer(waveFormat, ReturnVoice);
        _volume = new VolumeSampleProvider(_mixer) { Volume = 1.0f };

        _output = CreateOutput();

        // 启动时全量预合成进内存，运行时零文件 IO（ADR-006 规则 1）
        foreach ((string group, List<float[]> variants) in BongoSynthesizer.BuildDefaultPools(random))
        {
            _pools[group] = new ShuffleBag<float[]>(variants, random);
        }
    }

    /// <summary>
    /// 创建默认设备输出（epics E2-S4：热拔插恢复 = 设备失效时用新默认设备重建 WaveOutEvent，
    /// WaveOutEvent 构造时总是取"当前默认设备"，因此重建即完成切换）。
    /// 混音器与音量节点全生命周期复用，重建只换输出端。
    /// </summary>
    private WaveOutEvent CreateOutput()
    {
        var output = new WaveOutEvent { DesiredLatency = 60 }; // ms：低延迟不爆音
        output.Init(_volume);
        output.PlaybackStopped += OnPlaybackStopped;
        return output;
    }

    /// <summary>
    /// 设备失效检测入口（epics E2-S4）：WaveOutEvent 出错（拔耳机/默认设备消失/驱动重置）
    /// 时以非空 Exception 触发 PlaybackStopped——正常 Stop/播完为 null，不误报。
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _log?.Invoke($"[Audio] 输出设备异常：{e.Exception.Message}，开始重建（E2-S4 热拔插恢复）");
        Task.Run(() => RebuildOutput());
    }

    /// <summary>
    /// 重建输出设备：换新 WaveOutEvent（自动绑定当前默认设备）→ 释放旧实例。
    /// 失败指数退避重试（拔掉所有音频设备时短暂重试，设备回来即恢复），不抛出、不阻塞 Play。
    /// </summary>
    private void RebuildOutput()
    {
        lock (_deviceLock)
        {
            try
            {
                var fresh = CreateOutput();
                var old = _output;
                _output = fresh;
                _rebuildAttempts = 0;
                try
                {
                    old.Dispose();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Audio] 旧设备实例释放异常（已忽略）：{ex.Message}");
                }

                _log?.Invoke("[Audio] WaveOutEvent 已重建，后续音效走新默认设备");
            }
            catch (Exception ex)
            {
                _rebuildAttempts++;
                if (_rebuildAttempts > MaxRebuildAttempts)
                {
                    _log?.Invoke($"[Audio] 设备重建连续失败 {_rebuildAttempts - 1} 次，停止重试（有设备接入后任一有效音效触发前仍可经 Play 路径感知，重启应用恢复）");
                    return;
                }

                int delayMs = Math.Min(1000 * (int)Math.Pow(2, _rebuildAttempts - 1), 30_000);
                _log?.Invoke($"[Audio] 设备重建失败：{ex.Message}，{delayMs}ms 后重试（第 {_rebuildAttempts}/{MaxRebuildAttempts} 次）");
                Thread.Sleep(delayMs);
                RebuildOutput();
            }
        }
    }

    /// <inheritdoc />
    public float MasterVolume
    {
        get => _volume.Volume;
        set => _volume.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <inheritdoc />
    public bool Muted { get; set; }

    /// <inheritdoc />
    public void Play(string soundGroup)
    {
        if (Muted || !_pools.TryGetValue(soundGroup, out ShuffleBag<float[]>? bag))
        {
            return;
        }

        // 洗牌袋取一个变体（连击不复读机）→ 从池中取空闲音色实例投入混音器
        if (!_voicePool.TryDequeue(out CachedSoundSampleProvider? voice))
        {
            if (_voicesCreated >= MaxPooledVoices)
            {
                return; // 池耗尽（>24 重叠音）时丢弃本次触发，防内存增长
            }

            voice = new CachedSoundSampleProvider(bag.Next(), _mixer.WaveFormat);
            _voicesCreated++;
        }
        else
        {
            voice.Reset(bag.Next());
        }

        _mixer.AddInput(voice);
    }

    /// <summary>音色播放完毕（混音器回调）→ 归还对象池复用（ADR-006 规则 2：播放不新建对象）。</summary>
    private void ReturnVoice(ISampleProvider input)
    {
        if (input is CachedSoundSampleProvider voice && _voicePool.Count < MaxPooledVoices)
        {
            _voicePool.Enqueue(voice);
        }
    }

    public void Dispose()
    {
        lock (_deviceLock)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
        }
    }
}

/// <summary>
/// 自写单声道浮点混音器：把所有活动音色按样本叠加，读完后自动移除并回收。
/// 对应 NAudio.MixingSampleProvider 的 ReadFully=false 语义：无输入时立即返回静音。
/// </summary>
public sealed class BongoMixer : ISampleProvider
{
    private readonly List<ISampleProvider> _inputs = [];
    private readonly Action<ISampleProvider> _onInputFinished;
    private readonly object _lock = new();
    private float[] _scratch = [];

    public BongoMixer(WaveFormat waveFormat, Action<ISampleProvider>? onInputFinished = null)
    {
        WaveFormat = waveFormat;
        _onInputFinished = onInputFinished ?? (_ => { });
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>注册一个音色输入（线程安全：Play 可从任意线程调用）。</summary>
    public void AddInput(ISampleProvider input)
    {
        lock (_lock)
        {
            _inputs.Add(input);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ISampleProvider[] inputs;

        lock (_lock)
        {
            inputs = [.. _inputs]; // 快照：Read 期间不持锁，允许 Play 并发注册
        }

        if (_scratch.Length < count)
        {
            _scratch = new float[count]; // 复用的叠加暂存区（避免音频线程反复分配）
        }

        int maxRead = 0;
        bool first = true;

        foreach (ISampleProvider input in inputs)
        {
            int read;
            if (first)
            {
                // 第一个输入直接写入输出缓冲
                read = input.Read(buffer, offset, count);
                if (read == 0)
                {
                    RemoveFinished(input);
                    continue;
                }

                maxRead = read;
                first = false;
            }
            else
            {
                // 后续输入读到暂存区后逐样本叠加（混音 = 相加）
                read = input.Read(_scratch, 0, count);
                if (read == 0)
                {
                    RemoveFinished(input);
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    buffer[offset + i] += _scratch[i];
                }

                maxRead = Math.Max(maxRead, read);
            }
        }

        if (!first)
        {
            // 软限幅 tanh：小信号近似线性，重叠加时平滑压缩，杜绝硬削波爆音（ADR-006 零爆音）
            for (int i = 0; i < maxRead; i++)
            {
                buffer[offset + i] = MathF.Tanh(buffer[offset + i]);
            }
        }

        // 未覆盖区间清零（上一轮叠加残留）
        for (int i = offset + maxRead; i < offset + count; i++)
        {
            buffer[i] = 0f;
        }

        return maxRead;
    }

    private void RemoveFinished(ISampleProvider input)
    {
        lock (_lock)
        {
            _inputs.Remove(input);
        }

        _onInputFinished(input);
    }
}

/// <summary>
/// 缓存音效样本播放器：把预合成的 float[] 按 ISampleProvider 流式读出，读毕返回 0。
/// 实例可经 <see cref="Reset"/> 复用（配对新的样本数据）。
/// </summary>
public sealed class CachedSoundSampleProvider : ISampleProvider
{
    private float[] _samples;
    private int _position;

    public CachedSoundSampleProvider(float[] samples, WaveFormat targetFormat)
    {
        _samples = samples;
        WaveFormat = targetFormat;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>复用：装载新样本并回到起点。</summary>
    public void Reset(float[] samples)
    {
        _samples = samples;
        _position = 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = _samples.Length - _position;
        int toCopy = Math.Min(available, count);
        if (toCopy <= 0)
        {
            return 0;
        }

        Array.Copy(_samples, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }
}
