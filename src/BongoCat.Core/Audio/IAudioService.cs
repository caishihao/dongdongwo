namespace BongoCat.Core.Audio;

/// <summary>音效组常量（随机池键名，ADR-006：同组内洗牌袋不重复随机）。</summary>
public static class AudioGroups
{
    /// <summary>左爪低音鼓组。</summary>
    public const string DrumLow = "drum_low";

    /// <summary>右爪高音鼓组。</summary>
    public const string DrumHigh = "drum_high";
}

/// <summary>
/// 音频播放服务（ADR-006）。线程安全、非阻塞：Play 可在任意线程调用，
/// 实际混音由音频设备回调线程完成，永不卡键盘轮询 / UI 线程。
/// </summary>
public interface IAudioService
{
    /// <summary>从指定随机池播放一个音效（洗牌袋，连击不复读机）。</summary>
    void Play(string soundGroup);

    /// <summary>主音量 0~1（ADR-006 规则 5：经 VolumeSampleProvider）。</summary>
    float MasterVolume { get; set; }

    /// <summary>静音开关（静音时 Play 直接跳过，不进入混音器）。</summary>
    bool Muted { get; set; }
}
