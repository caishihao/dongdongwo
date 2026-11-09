using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Exchange;

/// <summary>
/// <see cref="IExchangeService"/> 默认实现：规则在 App 层（纯逻辑可测），
/// 状态全部落在存档模型（库存/图鉴经 IInventoryService，金币经 IEconomyService 单一出口）。
/// 时间/随机经注入（输出随机用 <see cref="Random"/>，可复现测试）。
/// 线程纪律：全部方法假定 UI 线程调用（与 EconomyService/InventoryService 一致）。
/// </summary>
public sealed class ExchangeService : IExchangeService
{
    /// <summary>单次合成输入件数（GDD-04 §6：3 → 1 固定，不可调）。</summary>
    public const int InputsPerExchange = 3;

    private readonly IInventoryService _inventory;
    private readonly IEconomyService _economy;
    private readonly EconomyConfig _config;
    private readonly Random _random;
    private readonly Action<string>? _log;

    public event Action<IReadOnlyList<string>, CosmeticItem>? ExchangeOccurred;

    public ExchangeService(
        IInventoryService inventory,
        IEconomyService economy,
        EconomyConfig config,
        Random random,
        Action<string>? log = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _economy = economy ?? throw new ArgumentNullException(nameof(economy));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _log = log;
    }

    /// <inheritdoc />
    public bool CanSynthesize(string anchorItemId, out string? failureReason)
        => TryPickInputs(anchorItemId, out _, out failureReason);

    /// <inheritdoc />
    public bool TrySynthesizeAuto(string anchorItemId, out ExchangeResult result, out string? failureReason)
    {
        result = null!;

        if (!TryPickInputs(anchorItemId, out IReadOnlyList<string>? inputs, out failureReason))
        {
            return false;
        }

        return TryExchange(inputs, out result, out failureReason);
    }

