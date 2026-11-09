using BongoCat.App;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 托盘菜单命令编排测试（epics E2：菜单命令对 VM/存档状态变更）。
/// 策略：FakeSaveService + FakeAudioService，验证命令 → 内存模型/音频服务/事件/落盘四条路径。
/// </summary>
public class TrayCommandTests
{
    private readonly FakeSaveService _save = new();
    private readonly FakeAudioService _audio = new();
    private readonly TrayCommandController _controller;

    public TrayCommandTests()
    {
        _controller = new TrayCommandController(_save, _audio);
    }

    [Fact]
    public void 启动应用设置_把存档音量与静音同步进音频服务()
    {
        _save.Current.MasterVolume = 0.4f;
        _save.Current.Muted = true;

        _controller.ApplyStartupSettings();

        Assert.Equal(0.4f, _audio.MasterVolume);
        Assert.True(_audio.Muted);
        Assert.Equal(0, _save.SaveCount); // 只同步，不落盘
    }

    [Fact]
    public void 音量调整_即时生效进音频服务与内存模型_不立即落盘()
    {
        bool volumeChanged = false;
        _controller.VolumeChanged += _ => volumeChanged = true;

        _controller.SetVolume(0.6f);

        Assert.Equal(0.6f, _audio.MasterVolume);
        Assert.Equal(0.6f, _save.Current.MasterVolume);
        Assert.True(volumeChanged);
        Assert.Equal(0, _save.SaveCount); // 防抖策略：拖动期间不写盘

        _controller.PersistSettings();
        Assert.Equal(1, _save.SaveCount);
    }

    [Fact]
    public void 音量钳制_超界值收敛到01()
    {
        _controller.SetVolume(1.7f);
        Assert.Equal(1f, _audio.MasterVolume);

        _controller.SetVolume(-0.3f);
        Assert.Equal(0f, _audio.MasterVolume);
    }

    [Fact]
    public void 静音切换_同步音频服务并立即落盘_解除时恢复音量()
    {
        _save.Current.MasterVolume = 0.5f;
        _controller.ApplyStartupSettings();

        _controller.ToggleMute();
        Assert.True(_save.Current.Muted);
        Assert.True(_audio.Muted);
        Assert.Equal(1, _save.SaveCount);

        _controller.ToggleMute();
        Assert.False(_save.Current.Muted);
        Assert.False(_audio.Muted);
        Assert.Equal(0.5f, _audio.MasterVolume); // 解除静音恢复存档音量
        Assert.Equal(2, _save.SaveCount);
    }

    [Fact]
    public void 镜像切换_写存档并广播事件()
    {
        bool? received = null;
        _controller.MirroredChanged += m => received = m;

        _controller.SetMirrored(true);

        Assert.True(_save.Current.CatMirrored);
        Assert.True(received);
        Assert.Equal(1, _save.SaveCount);
    }

    [Fact]
    public void 尺寸档位_合法值写入存档并广播()
    {
        double? received = null;
        _controller.ScaleChanged += s => received = s;

        _controller.SetWindowScale(1.5);

        Assert.Equal(1.5, _save.Current.WindowScale);
        Assert.Equal(1.5, received);
        Assert.Equal(1, _save.SaveCount);
    }

    [Fact]
    public void 尺寸档位_非法值收敛到100()
    {
        _controller.SetWindowScale(2.3); // 不在 0.75/1.0/1.5 档位表内

        Assert.Equal(1.0, _save.Current.WindowScale);
    }

    [Fact]
    public void 显示切换_翻转可见状态并广播()
    {
        var states = new List<bool>();
        _controller.VisibilityRequested += states.Add;

        Assert.True(_controller.IsCatVisible); // 初始可见

        _controller.ToggleCatVisible();
        _controller.ToggleCatVisible();

        Assert.Equal([false, true], states);
        Assert.True(_controller.IsCatVisible);
    }

    [Fact]
    public void 退出_广播事件并兜底落盘()
    {
        bool exitRequested = false;
        _controller.ExitRequested += () => exitRequested = true;

        _controller.Exit();

        Assert.True(exitRequested);
        Assert.Equal(1, _save.SaveCount);
    }

    // ── GDD-09：物种切换命令 ───────────────────────────────────────────

    [Fact]
    public void SetCatSpecies_写入存档并广播_同时置StyleChosen()
    {
        string? changed = null;
        _controller.SpeciesChanged += id => changed = id;

        _controller.SetCatSpecies("dino");

        Assert.Equal("dino", changed);
        Assert.Equal("dino", _save.Current.SpeciesId);
        Assert.True(_save.Current.StyleChosen); // 物种与风格在选择器一次同时确定（GDD-09 §3.2）
        Assert.Equal(1, _save.SaveCount);
    }

    [Fact]
    public void SetCatSpecies_空id_抛出拒绝()
    {
        Assert.Throws<ArgumentException>(() => _controller.SetCatSpecies(string.Empty));
        Assert.Throws<ArgumentException>(() => _controller.SetCatSpecies(" "));
    }

    // ── E9：宠物取名/重命名 ─────────────────────────────────────────────

    [Fact]
    public void SetPetName_清洗后写存档并广播_立即落盘()
    {
        string? changed = null;
        _controller.PetNameChanged += name => changed = name;
        _save.Current.SpeciesId = "dino";

        _controller.SetPetName("  小\u0000龙  ");

        Assert.Equal("小龙", changed);
        Assert.Equal("小龙", _save.Current.PetName);
        Assert.Equal("小龙", _save.Current.MpDisplayName); // 兼容旧字段镜像
        Assert.Equal(1, _save.SaveCount);
    }

    [Fact]
    public void SetPetName_空输入_按当前物种回退默认名()
    {
        _save.Current.SpeciesId = "agua";

        _controller.SetPetName("   ");

        Assert.Equal("阿瓜", _save.Current.PetName);
    }

    [Fact]
    public void RequestRename_广播事件不写盘()
    {
        bool requested = false;
        _controller.RenameRequested += () => requested = true;

        _controller.RequestRename();

        Assert.True(requested);
        Assert.Equal(0, _save.SaveCount);
    }
}
