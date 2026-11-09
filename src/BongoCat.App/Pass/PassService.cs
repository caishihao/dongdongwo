using System.Globalization;
using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

namespace BongoCat.App.Pass;

/// <summary>
/// <see cref="IPassService"/> 默认实现：规则在 App 层（纯逻辑可测），
/// 状态全部落在存档模型（存档即单一数据源）。
/// </summary>
/// <remarks>
/// 防刷边界（GDD-05 §5/§6）：
/// - 敲击 XP 复用 EconomyService 的有效敲击计数（防刷判定共用），每日上限 300
///   （§6 数值表：上限挂在"敲击 XP"行——任务 XP 量级固定 75，不占敲击额度）；
/// - 日上限跨天以"日期字符串序比较"重置（与 EconomyService 同策略）——回拨时钟只会让
///   today &lt; saved，走"不重置不奖励"分支，XP 结算冻结当日；
/// - 赛季切换惰性判定：save.SeasonId 与今日激活配置不符 → 重置赛季字段
///   （仅 SeasonXP/ClaimedMilestones/PremiumUnlocked，金币/物品/图鉴/预设永不动，用户拍板）；
/// - 满级后停止累计 XP（溢出转金币的汇率 GDD-05 未定义 → 记开放问题，不实现）。
/// </remarks>
public sealed class PassService : IPassService
{
    /// <summary>敲击 XP：每 KeystrokesPerStep 次有效敲击 TapXpPerStep 点（GDD-05 §6：10 XP/1000 键）。</summary>
    public const int TapXpPerStep = 10;

    public const int KeystrokesPerStep = 1000;

    /// <summary>敲击 XP 每日上限（GDD-05 §6；任务 XP 不占此额度）。</summary>
    public const int DailyTapXpCap = 300;

    private readonly ISaveService _save;
    private readonly IReadOnlyList<SeasonConfig> _seasons;
    private readonly IInventoryService _inventory;
    private readonly IEconomyService _economy;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    private SeasonConfig? _current;

