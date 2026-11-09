using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Pass;

/// <summary>一次里程碑领取的结果快照（UI 品质色 chip + 事件文本用；纯数据不持有服务引用）。</summary>
/// <param name="OverflowCoins">物品奖励超出单件上限被自动折算的金币（0 = 未触发）。</param>
public sealed record PassClaimResult(
    int Level,
    PassTrack Track,
    PassRewardKind Kind,
    long Coins,
    CosmeticItem? Item,
    bool WasDuplicate,
    long OverflowCoins);

/// <summary>每日任务的视图快照（描述/进度/奖励 XP 由服务生成，UI 只渲染）。</summary>
public sealed record PassQuestView(string Id, string Description, int Progress, int Target, int Xp, bool Completed);

/// <summary>
/// 赛季通行证服务（epics E6-S1/S2/S3 + GDD-05）：赛季窗口判定与惰性切换、通行证 XP
/// （敲击 + 每日任务）、等级阈值推进、双轨里程碑领取（幂等）、高级轨金币解锁、每日任务自动入账。
/// 纯逻辑、零 UI/Win32 依赖：时间经 <see cref="TimeProvider"/> 注入，全部可单测。
/// </summary>
/// <remarks>
/// 线程纪律：全部方法假定在 UI 线程调用（Tick 由 DispatcherTimer 驱动、OnValidTap 经 CatDirector
/// TapStarted 转发且必须晚于 EconomyService.OnValidTap 订阅——敲击 XP 复用经济层有效敲击计数）。
/// 状态全部落在 <see cref="BongoCat.Core.Save.ISaveService"/> 内存模型（存档即单一数据源）。
/// 数值纪律（GDD-05 §6）：敲击 XP 10 XP/1000 键（每日上限 300，任务 XP 不占此额度）；
/// 高级轨解锁价随赛季配置（用户拍板 2500 金币、无内购）；赛季重置仅清赛季字段，
/// 金币/物品/图鉴/预设为永久资产永不清空（用户拍板）。
/// </remarks>
public interface IPassService
{
    /// <summary>当前激活赛季（无激活赛季 → null；通行证 UI 显示"暂无赛季"，进度保留不删，GDD-05 §7）。</summary>
    SeasonConfig? CurrentSeason { get; }

    /// <summary>本赛季累计 XP（存档单一数据源）。</summary>
    long SeasonXP { get; }

    /// <summary>当前等级（1~maxLevel，由曲线阈值表推导；满级封顶）。</summary>
    int Level { get; }

    /// <summary>当前等级内已获得的 XP（进度条分子；满级为 0）。</summary>
    long XpIntoLevel { get; }

    /// <summary>升到下一级所需 XP（进度条分母；满级为 0）。</summary>
    long XpForNextLevel { get; }

    /// <summary>高级轨是否已解锁（赛季内一次性）。</summary>
    bool PremiumUnlocked { get; }

    /// <summary>赛季剩余天数（含当日；end 已过为 0）。</summary>
    int DaysRemaining { get; }

    /// <summary>XP/赛季/领取/解锁任何变化（UI 订阅整体刷新）。</summary>
    event Action? PassChanged;

    /// <summary>里程碑可领取（参数 = 达到的等级；UI 经托盘气泡提示，复用掉落气泡模式）。</summary>
    event Action<int>? MilestoneReady;

    /// <summary>赛季切换完成（参数 = 新赛季名；一次性公告气泡，GDD-05 §7 惰性切换不打断会话）。</summary>
    event Action<string>? SeasonSwitched;

    /// <summary>
    /// 启动初始化：赛季窗口判定与惰性装载 → 每日重置判定 → 任务完成结算。
    /// </summary>
    void Initialize();

    /// <summary>心跳（~1Hz）：每日重置惰性判定 + 赛季切换惰性判定（GDD-05 §7：次日 0 点惰性触发）。</summary>
    void Tick();

    /// <summary>
    /// 一次有效敲击（必须在 EconomyService.OnValidTap 之后调用——敲击 XP 复用其计数）：
    /// 敲击 XP 入账（10 XP/1000 键，日上限 300）+ 敲击类任务进度结算。
    /// </summary>
    void OnValidTap();

    /// <summary>一次宝箱成功开启（EconomyService.ChestOpened 事件转发；任务进度计数 + 结算）。</summary>
    void NotifyChestOpened();

    /// <summary>一次合成成功（IExchangeService.ExchangeOccurred 事件转发；任务进度计数 + 结算）。</summary>
    void NotifySynthesized();

    /// <summary>今日激活的每日任务视图（GDD-05 §5：3 条/日池轮换，完成自动入账无需手动领）。</summary>
    IReadOnlyList<PassQuestView> GetDailyQuests();

    /// <summary>该里程碑该轨是否已领取（幂等查询）。</summary>
    bool IsClaimed(int level, PassTrack track);

    /// <summary>该里程碑是否已达阈值（SeasonXP ≥ 曲线阈值）。</summary>
    bool IsMilestoneReached(int level);

    /// <summary>
    /// 解锁高级轨（赛季内一次性；金币经 TrySpendCoins 审计）。无激活赛季 / 已解锁 /
    /// 金币不足 → false + failureReason，零状态变化。
    /// </summary>
    bool TryUnlockPremium(out string? failureReason);

    /// <summary>
    /// 领取里程碑奖励（手动领取，GDD-05 §5）。未达阈值 / 未解锁高级轨 / 已领取（幂等拒绝）/
    /// 无该节点奖励 → false + failureReason，零状态变化；成功即发奖（金币 GrantCoins 审计 /
    /// 物品 GrantItem 入库含上限折算）并记入 ClaimedMilestones 立即落盘。
    /// </summary>
    bool TryClaim(int level, PassTrack track, out PassClaimResult result, out string? failureReason);
}
