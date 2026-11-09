using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using BongoCat.App;
using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.Core.Input;
using BongoCat.Core.Save;
using BongoCat.Platform.Window;
using BongoCat.UI.Cosmetics;
using BongoCat.UI.ViewModels;

namespace BongoCat.UI;

/// <summary>
/// 透明置顶桌宠主窗口（ADR-001 / ADR-003）。
/// 代码后置只做三类事：Win32 窗口胶水、爪子动画、拖动 + 位置记忆。
/// 游戏状态一律走 <see cref="CatViewModel"/>（UI 层不持有状态规则）。
/// E2：托盘菜单命令（可见性/尺寸/镜像）经 <see cref="TrayCommandController"/> 事件接入本窗口。
/// GDD-08：猫本体渲染由 <see cref="StyleRenderer"/> 装载（风格模板 + 参数化敲击动画），
/// 与远程猫共用同一渲染入口（单一数据源）。
/// </summary>
public partial class MainWindow : Window
{
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WmHotkey = 0x0312;

    /// <summary>基准窗口尺寸（DIP，E1 用户拍板的 50% 尺寸）。缩放档位 = 相对此基准的比例。</summary>
    public const double BaseWindowWidth = 130;
    public const double BaseWindowHeight = 110;

    private readonly CatViewModel _vm;
    private readonly ITransparentWindowService _windowService;
    private readonly GameConfig _config;
    private readonly TrayCommandController _trayCommands;
    private readonly StyleCatalog _styles;
    private System.Windows.Interop.HwndSource? _hwndSource;

    /// <summary>当前风格模板部件（OnLoaded 装载；风格/物种切换时整体重载）。</summary>
    private StyleCatParts? _parts;

    /// <summary>当前风格参数包（敲击动画数据源）。</summary>
    private StylePack _style = null!;

    /// <summary>当前风格 id（物种切换时重载模板用）。</summary>
    private string _styleId = StyleCatalog.DefaultStyleId;

    /// <summary>当前物种 id（GDD-09：形体层；OnLoaded 按存档装载，托盘/选择器切换时重载）。</summary>
    private string _speciesId = Core.Config.SpeciesCatalog.DefaultSpeciesId;

    /// <summary>拖动结束（E7：参数 = 光标位移，屏幕像素；教程"拖拖看"步订阅判定阈值）。</summary>
    public event Action<double, double>? DragCompleted;

    // 拖动状态（屏幕像素坐标，全程不做 DPI 换算，见 OnMouseMove）
    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartWindowX;
    private int _dragStartWindowY;

