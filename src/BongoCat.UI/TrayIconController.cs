using System.Windows.Forms;
using System.Windows.Media;
using BongoCat.App;
using BongoCat.App.Theming;
using BongoCat.Core.Config;
using BongoCat.Core.Save;
using Bitmap = System.Drawing.Bitmap;
using Icon = System.Drawing.Icon;

namespace BongoCat.UI;

/// <summary>
/// 托盘图标 + 右键菜单（epics E2：托盘图标 + 右键菜单 / 退出唯一入口）。
/// </summary>
/// <remarks>
/// 选型理由（托盘方案：Hardcodet.NotifyIcon.Wpf vs BCL WinForms NotifyIcon）：
/// 选 BCL `System.Windows.Forms.NotifyIcon`——① 托盘只承载"菜单 + 命令路由"，无需
/// Hardcodet 的 TaskbarIcon/XAML 绑定能力，引第三方包收益为零；② 减一个 NuGet 依赖面
/// （架构约束：依赖越少越稳）；③ 菜单里要塞音量滑条，WinForms 的
/// ToolStripControlHost + TrackBar 开箱即用；④ NotifyIcon 与 WPF 共用 UI 线程消息循环，
/// 菜单命令天然在 Dispatcher 线程回调，与 MainWindow 事件订阅无跨线程问题。
///
/// 图标：运行时用 WPF DrawingVisual 原创绘制（圆头 + 三角耳 + 豆豆眼简笔猫脸），
/// 渲染成 32×32 RGBA 后转 GDI Icon——零外部素材文件，满足"禁止复制原作素材"铁律。
///
/// 命令路由纪律：菜单项一律调 <see cref="TrayCommandController"/>（App 层），
/// 窗口动作（显示/隐藏/缩放/退出）由控制器广播事件、MainWindow 订阅执行——
/// 托盘 UI 不直接改窗口状态（分层纪律：UI 不持有游戏状态）。
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    /// <summary>音量滑条防抖落盘间隔（拖动期间只改内存，停手 400ms 后统一写盘）。</summary>
    private const int VolumeSaveDebounceMs = 400;

    private readonly TrayCommandController _commands;
    private readonly MainWindow _window;
    private readonly Action<string>? _log;

    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _coinItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _mirrorItem;
    private readonly IReadOnlyList<ToolStripMenuItem> _scaleItems;
    private readonly IReadOnlyList<ToolStripMenuItem> _themeItems;
    private readonly IReadOnlyList<ToolStripMenuItem> _styleItems;
    private readonly IReadOnlyList<ToolStripMenuItem> _speciesItems;
    private readonly ToolStripMenuItem _updateItem;
    private readonly System.Windows.Forms.Timer _volumeSaveTimer;
    private bool _disposed;

    public TrayIconController(
        TrayCommandController commands,
        MainWindow window,
        Action<string>? log = null,
        IThemeService? themeService = null,
        StyleCatalog? styles = null,
        SpeciesCatalog? species = null)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _log = log;

        // 防抖计时器先于音量滑条创建（滑条 ValueChanged 闭包引用它，可空性分析要求先赋值）
        _volumeSaveTimer = new System.Windows.Forms.Timer { Interval = VolumeSaveDebounceMs };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            _commands.PersistSettings();
        };

        Icon icon = BuildCatIcon();
        _menu = new ContextMenuStrip();

        // ── 显示 / 隐藏猫（文案在菜单打开时按当前状态刷新）──────────
        _toggleItem = new ToolStripMenuItem("隐藏猫");
        _toggleItem.Click += (_, _) => _commands.ToggleCatVisible();
        _menu.Items.Add(_toggleItem);

        // ── 背包/图鉴（E3：命令经控制器广播，App 订阅创建/激活窗口——托盘 UI 不持有视图）──
        var bagItem = new ToolStripMenuItem("背包");
        bagItem.Click += (_, _) => _commands.OpenInventory();
        _menu.Items.Add(bagItem);

        // ── 通行证（E6：与背包同模式广播事件）──
        var passItem = new ToolStripMenuItem("通行证");
        passItem.Click += (_, _) => _commands.OpenPass();
        _menu.Items.Add(passItem);

        // ── 新手教程（E7：完成态也能重看；重播只重置教程计数，不动经济）──
        var tutorialItem = new ToolStripMenuItem("新手教程");
        tutorialItem.Click += (_, _) => _commands.ReplayTutorial();
        _menu.Items.Add(tutorialItem);

        // ── 多人联机（E8：打开多人窗口——服务器地址/创建房间/房间码加入）──
        var multiplayerItem = new ToolStripMenuItem("多人");
        multiplayerItem.Click += (_, _) => _commands.OpenMultiplayer();
        _menu.Items.Add(multiplayerItem);

        // ── 主题三选一（E7：ResourceDictionary 热切换，勾选态单一数据源 = 存档 UiThemeId）──
        var themeMenu = new ToolStripMenuItem("主题");
        var themeItems = new List<ToolStripMenuItem>();
        if (themeService is not null)
        {
            foreach (UiTheme theme in themeService.Themes)
            {
                var item = new ToolStripMenuItem(theme.Name) { Tag = theme.Id };
                item.Click += (_, _) => themeService.SetTheme(theme.Id);
                themeMenu.DropDownItems.Add(item);
                themeItems.Add(item);
            }
        }

        _themeItems = themeItems;
        _menu.Items.Add(themeMenu);

        // ── 形象：物种 + 风格两个并列子菜单（GDD-09 §3.3 升级版；风格六选一为 GDD-08 既有）──
        // 勾选态单一数据源 = 存档 SpeciesId / CatStyleId；切换即时生效、互不清空装扮。
        var appearanceMenu = new ToolStripMenuItem("形象");

        var speciesMenu = new ToolStripMenuItem("物种");
        var speciesItems = new List<ToolStripMenuItem>();
        if (species is not null)
        {
            foreach (SpeciesPack pack in species.Species)
            {
                var item = new ToolStripMenuItem(pack.Name) { Tag = pack.Id };
                item.Click += (_, _) => _commands.SetCatSpecies(pack.Id);
                speciesMenu.DropDownItems.Add(item);
                speciesItems.Add(item);
            }
        }

        _speciesItems = speciesItems;
        appearanceMenu.DropDownItems.Add(speciesMenu);

        var styleMenu = new ToolStripMenuItem("风格");
        var styleItems = new List<ToolStripMenuItem>();
        if (styles is not null)
        {
            foreach (StylePack style in styles.Styles)
            {
                var item = new ToolStripMenuItem(style.Name) { Tag = style.Id };
                item.Click += (_, _) => _commands.SetCatStyle(style.Id);
                styleMenu.DropDownItems.Add(item);
                styleItems.Add(item);
            }
        }

        _styleItems = styleItems;
        appearanceMenu.DropDownItems.Add(styleMenu);

        _menu.Items.Add(appearanceMenu);

        // ── 金币余额（E4：静态文本菜单项，不可点；点开背包查看详情。文案随菜单打开刷新）──
        _coinItem = new ToolStripMenuItem("金币 0") { Enabled = false };
        _menu.Items.Add(_coinItem);

        // ── 音量滑条（TrackBar 承载连续值；SetVolume 即时生效、防抖落盘）──
        var track = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = System.Windows.Forms.TickStyle.None,
            AutoSize = false,
            Width = 160,
            Height = 28,
            Value = (int)Math.Round(Math.Clamp(commands.Save.MasterVolume, 0f, 1f) * 100),
        };
        track.ValueChanged += (_, _) =>
        {
            _commands.SetVolume(track.Value / 100f);
            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        };
        var volumeItem = new ToolStripMenuItem("音量");
        volumeItem.DropDownItems.Add(new ToolStripControlHost(track));
        _menu.Items.Add(volumeItem);

        // ── 静音 ──
        _muteItem = new ToolStripMenuItem("静音") { CheckOnClick = true };
        _muteItem.Click += (_, _) => _commands.ToggleMute();
        _menu.Items.Add(_muteItem);

        // ── 水平镜像 ──
        _mirrorItem = new ToolStripMenuItem("水平镜像") { CheckOnClick = true };
        _mirrorItem.Click += (_, _) => _commands.SetMirrored(_mirrorItem.Checked);
        _menu.Items.Add(_mirrorItem);

        // ── 猫尺寸三档（75% / 100% / 150%，单选）──
        var scaleMenu = new ToolStripMenuItem("猫尺寸");
        var scaleItems = new List<ToolStripMenuItem>();
        foreach (double scale in SaveModel.ValidWindowScales)
        {
            var item = new ToolStripMenuItem($"{scale * 100:0}%") { Tag = scale };
            item.Click += (_, _) => _commands.SetWindowScale(scale);
            scaleMenu.DropDownItems.Add(item);
            scaleItems.Add(item);
        }

        _scaleItems = scaleItems;
        _menu.Items.Add(scaleMenu);

        // ── 检查更新 / 重启并更新（暂存就绪后同一条目文案切换；命令经控制器广播）──
        _updateItem = new ToolStripMenuItem("检查更新");
        _updateItem.Click += (_, _) =>
        {
            if (_commands.HasPendingUpdate)
            {
                _commands.RestartAndUpdate();
            }
            else
            {
                _commands.CheckForUpdate();
            }
        };
        _menu.Items.Add(_updateItem);

        _menu.Items.Add(new ToolStripSeparator());

        // ── 退出（E2 起唯一退出入口：根治 WM_CLOSE 不退的已知边界）──
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => _commands.Exit();
        _menu.Items.Add(exitItem);

        // 菜单打开时同步勾选态（存档可能在别处被改，显示前刷新最可靠）
        _menu.Opening += (_, _) => SyncMenuState();

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "咚咚窝 DongDongNest（原创桌宠）",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => _commands.ToggleCatVisible(); // 双击托盘 = 显示/隐藏快捷方式

        // 退出请求（控制器广播）→ 保存窗口位置 → 移除托盘 → 关应用
        _commands.ExitRequested += OnExitRequested;

        _log?.Invoke("[Tray] 托盘图标已创建（菜单：显示隐藏/背包/通行证/新手教程/多人/主题/形象/音量/静音/镜像/尺寸/退出）");
    }

    private void OnExitRequested()
    {
        _window.PersistNow();
        _log?.Invoke("[Tray] 托盘退出：窗口位置已保存，准备关闭应用");
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>菜单每次打开前刷新勾选态与文案（单一数据源 = 存档模型 + 控制器状态）。</summary>
    private void SyncMenuState()
    {
        SaveModel save = _commands.Save;

        _toggleItem.Text = _commands.IsCatVisible ? "隐藏猫" : "显示猫";
        _coinItem.Text = $"金币 {save.Coins}"; // E4：静态余额展示（详情点开背包）
        _muteItem.Checked = save.Muted;
        _mirrorItem.Checked = save.CatMirrored;

        foreach (ToolStripMenuItem item in _scaleItems)
        {
            item.Checked = Math.Abs((double)item.Tag! - save.WindowScale) < 1e-6;
        }

        // E7：主题勾选态（单一数据源 = 存档 UiThemeId，切换在别处发生也能同步）
        foreach (ToolStripMenuItem item in _themeItems)
        {
            item.Checked = string.Equals((string?)item.Tag, save.UiThemeId, StringComparison.Ordinal);
        }

        // GDD-08：风格勾选态（单一数据源 = 存档 CatStyleId；空 = 待选择 → 无勾选）
        foreach (ToolStripMenuItem item in _styleItems)
        {
            item.Checked = string.Equals((string?)item.Tag, save.CatStyleId, StringComparison.Ordinal);
        }

        // GDD-09：物种勾选态（单一数据源 = 存档 SpeciesId；空串按 cat 解析——防御一致口径）
        foreach (ToolStripMenuItem item in _speciesItems)
        {
            string speciesId = save.SpeciesId.Length == 0
                ? SpeciesCatalog.DefaultSpeciesId
                : save.SpeciesId;
            item.Checked = string.Equals((string?)item.Tag, speciesId, StringComparison.Ordinal);
        }

        // 自更新：暂存就绪 → "重启并更新"；否则 → "检查更新"
        _updateItem.Text = _commands.HasPendingUpdate ? "重启并更新" : "检查更新";
    }

    /// <summary>
    /// 原创托盘图标：DrawingVisual 简笔猫脸（与主窗口同色系）→ RenderTargetBitmap → GDI Icon。
    /// 必须在 UI（STA）线程调用（本类只在 App.OnStartup / OnExit 触碰）。
    /// </summary>
    private static Icon BuildCatIcon()
    {
        const int size = 32;
        var head = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xC4, 0xB3));
        var innerEar = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x82, 0x6B));
        var ink = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x2B, 0x13));
        var blush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xC0, 0xD1));

        var visual = new System.Windows.Media.DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // 耳朵（三角）
            dc.DrawGeometry(innerEar, null, Geometry.Parse("M 8,16 L 6,3 L 16,10 Z"));
            dc.DrawGeometry(innerEar, null, Geometry.Parse("M 24,16 L 26,3 L 16,10 Z"));
            // 圆头
            dc.DrawEllipse(head, null, new System.Windows.Point(16, 19), 12, 11);
            // 腮红
            dc.DrawEllipse(blush, null, new System.Windows.Point(9, 22), 2.4, 1.6);
            dc.DrawEllipse(blush, null, new System.Windows.Point(23, 22), 2.4, 1.6);
            // 豆豆眼
            dc.DrawEllipse(ink, null, new System.Windows.Point(11.5, 17), 1.8, 1.8);
            dc.DrawEllipse(ink, null, new System.Windows.Point(20.5, 17), 1.8, 1.8);
            // 小鼻头
            dc.DrawGeometry(ink, null, Geometry.Parse("M 15,21 L 17,21 L 16,22.5 Z"));
            // w 嘴
            var pen = new Pen(ink, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Geometry.Parse("M 12,24 Q 14,26.5 16,24 Q 18,26.5 20,24"));
        }

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // WPF 位图 → GDI+ 位图 → Icon（行拷贝，不做任何外部素材读取）
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var rect = new System.Drawing.Rectangle(0, 0, size, size);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            rtb.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Height * data.Stride, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// 掉落托盘气泡提示（E4：物品掉落/金币包时通知；由 App 在 UI 线程订阅经济事件后调用）。
    /// </summary>
    public void ShowDropBalloon(string title, string text)
        => _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    /// <summary>更新流程气泡提示（失败用警示图标）。</summary>
    public void ShowUpdateBalloon(string title, string text, bool warning = false)
        => _notifyIcon.ShowBalloonTip(4000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.ExitRequested -= OnExitRequested;
        _volumeSaveTimer.Dispose();
        _notifyIcon.Visible = false; // 先隐藏再释放，避免托盘残影
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
