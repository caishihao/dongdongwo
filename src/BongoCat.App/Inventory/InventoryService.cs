using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

namespace BongoCat.App.Inventory;

/// <summary>
/// <see cref="IInventoryService"/> 默认实现：规则在 App 层（纯逻辑可测），
/// 状态全部落在 <see cref="ISaveService"/> 内存模型（存档即单一数据源）。
/// </summary>
/// <remarks>
/// items.json 加载器为何放 App（而非 Platform）：
/// ① 物品定义是 App 层库存逻辑的"消费数据"，与 <see cref="CosmeticCatalog"/> 解析（Core）同链路；
/// ② 嵌入资源走程序集清单流（GetManifestResourceStream），不是文件系统 IO——
///    随程序集分发、无路径依赖、测试项目引用 App 即可读到同一份真实数据；
/// ③ Platform 保留给真正的文件 IO（存档），边界不模糊。
/// JSON 解析本身在 Core（CosmeticCatalog.ParseJson，纯逻辑）。
/// E5：单件上限与折算价取自 EconomyConfig（inventory.maxPerItem / salvage 表），
/// 折算金额经 overflowCoinsSink 回调交给经济层记账（本类只算不记，金币单一出口）；
/// economy 参数为 null → 不设上限（老测试/极端环境兜底）。
/// </remarks>
public sealed class InventoryService : IInventoryService
{
    /// <summary>新手装：每槽 1 件 common + 另送 2 件 uncommon（图鉴有探索感，epics E3 首启动授予）。</summary>
    public static readonly string[] StarterKitIds =
    [
        "hat_dev_cap",   // 帽子 common
        "skin_cream",    // 皮肤 common
        "bongos_wood",   // 鼓 common
        "skin_orange",   // 另送：皮肤 uncommon
        "hat_beret",     // 另送：帽子 uncommon
    ];

    private readonly ISaveService _save;
    private readonly EconomyConfig? _economy;
    private readonly Action<string, long>? _overflowCoinsSink;
    private readonly Action<string>? _log;

    public CosmeticCatalog Catalog { get; }

    public event Action? Changed;

    public InventoryService(
        ISaveService save,
        CosmeticCatalog catalog,
        EconomyConfig? economy = null,
        Action<string, long>? overflowCoinsSink = null,
        Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _economy = economy;
        _overflowCoinsSink = overflowCoinsSink;
        _log = log;
    }

    private SaveModel Save => _save.Current;

    public IReadOnlyDictionary<string, int> Owned => Save.OwnedItems;

    public string EquippedHat => Save.EquippedHat;
    public string EquippedSkin => Save.EquippedSkin;
    public string EquippedBongos => Save.EquippedBongos;

    /// <inheritdoc />
    public void Initialize()
    {
        // ① 校验装备引用（GDD-03 §7：物品 id 失效 → 卸下占位，不崩溃）
        ValidateEquipmentRef(nameof(SaveModel.EquippedHat), Save.EquippedHat, v => Save.EquippedHat = v);
        ValidateEquipmentRef(nameof(SaveModel.EquippedSkin), Save.EquippedSkin, v => Save.EquippedSkin = v);
        ValidateEquipmentRef(nameof(SaveModel.EquippedBongos), Save.EquippedBongos, v => Save.EquippedBongos = v);

        // ①b 物种限定校验（GDD-09 §1 speciesOnly：与当前物种不符的装备 → 卸下）
        ValidateSpeciesCompatibility();

        // ② 新手装授予（InventoryInitialized 标记保证幂等）
        if (!Save.InventoryInitialized)
        {
            foreach (string id in StarterKitIds)
            {
                if (!Catalog.TryGet(id, out _))
                {
                    // 目录与新手装清单不一致属于程序 bug：fail-fast 暴露，不静默少发
                    throw new InvalidOperationException($"新手装 id 不在 items.json 中：{id}");
                }

                Save.OwnedItems[id] = Save.OwnedItems.GetValueOrDefault(id) + 1;
                MarkDiscovered(id); // 新手装同样点亮图鉴
            }

            // 默认装上三件 common 新手装（玩家开箱即见装扮效果；卸下即恢复原创基础皮肤）
            Save.EquippedHat = "hat_dev_cap";
            Save.EquippedSkin = "skin_cream";
            Save.EquippedBongos = "bongos_wood";
            Save.InventoryInitialized = true;
            _save.Save();
            _log?.Invoke($"[Inventory] 首次启动：已授予 {StarterKitIds.Length} 件新手装并默认装备 common 三件套");
        }
        else if (_dirty)
        {
            _save.Save(); // 仅发生过失效引用卸下时才落盘
            _dirty = false;
        }
    }

