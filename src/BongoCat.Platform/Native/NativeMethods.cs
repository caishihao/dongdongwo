using System.Runtime.InteropServices;

namespace BongoCat.Platform.Native;

/// <summary>
/// user32.dll / dwmapi.dll P/Invoke 集中声明（architecture.md §4：Platform P/Invoke 全部集中 Native/ 子目录）。
/// 教学说明：用经典 DllImport 而非 LibraryImport（后者源生成器要求开启 unsafe）；
/// 涉及输入/安全检查的 API 声明 SetLastError = true，失败时可用 Marshal.GetLastWin32Error() 取错误码。
/// </summary>
internal static class NativeMethods
{
    /// <summary>键按下状态位（GetAsyncKeyState 返回值高位）。</summary>
    public const short KeyPressedBit = unchecked((short)0x8000);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>SWP flags：不动 Z 序、不改尺寸、不激活、不同步 frame。</summary>
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint DragSwpFlags = SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

    // ── 窗口样式（ADR-001：TOPMOST | TOOLWINDOW | NOACTIVATE）────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int index, nint value);

    /// <summary>GetWindowLongPtr 的 32/64 位自适应封装。</summary>
    public static nint GetWindowLongPtr(nint hWnd, int index)
        => nint.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new nint(GetWindowLong32(hWnd, index));

    /// <summary>SetWindowLongPtr 的 32/64 位自适应封装。</summary>
    public static nint SetWindowLongPtr(nint hWnd, int index, nint value)
        => nint.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : new nint(SetWindowLong32(hWnd, index, (int)value));

    // ── 全局退出热键（E1 无托盘菜单的临时退出方式）──────────────────

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_ALT = 0x0001;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    // ── 显示器与窗口矩形（位置记忆全程物理像素，修复 WPF DIP 的 DPI 混用）──

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT rect);
}

/// <summary>dwmapi.dll P/Invoke（ADR-001：DWM 逐像素透明）。</summary>
internal static class DwmNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public static MARGINS FullWindow => new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
    }

    [DllImport("dwmapi.dll")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS margins);
}