    public MainWindow(
        CatViewModel vm,
        ITransparentWindowService windowService,
        GameConfig config,
        TrayCommandController trayCommands,
        StyleCatalog styles)
    {
        InitializeComponent();
        _vm = vm;
        _windowService = windowService;
        _config = config;
        _trayCommands = trayCommands;
        _styles = styles;
        DataContext = _vm;

        // E2：尺寸档位（存档已在 App.OnStartup 加载）→ 启动即按档位定窗（比例还原随后的物理像素重算自动适配）
        // 注意：存档加载发生在 MainWindow 构造之后（App.OnStartup 顺序），默认档位在此取 100%，
        // OnLoaded 再按真实存档风格装载模板。
        double scale = SaveModel.IsValidWindowScale(_vm.Save.WindowScale) ? _vm.Save.WindowScale : 1.0;
        Width = BaseWindowWidth * scale;
        Height = BaseWindowHeight * scale;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwndSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!;
        nint hwnd = _hwndSource.Handle;

        // ADR-001：扩展样式（TOPMOST | TOOLWINDOW | NOACTIVATE）+ DWM 逐像素透明
        _windowService.ApplyPetWindowStyles(hwnd);
        if (!_windowService.TryEnableDwmTransparency(hwnd))
        {
            // E1 按用户拍板：色键兜底留接口不实现；DWM 在 Win10 1607+ 实际不可禁用，
            // 此处仅提示降级（设置面板自检项在 E2 补）
            FileLog.Write("[Window] DWM 透明启用失败，处于未透明降级模式");
        }
        else
        {
            FileLog.Write($"[Window] DWM 透明已启用（hwnd=0x{hwnd:X}）");
        }

        // E1 修复（黑底 bug）：DwmExtendFrameIntoClientArea 只是把"玻璃"铺进客户区，
        // 还必须把 WPF 合成目标的背景设为透明黑（alpha=0），未绘制区域才会被 DWM
        // 合成为透明——否则 WPF 会把 Background=Transparent 渲染成不透明黑块。
        _hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;

        // E1 修复（热键/点击失效 bug）：WndProc 必须挂接到 HwndSource 才能收到
        // WM_HOTKEY 与 WM_MOUSEACTIVATE——此前从未注册，退出热键形同虚设。
        _hwndSource.AddHook(WndProc);

        // E1 临时退出方式：Ctrl+Alt+Q（托盘菜单 E2 补）
        bool hotkey = _windowService.TryRegisterQuitHotkey(hwnd, App.QuitHotkeyId);
        FileLog.Write($"[Window] 退出热键 Ctrl+Alt+Q 注册: {(hotkey ? "成功" : "失败")}");

        RestorePosition();
        if (_windowService.TryGetWindowRectPixels(hwnd, out int vx, out int vy, out int vw2, out int vh2))
        {
            FileLog.Write($"[Window] 还原后窗口物理矩形 ({vx},{vy}) {vw2}x{vh2}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.TapStarted += OnTapStarted;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CatViewModel.IsMirrored))
            {
                MirrorScale.ScaleX = _vm.IsMirrored ? -1 : 1;
            }
            else if (args.PropertyName is nameof(CatViewModel.EquippedHat)
                     or nameof(CatViewModel.EquippedSkin)
                     or nameof(CatViewModel.EquippedBongos))
            {
                ApplyOutfit(); // E3：背包换装 → 猫渲染层热换绑（GDD-03 §2 < 100ms，纯渲染属性）
            }
        };

        // E2：托盘菜单命令 → 窗口动作（App 层广播事件，UI 层执行视图操作）
        _trayCommands.VisibilityRequested += OnVisibilityRequested;
        _trayCommands.MirroredChanged += OnMirroredChanged;
        _trayCommands.ScaleChanged += ApplyScale;

        // GDD-08/GDD-09：按存档风格×物种装载模板（未知 id 防御回退 clay/cat），随后按存档装扮换绑
        _speciesId = ResolveSpeciesId(_vm.Save.SpeciesId);
        ApplyStyle(ResolveStyleId(_vm.Save.CatStyleId));
        _vm.Start();
        StartBobbing();

        // 一次性回归探针：3 秒后回读物理矩形，若与还原位置不符说明有外部力量在移动窗口
        var verify = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        verify.Tick += (_, _) =>
        {
            verify.Stop();
            if (_windowService.TryGetWindowRectPixels(_hwndSource!.Handle, out int x, out int y, out int w, out int h))
            {
                FileLog.Write($"[Window] 3s位置验证 物理({x},{y}) {w}x{h}");
            }
        };
        verify.Start();

        FileLog.Write("[Window] Loaded：轮询已启动，动画已启动");
    }

    /// <summary>Win32 消息钩子：点击不抢焦点 + 全局热键退出（ADR-001 后果节）。</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmMouseActivate:
                // 点击猫绝不激活窗口——否则会抢走玩家游戏焦点（桌宠体验关键细节）
                handled = true;
                return MaNoActivate;

            case WmHotkey:
                // Ctrl+Alt+Q：保存位置并退出
                FileLog.Write("[Window] 收到退出热键，保存并退出");
                SavePosition();
                Application.Current.Shutdown();
                handled = true;
                break;
        }

