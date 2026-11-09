using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.App.Pass;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 通行证服务测试（epics E6-S1/S2/S3）：XP 曲线推进、里程碑阈值边界、双轨领取幂等、
/// 高级轨 2500 解锁审计、赛季切换重置范围、每日任务自动入账、敲击 XP 日上限、
/// 时钟回拨冻结、满级封顶、真实嵌入赛季配置对账。
/// </summary>
public class PassServiceTests
{
    // 两个相邻 42 天赛季窗口（FakeTimeProvider 历元 2025-01-01 UTC；±8 时区下 0/24/44 天推进均落在窗口内）。
    internal const string Season1Json = """
        {
          "seasonId": "season_t1",
          "name": "测试赛季一",
          "startDate": "2024-12-01",
          "endDate": "2025-02-12",
          "maxLevel": 5,
          "xpCurve": { "base": 100, "step": 10 },
          "premiumUnlockCoins": 2500,
          "dailyQuestPool": [
            { "id": "open_chests_1", "type": "openChest", "target": 1, "xp": 30 },
            { "id": "exchange_1",    "type": "exchange", "target": 1, "xp": 25 }
          ],
          "rewards": [
            { "level": 1, "track": "free",    "type": "coins", "amount": 100 },
            { "level": 1, "track": "premium", "type": "item",  "itemId": "hat_dev_cap" },
            { "level": 2, "track": "free",    "type": "item",  "itemId": "skin_orange" },
            { "level": 2, "track": "premium", "type": "coins", "amount": 250 },
            { "level": 5, "track": "free",    "type": "coins", "amount": 500 },
            { "level": 5, "track": "premium", "type": "item",  "itemId": "bongos_gold" }
          ]
        }
        """;

    internal const string Season2Json = """
        {
          "seasonId": "season_t2",
          "name": "测试赛季二",
          "startDate": "2025-02-13",
          "endDate": "2025-03-26",
          "maxLevel": 5,
          "xpCurve": { "base": 100, "step": 10 },
          "premiumUnlockCoins": 2500,
          "rewards": [
            { "level": 1, "track": "free", "type": "coins", "amount": 120 },
            { "level": 1, "track": "premium", "type": "item", "itemId": "hat_beret" }
          ]
        }
        """;

    /// <summary>曲线阈值（base=100 step=10）：L1=0 / L2=100 / L3=210 / L4=330 / L5=460。</summary>

    internal static (PassService Pass, EconomyService Economy, InventoryService Inventory,
        FakeSaveService Save, FakeTimeProvider Time) CreatePass(
        params string[] seasonJsons)
    {
        var save = new FakeSaveService();
        var time = new FakeTimeProvider();
        CosmeticCatalog catalog = ItemCatalogLoader.LoadFromAssembly();
        var inventory = new InventoryService(save, catalog);
        var economy = new EconomyService(save, inventory, new EconomyConfig(), new Random(7), time);
        var seasons = seasonJsons.Select(SeasonConfig.ParseJson).ToList();
        var pass = new PassService(save, seasons, inventory, economy, time);
        economy.ChestOpened += pass.NotifyChestOpened; // 与 App.OnStartup 同链路
        economy.Initialize();
        pass.Initialize();
        return (pass, economy, inventory, save, time);
    }

    private static void TapN(PassService pass, EconomyService economy, int n)
    {
        for (int i = 0; i < n; i++)
        {
            economy.OnValidTap(); // 订阅顺序与 App 一致：经济先计数，通行证后取数
            pass.OnValidTap();
        }
    }

    // ── 赛季装载 ─────────────────────────────────────────────────────────

    [Fact]
    public void 初始化_装载当前赛季_存档SeasonId写入()
    {
        (PassService pass, _, _, FakeSaveService save, _) = CreatePass(Season1Json, Season2Json);

        Assert.NotNull(pass.CurrentSeason);
        Assert.Equal("season_t1", pass.CurrentSeason!.SeasonId);
        Assert.Equal("season_t1", save.Current.SeasonId);
        Assert.Equal(0, save.Current.SeasonXP);
        Assert.True(pass.DaysRemaining > 0);
    }

