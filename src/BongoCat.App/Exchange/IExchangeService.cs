using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Exchange;

/// <summary>一次成功合成的事务快照（输入 3 件 id + 输出 1 件；UI 快照，不持有服务引用）。</summary>
/// <param name="DuplicateCoins">输出为重复品时自动折算的金币（0 = 新物品）。</param>
public sealed record ExchangeResult(
    IReadOnlyList<string> InputIds,
    CosmeticItem OutputItem,
    bool WasDuplicate,
    long DuplicateCoins);

/// <summary>
/// 兑换合成服务（epics E5-S1/S2 + GDD-04）：同品质 3 合 1 升阶 + 手动拆解。
/// 与 <see cref="BongoCat.App.Economy.IEconomyService"/> 分拆的理由：
/// 金币服务管"钱的流入流出与宝箱"，兑换服务管"物品的转化"——两者只在折算金币处
/// 通过 GrantCoins 单向对接，避免 IEconomyService 膨胀成物+币双域上帝接口，
/// 且物品转化规则可独立全矩阵单测。
/// </summary>
/// <remarks>
/// 规则（GDD-04 §5，V1 采纳"建议"项）：
/// - 同品质 + 同槽位（V1 限制同槽位合成，结果槽位 = 输入槽位，跨槽位留 V2 开关）；
/// - 品质 &lt; legendary：3 件可含同名（一键重复品合成，GDD-04 §2）；
/// - legendary：必须 3 件<b>不同</b> legendary（→ unique，唯一 unique 获取途径，GDD-04 §5/§7）；
/// - 非装备中（装备中的物品不可消耗）；
/// - unique 为顶阶不可再合成；
/// - 结果：目标品质同槽位全池等权随机，<b>排除本次输入 id</b>（采纳 GDD-04 §8 开放问题 1 建议，
///   防"3 顶帽子合出同一顶"挫败；已拥有的仍在池内 → 重复折金币为隐性成本，GDD-04 §5）；
/// - 金币零成本（GDD-04 §2：消耗物品即成本；§8 开放问题 3：unique 合成不加费）。
/// 原子性（GDD-04 §7）：先扣 3 件后加 1 件、立即落盘；崩溃窗口内可能丢失 3 件输入——
/// V1 不做流水回滚（UI 单线程 + 落盘瞬时，窗口毫秒级），记为已知限制。
/// </remarks>
public interface IExchangeService
{
    /// <summary>合成审计事件（输入 3 件 id + 输出 1 件；UI 订阅做结果提示，测试断言守恒）。</summary>
    event Action<IReadOnlyList<string>, CosmeticItem>? ExchangeOccurred;

    /// <summary>
    /// 合成可行性判定（纯检查零副作用；UI 据此灰置按钮 + tooltip 展示 failureReason）。
    /// anchorId = 玩家选中物品，自动选取与其同品质同槽位的 3 件输入（优先重复品，GDD-04 §2）。
    /// </summary>
    bool CanSynthesize(string anchorItemId, out string? failureReason);

    /// <summary>
    /// 一键合成：自动选取输入（同品质同槽位、非装备中、优先 anchor 重复品）→ 校验 → 事务执行。
    /// 失败 → false + failureReason（中文可直显），零状态变化。
    /// </summary>
    bool TrySynthesizeAuto(string anchorItemId, out ExchangeResult result, out string? failureReason);

    /// <summary>
    /// 显式合成（E5-S2 多选路径的核心事务）：按给定 3 件 id 校验并执行。
    /// 校验全过才入库——失败路径不给任何入库（epics E5-S2 验收标准）。
    /// </summary>
    bool TryExchange(IReadOnlyList<string> inputIds, out ExchangeResult result, out string? failureReason);

    /// <summary>拆解价查询（拆解按钮文案"+N 币"用；未知 id / unique → false）。</summary>
    bool TryGetSalvageValue(string itemId, out long coins);

    /// <summary>拆解可行性判定（纯检查零副作用；UI 灰置 + tooltip 用）。</summary>
    bool CanSalvage(string itemId, out string? failureReason);

    /// <summary>
    /// 手动拆解：物品 → 拆解价全额金币（GDD-02 §5/§6.1；unique 不可拆）。
    /// 装备中不可拆（先卸下）；图鉴点亮不回灰（DiscoveredItems 永久记录）。
    /// 失败 → false + failureReason，零状态变化。
    /// </summary>
    bool TrySalvage(string itemId, out long coins, out string? failureReason);
}
