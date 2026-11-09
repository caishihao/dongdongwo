namespace BongoCat.Core.Config;

/// <summary>
/// 全局可调参数（architecture.md §4：配置常量集中于此，数据驱动）。
/// 数值依据：GDD-01 §6 数值框架 + ADR-002 轮询参数（用户已拍板 8ms/50ms）。
/// </summary>
public sealed class GameConfig
{
    // ── 键盘轮询（ADR-002）──────────────────────────────────────────
    /// <summary>活动档轮询间隔（毫秒）。最短按键 ~40ms，8ms 轮询不会漏检。</summary>
    public int ActivePollIntervalMs { get; init; } = 8;

    /// <summary>空闲档轮询间隔（毫秒），省电用。</summary>
    public int IdlePollIntervalMs { get; init; } = 50;

    /// <summary>连续无按键多少秒后降为空闲档。</summary>
    public int IdleAfterSeconds { get; init; } = 10;

    // ── 敲击手感（GDD-01 §6）────────────────────────────────────────
    /// <summary>去抖窗口（毫秒）：窗口内的重复事件整体忽略（按住只算一次，GDD-01 §7）。</summary>
    public int DebounceMs { get; init; } = 30;

    /// <summary>爪子回弹时长（毫秒）：Idle → Down(立即) → Up(回弹)。</summary>
    public int ReboundMs { get; init; } = 120;

    /// <summary>敲击时爪子下压位移（逻辑像素）。</summary>
    public double TapDownY { get; init; } = 18.0;

    // ── 呼吸浮动（GDD-01 §6：周期 ~2.4s）────────────────────────────
    /// <summary>呼吸浮动周期（秒）。</summary>
    public double BobbingPeriodSec { get; init; } = 2.4;

    /// <summary>呼吸浮动幅度（逻辑像素，约 2% 身高）。</summary>
    public double BobbingAmpPx { get; init; } = 3.5;

    // ── 窗口（ADR-001/ADR-003：~200×180 逻辑像素量级）──────────────
    /// <summary>主窗口宽度（逻辑像素）。</summary>
    public double WindowWidth { get; init; } = 260;

    /// <summary>主窗口高度（逻辑像素）。</summary>
    public double WindowHeight { get; init; } = 220;

    /// <summary>退出热键虚拟键码：Ctrl+Alt+Q（E1 无托盘菜单，暂以此退出）。</summary>
    public const int QuitHotkeyVk = 0x51; // 'Q'
}
