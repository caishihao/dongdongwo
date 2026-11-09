using System.Collections.ObjectModel;
using System.Windows.Threading;
using BongoCat.App.Economy;
using BongoCat.App.Exchange;
using BongoCat.App.Inventory;
using BongoCat.App.Presets;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BongoCat.UI.ViewModels;

/// <summary>
/// 背包/图鉴窗口 ViewModel（UI 层不持有规则：库存计算全部在 IInventoryService，
/// 经济状态全部在 IEconomyService，兑换/拆解规则全部在 IExchangeService，
/// 预设规则全部在 IPresetService——本 VM 只做列表编排与命令转发）。
/// E4：头部金币显示、下次掉落倒计时（TickCountdown 由窗口 DispatcherTimer 1Hz 调用）、开宝箱命令与结果品质色高亮。
/// E5：合成/拆解操作按钮（灰置 + tooltip 说明条件）、宝箱商店（金币买箱）、装扮预设区、金币变化闪动反馈。
/// </summary>
public partial class InventoryViewModel : ObservableObject
{
    private readonly IInventoryService _inventory;
    private readonly IEconomyService _economy;
    private readonly IExchangeService _exchange;
    private readonly IPresetService _presets;
    private readonly EconomyConfig _config;
    private readonly Action<Action, TimeSpan> _delayCallback;

    /// <summary>背包列表（当前过滤下的已拥有物品卡）。</summary>
    public ObservableCollection<InventoryItemCard> Cards { get; } = [];

    /// <summary>图鉴列表（全目录逐条：点亮彩色 / 未点亮灰剪影；曾获得不回灰，GDD-03 §5 + E5）。</summary>
    public ObservableCollection<CollectopediaEntry> DexEntries { get; } = [];

    /// <summary>图鉴完成度（曾拥有件数 / 总件数——点亮是永久资产，拆解不回灰）。</summary>
    [ObservableProperty]
    private int _dexOwnedCount;

    public int DexTotalCount => _inventory.Catalog.Count;

