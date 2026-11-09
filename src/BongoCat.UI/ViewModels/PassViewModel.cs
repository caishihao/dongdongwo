using System.Collections.ObjectModel;
using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.App.Pass;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BongoCat.UI.ViewModels;

/// <summary>
/// 通行证窗口 ViewModel（UI 层不持有规则：赛季窗口/XP/领取/解锁全部在 IPassService，
/// 物品目录在 IInventoryService，金币余额在 IEconomyService——本 VM 只做节点编排与命令转发）。
/// E6：顶部赛季信息 + 等级 XP 进度条 + 高级轨解锁按钮；主体里程碑横向列表（上免费轨/下高级轨，
/// 三态：已领取 ✓ / 可领取高亮 / 未达成灰）；领取后品质色 chip 反馈（复用 E4 chip 模式）。
/// </summary>
public partial class PassViewModel : ObservableObject
{
    private readonly IPassService _pass;
    private readonly IInventoryService _inventory;
    private readonly IEconomyService _economy;

    /// <summary>里程碑节点列表（左→右按 XP 阈值升序；Refresh 时整体重建）。</summary>
    public ObservableCollection<MilestoneNodeViewModel> Nodes { get; } = [];

    /// <summary>最近一次领取的结果 chip（品质色点 + 名称 + 角标，复用 E4 背包 chip 结构）。</summary>
    public ObservableCollection<ChestItemChip> ResultChips { get; } = [];

    /// <summary>赛季信息行（"第一赛季·初来乍到 · 剩余 28 天"；无赛季 = "暂无进行中的赛季"）。</summary>
    [ObservableProperty] private string _seasonText = string.Empty;

    /// <summary>等级 + XP 文本（"Lv 12 · 430 / 520 XP"；满级 = "Lv 50 · MAX"）。</summary>
    [ObservableProperty] private string _levelXpText = string.Empty;

    /// <summary>等级进度条比例 0~1（满级为 1）。</summary>
    [ObservableProperty] private double _xpProgressRatio;

    /// <summary>今日任务进度行（"今日任务：开启 2 个宝箱 1/2 · 累计敲击 3000 次 320/3000 · …"；无赛季为空）。</summary>
    [ObservableProperty] private string _questText = string.Empty;

    /// <summary>高级轨是否已解锁（解锁按钮显隐）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockPremiumCommand))]
    private bool _premiumUnlocked;

    /// <summary>高级轨解锁按钮文案（未解锁 = "解锁高级轨（2500 金币）"）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockPremiumCommand))]
    private string _premiumButtonText = string.Empty;

    /// <summary>最近一次操作提示（领取失败原因 / 解锁结果）。</summary>
    [ObservableProperty] private string _lastEventText = string.Empty;

    public PassViewModel(
        IPassService pass,
        IInventoryService inventory,
        IEconomyService economy)
    {
        _pass = pass;
        _inventory = inventory;
        _economy = economy;

        _pass.PassChanged += Refresh;
        _economy.CoinsChanged += (_, _) => OnCoinsChanged();
        _economy.CoinsSpent += (_, _) => OnCoinsChanged();
        Refresh();
    }

    /// <summary>整体刷新（打开窗口 / PassChanged 事件 / 领取与解锁后调用）。</summary>
    public void Refresh()
    {
        SeasonConfig? season = _pass.CurrentSeason;
        if (season is null)
        {
            SeasonText = "暂无进行中的赛季";
            LevelXpText = string.Empty;
            XpProgressRatio = 0;
            QuestText = string.Empty;
            PremiumUnlocked = false;
            PremiumButtonText = string.Empty;
            Nodes.Clear();
            return;
        }

        SeasonText = $"{season.Name} · 剩余 {_pass.DaysRemaining} 天";
        int level = _pass.Level;
        LevelXpText = _pass.XpForNextLevel == 0
            ? $"Lv {level} · MAX"
            : $"Lv {level} · {_pass.XpIntoLevel} / {_pass.XpForNextLevel} XP";
        XpProgressRatio = _pass.XpForNextLevel == 0
            ? 1
            : (double)_pass.XpIntoLevel / _pass.XpForNextLevel;
        PremiumUnlocked = _pass.PremiumUnlocked;
        PremiumButtonText = $"解锁高级轨（{season.PremiumUnlockCoins} 金币）";

        QuestText = "今日任务：" + string.Join(" · ", _pass.GetDailyQuests()
            .Select(q => $"{q.Description} {q.Progress}/{q.Target}" + (q.Completed ? " ✓" : string.Empty)));

        Nodes.Clear();
        for (int lv = 1; lv <= season.MaxLevel; lv++)
        {
            season.TryGetReward(lv, PassTrack.Free, out PassReward free);
            season.TryGetReward(lv, PassTrack.Premium, out PassReward premium);
            Nodes.Add(new MilestoneNodeViewModel(lv, season.ThresholdForLevel(lv),
                DescribeReward(free, out string freeHex), freeHex,
                DescribeReward(premium, out string premiumHex), premiumHex,
                this));
        }
    }

    private void OnCoinsChanged() => UnlockPremiumCommand.NotifyCanExecuteChanged();

    /// <summary>奖励文案（"金币 ×100" / 物品显示名）；品质色：金币 = 琥珀金，物品 = 品质色板。</summary>
    private string DescribeReward(PassReward reward, out string qualityHex)
    {
        if (reward.Kind == PassRewardKind.Coins)
        {
            qualityHex = "#E8A020"; // 经济主色调（GDD-02 §6.1）
            return $"金币 ×{reward.Amount}";
        }

        if (_inventory.Catalog.TryGet(reward.ItemId, out CosmeticItem? item))
        {
            qualityHex = QualityPalette.GetHex(item.Quality);
            return item.DisplayName;
        }

        qualityHex = "#999999";
        return reward.ItemId; // 加载期已校验引用；防御兜底
    }

