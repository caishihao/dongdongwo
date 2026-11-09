using BongoCat.Core.Audio;
using BongoCat.Core.Config;
using BongoCat.Core.Input;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// CatDirector 编排测试（epics E1-S4 验收：交替序列正确 + 去抖）。
/// 测试策略：直接灌 KeyPressEvent（module-map §4 FakeKeySource 等效）。
/// </summary>
public class CatDirectorTests
{
    private static readonly GameConfig Config = new();

    private static KeyPressEvent Key(FakeTimeProvider time) => new(0x41, time.GetTimestamp());

    [Fact]
    public void 连续按键_左右爪交替()
    {
        var audio = new FakeAudioService();
        var time = new FakeTimeProvider();
        var director = new CatDirector(audio, Config, time);
        var sides = new List<PawSide>();
        director.TapStarted += s => sides.Add(s);

        // 间隔 100ms 连打 4 次（远超去抖窗口）
        for (int i = 0; i < 4; i++)
        {
            director.OnKeyPressed(Key(time));
            time.AdvanceMilliseconds(100);
        }

        Assert.Equal([PawSide.Left, PawSide.Right, PawSide.Left, PawSide.Right], sides);
        // 左爪低音、右爪高音（GDD-01 §4）
        Assert.Equal(
            [AudioGroups.DrumLow, AudioGroups.DrumHigh, AudioGroups.DrumLow, AudioGroups.DrumHigh],
            audio.PlayedGroups);
    }

    [Fact]
    public void 去抖窗口内重复事件_整体忽略()
    {
        var audio = new FakeAudioService();
        var time = new FakeTimeProvider();
        var director = new CatDirector(audio, Config, time);

        director.OnKeyPressed(Key(time));
        time.AdvanceMilliseconds(Config.DebounceMs - 5); // 仍在 30ms 去抖窗口内
        director.OnKeyPressed(Key(time));

        Assert.Equal(1, audio.PlayCount);
        Assert.Equal(1, director.TotalTaps);
    }

    [Fact]
    public void 去抖窗口后恢复响应()
    {
        var audio = new FakeAudioService();
        var time = new FakeTimeProvider();
        var director = new CatDirector(audio, Config, time);

        director.OnKeyPressed(Key(time));
        time.AdvanceMilliseconds(Config.DebounceMs + 10);
        director.OnKeyPressed(Key(time));

        Assert.Equal(2, audio.PlayCount);
        // 第二次应翻到右爪
        Assert.Equal(AudioGroups.DrumHigh, audio.PlayedGroups[1]);
    }

    [Fact]
    public void 每次按键恰好触发一次音效调用()
    {
        var audio = new FakeAudioService();
        var time = new FakeTimeProvider();
        var director = new CatDirector(audio, Config, time);
        int taps = 0;
        director.TapStarted += _ => taps++;

        // 静音开关由音频服务内部实现（ADR-006），导演层保证"每键恰一次 Play"
        director.OnKeyPressed(Key(time));

        Assert.Equal(1, taps);
        Assert.Equal(1, director.TotalTaps);
        Assert.Equal(1, audio.PlayCount);
    }
}
