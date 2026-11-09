using System.Windows;
using System.Windows.Media;
using BongoCat.App.Theming;

namespace BongoCat.UI.Themes;

/// <summary>
/// 主题 ResourceDictionary 工厂 + 热切换管理（epics E7-S2 验收：主题切换全局生效）。
/// 机制：把主题色表（App 层 <see cref="UiTheme"/>，单一数据源）构建为 ResourceDictionary，
/// 替换 Application.Resources.MergedDictionaries 中的旧主题字典——所有已开窗口里
/// DynamicResource 引用即时重取，无需重启、无需重建窗口（GDD-07 §6 &lt; 200ms）。
/// </summary>
public sealed class ThemeManager
{
    private readonly IThemeService _themeService;
    private ResourceDictionary? _currentDictionary;

    public ThemeManager(IThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
    }

    /// <summary>
    /// 应用当前主题（App.OnStartup 在任何窗口创建前调用一次，避免启动闪色）。
    /// </summary>
    public void ApplyCurrent() => Apply(_themeService.Current);

    /// <summary>订阅 <see cref="IThemeService.ThemeChanged"/> 后由切换事件驱动（必须在 UI 线程）。</summary>
    public void Apply(UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_currentDictionary is not null)
        {
            merged.Remove(_currentDictionary);
        }

        _currentDictionary = BuildDictionary(theme);
        merged.Insert(0, _currentDictionary); // 优先级最高，窗口内局部资源仍可覆盖
    }

    /// <summary>主题色表 → 画刷字典（键 = <see cref="UiThemeKeys"/>，值 = SolidColorBrush）。</summary>
    private static ResourceDictionary BuildDictionary(UiTheme theme)
    {
        var dict = new ResourceDictionary();
        foreach ((string key, string hex) in theme.Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            dict[key] = new SolidColorBrush(color);
        }

        return dict;
    }
}
