using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Inventory;

/// <summary>
/// 图鉴条目（GDD-03 §5/§7：已获得显示全信息，未获得剪影 + 名称打码"？？？"）。
/// E5 拆解规则：点亮 = <b>曾经拥有</b>（DiscoveredItems 永久记录），拆解后不回灰。
/// 纯计算结果，UI 据此渲染彩色图标或灰剪影。
/// </summary>
public sealed record CollectopediaEntry(CosmeticItem Item, bool Owned, bool Discovered, int Count)
{
    /// <summary>图鉴显示名：未点亮打码（保留收集惊喜，GDD-03 §7）；曾获得（已拆解）仍显示全名。</summary>
    public string DisplayName => Discovered ? Item.DisplayName : "？？？";

    /// <summary>图鉴状态角标文案。</summary>
    public string OwnedText => Owned ? "已获得" : Discovered ? "曾获得" : "未获得";
}

/// <summary>单件物品入库结果（掉落/宝箱授予路径的快照；重复判定由 EconomyService 折算金币）。</summary>
/// <param name="OverflowCoins">超出库存上限被自动折算的金币（0 = 未触发上限，E5）。</param>
public sealed record ItemGrantResult(string ItemId, bool WasDuplicate, int NewCount, long OverflowCoins = 0);

/// <summary>
/// 库存/装备服务（epics E3-S2；App 层纯逻辑，零 UI/Win32 依赖）。
/// 装备规则（GDD-03 §5）：每槽互斥只能一件；卸下恢复默认（空 artKey = 原创基础皮肤）；
/// unique 物品无额外互斥（设计文档 §5 未定义 unique 专属规则，仅按槽位互斥——如需收紧在 E4 补 ADR）。
/// </summary>
public interface IInventoryService
{
    /// <summary>物品总目录（items.json 全量，含未拥有——图鉴用）。</summary>
    CosmeticCatalog Catalog { get; }

    /// <summary>当前库存（itemId → 数量；E3 全部为 1，字典为 E4 宝箱重复掉落预留）。</summary>
    IReadOnlyDictionary<string, int> Owned { get; }

    /// <summary>当前装备的帽子 artKey/id（空串 = 默认无帽）。</summary>
    string EquippedHat { get; }

    /// <summary>当前装备的皮肤 id（空串 = 原创基础皮肤）。</summary>
    string EquippedSkin { get; }

    /// <summary>当前装备的鼓 id（空串 = 原创基础鼓色）。</summary>
    string EquippedBongos { get; }

    /// <summary>库存或装备变化（UI 订阅刷新列表 / 重绑猫渲染层）。</summary>
    event Action? Changed;

    /// <summary>
    /// 启动初始化：① 校验存档装备引用，失效引用静默卸下不崩溃（GDD-03 §7）；
    /// ② 首次启动授予新手装（每槽 1 件 common + 另送 2 件）并默认装备三件 common，写入存档。
    /// 以存档 InventoryInitialized 标记保证幂等——重复调用绝不重复发放。
    /// </summary>
    void Initialize();

    /// <summary>
    /// 装备物品（槽位互斥：同槽旧装备自动被替换）。
    /// 未拥有 / 未知 id / 未知槽位 → false 且不产生任何状态变化。
    /// 成功：写存档（立即落盘，与托盘命令纪律一致）并广播 <see cref="Changed"/>。
    /// </summary>
    bool Equip(string itemId);

    /// <summary>卸下指定槽位（恢复默认）。成功写存档并广播变化。</summary>
    bool Unequip(ItemSlot slot);

    /// <summary>是否拥有该物品。</summary>
    bool IsOwned(string itemId);

    /// <summary>
    /// 授予一件物品（E4 掉落/宝箱入库路径）：数量 +1；重复获得不覆盖（数量累加，
    /// 是否重复由 <see cref="ItemGrantResult.WasDuplicate"/> 返回，折算金币由经济层负责）。
    /// E5：同时写入图鉴永久点亮（DiscoveredItems）；超出单件上限部分自动折算金币
    /// （<see cref="ItemGrantResult.OverflowCoins"/> 报告数额，记账经注入的 sink 走经济层）。
    /// 未知 id 视为程序 bug（fail-fast）；成功即落盘并广播 <see cref="Changed"/>。
    /// </summary>
    ItemGrantResult GrantItem(string itemId);

    /// <summary>
    /// 移除一件物品（E5 合成消耗/手动拆解路径）：数量 -1，归零移除字典键。
    /// 未拥有/未知 id → false 且零状态变化；成功即落盘并广播 <see cref="Changed"/>。
    /// 图鉴点亮（DiscoveredItems）<b>不</b>随之移除——点亮是永久资产。
    /// </summary>
    bool RemoveOne(string itemId);

    /// <summary>
    /// 整套装扮（E5 预设应用路径）：三槽一次写入——空串 = 卸下恢复默认；
    /// 非空 id 校验"目录存在 + 拥有"，非法 id 跳过该槽（保留原装备，防御拆解后残留引用）。
    /// 全部槽位一次落盘 + 一次 <see cref="Changed"/> 广播（避免三连闪）。
    /// </summary>
    void ApplyOutfit(string hatId, string skinId, string bongosId);

    /// <summary>该物品是否曾被拥有过（图鉴点亮状态，E5 拆解不回灰依据）。</summary>
    bool IsDiscovered(string itemId);

    /// <summary>
    /// 物种限定装备校验（GDD-09 §1 speciesOnly）：按存档当前物种重新校验三槽装备，
    /// 物种不符的装备（如猫身上还穿着 dino_default）静默卸下恢复默认并广播 <see cref="Changed"/>。
    /// 换物种后由 App 层调用一次；启动初始化（Initialize）内同样执行。
    /// </summary>
    void ValidateSpeciesCompatibility();

    /// <summary>图鉴状态：全目录逐条标注已拥有/未拥有（UI 只管渲染，不持有规则）。</summary>
    IReadOnlyList<CollectopediaEntry> GetCollectopedia();
}