    /// <summary>背包当前选中物品（装备/合成/拆解按钮的目标）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EquipCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnequipCommand))]
    [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SalvageCommand))]
    private InventoryItemCard? _selectedCard;

    // 过滤器（左栏单选；互斥用三个 bool，True 才触发重算——勾选其他项时本项置 False 不动作）
    [ObservableProperty] private bool _filterAll = true;
    [ObservableProperty] private bool _filterHat;
    [ObservableProperty] private bool _filterSkin;
    [ObservableProperty] private bool _filterBongos;

    // ── E4：经济头部（金币 / 掉落倒计时 / 宝箱）──────────────────────────

    /// <summary>头部金币显示（品质色点旁；金币余额单一数据源 = IEconomyService/存档）。</summary>
    [ObservableProperty] private string _coinsText = "金币 0";

    /// <summary>金币变化闪动反馈（E5：金色高亮一闪，纯文字色，不做花动画）。</summary>
    [ObservableProperty] private bool _coinsFlash;

    /// <summary>下次掉落倒计时（"下次掉落 mm:ss"；桶满时显示暂停提示）。</summary>
    [ObservableProperty] private string _dropCountdownText = string.Empty;

    /// <summary>开宝箱按钮文案（可开 = "开宝箱"；充能中显示倒计时）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenChestCommand))]
    private bool _chestReady;

    [ObservableProperty] private string _chestButtonText = "开宝箱";

    /// <summary>最近一次掉落/开箱/合成/拆解/购买结果摘要（头部一行提示）。</summary>
    [ObservableProperty] private string _lastEventText = string.Empty;

    /// <summary>最近一次开箱/掉落/合成的物品 chips（品质色点 + 名称 + 重复折算角标）。</summary>
    public ObservableCollection<ChestItemChip> ResultChips { get; } = [];

    // ── E5：选中物品操作按钮（合成/拆解）────────────────────────────────

    /// <summary>合成按钮 tooltip（灰置时说明条件；可合成时说明规则）。</summary>
    [ObservableProperty] private string _synthesizeTooltip = "选中一件物品后合成";

    /// <summary>拆解按钮文案（含拆解价"+N 币"）。</summary>
    [ObservableProperty] private string _salvageButtonText = "拆解";

    /// <summary>拆解按钮 tooltip（灰置时说明条件）。</summary>
    [ObservableProperty] private string _salvageTooltip = "选中一件物品后拆解";

    // ── E5：宝箱商店 / 装扮预设 ──────────────────────────────────────────

    /// <summary>商店区宝箱卡（三档：id/显示名/售价，来自 economy.json chests，数据驱动）。</summary>
    public ObservableCollection<ChestShopCard> ChestShopCards { get; } = [];

    /// <summary>预设名列表（下拉选择）。</summary>
    public ObservableCollection<string> PresetNames { get; } = [];

    /// <summary>下拉选中的预设名（应用/删除目标）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    private string? _selectedPreset;

    /// <summary>保存预设的名称输入框。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string _newPresetName = string.Empty;

    public InventoryViewModel(
        IInventoryService inventory,
        IEconomyService economy,
        IExchangeService exchange,
        IPresetService presets,
        EconomyConfig config,
        Action<Action, TimeSpan>? delayCallback = null)
    {
        _inventory = inventory;
        _economy = economy;
        _exchange = exchange;
        _presets = presets;
        _config = config;
        _delayCallback = delayCallback ?? DefaultDelay; // 测试可注入假延时（金币闪动复位）

        _inventory.Changed += Refresh; // 装备/授予变化 → 列表与选中态自动刷新
        _economy.CoinsChanged += (_, _) => OnCoinsChanged();
        _economy.CoinsSpent += (_, _) => OnCoinsChanged();
        _economy.DropOccurred += OnDropOccurred;
        RebuildShopCards();
        RefreshPresetNames();
        UpdateCoinsText(); // 初始不闪动
        TickCountdown();
    }

    /// <summary>金币闪动默认实现：UI 线程 DispatcherTimer 700ms 后复位。</summary>
    private static void DefaultDelay(Action callback, TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            callback();
        };
        timer.Start();
    }

    private bool _coinsFlashInitialized;

    private void OnCoinsChanged()
    {
        UpdateCoinsText();
        if (_coinsFlashInitialized)
        {
            CoinsFlash = true;
            _delayCallback(() => CoinsFlash = false, TimeSpan.FromMilliseconds(700));
        }
    }

    private void UpdateCoinsText()
    {
        CoinsText = $"金币 {_economy.Coins}";
        _coinsFlashInitialized = true;
        BuyChestCommand.NotifyCanExecuteChanged(); // 余额变化 → 商店购买按钮可用性联动
    }

    partial void OnFilterAllChanged(bool value) { if (value) { RebuildCards(); } }
    partial void OnFilterHatChanged(bool value) { if (value) { RebuildCards(); } }
    partial void OnFilterSkinChanged(bool value) { if (value) { RebuildCards(); } }
    partial void OnFilterBongosChanged(bool value) { if (value) { RebuildCards(); } }

    partial void OnSelectedCardChanged(InventoryItemCard? value) => UpdateSelectionActions();

    /// <summary>帽子分类是否展示（与 StyleRenderer.HatsVisible 联动；关闭时换装面板无帽子页签）。</summary>
    public bool ShowHatFilter => Cosmetics.StyleRenderer.HatsVisible;

    private ItemSlot? CurrentFilter
        => FilterHat && ShowHatFilter ? ItemSlot.Hat
         : FilterSkin ? ItemSlot.Skin
         : FilterBongos ? ItemSlot.Bongos
         : null; // FilterAll

    /// <summary>重建背包 + 图鉴 + 选中操作按钮（窗口打开/库存变化时调用）。</summary>
    public void Refresh()
    {
        RebuildCards();

        DexEntries.Clear();
        int discovered = 0;
        foreach (CollectopediaEntry entry in _inventory.GetCollectopedia())
        {
            DexEntries.Add(entry);
            if (entry.Discovered)
            {
                discovered++;
            }
        }

        DexOwnedCount = discovered;
        UpdateSelectionActions();
    }

    private void RebuildCards()
    {
        ItemSlot? filter = CurrentFilter;
        Cards.Clear();
        // 已拥有物品按 目录顺序 + 槽位升序 排列（稳定可预期，同槽新获得的追加在尾部）
        foreach (CollectopediaEntry entry in _inventory.GetCollectopedia()
                     .Where(e => e.Owned
                         && (ShowHatFilter || e.Item.Slot != ItemSlot.Hat)
                         && (filter is null || e.Item.Slot == filter)))
        {
            var card = new InventoryItemCard(entry.Item, entry.Count)
            {
                IsEquipped = IsEquipped(entry.Item),
            };
            Cards.Add(card);
        }

        // 选中项被过滤掉 → 清空选中（CanExecute 联动禁用按钮）
        if (SelectedCard is { } selected && !Cards.Contains(selected))
        {
            SelectedCard = null;
        }
    }

    /// <summary>按选中物品刷新合成/拆解按钮的可用性与 tooltip（灰置 + 说明条件）。</summary>
    private void UpdateSelectionActions()
    {
        if (SelectedCard is not { } card)
        {
            SynthesizeTooltip = "选中一件物品后合成";
            SalvageButtonText = "拆解";
            SalvageTooltip = "选中一件物品后拆解";
            return;
        }

        SynthesizeTooltip = _exchange.CanSynthesize(card.Item.Id, out string? synthReason)
            ? "同品质同槽位 3 件 → 1 件高一品质（随机，自动选取重复品）"
            : synthReason ?? "不可合成";
        SalvageButtonText = _exchange.TryGetSalvageValue(card.Item.Id, out long salvageValue)
            ? $"拆解 +{salvageValue}"
            : "拆解";
        SalvageTooltip = _exchange.CanSalvage(card.Item.Id, out string? salvageReason)
            ? "拆解为金币（全额拆解价；图鉴保持点亮）"
            : salvageReason ?? "不可拆解";
    }

    private bool IsEquipped(CosmeticItem item) => item.Slot switch
    {
        ItemSlot.Hat => _inventory.EquippedHat == item.Id,
        ItemSlot.Skin => _inventory.EquippedSkin == item.Id,
        ItemSlot.Bongos => _inventory.EquippedBongos == item.Id,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanEquip))]
    private void Equip()
    {
        if (SelectedCard is { } card)
        {
            _inventory.Equip(card.Item.Id); // 成功后 Changed → Refresh
        }
    }

    private bool CanEquip() => SelectedCard is { IsEquipped: false }; // 背包列表只含已拥有物品

    [RelayCommand(CanExecute = nameof(CanUnequip))]
    private void Unequip()
    {
        if (SelectedCard is { } card)
        {
            _inventory.Unequip(card.Item.Slot);
        }
    }

    private bool CanUnequip() => SelectedCard is { } card && card.IsEquipped;

    // ── E5：合成 / 拆解 ──────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSynthesizeSelected))]
    private void Synthesize()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        if (_exchange.TrySynthesizeAuto(card.Item.Id, out ExchangeResult result, out string? reason))
        {
            ShowChips([new ChestItemChip(
                result.OutputItem.DisplayName,
                QualityPalette.GetHex(result.OutputItem.Quality),
                result.WasDuplicate ? $"重复 +{result.DuplicateCoins}" : "新物品！")]);
            LastEventText = $"合成成功：{string.Join(" + ", result.InputIds.Select(IdToName))} → {result.OutputItem.DisplayName}" +
                            (result.WasDuplicate ? $"（重复 → +{result.DuplicateCoins} 金币）" : "（新物品！）");
            Refresh();
        }
        else
        {
            LastEventText = $"合成失败：{reason}";
        }
    }

    private bool CanSynthesizeSelected()
        => SelectedCard is { } card && _exchange.CanSynthesize(card.Item.Id, out _);

    [RelayCommand(CanExecute = nameof(CanSalvageSelected))]
    private void Salvage()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        if (_exchange.TrySalvage(card.Item.Id, out long coins, out string? reason))
        {
            LastEventText = $"拆解 {card.Item.DisplayName} → +{coins} 金币（图鉴保持点亮）";
            Refresh();
        }
        else
        {
            LastEventText = $"拆解失败：{reason}";
        }
    }

    private bool CanSalvageSelected()
        => SelectedCard is { } card && _exchange.CanSalvage(card.Item.Id, out _);

    // ── E4：开箱 / 倒计时 ────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOpenChestNow))]
    private void OpenChest()
    {
        if (_economy.TryOpenChest(out IReadOnlyList<ChestOpenResult> results))
        {
            ShowChips(results.Select(r => new ChestItemChip(
                r.Item.DisplayName,
                QualityPalette.GetHex(r.Item.Quality),
                r.WasDuplicate ? $"重复 +{r.DuplicateCoins}" : "新物品！")));
            LastEventText = results.Count == 1
                ? DescribeChip(results[0])
                : $"开箱获得 {results.Count} 件：" + string.Join("、", results.Select(DescribeChip));
            Refresh(); // 物品已入库（Changed 事件也会触发，此处显式刷新保证结果立即可见）
        }
    }

    private bool CanOpenChestNow() => ChestReady;

    // ── E5：宝箱商店（金币买箱）──────────────────────────────────────────

    private void RebuildShopCards()
    {
        ChestShopCards.Clear();
        foreach (ChestRule chest in _config.Chests)
        {
            ChestShopCards.Add(new ChestShopCard(chest.Id, ChestDisplayName(chest.Id), chest.Price));
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuyChest))]
    private void BuyChest(string? chestId)
    {
        if (chestId is null)
        {
            return;
        }

        if (_economy.TryBuyChest(chestId, out IReadOnlyList<ChestOpenResult> results))
        {
            ShowChips(results.Select(r => new ChestItemChip(
                r.Item.DisplayName,
                QualityPalette.GetHex(r.Item.Quality),
                r.WasDuplicate ? $"重复 +{r.DuplicateCoins}" : "新物品！")));
            LastEventText = $"购买并开启{ChestDisplayName(chestId)}：" +
                            string.Join("、", results.Select(DescribeChip));
            Refresh();
        }
        else
        {
            LastEventText = "购买失败：金币不足";
        }
    }

    private bool CanBuyChest(string? chestId)
        => chestId is not null &&
           _config.TryGetChest(chestId, out ChestRule chest) &&
           _economy.Coins >= chest.Price;

    // ── E5：装扮预设 ─────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        if (_presets.SavePreset(NewPresetName, out string? reason))
        {
            LastEventText = $"已保存预设「{NewPresetName.Trim()}」";
            NewPresetName = string.Empty;
            RefreshPresetNames();
        }
        else
        {
            LastEventText = $"保存预设失败：{reason}";
        }
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(NewPresetName);

    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private void ApplyPreset()
    {
        if (SelectedPreset is not { } name)
        {
            return;
        }

        if (_presets.ApplyPreset(name, out string? reason))
        {
            LastEventText = $"已应用预设「{name}」";
            Refresh();
        }
        else
        {
            LastEventText = $"应用预设失败：{reason}";
        }
    }

    private bool CanApplyPreset() => SelectedPreset is not null;

    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is { } name && _presets.DeletePreset(name))
        {
            LastEventText = $"已删除预设「{name}」";
            RefreshPresetNames();
        }
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        foreach (CosmeticPreset preset in _presets.Presets)
        {
            PresetNames.Add(preset.Name);
        }

        SelectedPreset = PresetNames.FirstOrDefault();
    }

    // ── E4：掉落 / 结果展示 ──────────────────────────────────────────────

    /// <summary>倒计时刷新（InventoryWindow 的 1Hz DispatcherTimer 调用；纯文本更新不触发布局树重建）。</summary>
    public void TickCountdown()
    {
        DropCountdownText = $"下次掉落 {FormatCountdown(_economy.TimeToNextDrop)}";

        ChestReady = _economy.CanOpenChest;
        ChestButtonText = ChestReady
            ? "开宝箱"
            : $"充能中 {FormatCountdown(_economy.TimeToNextChest)}";
    }

    private void OnDropOccurred(DropEvent e)
    {
        switch (e.Kind)
        {
            case DropKind.Item when e.Item is not null:
                ShowChips([new ChestItemChip(
                    e.Item.DisplayName,
                    QualityPalette.GetHex(e.Item.Quality),
                    e.WasDuplicate ? $"重复 +{e.DuplicateCoins}" : "新物品！")]);
                LastEventText = e.WasDuplicate
                    ? $"捡到 {e.Item.DisplayName}（重复 → +{e.DuplicateCoins} 金币）"
                    : $"捡到新装扮：{e.Item.DisplayName}！";
                break;
            case DropKind.CoinPack:
                LastEventText = $"捡到金币包 +{e.Coins} 金币";
                break;
            case DropKind.Suppressed:
                LastEventText = "掉落桶已满，物品掉落暂停（最老掉落过期后恢复）";
                break;
        }
    }

    private void ShowChips(IEnumerable<ChestItemChip> chips)
    {
        ResultChips.Clear();
        foreach (ChestItemChip chip in chips)
        {
            ResultChips.Add(chip);
        }
    }

    private string IdToName(string itemId)
        => _inventory.Catalog.TryGet(itemId, out CosmeticItem? item) ? item.DisplayName : itemId;

    private static string DescribeChip(ChestOpenResult r)
        => r.WasDuplicate ? $"{r.Item.DisplayName}+{r.DuplicateCoins}" : r.Item.DisplayName;

    private static string ChestDisplayName(string chestId) => chestId switch
    {
        "wooden" => "木箱",
        "silver" => "银箱",
        "gold" => "金箱",
        _ => chestId,
    };

    /// <summary>mm:ss 倒计时格式（充能 2h 显示 119:59 式分钟，不引入时档）。</summary>
    private static string FormatCountdown(TimeSpan t)
        => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
}

/// <summary>开箱/掉落结果 chip（品质色点 + 名称 + 角标；View 侧品质色 HEX 转 Brush）。</summary>
public sealed record ChestItemChip(string DisplayName, string QualityHex, string Badge);

/// <summary>E5 商店宝箱卡（economy.json chests 数据驱动；View 绑定购买按钮 CommandParameter=ChestId）。</summary>
public sealed record ChestShopCard(string ChestId, string DisplayName, long Price);

/// <summary>背包列表物品卡（数量/品质/装备态快照，Refresh 时整体重建）。</summary>
public sealed class InventoryItemCard
{
    public CosmeticItem Item { get; }

    /// <summary>持有数量（E3 恒为 1；E4 宝箱重复掉落起 >1 显示角标）。</summary>
    public int Count { get; }

    /// <summary>品质色 HEX（自设计色板，View 侧转 Brush）。</summary>
    public string QualityHex => QualityPalette.GetHex(Item.Quality);

    public bool IsEquipped { get; set; }

    public InventoryItemCard(CosmeticItem item, int count)
    {
        Item = item;
        Count = count;
    }
}
