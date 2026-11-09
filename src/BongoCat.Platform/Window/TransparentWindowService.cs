using System.Runtime.InteropServices;
using BongoCat.Platform.Native;

namespace BongoCat.Platform.Window;

/// <summary>透明模式（ADR-001：DWM 主方案 / 色键兜底）。</summary>
public enum TransparencyMode
{
    /// <summary>DWM ExtendFrameIntoClientArea 逐像素透明（主方案）。</summary>
    Dwm,

    /// <summary>色键分层窗口兜底（E1 留接口未实现，见 TryEnableDwmTransparency 注释）。</summary>
    ColorKey,
}

/// <summary>
/// 透明置顶窗口能力（ADR-001）。接口定义于 Platform（窗口 Win32 胶水属视图关切，
/// UI 经 DI 消费、不感知具体实现，满足"UI 不直接调 Platform"的解耦意图）。
/// </summary>
public interface ITransparentWindowService
{
    /// <summary>当前生效的透明模式（设置面板自检显示用，ADR-001 后果节）。</summary>
    TransparencyMode ActiveMode { get; }

    /// <summary>启用 DWM 逐像素透明。失败返回 false（E1 色键兜底未实现，调用方记日志降级）。</summary>
    bool TryEnableDwmTransparency(nint hwnd);

    /// <summary>套用桌宠窗口扩展样式：TOPMOST | TOOLWINDOW | NOACTIVATE（ADR-001 决定节）。</summary>
    void ApplyPetWindowStyles(nint hwnd);

    /// <summary>
    /// 叠加 WS_EX_TRANSPARENT 点击穿透（E8 远程猫窗口专用：纯展示元素不响应任何鼠标输入）。
    /// </summary>
    void EnableClickThrough(nint hwnd);

    /// <summary>按屏幕像素坐标移动窗口（拖动用：NOACTIVATE + 不动 Z 序）。</summary>
    void SetWindowScreenPos(nint hwnd, int x, int y);

    /// <summary>取鼠标屏幕像素坐标（拖动 delta 计算用）。</summary>
    bool TryGetCursorScreenPos(out int x, out int y);

    /// <summary>注册全局退出热键 Ctrl+Alt+Q（E1 无托盘菜单的临时退出方式）。</summary>
    bool TryRegisterQuitHotkey(nint hwnd, int hotkeyId);

    /// <summary>取窗口所在显示器的工作区矩形（物理像素，已排除任务栏）。位置记忆用。</summary>
    bool TryGetWorkAreaPixels(nint hwnd, out int x, out int y, out int width, out int height);

    /// <summary>取窗口当前矩形（物理像素）。位置记忆与启动自检用。</summary>
    bool TryGetWindowRectPixels(nint hwnd, out int x, out int y, out int width, out int height);
}

/// <summary>TransparentWindowService（module-map：DwmTransparentWindow）。</summary>
public sealed class TransparentWindowService : ITransparentWindowService
{
    public TransparencyMode ActiveMode { get; private set; } = TransparencyMode.Dwm;

    /// <inheritdoc />
    public bool TryEnableDwmTransparency(nint hwnd)
    {
        int hr = DwmNative.DwmIsCompositionEnabled(out bool enabled);
        if (hr != 0 || !enabled)
        {
            // DWM 关闭/失败：E1 按用户拍板"色键兜底留接口不实现"，此处仅记录降级
            ActiveMode = TransparencyMode.ColorKey;
            return false;
        }

        var margins = DwmNative.MARGINS.FullWindow;
        hr = DwmNative.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (hr != 0)
        {
            ActiveMode = TransparencyMode.ColorKey;
            return false;
        }

        ActiveMode = TransparencyMode.Dwm;
        return true;
    }

    /// <inheritdoc />
    public void ApplyPetWindowStyles(nint hwnd)
    {
        nint exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= new nint(NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    /// <inheritdoc />
    public void EnableClickThrough(nint hwnd)
    {
        nint exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | new nint(NativeMethods.WS_EX_TRANSPARENT));
    }

    /// <inheritdoc />
    public void SetWindowScreenPos(nint hwnd, int x, int y)
        => NativeMethods.SetWindowPos(hwnd, 0, x, y, 0, 0, NativeMethods.DragSwpFlags);

    /// <inheritdoc />
    public bool TryGetCursorScreenPos(out int x, out int y)
    {
        bool ok = NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        x = pt.X;
        y = pt.Y;
        return ok;
    }

    /// <inheritdoc />
    public bool TryRegisterQuitHotkey(nint hwnd, int hotkeyId)
        => NativeMethods.RegisterHotKey(hwnd, hotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, (uint)Core.Config.GameConfig.QuitHotkeyVk);

    /// <inheritdoc />
    public bool TryGetWorkAreaPixels(nint hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        nint monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        x = info.rcWork.Left;
        y = info.rcWork.Top;
        width = info.rcWork.Right - info.rcWork.Left;
        height = info.rcWork.Bottom - info.rcWork.Top;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetWindowRectPixels(nint hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return false;
        }

        x = rect.Left;
        y = rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return true;
    }
}