    private bool _dirty; // Initialize 内校验是否卸下过失效引用（避免无谓落盘）

    private void ValidateEquipmentRef(string slotName, string itemId, Action<string> clearOrKeep)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }

        if (Catalog.TryGet(itemId, out CosmeticItem? item) && IsOwned(itemId))
        {
            return;
        }

        _log?.Invoke($"[Inventory] 存档装备引用失效（{slotName}={itemId}），已卸下恢复默认");
        clearOrKeep(string.Empty);
        _dirty = true;
    }

    /// <inheritdoc />
    public bool Equip(string itemId)
    {
        if (!Catalog.TryGet(itemId, out CosmeticItem? item) || !IsOwned(itemId))
        {
            _log?.Invoke($"[Inventory] 装备拒绝：{itemId} 未知或未拥有");
            return false;
        }

        // GDD-09 §1 speciesOnly：物种限定物品跨物种防御（猫穿 dino_default → 拒绝）
        if (!IsAllowedForSpecies(item))
        {
            _log?.Invoke($"[Inventory] 装备拒绝：{itemId} 仅限物种 '{item.SpeciesOnly}'（当前 '{Save.SpeciesId}'）");
            return false;
        }

        switch (item.Slot)
        {
            case ItemSlot.Hat:
                Save.EquippedHat = itemId;
                break;
            case ItemSlot.Skin:
                Save.EquippedSkin = itemId;
                break;
            case ItemSlot.Bongos:
                Save.EquippedBongos = itemId;
                break;
            default:
                // E3 未开放槽位（emote/uitheme/consumable）：目录未来扩充时先拒绝
                _log?.Invoke($"[Inventory] 装备拒绝：槽位 {item.Slot} 未开放");
                return false;
        }

        _save.Save();
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public bool Unequip(ItemSlot slot)
    {
        switch (slot)
        {
            case ItemSlot.Hat:
                Save.EquippedHat = string.Empty;
                break;
            case ItemSlot.Skin:
                Save.EquippedSkin = string.Empty;
                break;
            case ItemSlot.Bongos:
                Save.EquippedBongos = string.Empty;
                break;
            default:
                return false;
        }

        _save.Save();
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public bool IsOwned(string itemId) => Save.OwnedItems.ContainsKey(itemId) && Save.OwnedItems[itemId] > 0;

    /// <inheritdoc />
    public ItemGrantResult GrantItem(string itemId)
    {
        if (!Catalog.TryGet(itemId, out CosmeticItem? item))
        {
            // 掉落/宝箱只从目录 roll 物品，未知 id 属于程序 bug：fail-fast 暴露
            throw new InvalidOperationException($"GrantItem 拒绝未知物品 id：{itemId}");
        }

        bool wasDuplicate = IsOwned(itemId);
        int newCount = Save.OwnedItems.GetValueOrDefault(itemId) + 1;

        // E5 库存上限折算：超出单件上限的部分自动折算金币（按拆解价，全额）。
        // 折算金额经 sink 回调交经济层记账（CoinSource.OverflowConversion），本类不持金币状态。
        long overflowCoins = 0;
        int maxPerItem = _economy?.InventoryLimit.MaxPerItem ?? int.MaxValue;
        if (newCount > maxPerItem)
        {
            int overflow = newCount - maxPerItem;
            newCount = maxPerItem;
            overflowCoins = overflow * GetSalvagePrice(item.Quality);
            _log?.Invoke($"[Inventory] {itemId} 超出上限 {maxPerItem}，{overflow} 件自动折算 {overflowCoins} 金币");
        }

        Save.OwnedItems[itemId] = newCount;
        MarkDiscovered(itemId); // 图鉴永久点亮（E5：拆解后不回灰）
        _save.Save();
        if (overflowCoins > 0)
        {
            _overflowCoinsSink?.Invoke(itemId, overflowCoins);
        }

        Changed?.Invoke();
        return new ItemGrantResult(itemId, wasDuplicate, newCount, overflowCoins);
    }

    /// <inheritdoc />
    public bool RemoveOne(string itemId)
    {
        if (!Catalog.TryGet(itemId, out _) || !IsOwned(itemId))
        {
            _log?.Invoke($"[Inventory] 移除拒绝：{itemId} 未知或未拥有");
            return false;
        }

        int remaining = Save.OwnedItems[itemId] - 1;
        if (remaining <= 0)
        {
            Save.OwnedItems.Remove(itemId); // 归零移除键；图鉴点亮不受影响（E5 不回灰）
        }
        else
        {
            Save.OwnedItems[itemId] = remaining;
        }

        _save.Save();
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public void ApplyOutfit(string hatId, string skinId, string bongosId)
    {
        Save.EquippedHat = ResolveOutfitSlot(hatId, ItemSlot.Hat, Save.EquippedHat);
        Save.EquippedSkin = ResolveOutfitSlot(skinId, ItemSlot.Skin, Save.EquippedSkin);
        Save.EquippedBongos = ResolveOutfitSlot(bongosId, ItemSlot.Bongos, Save.EquippedBongos);
        _save.Save();
        Changed?.Invoke();
    }

    /// <summary>单槽解析：空串 = 卸下；非法/未拥有/物种限定不符 id 跳过（保留原装备），合法才替换。</summary>
    private string ResolveOutfitSlot(string itemId, ItemSlot slot, string current)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return string.Empty;
        }

        if (Catalog.TryGet(itemId, out CosmeticItem? item) && item.Slot == slot && IsOwned(itemId) && IsAllowedForSpecies(item))
        {
            return itemId;
        }

        _log?.Invoke($"[Inventory] 预设应用：{itemId}（{slot}）非法/未拥有/物种不符，该槽保留原装备");
        return current;
    }

    /// <summary>
    /// 物种限定校验（GDD-09 §1）：speciesOnly 空 = 全物种可穿；非空 = 仅存档当前物种可穿。
    /// </summary>
    private bool IsAllowedForSpecies(CosmeticItem item)
        => item.SpeciesOnly.Length == 0 || string.Equals(item.SpeciesOnly, Save.SpeciesId, StringComparison.Ordinal);

    /// <inheritdoc />
    public void ValidateSpeciesCompatibility()
    {
        bool dirty = ClearSpeciesMismatch(nameof(SaveModel.EquippedHat), Save.EquippedHat, v => Save.EquippedHat = v);
        dirty |= ClearSpeciesMismatch(nameof(SaveModel.EquippedSkin), Save.EquippedSkin, v => Save.EquippedSkin = v);
        dirty |= ClearSpeciesMismatch(nameof(SaveModel.EquippedBongos), Save.EquippedBongos, v => Save.EquippedBongos = v);

        if (dirty)
        {
            _save.Save();
            Changed?.Invoke();
        }
    }

    /// <summary>单槽物种校验：装备存在且 speciesOnly 与当前物种不符 → 卸下（返回是否动过）。</summary>
    private bool ClearSpeciesMismatch(string slotName, string itemId, Action<string> clear)
    {
        if (itemId.Length == 0 || !Catalog.TryGet(itemId, out CosmeticItem? item) || IsAllowedForSpecies(item))
        {
            return false;
        }

        _log?.Invoke($"[Inventory] 物种切换：{slotName}={itemId} 仅限物种 '{item.SpeciesOnly}'，已卸下恢复默认（GDD-09 §1）");
        clear(string.Empty);
        return true;
    }

    /// <inheritdoc />
    public bool IsDiscovered(string itemId) => Save.DiscoveredItems.Contains(itemId);

    /// <summary>写入图鉴永久点亮（幂等；List 容量小，线性包含检查足够）。</summary>
    private void MarkDiscovered(string itemId)
    {
        if (!Save.DiscoveredItems.Contains(itemId))
        {
            Save.DiscoveredItems.Add(itemId);
        }
    }

    /// <summary>取拆解价（unique 无拆解价 → 0；未知品质防御 → 0）。</summary>
    private long GetSalvagePrice(ItemQuality quality)
        => _economy is not null && _economy.Salvage.TryGetValue(quality, out long price) ? price : 0;

    /// <inheritdoc />
    public IReadOnlyList<CollectopediaEntry> GetCollectopedia()
        => Catalog.Items
            .Select(i => new CollectopediaEntry(
                i,
                IsOwned(i.Id),
                IsDiscovered(i.Id),
                Save.OwnedItems.GetValueOrDefault(i.Id)))
            .ToList();
}
