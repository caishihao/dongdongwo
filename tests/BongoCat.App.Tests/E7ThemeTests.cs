using BongoCat.App.Theming;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// E7 UI 主题服务测试（GDD-07 §5/§7：三套主题、切换持久化、非法 id 防御回退、对比度 AA）。
/// </summary>
public class E7ThemeTests
{
    private static (ThemeService Sut, FakeSaveService Save) Make()
    {
        var save = new FakeSaveService();
        return (new ThemeService(save), save);
    }

    [Fact]
    public void 目录_三套主题_默认奶油_墨夜_青竹()
    {
        Assert.Equal(3, UiThemeCatalog.All.Count);
        Assert.Equal(["cream", "ink", "bamboo"], UiThemeCatalog.All.Select(t => t.Id).ToArray());
        Assert.Equal("cream", UiThemeCatalog.DefaultThemeId);
    }

    [Fact]
    public void 三主题_键集合一致_覆盖UiThemeKeys全部键()
    {
        string[] expected =
        [
            UiThemeKeys.WindowBackground,
            UiThemeKeys.CardBackground,
            UiThemeKeys.CardBorder,
            UiThemeKeys.TextPrimary,
            UiThemeKeys.TextSecondary,
            UiThemeKeys.ButtonAccentBackground,
            UiThemeKeys.ButtonAccentBorder,
            UiThemeKeys.HighlightBackground,
            UiThemeKeys.Amber,
            UiThemeKeys.Success,
        ];

        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), theme.Colors.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Resolve_未知或空白id_回退默认奶油_不白屏()
    {
        Assert.Equal("cream", UiThemeCatalog.Resolve("nonexistent").Id);
        Assert.Equal("cream", UiThemeCatalog.Resolve("").Id);
        Assert.Equal("cream", UiThemeCatalog.Resolve(null).Id);
    }

    [Fact]
    public void Initialize_读存档主题id_载入对应主题()
    {
        var (sut, save) = Make();
        save.Current.UiThemeId = "ink";

        sut.Initialize();

        Assert.Equal("ink", sut.Current.Id);
    }

    [Fact]
    public void Initialize_存档非法id_回退默认并写回修正存档()
    {
        var (sut, save) = Make();
        save.Current.UiThemeId = "hackemode";

        sut.Initialize();

        Assert.Equal("cream", sut.Current.Id);       // GDD-07 §7：校验失败回退默认主题
        Assert.Equal("cream", save.Current.UiThemeId); // 写回修正，下次启动不再告警
        Assert.True(save.SaveCount > 0);
    }

    [Fact]
    public void SetTheme_合法id_切换持久化并广播()
    {
        var (sut, save) = Make();
        sut.Initialize();
        UiTheme? changed = null;
        sut.ThemeChanged += t => changed = t;

        sut.SetTheme("ink");

        Assert.Equal("ink", sut.Current.Id);
        Assert.Equal("ink", save.Current.UiThemeId);
        Assert.Equal("ink", changed!.Id);
        Assert.True(save.SaveCount > 0);
    }

    [Fact]
    public void SetTheme_同主题id_noOp_不重复落盘()
    {
        var (sut, save) = Make();
        sut.Initialize();
        int events = 0;
        sut.ThemeChanged += _ => events++;
        int savesBefore = save.SaveCount;

        sut.SetTheme("cream");

        Assert.Equal(0, events);
        Assert.Equal(savesBefore, save.SaveCount);
    }

    [Fact]
    public void SetTheme_非法id_拒绝并保持当前主题不变()
    {
        var (sut, save) = Make();
        sut.Initialize();
        sut.SetTheme("ink");
        int savesBefore = save.SaveCount;
        int events = 0;
        sut.ThemeChanged += _ => events++;

        sut.SetTheme("../../evil");

        Assert.Equal("ink", sut.Current.Id);        // 保持当前
        Assert.Equal("ink", save.Current.UiThemeId); // 存档不被污染
        Assert.Equal(0, events);
        Assert.Equal(savesBefore, save.SaveCount);
    }

    [Theory]
    [InlineData("ink")]
    [InlineData("bamboo")]
    [InlineData("cream")]
    public void 全主题_正文对比度满足WCAG_AA_4点5比1(string themeId)
    {
        UiTheme theme = UiThemeCatalog.Resolve(themeId);

        // 正文出现在窗口底与卡片底两种底色上，两处都要 ≥ 4.5:1（GDD-07 §7 硬约束）
        double onWindow = ContrastRatio(theme.Colors[UiThemeKeys.TextPrimary], theme.Colors[UiThemeKeys.WindowBackground]);
        double onCard = ContrastRatio(theme.Colors[UiThemeKeys.TextPrimary], theme.Colors[UiThemeKeys.CardBackground]);
        Assert.True(onWindow >= 4.5, $"{themeId} 窗口底正文对比度 {onWindow:F2} < 4.5");
        Assert.True(onCard >= 4.5, $"{themeId} 卡片底正文对比度 {onCard:F2} < 4.5");
    }

    [Fact]
    public void 墨夜_深底浅字_语义自洽()
    {
        UiTheme ink = UiThemeCatalog.Resolve("ink");

        Assert.True(Luminance(ink.Colors[UiThemeKeys.WindowBackground]) < 0.2, "墨夜窗口底应为深色");
        Assert.True(Luminance(ink.Colors[UiThemeKeys.TextPrimary]) > 0.8, "墨夜主文字应为浅色");
    }

    // ── WCAG 相对亮度 / 对比度计算（WCAG 2.1 §1.4.3）────────────────────

    private static double Luminance(string hex)
    {
        byte r = Convert.ToByte(hex[1..3], 16);
        byte g = Convert.ToByte(hex[3..5], 16);
        byte b = Convert.ToByte(hex[5..7], 16);

        static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    private static double ContrastRatio(string a, string b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
