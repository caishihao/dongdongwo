using BongoCat.Core.Save;

namespace BongoCat.App.Tutorial;

/// <summary>
/// 新手教程服务（GDD-07 §5 三步口径 + epics E7-S1）。
/// 步骤定义（E7 交接拍板，气泡指向 + 完成条件）：
///   步 0「敲敲看」：指向猫 → 任意按键敲击 3 次完成；
///   步 1「拖拖看」：指向猫 → 拖动一次（MouseLeftButtonUp 且位移 &gt; 阈值）完成；
///   步 2「找背包」：指向托盘方向 → 打开背包窗口完成（气泡"打开背包"按钮同路径直达）。
/// </summary>
/// <remarks>
/// 纪律（GDD-07 §5/§7）：
/// - 无失败状态、无超时，跳过常驻（跳过 = 直接置完成并落盘）；
/// - 教程非模态：通知方法可在任意时刻被调用，服务内部按当前步守卫，绝不阻塞经济/掉落；
/// - 按步骤粒度存进度（崩溃/退出后从当前步续接），跳过标志优先于进度；
/// - 玩家在教程前就自己达成条件（如提前开背包）：先行跳过已满足的步骤（幂等）；
/// - 重播（托盘菜单）：完成态也能重看，重播只重置教程内部计数，绝不重置经济/装扮/赛季字段。
/// </remarks>
public interface ITutorialService
{
    /// <summary>当前步号（0~2；未激活时为 -1）。</summary>
    int CurrentStep { get; }

    /// <summary>教程会话是否进行中（首次启动或重播期间为 true）。</summary>
    bool IsActive { get; }

    /// <summary>教程是否已完成/跳过（持久化标志，完成后永不打扰）。</summary>
    bool IsCompleted { get; }

    /// <summary>进入新步骤（参数 = 步号；UI 订阅刷新气泡文案与指向）。</summary>
    event Action<int>? StepEntered;

    /// <summary>教程完成/跳过（UI 订阅关闭气泡）。</summary>
    event Action? Completed;

    /// <summary>
    /// 首次启动开始教程：从存档步进度续接（GDD-07 §7 崩溃续接）。
    /// 已完成时为 no-op（完成态优先于进度，绝不打扰）。
    /// </summary>
    void Start();

    /// <summary>托盘重播：从步 0 重新开始。不改动 TutorialCompleted 与任何经济字段。</summary>
    void StartReplay();

    /// <summary>通知一次有效敲击（步 0 计数；其他步 no-op）。</summary>
    void NotifyTap();

    /// <summary>通知拖动结束（参数 = 光标位移，屏幕像素；步 1 判阈值）。</summary>
    void NotifyDragEnded(double deltaX, double deltaY);

    /// <summary>
    /// 通知背包窗口已打开（步 2 完成条件；若玩家在更早步骤就打开背包，
    /// 按 GDD-07 §7 幂等口径直接快进完成——条件已自行满足）。
    /// </summary>
    void NotifyInventoryOpened();

    /// <summary>手动前进一步（气泡"下一步"按钮；最后一步 = 完成）。</summary>
    void Advance();

    /// <summary>跳过教程：直接置完成并落盘（跳过标志优先于进度）。</summary>
    void Skip();
}

/// <summary><see cref="ITutorialService"/> 默认实现：纯逻辑、可单测（存档注入，TimeProvider 不需要——无超时设计）。</summary>
public sealed class TutorialService : ITutorialService
{
    /// <summary>步 0 完成所需敲击次数（GDD-07 §5 宽松条件；E7 拍板 3 次）。</summary>
    public const int TapGoalCount = 3;

    /// <summary>步 1 完成所需最小拖动位移（屏幕像素；过滤原地点击误触）。</summary>
    public const double DragMinDistancePx = 20.0;

    /// <summary>总步数（GDD-07 §6：上限 4）。</summary>
    public const int StepCount = 3;

    private readonly ISaveService _save;
    private readonly Action<string>? _log;

