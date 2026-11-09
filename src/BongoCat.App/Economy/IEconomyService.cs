using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Economy;

/// <summary>金币来源枚举（epics E4-S1：来源审计——每笔入账必须可归因）。</summary>
public enum CoinSource
{
    /// <summary>有效敲击产出（每 N 次 1 币，日上限封顶）。</summary>
    Tap,

    /// <summary>在线滴灌（每 N 秒 1 币）。</summary>
    Drip,

    /// <summary>每日首次启动奖励。</summary>
    DailyLogin,

    /// <summary>挂机掉落金币包。</summary>
    DropCoinPack,

    /// <summary>重复物品自动折算（拆解价 × 折算率）。</summary>
    DuplicateConversion,

    /// <summary>金币购买宝箱（E5 支出路径；TrySpendCoins 审计来源）。</summary>
    Purchase,

    /// <summary>手动拆解物品收入（拆解价全额，E5）。</summary>
    ManualSalvage,

    /// <summary>库存超上限自动折算收入（拆解价全额，E5）。</summary>
    OverflowConversion,

    /// <summary>通行证里程碑金币奖励入账（E6；来源审计）。</summary>
    PassReward,

    /// <summary>高级轨一次性解锁支出（E6：2500 金币，TrySpendCoins 审计）。</summary>
    PremiumUnlock,
}

/// <summary>挂机掉落结果类型（GDD-02 §5：50% 无 / 30% 金币包 / 20% 物品；桶满时物品掉落暂停）。</summary>
public enum DropKind
{
    /// <summary>roll 中"无掉落"。</summary>
    Nothing,

    /// <summary>金币包（uniform[min,max]）。</summary>
    CoinPack,

    /// <summary>物品掉落（已入库；重复品已折算金币）。</summary>
    Item,

    /// <summary>物品掉落被掉落桶上限拦截（暂停，最老条目过期后自动恢复）。</summary>
    Suppressed,
}

/// <summary>一次挂机掉落事件（UI 订阅做气泡/背包提示；纯快照，不持有服务引用）。</summary>
public sealed record DropEvent(
    DropKind Kind,
    long Coins,
    CosmeticItem? Item,
    bool WasDuplicate,
    long DuplicateCoins)
{
    public static DropEvent Nothing { get; } = new(DropKind.Nothing, 0, null, false, 0);
}

/// <summary>一次宝箱开出的单件物品结果（品质色高亮 + 重复折算信息，UI 快照）。</summary>
public sealed record ChestOpenResult(CosmeticItem Item, bool WasDuplicate, long DuplicateCoins);

/// <summary>
/// 经济服务（epics E4-S1/S2 + GDD-02）：金币记账（只增不减、来源审计）、敲击产币（日上限 + 防时钟回拨）、
/// 在线滴灌、挂机掉落 roll（品质权重 + 掉落桶 24h 滚动上限）、免费宝箱充能与开箱。
/// 纯逻辑、零 UI/Win32 依赖：时间经 <see cref="TimeProvider"/> 注入、随机经 <see cref="Random"/> 注入，全部可单测。
/// </summary>
/// <remarks>
/// 线程纪律：全部方法假定在 UI 线程调用（Tick 由 DispatcherTimer 驱动、OnValidTap 由 CatDirector.TapStarted
/// 经 Dispatcher 转发），与 InventoryService 的存档写入互不并发。
/// 落盘策略（epics E4-S2 存档节流）：敲击/滴灌/充能只标脏，Tick 内 ≥30s 才落盘一次；
/// 掉落/开箱/每日重置为低频高价值事件，立即落盘。退出时 App.Exit 强制保存兜底。
/// </remarks>
public interface IEconomyService
{
    /// <summary>当前金币余额（单一数据源 = 存档模型；记账不可为负）。</summary>
    long Coins { get; }

    /// <summary>金币入账（参数 = 来源, 入账数额；UI 订阅刷新金币显示）。</summary>
    event Action<CoinSource, long>? CoinsChanged;

    /// <summary>金币支出（参数 = 来源, 支出数额恒为正；UI 订阅刷新金币显示 + 闪动反馈，E5）。</summary>
    event Action<CoinSource, long>? CoinsSpent;

    /// <summary>一次挂机掉落结算（含 Nothing/Suppressed；UI 订阅做托盘气泡与背包提示）。</summary>
    event Action<DropEvent>? DropOccurred;

    /// <summary>一次宝箱成功开启（免费或购买，E6：通行证每日任务 openChest 的事件源；零参数审计信号）。</summary>
    event Action? ChestOpened;

    /// <summary>
    /// 启动初始化：清理过期掉落桶 → 每日重置判定（跨天重置敲击计数 + 每日登录奖励；时钟回拨不重置）→
    /// 记录掉落间隔起点（离线不累计，重启即重新起算）。
    /// </summary>
    void Initialize();

    /// <summary>一次有效敲击（CatDirector 去抖后的事件转发；每 CoinsPer 次 +1 币，受日上限封顶）。</summary>
    void OnValidTap();

    /// <summary>
    /// 心跳（UI DispatcherTimer ~1Hz 调用）：滴灌产币、宝箱充能、掉落间隔 roll、脏存档节流落盘。
    /// 时间全部由 TimeProvider 内部测算；待机恢复超 10 分钟的间隔不补算（GDD-02 §3）。
    /// </summary>
    void Tick();

    /// <summary>宝箱充能是否已满（可开）。</summary>
    bool CanOpenChest { get; }

    /// <summary>距下个免费宝箱可开的剩余时间（已可开则为零）。</summary>
    TimeSpan TimeToNextChest { get; }

    /// <summary>距下次掉落 roll 的剩余时间。</summary>
    TimeSpan TimeToNextDrop { get; }

    /// <summary>掉落桶当前件数（24h 滚动窗口内；背包 UI 显示掉落暂停状态用）。</summary>
    int DropBucketCount { get; }

    /// <summary>
    /// 开免费宝箱：按充能对应宝箱的品质权重 roll 出 Items 件物品入库
    /// （重复品自动折算金币）。充能未满 → false 且零状态变化；成功即扣充能并立即落盘。
    /// E5：计入宝箱保底（连续 15 箱无 epic+ → 本箱必出 epic+，出 epic+ 清零计数）。
    /// </summary>
    bool TryOpenChest(out IReadOnlyList<ChestOpenResult> results);

    /// <summary>
    /// 金币入账（E5 公开收入出口：手动拆解/超上限折算/合成重复折算经此入账，
    /// 来源审计不变；amount ≤ 0 忽略——记账不可为负）。
    /// </summary>
    void GrantCoins(long amount, CoinSource source);

    /// <summary>
    /// 金币支出（E5：买箱等唯一支出路径）。余额不足 → false 且零状态变化（记账不可为负）；
    /// 成功扣款立即落盘并广播 <see cref="CoinsSpent"/>。
    /// </summary>
    bool TrySpendCoins(long amount, CoinSource source);

    /// <summary>
    /// 金币购买宝箱并立即按该箱权重开箱（E5：复用免费开箱同一 roll/保底/入库/重复折算链路）。
    /// 金币不足 / 未知箱 id → false 且零状态变化；成功即扣币 + 入库 + 立即落盘。
    /// </summary>
    bool TryBuyChest(string chestId, out IReadOnlyList<ChestOpenResult> results);
}
