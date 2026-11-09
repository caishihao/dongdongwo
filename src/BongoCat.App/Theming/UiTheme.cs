namespace BongoCat.App.Theming;

/// <summary>
/// 主题资源键集合（GDD-07 §5：主题只作用于背包/通行证/教程等普通窗口 UI，
/// 不影响猫本体渲染——那是 skin 槽位职责）。XAML 侧一律 DynamicResource 引用，
/// 运行时替换合并字典即全窗口热刷新（&lt; 200ms，GDD-07 §6）。
/// 品质色（common/…/epic）为收集品语义色，不随主题变化（GDD-03 品质色板）。
/// </summary>
public static class UiThemeKeys
{
    /// <summary>窗口底色（背包/通行证/教程窗口 Background）。</summary>
    public const string WindowBackground = "Bg.Window";

    /// <summary>卡片底色（顶栏/底栏/物品卡/预设区等 Border）。</summary>
    public const string CardBackground = "Bg.Card";

    /// <summary>卡片描边。</summary>
    public const string CardBorder = "Border.Card";

    /// <summary>主文字（标题/正文）。</summary>
    public const string TextPrimary = "Text.Primary";

    /// <summary>次文字（辅助说明）。</summary>
    public const string TextSecondary = "Text.Secondary";

    /// <summary>主按钮底色（合成/装备/购买等强调按钮）。</summary>
    public const string ButtonAccentBackground = "Button.AccentBg";

    /// <summary>主按钮描边。</summary>
    public const string ButtonAccentBorder = "Button.AccentBorder";

    /// <summary>高亮底色（里程碑"可领取"格等强调背景）。</summary>
    public const string HighlightBackground = "Accent.HighlightBg";

    /// <summary>琥珀强调色（金币/点击领取/进度条前景等经济语义色）。</summary>
    public const string Amber = "Accent.Amber";

    /// <summary>成功色（"已领取 ✓"等收敛反馈）。</summary>
    public const string Success = "Accent.Success";
}

/// <summary>
/// UI 主题定义（GDD-07 §5 uitheme：主色/强调色/背景色/文字色引用）。
/// Colors 为 主题键 → #RRGGBB 十六进制色串（UI 层据此构建 ResourceDictionary，
/// 键名用 <see cref="UiThemeKeys"/> 常量，单一数据源）。
/// </summary>
public sealed record UiTheme(string Id, string Name, IReadOnlyDictionary<string, string> Colors);

/// <summary>
/// 主题目录（V1 三套：默认奶油 = E1-E6 现状色系、墨夜 = 深底浅字、青竹 = 浅绿系）。
/// 代码内建为单一数据源（可单测对比度/键集一致性）；未来 uitheme 物品化
/// （GDD-07 §6 items.json theme_* 条目）时由内容管线扩充为 JSON 驱动，本目录保留为内置兜底。
/// </summary>
public static class UiThemeCatalog
{
    /// <summary>默认主题 id（存档 UiThemeId 默认值）。</summary>
    public const string DefaultThemeId = "cream";

    /// <summary>默认奶油（E1-E6 现状色系：奶油底 + 深棕文字 + 黏土粉按钮）。</summary>
    public static readonly UiTheme Cream = new(
        "cream", "奶油",
        new Dictionary<string, string>
        {
            [UiThemeKeys.WindowBackground] = "#FFF8EE",
            [UiThemeKeys.CardBackground] = "#FFFFFF",
            [UiThemeKeys.CardBorder] = "#E8DCC8",
            [UiThemeKeys.TextPrimary] = "#6B4A32",
            [UiThemeKeys.TextSecondary] = "#8A6A50",
            [UiThemeKeys.ButtonAccentBackground] = "#F5C4B3",
            [UiThemeKeys.ButtonAccentBorder] = "#D85A30",
            [UiThemeKeys.HighlightBackground] = "#FFF3D6",
            [UiThemeKeys.Amber] = "#E8A020",
            [UiThemeKeys.Success] = "#7A9B6D",
        });

    /// <summary>墨夜（深底浅字；对比度 ≥ WCAG AA 4.5:1，GDD-07 §7 可读性硬约束）。</summary>
    public static readonly UiTheme Ink = new(
        "ink", "墨夜",
        new Dictionary<string, string>
        {
            [UiThemeKeys.WindowBackground] = "#26262E",
            [UiThemeKeys.CardBackground] = "#33333D",
            [UiThemeKeys.CardBorder] = "#4A4A58",
            [UiThemeKeys.TextPrimary] = "#EAEAEA",
            [UiThemeKeys.TextSecondary] = "#B8B8C4",
            [UiThemeKeys.ButtonAccentBackground] = "#A64B38",
            [UiThemeKeys.ButtonAccentBorder] = "#D85A30",
            [UiThemeKeys.HighlightBackground] = "#4A4030",
            [UiThemeKeys.Amber] = "#F0B24A",
            [UiThemeKeys.Success] = "#9BC48A",
        });

    /// <summary>青竹（浅绿系：淡竹底 + 竹叶描边，正文深绿保证 AA 对比）。</summary>
    public static readonly UiTheme Bamboo = new(
        "bamboo", "青竹",
        new Dictionary<string, string>
        {
            [UiThemeKeys.WindowBackground] = "#F0F7EC",
            [UiThemeKeys.CardBackground] = "#FFFFFF",
            [UiThemeKeys.CardBorder] = "#C2DDBC",
            [UiThemeKeys.TextPrimary] = "#3D5232",
            [UiThemeKeys.TextSecondary] = "#6B8560",
            [UiThemeKeys.ButtonAccentBackground] = "#BFE3B4",
            [UiThemeKeys.ButtonAccentBorder] = "#5E8C57",
            [UiThemeKeys.HighlightBackground] = "#E3F2D8",
            [UiThemeKeys.Amber] = "#C98A1B",
            [UiThemeKeys.Success] = "#6F9B5E",
        });

    /// <summary>全部内置主题（托盘"主题"子菜单数据源，顺序 = 菜单顺序）。</summary>
    public static readonly IReadOnlyList<UiTheme> All = [Cream, Ink, Bamboo];

    /// <summary>
    /// 按 id 解析主题；未知/空白 id 回退默认奶油主题（GDD-07 §7 边界：
    /// 主题资源缺失/损坏 → 回退默认不白屏，调用方记日志）。
    /// </summary>
    public static UiTheme Resolve(string? themeId)
        => All.FirstOrDefault(t => t.Id == themeId) ?? Cream;
}
