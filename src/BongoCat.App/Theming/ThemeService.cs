using BongoCat.Core.Save;

namespace BongoCat.App.Theming;

/// <summary>
/// UI 主题服务（epics E7-S2 用例层）：主题切换、持久化、非法 id 防御。
/// 纯逻辑零 UI 依赖——UI 层订阅 <see cref="ThemeChanged"/> 替换合并字典实现全窗口热刷新。
/// </summary>
public interface IThemeService
{
    /// <summary>当前主题（启动时由存档解析；非法 id 回退默认奶油）。</summary>
    UiTheme Current { get; }

    /// <summary>可选主题列表（托盘"主题"子菜单数据源）。</summary>
    IReadOnlyList<UiTheme> Themes { get; }

    /// <summary>主题切换（参数 = 新主题；UI 订阅替换 ResourceDictionary）。</summary>
    event Action<UiTheme>? ThemeChanged;

    /// <summary>
    /// 启动初始化：读存档 UiThemeId 并解析（空白/非法 → 回退默认并写回修正，
    /// GDD-07 §7：校验失败回退默认主题记日志，不白屏）。必须在 <see cref="ISaveService.Load"/> 后调用。
    /// </summary>
    void Initialize();

    /// <summary>
    /// 切换主题并立即持久化。非法 id 拒绝（保持当前主题不变，记日志）——防御手改存档/传参。
    /// </summary>
    void SetTheme(string themeId);
}

/// <summary><see cref="IThemeService"/> 默认实现。</summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISaveService _save;
    private readonly Action<string>? _log;

    /// <inheritdoc />
    public UiTheme Current { get; private set; } = UiThemeCatalog.Cream;

    /// <inheritdoc />
    public IReadOnlyList<UiTheme> Themes => UiThemeCatalog.All;

    /// <inheritdoc />
    public event Action<UiTheme>? ThemeChanged;

    public ThemeService(ISaveService save, Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _log = log;
    }

    /// <inheritdoc />
    public void Initialize()
    {
        string saved = _save.Current.UiThemeId;
        Current = UiThemeCatalog.Resolve(saved);

        if (!string.Equals(Current.Id, saved, StringComparison.Ordinal))
        {
            // 非法/空白主题 id：回退默认并写回修正（下次启动不再重复告警）
            _log?.Invoke($"[Theme] 存档主题 id '{saved}' 非法，回退默认 {Current.Id}");
            _save.Current.UiThemeId = Current.Id;
            _save.Save();
        }
        else
        {
            _log?.Invoke($"[Theme] 已载入主题：{Current.Name}（{Current.Id}）");
        }
    }

    /// <inheritdoc />
    public void SetTheme(string themeId)
    {
        UiTheme theme = UiThemeCatalog.Resolve(themeId);
        if (!string.Equals(theme.Id, themeId, StringComparison.Ordinal))
        {
            _log?.Invoke($"[Theme] 拒绝非法主题 id '{themeId}'（保持当前 {Current.Id}）");
            return;
        }

        if (string.Equals(Current.Id, theme.Id, StringComparison.Ordinal))
        {
            return; // 同主题 no-op，避免无谓落盘与重刷
        }

        Current = theme;
        _save.Current.UiThemeId = theme.Id;
        _save.Save();
        _log?.Invoke($"[Theme] 切换主题 → {theme.Name}（{theme.Id}），已持久化");
        ThemeChanged?.Invoke(theme);
    }
}