    /// <summary>节点状态查询（MilestoneNodeViewModel 用；保持 _pass 私有，规则仍在服务内）。</summary>
    internal bool IsNodeReached(int level) => _pass.IsMilestoneReached(level);

    internal bool IsNodeClaimed(int level, PassTrack track) => _pass.IsClaimed(level, track);

    internal bool NodePremiumUnlocked => _pass.PremiumUnlocked;

    private bool CanUnlockPremium()
        => !_pass.PremiumUnlocked &&
           _pass.CurrentSeason is not null &&
           _economy.Coins >= _pass.CurrentSeason.PremiumUnlockCoins;

    [RelayCommand(CanExecute = nameof(CanUnlockPremium))]
    private void UnlockPremium()
    {
        if (_pass.TryUnlockPremium(out string? reason))
        {
            LastEventText = "高级轨已解锁！本期高级奖励立即可领取";
            Refresh(); // TryUnlockPremium 内部也广播 PassChanged，此处显式刷新保证按钮态即时
        }
        else
        {
            LastEventText = $"解锁失败：{reason}";
        }
    }

    /// <summary>节点领取入口（MilestoneNodeViewModel 命令转发；服务负责全部校验与幂等）。</summary>
    public void Claim(int level, PassTrack track)
    {
        if (_pass.TryClaim(level, track, out PassClaimResult result, out string? failureReason))
        {
            ShowResultChip(result);
            Refresh();
        }
        else
        {
            LastEventText = $"领取失败：{failureReason}";
        }
    }

    private void ShowResultChip(PassClaimResult result)
    {
        string display;
        string hex;
        string badge;
        if (result.Kind == PassRewardKind.Coins)
        {
            display = $"金币 ×{result.Coins}";
            hex = "#E8A020";
            badge = "已入账";
        }
        else if (result.Item is not null)
        {
            display = result.Item.DisplayName;
            hex = QualityPalette.GetHex(result.Item.Quality);
            badge = result.OverflowCoins > 0
                ? $"超上限 → +{result.OverflowCoins} 金币"
                : result.WasDuplicate ? "重复（已入库）" : "新物品！";
        }
        else
        {
            return;
        }

        ResultChips.Clear();
        ResultChips.Add(new ChestItemChip(display, hex, badge));
        LastEventText = $"已领取 Lv{result.Level} {TrackName(result.Track)}轨奖励：{display}" +
                        (result.OverflowCoins > 0 ? $"（超出上限折算 +{result.OverflowCoins} 金币）" : string.Empty);
    }

    private static string TrackName(PassTrack track)
        => track == PassTrack.Free ? "免费" : "高级";
}

/// <summary>
/// 单个里程碑节点的视图状态（等级 + 阈值 + 双轨文案 + 双轨三态字符串：
/// "Claimed" 已领取 ✓ / "Claimable" 可领取高亮 / "Locked" 未达成灰——View 用 DataTrigger 着色）。
/// </summary>
public partial class MilestoneNodeViewModel : ObservableObject
{
    private readonly PassViewModel _owner;

    public int Level { get; }

    /// <summary>达成本级所需累计 XP（节点 tooltip 用）。</summary>
    public long Threshold { get; }

    public string FreeRewardText { get; }

    /// <summary>免费轨奖励品质色 HEX（金币 = 琥珀金，物品 = 品质色板）。</summary>
    public string FreeRewardHex { get; }

    public string PremiumRewardText { get; }

    public string PremiumRewardHex { get; }

    public string ThresholdTooltip => $"需要 {Threshold} XP";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClaimFreeCommand))]
    private string _freeState;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClaimPremiumCommand))]
    private string _premiumState;

    public MilestoneNodeViewModel(
        int level,
        long threshold,
        string freeRewardText,
        string freeRewardHex,
        string premiumRewardText,
        string premiumRewardHex,
        PassViewModel owner)
    {
        Level = level;
        Threshold = threshold;
        FreeRewardText = freeRewardText;
        FreeRewardHex = freeRewardHex;
        PremiumRewardText = premiumRewardText;
        PremiumRewardHex = premiumRewardHex;
        _owner = owner;
        _freeState = "Locked";
        _premiumState = "Locked";
        RefreshState();
    }

    /// <summary>按服务当前状态重算双轨三态（父 VM Refresh 重建节点时调用）。</summary>
    public void RefreshState()
    {
        bool reached = _owner.IsNodeReached(Level);
        FreeState = ResolveState(reached, PassTrack.Free);
        PremiumState = ResolveState(reached, PassTrack.Premium);
    }

    private string ResolveState(bool reached, PassTrack track)
    {
        if (_owner.IsNodeClaimed(Level, track))
        {
            return "Claimed";
        }

        if (reached && (track == PassTrack.Free || _owner.NodePremiumUnlocked))
        {
            return "Claimable";
        }

        return "Locked";
    }

    private bool CanClaimFree() => FreeState == "Claimable";

    private bool CanClaimPremium() => PremiumState == "Claimable";

    [RelayCommand(CanExecute = nameof(CanClaimFree))]
    private void ClaimFree() => _owner.Claim(Level, PassTrack.Free);

    [RelayCommand(CanExecute = nameof(CanClaimPremium))]
    private void ClaimPremium() => _owner.Claim(Level, PassTrack.Premium);
}