    // ── XP 曲线推进 / 里程碑事件（E6-S2）─────────────────────────────────

    [Fact]
    public void 敲击XP_每1000键10点_跨阈值升级并广播里程碑()
    {
        (PassService pass, EconomyService economy, _, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        int? readyLevel = null;
        pass.MilestoneReady += lv => readyLevel = lv;

        TapN(pass, economy, 9999);
        Assert.Equal(90, save.Current.SeasonXP);   // 9 步 × 10 XP
        Assert.Equal(1, pass.Level);               // 未到 L2 阈值 100

        TapN(pass, economy, 1);                    // 第 10000 键：第 10 步 → 恰好 100 XP
        Assert.Equal(100, save.Current.SeasonXP);
        Assert.Equal(2, pass.Level);               // threshold(2)=100 ≤ 100
        Assert.Equal(2, readyLevel);               // 升级里程碑气泡事件
        Assert.Equal(0, pass.XpIntoLevel);
        Assert.Equal(110, pass.XpForNextLevel);    // L2→L3 需 110
    }

    [Fact]
    public void 敲击XP_每日上限300_任务XP不占额度()
    {
        (PassService pass, EconomyService economy, _, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        TapN(pass, economy, 1000);
        Assert.Equal(10, save.Current.SeasonXP);
        TapN(pass, economy, 9000);
        Assert.Equal(100, save.Current.SeasonXP);
        TapN(pass, economy, 10000);
        Assert.Equal(200, save.Current.SeasonXP);
        TapN(pass, economy, 10000);
        Assert.Equal(300, save.Current.SeasonXP);
        TapN(pass, economy, 10000); // 超出部分封顶（40 步 = 400 XP → 封顶 300）

        Assert.Equal(300, save.Current.SeasonXP);
        Assert.Equal(PassService.DailyTapXpCap, save.Current.DailyXpEarned);

        // 任务 XP 不占敲击额度：开箱任务 +30 照常入账
        pass.NotifyChestOpened();
        Assert.Equal(330, save.Current.SeasonXP);
    }

    [Fact]
    public void 满级后_XP停止累计()
    {
        (PassService pass, EconomyService economy, _, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        save.Current.SeasonXP = 460; // threshold(5)=460 → 满级（maxLevel=5）
        Assert.Equal(5, pass.Level);

        TapN(pass, economy, 1000);
        Assert.Equal(460, save.Current.SeasonXP); // 溢出 XP 不入账（转金币汇率未定义，记开放问题）
        Assert.Equal(5, pass.Level);
        Assert.Equal(0, pass.XpForNextLevel);
    }

    // ── 里程碑阈值边界 / 双轨领取幂等（E6-S3）────────────────────────────

    [Fact]
    public void 阈值边界_差1XP不可领_恰好达成可领()
    {
        (PassService pass, _, _, FakeSaveService save, _) = CreatePass(Season1Json, Season2Json);

        save.Current.SeasonXP = 99;
        Assert.False(pass.IsMilestoneReached(2));
        Assert.False(pass.TryClaim(2, PassTrack.Free, out _, out string? reason1));
        Assert.Contains("未达成", reason1);

        save.Current.SeasonXP = 100;
        Assert.True(pass.IsMilestoneReached(2));
        Assert.Equal(2, pass.Level);
        Assert.True(pass.TryClaim(2, PassTrack.Free, out PassClaimResult result, out _));
        Assert.Equal(PassRewardKind.Item, result.Kind);
        Assert.Equal("skin_orange", result.Item!.Id);
        Assert.False(result.WasDuplicate);
    }

    [Fact]
    public void 双轨领取_幂等拒绝_记录含轨别()
    {
        (PassService pass, EconomyService economy, _, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        save.Current.SeasonXP = 100;
        Assert.True(pass.TryClaim(1, PassTrack.Free, out PassClaimResult first, out _));
        Assert.Equal(100, first.Coins); // 金币奖励全额入账
        Assert.Equal(100 + 50, economy.Coins); // 经济层初始化每日登录奖励 50 + 奖励 100

        Assert.False(pass.TryClaim(1, PassTrack.Free, out _, out string? reason)); // 幂等拒绝
        Assert.Contains("已领取", reason);
        Assert.Equal(150, economy.Coins); // 拒绝路径零状态变化

        // 领取记录形如 "season_t1:1:free"（含轨别）
        Assert.Contains("season_t1:1:free", save.Current.ClaimedMilestones);
        Assert.True(pass.IsClaimed(1, PassTrack.Free));
        Assert.False(pass.IsClaimed(1, PassTrack.Premium)); // 同级异轨不受影响
    }

    // ── 高级轨解锁（2500 金币，TrySpendCoins 审计）────────────────────────

    [Fact]
    public void 高级轨_未解锁领取拒绝_解锁扣币后可领()
    {
        (PassService pass, EconomyService economy, InventoryService inventory, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        save.Current.SeasonXP = 100;
        Assert.False(pass.TryClaim(1, PassTrack.Premium, out _, out string? locked));
        Assert.Contains("高级轨未解锁", locked);

        save.Current.Coins = 100; // 不足 2500
        Assert.False(pass.TryUnlockPremium(out string? poor));
        Assert.Contains("金币不足", poor);
        Assert.Equal(100, save.Current.Coins); // 拒绝路径零扣币
        Assert.False(save.Current.PremiumUnlocked);

        save.Current.Coins = 2500;
        CoinSource? spentSource = null;
        long? spentAmount = null;
        economy.CoinsSpent += (src, amount) =>
        {
            spentSource = src;
            spentAmount = amount;
        };

        Assert.True(pass.TryUnlockPremium(out _));
        Assert.True(save.Current.PremiumUnlocked);
        Assert.Equal(0, save.Current.Coins);
        Assert.Equal(CoinSource.PremiumUnlock, spentSource);
        Assert.Equal(2500, spentAmount);

        // 解锁后高级轨立即可领（L1 高级轨奖励 = hat_dev_cap）
        Assert.True(pass.TryClaim(1, PassTrack.Premium, out PassClaimResult claim, out _));
        Assert.Equal("hat_dev_cap", claim.Item!.Id);
        Assert.Equal(1, inventory.Owned["hat_dev_cap"]);
        Assert.Contains("season_t1:1:premium", save.Current.ClaimedMilestones);

        Assert.False(pass.TryUnlockPremium(out string? again)); // 赛季内一次性
        Assert.Contains("已解锁", again);
    }

    [Fact]
    public void 里程碑_未达阈值不冒泡_L1开局即可领_L2未达成拒绝()
    {
        (PassService pass, _, _, _, _) = CreatePass(Season1Json, Season2Json);

        bool fired = false;
        pass.MilestoneReady += _ => fired = true;

        // L1 阈值为 0（赛季开局即可领 1 级奖励，GDD-05 配置示例含 level 1 奖励）
        Assert.True(pass.TryClaim(1, PassTrack.Free, out PassClaimResult claim, out _));
        Assert.Equal(100, claim.Coins);

        // L2 未达成（XP=100 < ... 已达 L2？—— 领取 L1 只发奖不加 XP，SeasonXP 仍为 0）
        Assert.False(pass.TryClaim(2, PassTrack.Free, out _, out string? reason));
        Assert.Contains("未达成", reason);
        Assert.False(fired); // 领取路径不触发升级气泡（气泡只在 AddXp 跨级时广播）
    }

    // ── 赛季周期与重置（用户拍板：只清赛季字段，资产保留）────────────────

    [Fact]
    public void 赛季切换_重置赛季字段_资产与进度全部保留()
    {
        (PassService pass, _, _, FakeSaveService save, FakeTimeProvider time) =
            CreatePass(Season1Json, Season2Json);

        // 造一份"赛季中期"状态
        save.Current.SeasonXP = 123;
        save.Current.ClaimedMilestones.Add("season_t1:1:free");
        save.Current.PremiumUnlocked = true;
        save.Current.Coins = 1234;
        save.Current.OwnedItems["hat_dev_cap"] = 3;
        save.Current.DiscoveredItems.Add("skin_cow");
        save.Current.Presets.Add(new Core.Save.CosmeticPreset("日常", "hat_dev_cap", "skin_cream", "bongos_wood"));
        save.Current.ChestPityCount = 3;

        string? switchedTo = null;
        pass.SeasonSwitched += name => switchedTo = name;

        time.AdvanceMilliseconds(44 * 86_400_000.0); // 跨入 season_t2 窗口（2025-02-13 起）
        pass.Tick();

        Assert.Equal("season_t2", save.Current.SeasonId);
        Assert.Equal("测试赛季二", switchedTo);
        Assert.Equal(0, save.Current.SeasonXP);              // 重置
        Assert.Empty(save.Current.ClaimedMilestones);        // 重置
        Assert.False(save.Current.PremiumUnlocked);          // 重置
        Assert.Equal(1234, save.Current.Coins);              // 金币保留（用户拍板）
        Assert.Equal(3, save.Current.OwnedItems["hat_dev_cap"]); // 物品保留
        Assert.Equal(["skin_cow"], save.Current.DiscoveredItems); // 图鉴保留
        Assert.Single(save.Current.Presets);                 // 预设保留
        Assert.Equal(3, save.Current.ChestPityCount);        // 保底计数保留
        Assert.True(pass.TryClaim(1, PassTrack.Free, out PassClaimResult claim, out _)); // 新赛季 1 级立即可领
        Assert.Equal(120, claim.Coins);                      // season_t2 L1 免费轨 120 金币
    }

    [Fact]
    public void 无激活赛季_进度保留_领取与解锁拒绝()
    {
        (PassService pass, _, _, FakeSaveService save, FakeTimeProvider time) =
            CreatePass(Season1Json, Season2Json);

        save.Current.SeasonXP = 100;
        save.Current.SeasonId = "season_t1";

        time.AdvanceMilliseconds(120 * 86_400_000.0); // 跳出全部赛季窗口（season_t2 结束 2025-03-26 之后）
        pass.Tick();

        Assert.Null(pass.CurrentSeason); // UI 显示"暂无赛季"，进度保留不删（GDD-05 §7）
        Assert.Equal("season_t1", save.Current.SeasonId);
        Assert.Equal(100, save.Current.SeasonXP);
        Assert.False(pass.TryClaim(1, PassTrack.Free, out _, out string? claimReason));
        Assert.Contains("暂无进行中的赛季", claimReason);
        Assert.False(pass.TryUnlockPremium(out string? unlockReason));
        Assert.Contains("暂无进行中的赛季", unlockReason);
    }

    // ── 每日任务（自动入账，无需手动领，GDD-05 §7）────────────────────────

    [Fact]
    public void 每日任务_宝箱完成自动入账_同日不重复()
    {
        (PassService pass, _, _, FakeSaveService save, _) = CreatePass(Season1Json, Season2Json);

        pass.NotifyChestOpened();
        Assert.Equal(1, save.Current.DailyChestCount);
        Assert.Equal(30, save.Current.SeasonXP); // openChest 任务 +30 自动入账
        Assert.Contains("open_chests_1", save.Current.DailyCompletedQuests);

        pass.NotifyChestOpened(); // 第二箱：任务已完成，不再入账
        Assert.Equal(2, save.Current.DailyChestCount);
        Assert.Equal(30, save.Current.SeasonXP);
    }

    [Fact]
    public void 每日任务_合成完成自动入账()
    {
        (PassService pass, _, _, FakeSaveService save, _) = CreatePass(Season1Json, Season2Json);

        pass.NotifySynthesized();
        Assert.Equal(1, save.Current.DailySynthCount);
        Assert.Equal(25, save.Current.SeasonXP); // exchange 任务 +25 自动入账
    }

    [Fact]
    public void 经济开箱事件_转发每日任务计数()
    {
        (PassService pass, EconomyService economy, _, FakeSaveService save, _) =
            CreatePass(Season1Json, Season2Json);

        save.Current.ChestChargeSeconds = 7200; // 充满免费宝箱
        Assert.True(economy.TryOpenChest(out _));

        Assert.Equal(1, save.Current.DailyChestCount); // ChestOpened → NotifyChestOpened
        Assert.Equal(30, save.Current.SeasonXP);
    }

    // ── 每日重置 / 时钟回拨（与经济层同策略）─────────────────────────────

    [Fact]
    public void 跨天重置_任务与XP计数清零_赛季XP保留()
    {
        (PassService pass, _, _, FakeSaveService save, FakeTimeProvider time) =
            CreatePass(Season1Json, Season2Json);

        pass.NotifyChestOpened(); // DailyChestCount=1 + 30 XP
        pass.NotifySynthesized(); // DailySynthCount=1 + 25 XP
        Assert.Equal(55, save.Current.SeasonXP);

        time.AdvanceMilliseconds(24 * 86_400_000.0);
        pass.Tick();

        Assert.Equal(0, save.Current.DailyChestCount);
        Assert.Equal(0, save.Current.DailySynthCount);
        Assert.Empty(save.Current.DailyCompletedQuests);
        Assert.Equal(0, save.Current.DailyXpEarned);
        Assert.Equal(55, save.Current.SeasonXP); // 赛季 XP 是赛季累计，不随天重置
    }

    [Fact]
    public void 时钟回拨_XP结算冻结当日_不重置不奖励()
    {
        (PassService pass, _, _, FakeSaveService save, _) = CreatePass(Season1Json, Season2Json);

        pass.NotifyChestOpened();
        save.Current.DailyXpDate = "2025-06-01"; // 存档日期"晚于今天"（模拟回拨后的存档状态）

        pass.Tick();

        Assert.Equal(1, save.Current.DailyChestCount); // 不重置
        Assert.Equal(30, save.Current.SeasonXP);       // 不重复入账
        Assert.Equal("2025-06-01", save.Current.DailyXpDate);
    }

    // ── 真实嵌入赛季配置对账（与生产同一份数据）──────────────────────────

    [Fact]
    public void 真实赛季配置_两份相邻窗口_奖励引用全部合法()
    {
        CosmeticCatalog catalog = ItemCatalogLoader.LoadFromAssembly();
        IReadOnlyList<SeasonConfig> seasons = SeasonConfigLoader.LoadFromAssembly(catalog);

        Assert.Equal(2, seasons.Count); // 当前赛季 + 下赛季占位
        Assert.True(seasons[0].StartDate < seasons[1].StartDate); // 按 StartDate 升序
        Assert.Equal(seasons[0].EndDate.AddDays(1), seasons[1].StartDate); // 窗口无缝衔接
        foreach (SeasonConfig season in seasons)
        {
            Assert.Equal(2500, season.PremiumUnlockCoins); // 用户拍板价
            Assert.Equal(50, season.MaxLevel);             // GDD-05 §6 满级
            Assert.Equal(100, season.XpCurveBase);
            Assert.Equal(10, season.XpCurveStep);
            Assert.Equal(16660, season.ThresholdForLevel(50)); // 49 级累计：4900 + 11760
            foreach (Core.Config.PassReward reward in season.Rewards)
            {
                if (reward.Kind == PassRewardKind.Item)
                {
                    Assert.True(catalog.TryGet(reward.ItemId, out _), $"{season.SeasonId} 引用未知物品 {reward.ItemId}");
                }
            }
        }
    }
}