    public PassService(
        ISaveService save,
        IReadOnlyList<SeasonConfig> seasons,
        IInventoryService inventory,
        IEconomyService economy,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _seasons = seasons ?? throw new ArgumentNullException(nameof(seasons));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _economy = economy ?? throw new ArgumentNullException(nameof(economy));
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    private SaveModel Save => _save.Current;

    public SeasonConfig? CurrentSeason => _current;

    public long SeasonXP => Save.SeasonXP;

    public int Level => _current is null ? 0 : LevelForXp(_current, Save.SeasonXP);

    public long XpIntoLevel => _current is null ? 0 : Save.SeasonXP - _current.ThresholdForLevel(Level);

    public long XpForNextLevel
    {
        get
        {
            if (_current is null || Level >= _current.MaxLevel)
            {
                return 0;
            }

            return _current.ThresholdForLevel(Level + 1) - _current.ThresholdForLevel(Level);
        }
    }

    public bool PremiumUnlocked => Save.PremiumUnlocked;

    public int DaysRemaining
    {
        get
        {
            if (_current is null)
            {
                return 0;
            }

            int days = _current.EndDate.DayNumber - Today().DayNumber + 1; // 含当日
            return Math.Max(0, days);
        }
    }

    public event Action? PassChanged;

    public event Action<int>? MilestoneReady;

    public event Action<string>? SeasonSwitched;

    /// <inheritdoc />
    public void Initialize()
    {
        EnsureSeason();
        EnsureDailyReset();
        SettleQuests();
        _log?.Invoke(_current is null
            ? "[Pass] 初始化完成：无激活赛季（进度保留）"
            : $"[Pass] 初始化完成：{_current.Name} Lv{Level}，XP {Save.SeasonXP}，" +
              $"高级轨 {(Save.PremiumUnlocked ? "已解锁" : "未解锁")}，已领 {Save.ClaimedMilestones.Count} 项");
    }

    /// <inheritdoc />
    public void Tick()
    {
        EnsureSeason();
        EnsureDailyReset();
    }

    /// <inheritdoc />
    public void OnValidTap()
    {
        EnsureSeason();
        EnsureDailyReset();

        // 敲击 XP：每跨过 1000 键里程碑 +10（复用 EconomyService 的 DailyTapCount——
        // 防刷判定共用；本方法必须晚于 economy.OnValidTap 订阅触发，taps 为已含本次的计数）
        int taps = Save.DailyTapCount;
        int before = Math.Min((taps - 1) / KeystrokesPerStep, DailyTapXpCap / TapXpPerStep) * TapXpPerStep;
        int after = Math.Min(taps / KeystrokesPerStep, DailyTapXpCap / TapXpPerStep) * TapXpPerStep;
        if (after > before)
        {
            AddXp(after - before, countsToDailyCap: true);
        }

        SettleQuests();
    }

    /// <inheritdoc />
    public void NotifyChestOpened()
    {
        if (_current is null)
        {
            return; // 无激活赛季不计数（进度归零语义在赛季切换时已保证）
        }

        Save.DailyChestCount++;
        SettleQuests();
        _save.Save();
    }

    /// <inheritdoc />
    public void NotifySynthesized()
    {
        if (_current is null)
        {
            return;
        }

        Save.DailySynthCount++;
        SettleQuests();
        _save.Save();
    }

    /// <inheritdoc />
    public IReadOnlyList<PassQuestView> GetDailyQuests()
    {
        if (_current is null || _current.DailyQuestPool.Count == 0)
        {
            return [];
        }

        DateOnly today = Today();
        var pool = _current.DailyQuestPool;
        int offset = today.DayOfYear; // 池轮换相位（GDD-05 §5：每日从池中轮换取 3 条）
        var views = new List<PassQuestView>(Math.Min(SeasonConfig.QuestsPerDay, pool.Count));
        for (int i = 0; i < Math.Min(SeasonConfig.QuestsPerDay, pool.Count); i++)
        {
            PassQuestRule quest = pool[(offset + i) % pool.Count];
            int progress = quest.Type switch
            {
                PassQuestType.OpenChest => Save.DailyChestCount,
                PassQuestType.Keystrokes => Save.DailyTapCount,
                PassQuestType.Exchange => Save.DailySynthCount,
                _ => 0,
            };
            views.Add(new PassQuestView(
                quest.Id, DescribeQuest(quest), Math.Min(progress, quest.Target), quest.Target, quest.Xp,
                Save.DailyCompletedQuests.Contains(quest.Id)));
        }

        return views;
    }

    /// <inheritdoc />
    public bool IsClaimed(int level, PassTrack track)
        => Save.ClaimedMilestones.Contains(MilestoneKey(_current?.SeasonId ?? Save.SeasonId, level, track));

    public bool IsMilestoneReached(int level)
        => _current is not null && level >= 1 && level <= _current.MaxLevel &&
           Save.SeasonXP >= _current.ThresholdForLevel(level);

    /// <inheritdoc />
    public bool TryUnlockPremium(out string? failureReason)
    {
        failureReason = null;

        if (_current is null)
        {
            failureReason = "暂无进行中的赛季";
            return false;
        }

        if (Save.PremiumUnlocked)
        {
            failureReason = "高级轨已解锁";
            return false;
        }

        if (!_economy.TrySpendCoins(_current.PremiumUnlockCoins, CoinSource.PremiumUnlock))
        {
            failureReason = $"金币不足：解锁高级轨需要 {_current.PremiumUnlockCoins} 金币";
            return false;
        }

        Save.PremiumUnlocked = true;
        _save.Save();
        PassChanged?.Invoke();
        _log?.Invoke($"[Pass] 高级轨已解锁（{_current.PremiumUnlockCoins} 金币）");

        // 解锁立即可领的高级里程碑 → 单次气泡提示（不逐级刷屏）
        for (int level = 1; level <= Level; level++)
        {
            if (_current.TryGetReward(level, PassTrack.Premium, out _) && !IsClaimed(level, PassTrack.Premium))
            {
                MilestoneReady?.Invoke(level);
                break;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryClaim(int level, PassTrack track, out PassClaimResult result, out string? failureReason)
    {
        result = null!;
        failureReason = null;

        if (_current is null)
        {
            failureReason = "暂无进行中的赛季";
            return false;
        }

        if (level < 1 || level > _current.MaxLevel)
        {
            failureReason = $"里程碑等级非法（1~{_current.MaxLevel}）";
            return false;
        }

        if (!IsMilestoneReached(level))
        {
            failureReason = $"Lv{level} 未达成（还需 {Math.Max(0, _current.ThresholdForLevel(level) - Save.SeasonXP)} XP）";
            return false;
        }

        if (track == PassTrack.Premium && !Save.PremiumUnlocked)
        {
            failureReason = "高级轨未解锁（需先花金币解锁）";
            return false;
        }

        if (IsClaimed(level, track))
        {
            failureReason = $"Lv{level} {track} 轨奖励已领取过（不可重复领取）";
            return false;
        }

        if (!_current.TryGetReward(level, track, out PassReward reward))
        {
            failureReason = $"Lv{level} {track} 轨没有配置奖励";
            return false;
        }

        // ── 发奖（金币经经济层审计；物品经库存层入库含上限折算）────────────
        long coins = 0;
        CosmeticItem? item = null;
        bool wasDuplicate = false;
        long overflowCoins = 0;
        if (reward.Kind == PassRewardKind.Coins)
        {
            coins = reward.Amount;
            _economy.GrantCoins(coins, CoinSource.PassReward);
        }
        else
        {
            item = _inventory.Catalog.TryGet(reward.ItemId, out CosmeticItem? found) ? found : null;
            if (item is null)
            {
                // 加载期已校验 itemId 引用；此处为程序 bug 防御，fail-fast
                throw new InvalidOperationException($"赛季奖励引用未知物品：{reward.ItemId}");
            }

            ItemGrantResult grant = _inventory.GrantItem(reward.ItemId); // 内部落盘 + Changed 广播
            wasDuplicate = grant.WasDuplicate;
            overflowCoins = grant.OverflowCoins;
        }

        Save.ClaimedMilestones.Add(MilestoneKey(_current.SeasonId, level, track));
        _save.Save();
        result = new PassClaimResult(level, track, reward.Kind, coins, item, wasDuplicate, overflowCoins);
        PassChanged?.Invoke();
        _log?.Invoke($"[Pass] 领取奖励：{DescribeResult(result)}");
        return true;
    }

    // ── 赛季窗口 / 赛季切换（惰性，GDD-05 §7）────────────────────────────

    private void EnsureSeason()
    {
        DateOnly today = Today();
        SeasonConfig? active = _seasons.FirstOrDefault(s => s.IsActiveOn(today));

        if (active is null)
        {
            // 无激活赛季：不切换不清进度（GDD-05 §7"进度文件保留不删"；UI 显示暂无赛季）
            _current = null;
            return;
        }

        if (_current == active && Save.SeasonId == active.SeasonId)
        {
            return; // 已装载，幂等返回（每 Tick 调用，快速路径）
        }

        if (Save.SeasonId == active.SeasonId)
        {
            _current = active; // 同赛季重装载（如配置内存刷新），进度延续
            return;
        }

        // 赛季切换/首次进入：重置赛季字段（用户拍板：金币/物品/图鉴/预设永不清空）
        bool firstEntry = string.IsNullOrEmpty(Save.SeasonId);
        Save.SeasonId = active.SeasonId;
        Save.SeasonXP = 0;
        Save.ClaimedMilestones.Clear();
        Save.PremiumUnlocked = false;
        _current = active;
        _save.Save();
        PassChanged?.Invoke();
        if (firstEntry)
        {
            _log?.Invoke($"[Pass] 首次进入赛季：{active.Name}（{active.StartDate} ~ {active.EndDate}）");
        }
        else
        {
            _log?.Invoke($"[Pass] 赛季切换 → {active.Name}（XP/领取记录/高级轨已重置，资产保留）");
            SeasonSwitched?.Invoke(active.Name);
        }
    }

    // ── 每日重置 / 防时钟回拨（与 EconomyService 同策略）─────────────────

    private void EnsureDailyReset()
    {
        string today = Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string saved = Save.DailyXpDate;

        if (string.IsNullOrEmpty(saved))
        {
            Save.DailyXpDate = today;
            return;
        }

        int cmp = string.CompareOrdinal(today, saved);
        if (cmp > 0)
        {
            Save.DailyXpDate = today;
            Save.DailyXpEarned = 0;
            Save.DailyChestCount = 0;
            Save.DailySynthCount = 0;
            Save.DailyCompletedQuests.Clear();
            _save.Save();
            _log?.Invoke($"[Pass] 跨天重置（{saved} → {today}）：敲击 XP/任务进度已清零");
        }
        else if (cmp < 0)
        {
            _log?.Invoke($"[Pass] 检测到系统时钟回拨（存档日期 {saved} 晚于今天 {today}），XP 结算冻结当日");
        }
    }

    // ── XP / 任务结算 ────────────────────────────────────────────────────

    /// <summary>
    /// XP 入账（满级后停止累计；跨越新等级 → MilestoneReady 气泡事件）。
    /// countsToDailyCap = true 时同步累计 <see cref="SaveModel.DailyXpEarned"/>
    /// （GDD-05 §6：每日 300 上限挂在"敲击 XP"行——任务 XP 不占额度，只累计不入账上限）。
    /// </summary>
    private void AddXp(long amount, bool countsToDailyCap = false)
    {
        if (_current is null || amount <= 0 || Level >= _current.MaxLevel)
        {
            return; // 满级封顶（溢出转金币汇率未定义 → 记开放问题不实现）
        }

        int beforeLevel = Level;
        Save.SeasonXP += amount;
        if (countsToDailyCap)
        {
            Save.DailyXpEarned += (int)amount;
        }

        _save.Save();

        int afterLevel = Level;
        if (afterLevel > beforeLevel)
        {
            _log?.Invoke($"[Pass] 等级提升：Lv{beforeLevel} → Lv{afterLevel}（+{amount} XP）");
            PassChanged?.Invoke();
            MilestoneReady?.Invoke(afterLevel);
        }
    }

    /// <summary>
    /// 每日任务结算：达成目标且今日未入账 → XP 自动入账（GDD-05 §7：任务 XP 无需手动领）。
    /// </summary>
    private void SettleQuests()
    {
        if (_current is null)
        {
            return;
        }

        foreach (PassQuestView quest in GetDailyQuests())
        {
            if (quest.Completed || quest.Progress < quest.Target)
            {
                continue;
            }

            PassQuestRule rule = _current.DailyQuestPool.First(q => q.Id == quest.Id);
            Save.DailyCompletedQuests.Add(rule.Id);
            _log?.Invoke($"[Pass] 每日任务完成：{quest.Description}（+{rule.Xp} XP 自动入账）");
            AddXp(rule.Xp);
        }
    }

    private static string DescribeQuest(PassQuestRule quest) => quest.Type switch
    {
        PassQuestType.OpenChest => $"开启 {quest.Target} 个宝箱",
        PassQuestType.Keystrokes => $"累计敲击 {quest.Target} 次",
        PassQuestType.Exchange => $"合成 {quest.Target} 次",
        _ => quest.Id,
    };

    private static string MilestoneKey(string seasonId, int level, PassTrack track)
        => $"{seasonId}:{level}:{track.ToString().ToLowerInvariant()}";

    private static string DescribeResult(PassClaimResult r) => r.Kind switch
    {
        PassRewardKind.Coins => $"Lv{r.Level} {r.Track} 轨 → +{r.Coins} 金币",
        PassRewardKind.Item when r.Item is not null && r.OverflowCoins > 0 =>
            $"Lv{r.Level} {r.Track} 轨 → {r.Item.DisplayName}（超上限折算 +{r.OverflowCoins} 金币）",
        PassRewardKind.Item when r.Item is not null && r.WasDuplicate =>
            $"Lv{r.Level} {r.Track} 轨 → {r.Item.DisplayName}（重复）",
        PassRewardKind.Item when r.Item is not null =>
            $"Lv{r.Level} {r.Track} 轨 → {r.Item.DisplayName}（新）",
        _ => $"Lv{r.Level} {r.Track} 轨",
    };

    /// <summary>由累计 XP 推导等级（1~maxLevel 封顶；纯查表无副作用）。</summary>
    private static int LevelForXp(SeasonConfig season, long xp)
    {
        int level = 1;
        while (level < season.MaxLevel && xp >= season.ThresholdForLevel(level + 1))
        {
            level++;
        }

        return level;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().Date);
}