        return nint.Zero;
    }

    // ── 风格装载（GDD-08；主猫/远程猫共用 StyleRenderer 单一渲染入口）────

    /// <summary>存档风格 id 合法性解析：空/未知 → clay + 日志（GDD-08 §6 边界 1）。</summary>
    public string ResolveStyleId(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId))
        {
            return StyleCatalog.DefaultStyleId;
        }

        if (!_styles.TryGet(styleId, out _))
        {
            FileLog.Write($"[Style] 存档风格 '{styleId}' 无效，回退 clay");
            return StyleCatalog.DefaultStyleId;
        }

        return styleId;
    }

    /// <summary>存档物种 id 合法性解析：空/未知 → cat + 日志（GDD-09 §5：空串按 cat 解析；§7 sprite 物种 agua 合法，缺资产由渲染层回退）。</summary>
    public string ResolveSpeciesId(string? speciesId)
    {
        if (string.IsNullOrEmpty(speciesId))
        {
            return Core.Config.SpeciesCatalog.DefaultSpeciesId;
        }

        if (speciesId is not ("cat" or "dino") && !BongoCat.App.Styles.SpriteSpeciesCatalog.IsSpriteSpecies(speciesId))
        {
            FileLog.Write($"[Species] 存档物种 '{speciesId}' 无效，回退 cat");
            return Core.Config.SpeciesCatalog.DefaultSpeciesId;
        }

        return speciesId;
    }

    /// <summary>
    /// 切换风格：重载对应模板 + 参数（零 Layout 重算原则：动画只动 Transform/Opacity），
    /// 并按当前装扮立即换绑。托盘"形象"与首启选择器共用本入口。
    /// </summary>
    public void ApplyStyle(string styleId)
    {
        string resolved = ResolveStyleId(styleId);
        _styleId = resolved;
        _parts = StyleRenderer.ApplyStyle(StyleHost, resolved, _styles, _speciesId, FileLog.Write);
        _styles.TryGet(resolved, out _style!);
        ApplyOutfit();
    }

    /// <summary>
    /// 切换物种（GDD-09 §3.3）：重载当前风格的对应物种形体模板并按当前装扮换绑。
    /// 存档写入由 TrayCommandController.SetCatSpecies 完成（单一写入路径）；
    /// 物种限定装备清洗由 App 层在 SpeciesChanged 接线上完成（先清洗后调用本方法）。
    /// </summary>
    public void ApplySpecies(string speciesId)
    {
        _speciesId = ResolveSpeciesId(speciesId);
        ApplyStyle(_styleId);
        FileLog.Write($"[Species] 主猫物种已切换 → '{_speciesId}'（风格保持 '{_styleId}'）");
    }

    // ── 敲击动画（ADR-003：只动 RenderTransform，永不触发 Layout 重算；参数来自 styles.json）──

    private void OnTapStarted(PawSide side)
    {
        if (_parts is null)
        {
            return;
        }

        // GDD-08 §2.6：reboundMs 用户设置作为全局系数缩放各风格时长（基线 120ms = 黏土）
        double reboundScale = _config.ReboundMs / 120.0;
        CatTapAnimator.PlayTap(_parts, _style, reboundScale, side);
    }

    /// <summary>呼吸浮动：sin 波动用 AutoReverse + SineEase 近似（周期 2.4s，GDD-01 §6）。</summary>
    private void StartBobbing()
    {
        var halfPeriod = TimeSpan.FromMilliseconds(_config.BobbingPeriodSec * 500);
        var bob = new DoubleAnimation(0, _config.BobbingAmpPx, halfPeriod)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        BobTranslate.BeginAnimation(TranslateTransform.YProperty, bob);
    }

    // ── 拖动 + 位置记忆（epics E1-S6）────────────────────────────────────

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_windowService.TryGetCursorScreenPos(out int cx, out int cy) || _hwndSource is null)
        {
            return;
        }

        // 边框无边框窗口：客户区原点 == 窗口原点，PointToScreen 即窗口屏幕像素坐标
        var origin = PointToScreen(new Point(0, 0));
        _dragStartCursorX = cx;
        _dragStartCursorY = cy;
        _dragStartWindowX = (int)origin.X;
        _dragStartWindowY = (int)origin.Y;
        _dragging = true;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging ||
            !_windowService.TryGetCursorScreenPos(out int cx, out int cy) ||
            _hwndSource is null)
        {
            return;
        }

        // 全程屏幕像素 delta，绕开 PerMonitorV2 混合 DPI 换算
        _windowService.SetWindowScreenPos(
            _hwndSource.Handle,
            _dragStartWindowX + (cx - _dragStartCursorX),
            _dragStartWindowY + (cy - _dragStartCursorY));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        // E7：拖动位移上报（光标全程屏幕像素坐标系；教程服务自行判阈值）
        if (_windowService.TryGetCursorScreenPos(out int ux, out int uy))
        {
            DragCompleted?.Invoke(ux - _dragStartCursorX, uy - _dragStartCursorY);
        }

        SavePosition();
    }

    /// <summary>
    /// 猫顶部中心锚点（E7 教程气泡指向用；返回屏幕物理像素——
    /// 全程物理像素纪律，消费侧（气泡窗口）按自身 DPI 变换转 DIP 定位）。
    /// </summary>
    public Point GetCatAnchorScreenPoint()
        => _hwndSource is not null
            ? PointToScreen(new Point(Width / 2, 0))
            : new Point(SystemParameters.WorkArea.Right / 2, SystemParameters.WorkArea.Top);

    /// <summary>
    /// 位置还原：全程物理像素（与拖动同坐标系）。
    /// E1 修复：此前用 WPF Left/Top（DIP）设置、而 Win32 侧按物理像素理解，
    /// 150% 缩放下窗口被推到工作区右下角不可见位置。
    /// </summary>
    private void RestorePosition()
    {
        if (_hwndSource is null)
        {
            return;
        }

        nint hwnd = _hwndSource.Handle;
        if (!_windowService.TryGetWorkAreaPixels(hwnd, out int ax, out int ay, out int aw, out int ah) ||
            !_windowService.TryGetWindowRectPixels(hwnd, out _, out _, out int ww, out int wh))
        {
            FileLog.Write("[Window] 位置还原失败：取不到工作区/窗口物理矩形");
            return;
        }

        double maxX = Math.Max(0, aw - ww);
        double maxY = Math.Max(0, ah - wh);
        int px = ax + (int)Math.Clamp(_vm.Save.CatPositionRelX * maxX, 0, maxX);
        int py = ay + (int)Math.Clamp(_vm.Save.CatPositionRelY * maxY, 0, maxY);
        _windowService.SetWindowScreenPos(hwnd, px, py);
        FileLog.Write($"[Window] 位置还原 物理({px},{py}) 比例({_vm.Save.CatPositionRelX:F2},{_vm.Save.CatPositionRelY:F2}) 工作区{aw}x{ah}");
    }

    /// <summary>位置保存：窗口物理矩形左上角 / 工作区可用行程 → 0~1 比例（越界 clamp）。</summary>
    private void SavePosition()
    {
        if (_hwndSource is null)
        {
            return;
        }

        nint hwnd = _hwndSource.Handle;
        if (!_windowService.TryGetWorkAreaPixels(hwnd, out int ax, out int ay, out int aw, out int ah) ||
            !_windowService.TryGetWindowRectPixels(hwnd, out int wx, out int wy, out int ww, out int wh))
        {
            return;
        }

        double maxX = Math.Max(1, aw - ww);
        double maxY = Math.Max(1, ah - wh);
        _vm.OnDragEnded(
            Math.Clamp((wx - ax) / maxX, 0, 1),
            Math.Clamp((wy - ay) / maxY, 0, 1));
    }

    // ── 托盘菜单命令入口（E2）────────────────────────────────────────

    private void OnVisibilityRequested(bool visible)
    {
        if (visible)
        {
            ShowAndRestorePosition();
        }
        else
        {
            HideCat();
        }
    }

    private void OnMirroredChanged(bool mirrored) => _vm.IsMirrored = mirrored;

    /// <summary>隐藏猫（托盘"显示/隐藏猫"）。轮询与存档服务继续运行；再显示时按存档比例重算位置。</summary>
    public void HideCat()
    {
        SavePosition();
        Hide();
        FileLog.Write("[Window] 猫已隐藏（托盘命令），位置比例已更新");
    }

    /// <summary>再显示猫：按存档比例 + 当前物理尺寸重算还原（窗口尺寸可能已变，全程物理像素）。</summary>
    public void ShowAndRestorePosition()
    {
        Show();
        RestorePosition();
        FileLog.Write("[Window] 猫已显示（托盘命令），位置已按存档比例重算还原");
    }

    /// <summary>
    /// 应用尺寸档位（托盘 75%/100%/150%）：改窗口 DIP 尺寸后，用物理像素重算
    /// 位置 clamp（防大档位放大后下半截出屏），并把新比例立即写回存档。
    /// </summary>
    public void ApplyScale(double scale)
    {
        scale = SaveModel.IsValidWindowScale(scale) ? scale : 1.0;
        Width = BaseWindowWidth * scale;
        Height = BaseWindowHeight * scale;

        if (_hwndSource is not null)
        {
            nint hwnd = _hwndSource.Handle;
            if (_windowService.TryGetWorkAreaPixels(hwnd, out int ax, out int ay, out int aw, out int ah) &&
                _windowService.TryGetWindowRectPixels(hwnd, out int wx, out int wy, out int ww, out int wh))
            {
                // 缩放后 WPF 锚定左上角，右/下可能越出工作区 → 物理像素 clamp（修复防回退 3 的同坐标系纪律）
                int px = Math.Clamp(wx, ax, Math.Max(ax, ax + aw - ww));
                int py = Math.Clamp(wy, ay, Math.Max(ay, ay + ah - wh));
                if (px != wx || py != wy)
                {
                    _windowService.SetWindowScreenPos(hwnd, px, py);
                }

                FileLog.Write($"[Window] 尺寸档位 → {scale * 100:0}%，物理矩形 ({px},{py})，clamp 前 ({wx},{wy})");
            }
        }

        SavePosition(); // 立即把缩放后的新位置比例写入存档
    }

    /// <summary>托盘"退出"入口：保存窗口位置比例（存档落盘由 App.Exit 统一强制执行）。</summary>
    public void PersistNow() => SavePosition();

    // ── E3：装扮换绑（GDD-03 §4 渲染层映射；GDD-08 起经 StyleRenderer 带风格转译）──

    /// <summary>
    /// 按当前 VM 装备状态换绑全部渲染层。只改 Fill/Stroke/Visibility（纯渲染属性），
    /// 不触碰布局树；由装备状态属性变化驱动（CatViewModel.Equipped*）。
    /// </summary>
    private void ApplyOutfit()
    {
        if (_parts is null)
        {
            return;
        }

        StyleRenderer.ApplyOutfit(_parts, _style, _vm.EquippedHat, _vm.EquippedSkin, _vm.EquippedBongos);
        FileLog.Write($"[Outfit] 换装应用 hat='{_vm.EquippedHat}' skin='{_vm.EquippedSkin}' bongos='{_vm.EquippedBongos}'");
    }
}