    private int _step = -1;
    private int _tapCount;
    private bool _active;

    /// <inheritdoc />
    public int CurrentStep => _step;

    /// <inheritdoc />
    public bool IsActive => _active;

    /// <inheritdoc />
    public bool IsCompleted => _save.Current.TutorialCompleted;

    /// <inheritdoc />
    public event Action<int>? StepEntered;

    /// <inheritdoc />
    public event Action? Completed;

    public TutorialService(ISaveService save, Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _log = log;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_save.Current.TutorialCompleted)
        {
            return; // 完成态优先于进度：永不打扰（重看走 StartReplay）
        }

        // 崩溃/退出续接：从存档步进度恢复；越界步号收敛到 0（防御手改存档）
        int saved = _save.Current.TutorialStep;
        _step = saved is >= 0 and < StepCount ? saved : 0;
        _tapCount = 0;
        _active = true;
        _log?.Invoke($"[Tutorial] 开始教程（从步 {_step} 续接）");
        StepEntered?.Invoke(_step);
    }

    /// <inheritdoc />
    public void StartReplay()
    {
        _step = 0;
        _tapCount = 0;
        _active = true;
        _log?.Invoke("[Tutorial] 重播教程（完成态保持，不影响经济/装扮/赛季字段）");
        StepEntered?.Invoke(0);
    }

    /// <inheritdoc />
    public void NotifyTap()
    {
        if (!_active || _step != 0)
        {
            return;
        }

        _tapCount++;
        if (_tapCount >= TapGoalCount)
        {
            _log?.Invoke($"[Tutorial] 步 0 完成：敲击 {TapGoalCount} 次");
            AdvanceTo(1);
        }
    }

    /// <inheritdoc />
    public void NotifyDragEnded(double deltaX, double deltaY)
    {
        if (!_active || _step != 1)
        {
            return;
        }

        // 位移阈值判定（欧氏距离；光标全程屏幕像素坐标系，与窗口拖动同一套）
        double distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (distance < DragMinDistancePx)
        {
            return; // 原地点击/微抖不算拖动
        }

        _log?.Invoke($"[Tutorial] 步 1 完成：拖动位移 {distance:0.#}px ≥ {DragMinDistancePx:0.#}px");
        AdvanceTo(2);
    }

    /// <inheritdoc />
    public void NotifyInventoryOpened()
    {
        if (!_active)
        {
            return;
        }

        if (_step == StepCount - 1)
        {
            Complete("步 2 完成：背包窗口已打开");
            return;
        }

        // GDD-07 §7 幂等口径：玩家在教程前就自己开过背包 → 快进完成（条件已自行满足）
        _log?.Invoke($"[Tutorial] 步 {_step} 期间玩家自行打开背包，快进完成（跳过未满足步骤无意义）");
        Complete("教程快进完成：背包已自行打开");
    }

    /// <inheritdoc />
    public void Advance()
    {
        if (!_active)
        {
            return;
        }

        if (_step >= StepCount - 1)
        {
            Complete("最后一步经\"下一步\"确认完成");
            return;
        }

        AdvanceTo(_step + 1);
    }

    /// <inheritdoc />
    public void Skip()
    {
        if (!_active)
        {
            return;
        }

        Complete("教程被跳过");
    }

    /// <summary>进入下一步：重置步内计数、按步骤粒度落盘（GDD-07 §7 崩溃续接依据）。</summary>
    private void AdvanceTo(int step)
    {
        _step = step;
        _tapCount = 0;
        _save.Current.TutorialStep = step;
        _save.Save();
        StepEntered?.Invoke(step);
    }

    /// <summary>完成/跳过：置完成标志并落盘（跳过标志优先于进度）。</summary>
    private void Complete(string reason)
    {
        _active = false;
        _step = -1;
        _save.Current.TutorialCompleted = true;
        _save.Current.TutorialStep = StepCount;
        _save.Save();
        _log?.Invoke($"[Tutorial] 教程结束：{reason}");
        Completed?.Invoke();
    }
}
