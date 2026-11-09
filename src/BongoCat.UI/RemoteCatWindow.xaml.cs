using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BongoCat.Core.Config;
using BongoCat.Core.Input;
using BongoCat.Core.Net;
using BongoCat.Platform.Native;
using BongoCat.Platform.Window;
using BongoCat.Protocol;
using BongoCat.UI.Cosmetics;

namespace BongoCat.UI;

/// <summary>
/// 远程猫窗口 code-behind：Win32 胶水（透明/置顶/点击穿透）、定位、敲击动画、风格模板装载、
/// 装扮换绑、渐隐消失。每只在线好友的猫一个实例（窗口数 = 在线好友数 ≤ 7）。
/// GDD-08：风格渲染与主猫共用 StyleRenderer（单一数据源）；style 字节变化 → 整体重载
/// 模板 + 复放已收到的装扮（CosmeticSync 早于/晚于风格同步都收敛到正确外观）。
/// </summary>
public partial class RemoteCatWindow : Window
{
    private readonly ITransparentWindowService _windowService;
    private readonly StyleCatalog _styles;
    private bool _fadingOut;

    private StyleCatParts? _parts;
    private StylePack _style = null!;
    private string _styleId = StyleCatalog.DefaultStyleId;

    /// <summary>当前物种 id（GDD-09：远端按对方物种渲染；旧客户端/未同步回退 cat）。</summary>
    private string _speciesId = Core.Config.SpeciesCatalog.DefaultSpeciesId;

    // 最近一次收到的装扮（风格重载模板后复放，E8 单一数据源换绑语义不变）
    private string _hatId = string.Empty;
    private string _skinId = string.Empty;
    private string _bongosId = string.Empty;

    public RemoteCatWindow(
        ITransparentWindowService windowService,
        StyleCatalog styles,
        ushort memberId,
        string displayName)
    {
        InitializeComponent();
        _windowService = windowService;
        _styles = styles;
        MemberId = memberId;
        NameText.Text = string.IsNullOrEmpty(displayName) ? "猫猫" : displayName;

        // 默认按黏土渲染（style 字节未到时的旧客户端回退语义，GDD-08 §6 边界 3）
        ApplyStyle(StyleCatalog.DefaultStyleId);
    }

    /// <summary>该窗口对应的房间成员 id（服务器分配）。</summary>
    public ushort MemberId { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwndSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!;
        nint hwnd = hwndSource.Handle;

        // ADR-001 同款扩展样式：TOPMOST | TOOLWINDOW | NOACTIVATE
        _windowService.ApplyPetWindowStyles(hwnd);

        // E8：叠加 WS_EX_TRANSPARENT——远程猫完全点击穿透（鼠标事件落到下层窗口/桌面）。
        _windowService.EnableClickThrough(hwnd);

        // 与主窗口同款透明管线（E1 修复防回退 1：CompositionTarget.BackgroundColor 必须置透明黑）
        if (!_windowService.TryEnableDwmTransparency(hwnd))
        {
            FileLog.Write("[RemoteCat] DWM 透明启用失败，处于未透明降级模式");
        }

        hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    /// <summary>
    /// 按归一化摆放坐标定窗（物理像素；语义与主猫位置记忆一致：
    /// rel × (工作区 − 窗口尺寸) + 工作区原点，跨分辨率各自换算）。
    /// </summary>
    public void ApplyNormalizedPosition(double normX, double normY)
    {
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this);
        if (source is null)
        {
            return; // 尚未显示（窗口句柄未建）：创建方随后会先定位再 Show
        }

        nint hwnd = source.Handle;
        if (!_windowService.TryGetWorkAreaPixels(hwnd, out int ax, out int ay, out int aw, out int ah) ||
            !_windowService.TryGetWindowRectPixels(hwnd, out _, out _, out int ww, out int wh))
        {
            return;
        }

        double maxX = Math.Max(0, aw - ww);
        double maxY = Math.Max(0, ah - wh);
        int px = ax + (int)Math.Clamp(normX * maxX, 0, maxX);
        int py = ay + (int)Math.Clamp(normY * maxY, 0, maxY);
        _windowService.SetWindowScreenPos(hwnd, px, py);
    }

    /// <summary>
    /// 远端风格装载（GDD-08）：byteId → 模板整体重载 + 复放最近装扮。
    /// 未知字节由协调器已收敛 clay；这里对 id 再做一次目录防御（双保险，绝不崩溃）。
    /// </summary>
    public void ApplyStyle(string styleId)
    {
        if (_fadingOut)
        {
            return;
        }

        if (!_styles.TryGet(styleId, out _))
        {
            styleId = StyleCatalog.DefaultStyleId;
        }

        _styleId = styleId;
        _parts = StyleRenderer.ApplyStyle(StyleHost, styleId, _styles, _speciesId, FileLog.Write);
        _styles.TryGet(styleId, out _style!);
        ApplyCosmetics(_hatId, _skinId, _bongosId);
    }

    /// <summary>
    /// 远端物种装载（GDD-09 §4）：按对方物种重载当前风格的形体模板并复放装扮。
    /// 未知 id 由协调器收敛 cat；这里再防御一次（双保险）。
    /// </summary>
    public void ApplySpecies(string speciesId)
    {
        if (_fadingOut)
        {
            return;
        }

        if (speciesId is not ("cat" or "dino"))
        {
            speciesId = Core.Config.SpeciesCatalog.DefaultSpeciesId;
        }

        _speciesId = speciesId;
        ApplyStyle(_styleId);
        FileLog.Write($"[RemoteCat] 远程猫 #{MemberId} 物种 → {speciesId}（风格保持 {_styleId}）");
    }

    /// <summary>远端装扮换绑（artKey 已由协调器过目录清洗，窗口内再叠加 CosmeticArt 自身防御——双保险）。</summary>
    public void ApplyCosmetics(string hatId, string skinId, string bongosId)
    {
        if (_fadingOut || _parts is null)
        {
            return;
        }

        // 缓存最近装扮：后续风格重载模板后复放（风格与装扮两层状态各自独立到达）
        _hatId = hatId;
        _skinId = skinId;
        _bongosId = bongosId;

        StyleRenderer.ApplyOutfit(_parts, _style, hatId, skinId, bongosId);
    }

    /// <summary>远端敲击动画：风格参数化下压回弹（只动 RenderTransform——ADR-003 纪律）。</summary>
    public void PlayTap(TapSide side)
    {
        if (_fadingOut || _parts is null)
        {
            return;
        }

        // 远程猫与主猫同手感（全局回弹系数 1：远端无用户设置上下文，用风格基线时长）
        CatTapAnimator.PlayTap(_parts, _style, 1.0, side == TapSide.Left ? PawSide.Left : PawSide.Right);
    }

    /// <summary>断线/离开：渐隐消失（300ms），动画结束自动关闭。</summary>
    public void FadeOutAndClose()
    {
        if (_fadingOut)
        {
            return;
        }

        _fadingOut = true;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        IsVisibleChanged += (_, args) =>
        {
            if (!(bool)args.NewValue)
            {
                // 兜底：渐隐动画被窗口隐藏打断时也要关闭（防窗口残影泄漏）
                Close();
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        FileLog.Write($"[RemoteCat] 远程猫窗口已关闭（member #{MemberId}，最后风格 {_styleId}）");
    }
}
