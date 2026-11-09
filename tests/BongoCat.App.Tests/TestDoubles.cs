using BongoCat.Core.Audio;
using BongoCat.Core.Save;

namespace BongoCat.App.Tests;

/// <summary>音频假实现：只计数触发，不发声（module-map §4 测试映射）。</summary>
public sealed class FakeAudioService : IAudioService
{
    public int PlayCount { get; private set; }
    public List<string> PlayedGroups { get; } = [];

    public float MasterVolume { get; set; } = 1.0f;
    public bool Muted { get; set; }

    public void Play(string soundGroup)
    {
        PlayCount++;
        PlayedGroups.Add(soundGroup);
    }
}

/// <summary>存档假实现：内存模型 + 落盘计数（托盘命令持久化路径测试用）。</summary>
public sealed class FakeSaveService : ISaveService
{
    public SaveModel Current { get; } = new();

    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }

    public void Load() => LoadCount++;

    public void Save() => SaveCount++;
}

/// <summary>
/// 可手动拨动时间的 TimeProvider 假实现。
/// 约定：TimestampFrequency = TimeSpan.TicksPerSecond，即时间戳值 == TimeSpan 刻度，便于推算。
/// E4 补充：GetUtcNow 与单调时钟同源（Epoch 2025-01-01 UTC + 已推进毫秒数），
/// 支持掉落桶 24h 墙钟窗口的可测化；GetLocalNow 由基类按系统时区从 GetUtcNow 派生
/// （日期边界用例均以 24h 步进推进，不跨时区歧义）；AdvanceMilliseconds 负值 = 模拟系统时钟回拨。
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    /// <summary>墙钟历元：GetUtcNow = Epoch + 已推进毫秒。</summary>
    public static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private double _nowMs;

    public FakeTimeProvider(double initialMs = 0) => _nowMs = initialMs;

    public void AdvanceMilliseconds(double ms) => _nowMs += ms;

    /// <summary>回拨（测试专用：模拟系统时钟回拨的防刷路径）。</summary>
    public void RewindMilliseconds(double ms) => _nowMs -= ms;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => (long)(_nowMs * TimeSpan.TicksPerMillisecond);

    public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(_nowMs);
}