    /// <inheritdoc />
    public bool TryExchange(IReadOnlyList<string> inputIds, out ExchangeResult result, out string? failureReason)
    {
        result = null!;
        failureReason = null;

        // ── 合式性校验（GDD-04 §5）——失败路径零状态变化 ──────────────────
        if (inputIds is null || inputIds.Count != InputsPerExchange)
        {
            failureReason = $"合成需要恰好 {InputsPerExchange} 件物品";
            return false;
        }

        var items = new List<CosmeticItem>(InputsPerExchange);
        foreach (string id in inputIds)
        {
            if (!_inventory.Catalog.TryGet(id, out CosmeticItem? item))
            {
                failureReason = $"未知物品：{id}";
                return false;
            }

            items.Add(item);
        }

        // 库存数量足够（同名多件各消耗一件）
        foreach (IGrouping<string, string> group in inputIds.GroupBy(id => id))
        {
            if (_inventory.Owned.GetValueOrDefault(group.Key) < group.Count())
            {
                failureReason = $"库存不足：{group.Key} 需 {group.Count()} 件";
                return false;
            }
        }

        ItemQuality quality = items[0].Quality;
        ItemSlot slot = items[0].Slot;

        if (items.Any(i => i.Quality != quality))
        {
            failureReason = "合成物品必须同品质";
            return false;
        }

        if (items.Any(i => i.Slot != slot))
        {
            failureReason = "合成物品必须同槽位（V1 限制，跨槽位合成留 V2）";
            return false;
        }

        if (quality == ItemQuality.Unique)
        {
            failureReason = "unique 为顶阶，不可再合成";
            return false;
        }

        if (IsEquippedAny(inputIds))
        {
            failureReason = "装备中的物品不可合成（请先卸下）";
            return false;
        }

        if (quality == ItemQuality.Legendary && inputIds.Distinct().Count() != InputsPerExchange)
        {
            failureReason = "传说合成需要 3 件不同的传说物品";
            return false;
        }

        // 目标品质 = 输入品质 +1（legendary → unique，GDD-04 §5 品质阶跃）
        var targetQuality = (ItemQuality)((int)quality + 1);

        // 输出池：目标品质同槽位全池，排除本次输入 id（GDD-04 §8 开放问题 1 采纳）
        List<CosmeticItem> pool = _inventory.Catalog.Items
            .Where(i => i.Quality == targetQuality && i.Slot == slot && !inputIds.Contains(i.Id))
            .ToList();

        if (pool.Count == 0)
        {
            failureReason = $"暂无 {targetQuality} 品质的{SlotName(slot)}可合成";
            return false;
        }

        // ── 事务执行：先扣 3 件 → 随机 1 件入库（GDD-04 §4/§7）────────────
        foreach (string id in inputIds)
        {
            if (!_inventory.RemoveOne(id))
            {
                // 校验已过仍失败 = 并发/程序 bug：fail-fast 暴露，不静默半事务
                throw new InvalidOperationException($"合成事务中断：移除 {id} 失败");
            }
        }

        CosmeticItem output = pool[_random.Next(pool.Count)];
        ItemGrantResult grant = _inventory.GrantItem(output.Id); // 内部落盘 + 点亮图鉴 + Changed

        // 重复折算（GDD-04 §4：重复则按 02 规则自动折算金币 = 拆解价 × 60%；
        // unique 无拆解价 → 0，不折算）
        long dupCoins = 0;
        if (grant.WasDuplicate && _config.Salvage.TryGetValue(targetQuality, out long targetSalvage))
        {
            dupCoins = (long)Math.Floor(targetSalvage * _config.DuplicateConversionRate);
            if (dupCoins > 0)
            {
                _economy.GrantCoins(dupCoins, CoinSource.DuplicateConversion);
            }
        }

        result = new ExchangeResult(inputIds.ToList(), output, grant.WasDuplicate, dupCoins);
        _log?.Invoke($"[Exchange] 合成：{string.Join(" + ", inputIds)} → {output.Id}" +
                     (grant.WasDuplicate ? $"（重复 → +{dupCoins} 金币）" : "（新物品）"));
        ExchangeOccurred?.Invoke(inputIds, output);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetSalvageValue(string itemId, out long coins)
    {
        coins = 0;

        if (!_inventory.Catalog.TryGet(itemId, out CosmeticItem? item) ||
            !_config.Salvage.TryGetValue(item.Quality, out long price))
        {
            return false; // 未知 id / unique（无拆解价）
        }

        coins = price;
        return true;
    }

    /// <inheritdoc />
    public bool CanSalvage(string itemId, out string? failureReason)
    {
        failureReason = null;

        if (!_inventory.Catalog.TryGet(itemId, out CosmeticItem? item))
        {
            failureReason = "未知物品";
            return false;
        }

        if (!_inventory.IsOwned(itemId))
        {
            failureReason = "未拥有该物品";
            return false;
        }

        if (item.Quality == ItemQuality.Unique)
        {
            failureReason = "unique 为顶阶收藏，不可拆解";
            return false;
        }

        if (IsEquippedAny([itemId]))
        {
            failureReason = "装备中的物品不可拆解（请先卸下）";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool TrySalvage(string itemId, out long coins, out string? failureReason)
    {
        coins = 0;

        if (!CanSalvage(itemId, out failureReason))
        {
            return false;
        }

        if (!_inventory.Catalog.TryGet(itemId, out CosmeticItem? item) ||
            !_inventory.RemoveOne(itemId))
        {
            failureReason = "拆解失败：物品状态已变化";
            return false;
        }

        coins = _config.Salvage[item.Quality]; // 全额拆解价（GDD-02 §5：非重复物品手动拆解为全额）
        _economy.GrantCoins(coins, CoinSource.ManualSalvage);
        // 图鉴点亮（DiscoveredItems）不随 RemoveOne 移除——拆解后图鉴不回灰（E5 规则）
        _log?.Invoke($"[Exchange] 拆解：{itemId} → +{coins} 金币（图鉴保持点亮）");
        return true;
    }

    // ── 输入自动选取（UI 一键合成；纯检查零副作用）──────────────────────

    /// <summary>
    /// 自动选取 3 件输入：同品质 + 同槽位 + 拥有 + 非装备中；
    /// 优先消耗 anchor 的重复件（GDD-04 §2"自动选取重复品"），不足再取同池其他件。
    /// legendary：必须 3 件不同 id。最后校验"目标池排除输入后非空"。
    /// </summary>
    private bool TryPickInputs(string anchorItemId, out IReadOnlyList<string> inputs, out string? failureReason)
    {
        inputs = [];
        failureReason = null;

        if (!_inventory.Catalog.TryGet(anchorItemId, out CosmeticItem? anchor))
        {
            failureReason = "未知物品";
            return false;
        }

        if (!_inventory.IsOwned(anchorItemId))
        {
            failureReason = "未拥有该物品";
            return false;
        }

        if (anchor.Quality == ItemQuality.Unique)
        {
            failureReason = "unique 为顶阶，不可再合成";
            return false;
        }

        if (IsEquippedAny([anchorItemId]))
        {
            failureReason = "装备中的物品不可合成（请先卸下）";
            return false;
        }

        // 候选池：同品质 + 同槽位 + 拥有 + 非装备中（目录顺序稳定）
        var equipped = EquippedIds().ToHashSet(StringComparer.Ordinal);
        List<CosmeticItem> candidates = _inventory.Catalog.Items
            .Where(i => i.Quality == anchor.Quality && i.Slot == anchor.Slot &&
                        _inventory.IsOwned(i.Id) &&
                        !equipped.Contains(i.Id))
            .ToList();

        var picked = new List<string>(InputsPerExchange);

        if (anchor.Quality == ItemQuality.Legendary)
        {
            // legendary：3 件不同 id（GDD-04 §5/§7）
            foreach (CosmeticItem candidate in candidates)
            {
                picked.Add(candidate.Id);
                if (picked.Count == InputsPerExchange)
                {
                    break;
                }
            }

            if (picked.Count < InputsPerExchange)
            {
                failureReason = "传说合成需要 3 件不同的传说物品（同槽位）";
                return false;
            }
        }
        else
        {
            // 非 legendary：优先 anchor 重复件，再补其他件
            int anchorCopies = Math.Min(_inventory.Owned.GetValueOrDefault(anchorItemId), InputsPerExchange);
            for (int i = 0; i < anchorCopies && picked.Count < InputsPerExchange; i++)
            {
                picked.Add(anchorItemId);
            }

            foreach (CosmeticItem candidate in candidates)
            {
                while (picked.Count < InputsPerExchange &&
                       _inventory.Owned.GetValueOrDefault(candidate.Id) > picked.Count(id => id == candidate.Id))
                {
                    picked.Add(candidate.Id);
                }

                if (picked.Count == InputsPerExchange)
                {
                    break;
                }
            }

            if (picked.Count < InputsPerExchange)
            {
                failureReason = $"同品质同槽位物品不足 {InputsPerExchange} 件";
                return false;
            }
        }

        // 目标池校验：排除输入后必须非空（否则合成必出"排除项"外的空集）
        var targetQuality = (ItemQuality)((int)anchor.Quality + 1);
        bool poolAvailable = _inventory.Catalog.Items.Any(i =>
            i.Quality == targetQuality && i.Slot == anchor.Slot && !picked.Contains(i.Id));
        if (!poolAvailable)
        {
            failureReason = $"暂无 {targetQuality} 品质的{SlotName(anchor.Slot)}可合成";
            return false;
        }

        inputs = picked;
        return true;
    }

    private bool IsEquippedAny(IEnumerable<string> itemIds)
    {
        var equipped = EquippedIds().ToHashSet(StringComparer.Ordinal);
        return itemIds.Any(equipped.Contains);
    }

    private IEnumerable<string> EquippedIds()
    {
        if (_inventory.EquippedHat.Length > 0)
        {
            yield return _inventory.EquippedHat;
        }

        if (_inventory.EquippedSkin.Length > 0)
        {
            yield return _inventory.EquippedSkin;
        }

        if (_inventory.EquippedBongos.Length > 0)
        {
            yield return _inventory.EquippedBongos;
        }
    }

    private static string SlotName(ItemSlot slot) => slot switch
    {
        ItemSlot.Hat => "帽子",
        ItemSlot.Skin => "皮肤",
        ItemSlot.Bongos => "鼓",
        _ => slot.ToString(),
    };
}
