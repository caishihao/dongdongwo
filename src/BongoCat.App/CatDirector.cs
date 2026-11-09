using BongoCat.Core.Audio;
using BongoCat.Core.Config;
using BongoCat.Core.Input;

namespace BongoCat.App;

/// <summary>
/// 猫行为导演（module-map.md §2）：输入事件 → 左右爪交替 → 音效 + UI 敲击通知。
/// 纯逻辑、零 UI 依赖，全部依赖构造函数注入，可用伪造接口单测（module-map §4）。
/// </summary>
/// <remarks>
/// 交替规则（GDD-01 §5）：维护 <see cref="_nextSide"/>，每次有效敲击输出并翻转。
/// 去抖规则（GDD-01 §7）：DebounceMs 窗口内的重复事件整体忽略——按住不放只算一次，
/// 防止轮询边沿重复或键盘自动重复导致"复读机"敲鼓。
/// </remarks>
public sealed class CatDirector
{
    private readonly IAudioService _audio;
    private readonly GameConfig _config;
    private readonly TimeProvider _time;

    private PawSide _nextSide = PawSide.Left;
    private long _lastTapTimestamp = -1; // -1 = 尚未敲过（0 是合法时间戳，不能当哨兵）

    /// <summary>一次有效敲击开始（UI 层订阅后驱动爪子动画；回调线程 = 调用方线程）。</summary>
    public event Action<PawSide>? TapStarted;

    /// <summary>累计有效敲击次数（供经济系统 E4 订阅）。</summary>
    public long TotalTaps { get; private set; }

    public CatDirector(IAudioService audio, GameConfig config, TimeProvider? time = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>处理一次按键按下事件（UI 线程经 Channel 投递后调用）。</summary>
    public void OnKeyPressed(KeyPressEvent e)
    {
        long now = _time.GetTimestamp();

        // 去抖：DebounceMs 内的重复事件忽略（GDD-01 §7）
        if (_lastTapTimestamp >= 0 &&
            _time.GetElapsedTime(_lastTapTimestamp, now).TotalMilliseconds < _config.DebounceMs)
        {
            return;
        }

        _lastTapTimestamp = now;
        TotalTaps++;

        PawSide side = _nextSide;
        _nextSide = side == PawSide.Left ? PawSide.Right : PawSide.Left;

        // 左爪低音、右爪高音（GDD-01 §4：各 2-3 变体随机轮换，由音效池实现）
        _audio.Play(side == PawSide.Left ? AudioGroups.DrumLow : AudioGroups.DrumHigh);
        TapStarted?.Invoke(side);
    }
}
