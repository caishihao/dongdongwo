using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 经济服务测试（epics E4-S1/S2/S4 + GDD-02）：敲击产币与日上限、跨天重置、时钟回拨防御、
/// 滴灌计时、掉落 roll 权重分布（固定种子统计断言）、掉落桶上限与过期恢复、宝箱充能/开箱/期望回收率红线。
/// 时间用 FakeTimeProvider、随机用固定种子 Random——全部可复现。
/// </summary>
public class EconomyServiceTests
{
    private readonly FakeSaveService _save = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private EconomyService CreateService(EconomyConfig config, int seed = 42)
        => new(_save, new InventoryService(_save, _catalog), config, new Random(seed), _time);

    /// <summary>测试用配置工厂：默认值 = GDD-02 数值，按用例覆盖。</summary>
    private static EconomyConfig MakeConfig(
        int coinsPer = 250,
        int dailyCap = 500,
        double dripSeconds = 120,
        int dailyBonus = 0,
        double intervalMinutes = 1,
        double nothing = 0.5,
        double coinPack = 0.3,
        double item = 0.2,
        int packMin = 15,
        int packMax = 35,
        IReadOnlyDictionary<ItemQuality, double>? dropWeights = null,
        double chargeSeconds = 7200,
        string chargeChestId = "wooden",
        IReadOnlyList<ChestRule>? chests = null,
        double bucketWindowHours = 24,
        int bucketMaxItems = 40)
        => new()
        {
            Keystroke = new KeystrokeRule(coinsPer, dailyCap),
            Drip = new DripRule(dripSeconds),
            DailyLoginBonus = dailyBonus,
            IdleDrop = new IdleDropRule(
                intervalMinutes, nothing, coinPack, item, packMin, packMax,
                dropWeights ?? DefaultQualityWeights),
            Chests = chests ?? DefaultChests,
            Salvage = DefaultSalvage,
            DuplicateConversionRate = 0.60,
            ChestCharge = new ChestChargeRule(chargeSeconds, chargeChestId),
            DropBucket = new DropBucketRule(bucketWindowHours, bucketMaxItems),
        };

    private static readonly IReadOnlyDictionary<ItemQuality, double> DefaultQualityWeights =
        new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.70,
            [ItemQuality.Uncommon] = 0.22,
            [ItemQuality.Rare] = 0.07,
            [ItemQuality.Epic] = 0.009,
            [ItemQuality.Legendary] = 0.001,
        };

    private static readonly IReadOnlyList<ChestRule> DefaultChests =
    [
        new ChestRule("wooden", 150, 2, new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.55,
            [ItemQuality.Uncommon] = 0.28,
            [ItemQuality.Rare] = 0.13,
            [ItemQuality.Epic] = 0.035,
            [ItemQuality.Legendary] = 0.005,
        }),
    ];

    private static readonly IReadOnlyDictionary<ItemQuality, long> DefaultSalvage =
        new Dictionary<ItemQuality, long>
        {
            [ItemQuality.Common] = 10,
            [ItemQuality.Uncommon] = 40,
            [ItemQuality.Rare] = 150,
            [ItemQuality.Epic] = 600,
            [ItemQuality.Legendary] = 2500,
        };

    // ── 每日登录奖励 / 跨天 / 时钟回拨 ───────────────────────────────────

    [Fact]
    public void 每日首次启动_发登录奖励并记日期_同日重复初始化不重发()
    {
        EconomyService svc = CreateService(MakeConfig(dailyBonus: 50));

        svc.Initialize();
        Assert.Equal(50, svc.Coins);
        Assert.Equal("2025-01-01", _save.Current.DailyCoinDate);

        svc.Initialize(); // 同日重复初始化（幂等路径）
        Assert.Equal(50, svc.Coins); // 绝不重复发
    }

    [Fact]
    public void 跨天重置_敲击计数清零_再发每日奖励()
    {
        EconomyService svc = CreateService(MakeConfig(dailyBonus: 50));
        svc.Initialize();

        for (int i = 0; i < 250; i++)
        {
            svc.OnValidTap(); // 250 次 → +1 币
        }

        Assert.Equal(51, svc.Coins);

        _time.AdvanceMilliseconds(TimeSpan.FromHours(24).TotalMilliseconds); // 跨到 2025-01-02
        svc.OnValidTap(); // 触发跨天重置

        Assert.Equal(101, svc.Coins); // +50 每日奖励（敲击计数清零后本次敲击不产币）
        Assert.Equal(1, _save.Current.DailyTapCount); // 重置后本 tap 计 1
        Assert.Equal("2025-01-02", _save.Current.DailyCoinDate);

        for (int i = 1; i < 250; i++)
        {
            svc.OnValidTap(); // 再凑满 250 次（本日重起算）
        }

        Assert.Equal(102, svc.Coins); // 新的一天重新产币（旧计数不复用）
    }

    [Fact]
    public void 系统时钟回拨_不重置计数不发奖励()
    {
        EconomyService svc = CreateService(MakeConfig(dailyBonus: 50));
        svc.Initialize(); // 2025-01-01
        Assert.Equal(50, svc.Coins);

        _time.AdvanceMilliseconds(TimeSpan.FromHours(24).TotalMilliseconds);
        svc.OnValidTap(); // 跨天 → +50，日期 = 2025-01-02
        Assert.Equal(100, svc.Coins);

        _time.RewindMilliseconds(TimeSpan.FromDays(10).TotalMilliseconds); // 回拨到 2024-12-23
        svc.OnValidTap();

        Assert.Equal(100, svc.Coins);        // 不重发奖励（防刷）
        Assert.Equal("2025-01-02", _save.Current.DailyCoinDate); // 日期不被回写
    }

    // ── 敲击产币 / 日上限 ────────────────────────────────────────────────

    [Fact]
    public void 敲击产币_每CoinsPer次1币()
    {
        EconomyService svc = CreateService(MakeConfig(coinsPer: 250, nothing: 1.0, coinPack: 0, item: 0));
        svc.Initialize();

        for (int i = 0; i < 250; i++)
        {
            svc.OnValidTap();
        }

        Assert.Equal(1, svc.Coins);

        for (int i = 0; i < 249; i++)
        {
            svc.OnValidTap();
        }

        Assert.Equal(1, svc.Coins); // 499 次：未到下一个 250 边界

        svc.OnValidTap();
        Assert.Equal(2, svc.Coins); // 500 次：+1
        Assert.Equal(500, _save.Current.DailyTapCount);
    }

    [Fact]
    public void 敲击产币_日上限封顶_达上限后敲击不再产币()
    {
        EconomyService svc = CreateService(MakeConfig(coinsPer: 10, dailyCap: 3, nothing: 1.0, coinPack: 0, item: 0));
        svc.Initialize();

        for (int i = 0; i < 200; i++)
        {
            svc.OnValidTap(); // 200 次，名义 20 币，封顶 3
        }

        Assert.Equal(3, svc.Coins);
        Assert.Equal(200, _save.Current.DailyTapCount); // 敲击计数照常累计（敲击不受伤，只是不产币）
    }

    // ── 在线滴灌 ─────────────────────────────────────────────────────────

    [Fact]
    public void 滴灌_每SecondsPerCoin秒1币_余数跨Tick续接()
    {
        EconomyService svc = CreateService(MakeConfig(dripSeconds: 100, nothing: 1.0, coinPack: 0, item: 0));
        svc.Initialize();

        svc.Tick(); // 首个 Tick 只起算（elapsed = 0）
        Assert.Equal(0, svc.Coins);

        _time.AdvanceMilliseconds(100_000);
        svc.Tick();
        Assert.Equal(1, svc.Coins);

        _time.AdvanceMilliseconds(150_000); // 累计 150s → 1 币 + 余 50s
        svc.Tick();
        Assert.Equal(2, svc.Coins);
        Assert.Equal(50, _save.Current.IdleCoinAccumulator, 1);

        _time.AdvanceMilliseconds(49_000);
        svc.Tick();
        Assert.Equal(2, svc.Coins); // 99s 不足 1 币

        _time.AdvanceMilliseconds(1_000);
        svc.Tick();
        Assert.Equal(3, svc.Coins); // 余数续接后到 100s → +1
    }

    [Fact]
    public void 心跳巨间隔_待机恢复不补算()
    {
        EconomyService svc = CreateService(MakeConfig(dripSeconds: 10, nothing: 1.0, coinPack: 0, item: 0));
        svc.Initialize();

        svc.Tick();
        _time.AdvanceMilliseconds(TimeSpan.FromMinutes(30).TotalMilliseconds); // > 10 分钟
        svc.Tick();

        Assert.Equal(0, svc.Coins); // 本段不补算（GDD-02 §3）
        Assert.Equal(0, _save.Current.IdleCoinAccumulator, 1);
    }

    // ── 挂机掉落 ─────────────────────────────────────────────────────────

    [Fact]
    public void 掉落_金币包金额落在配置区间()
    {
        EconomyService svc = CreateService(MakeConfig(
            nothing: 0, coinPack: 1.0, item: 0, packMin: 15, packMax: 35));
        var drops = new List<DropEvent>();
        svc.DropOccurred += drops.Add;
        svc.Initialize();

        for (int i = 0; i < 100; i++)
        {
            _time.AdvanceMilliseconds(TimeSpan.FromMinutes(1).TotalMilliseconds);
            svc.Tick();
        }

        Assert.Equal(100, drops.Count);
        Assert.All(drops, d => Assert.Equal(DropKind.CoinPack, d.Kind));
        Assert.All(drops, d => Assert.InRange(d.Coins, 15, 35));
        Assert.InRange(svc.Coins, 1500, 3500); // 总额必然落在区间和的范围
    }

    [Fact]
    public void 掉落_物品品质分布符合权重表_固定种子统计断言()
    {
        EconomyService svc = CreateService(MakeConfig(
            nothing: 0, coinPack: 0, item: 1.0,
            dropWeights: new Dictionary<ItemQuality, double>
            {
                [ItemQuality.Common] = 0.5,
                [ItemQuality.Uncommon] = 0.3,
                [ItemQuality.Epic] = 0.2,
            },
            bucketMaxItems: 1_000_000)); // 桶上限拉满，专注分布统计
        var drops = new List<DropEvent>();
        svc.DropOccurred += drops.Add;
        svc.Initialize();

        const int rolls = 4000;
        for (int i = 0; i < rolls; i++)
        {
            _time.AdvanceMilliseconds(TimeSpan.FromMinutes(1).TotalMilliseconds);
            svc.Tick();
        }

        Assert.Equal(rolls, drops.Count);
        Assert.All(drops, d => Assert.Equal(DropKind.Item, d.Kind));

        double common = drops.Count(d => d.Item!.Quality == ItemQuality.Common);
        double uncommon = drops.Count(d => d.Item!.Quality == ItemQuality.Uncommon);
        double epic = drops.Count(d => d.Item!.Quality == ItemQuality.Epic);

        // 期望 2000/1200/800；4σ 容差（固定种子实际可复现，容差为防种子敏感性）
        Assert.InRange(common, 1800, 2200);
        Assert.InRange(uncommon, 1050, 1350);
        Assert.InRange(epic, 650, 950);
    }

    [Fact]
    public void 掉落桶_24h窗口超上限暂停_过期后自动恢复()
    {
        EconomyService svc = CreateService(MakeConfig(
            nothing: 0, coinPack: 0, item: 1.0, bucketMaxItems: 2, bucketWindowHours: 24));
        var drops = new List<DropEvent>();
        svc.DropOccurred += drops.Add;
        svc.Initialize();

        // 第 1、2 次掉落正常入库；第 3 次被桶上限拦截
        for (int i = 0; i < 2; i++)
        {
            _time.AdvanceMilliseconds(TimeSpan.FromMinutes(1).TotalMilliseconds);
            svc.Tick();
        }

        Assert.Equal(2, drops.Count);
        Assert.All(drops.Take(2), d => Assert.Equal(DropKind.Item, d.Kind));
        Assert.Equal(2, _save.Current.DropBucket.Count);

        _time.AdvanceMilliseconds(TimeSpan.FromMinutes(1).TotalMilliseconds);
        svc.Tick();
        Assert.Equal(3, drops.Count);
        Assert.Equal(DropKind.Suppressed, drops[2].Kind);      // 暂停
        Assert.Equal(2, _save.Current.DropBucket.Count);       // 桶不超限
        Assert.Equal(2, _save.Current.OwnedItems.Values.Sum()); // 已入库 2 件（可能同 id 累加）

        // 推进 25h：最老条目过期 → 下次 roll 自动恢复
        _time.AdvanceMilliseconds(TimeSpan.FromHours(25).TotalMilliseconds);
        svc.Tick();

        Assert.Equal(4, drops.Count);
        Assert.Equal(DropKind.Item, drops[3].Kind);            // 恢复
        Assert.Single(_save.Current.DropBucket);               // 过期条目已清理
        Assert.Equal(3, _save.Current.OwnedItems.Values.Sum());
    }

    // ── 宝箱 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 宝箱_充能未满不能开_满后可开并消耗充能()
    {
        EconomyService svc = CreateService(MakeConfig(
            nothing: 1.0, coinPack: 0, item: 0,
            chargeSeconds: 100,
            chests: [new ChestRule("wooden", 150, 2, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 })]));
        svc.Initialize();

        _time.AdvanceMilliseconds(60_000);
        svc.Tick();
        Assert.False(svc.CanOpenChest);
        Assert.False(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> none));
        Assert.Empty(none);

        _time.AdvanceMilliseconds(40_000);
        svc.Tick();
        Assert.True(svc.CanOpenChest);
        Assert.Equal(TimeSpan.Zero, svc.TimeToNextChest);

        Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));
        Assert.Equal(2, results.Count); // 木箱每箱 2 件
        Assert.All(results, r => Assert.Equal(ItemQuality.Common, r.Item.Quality));
        Assert.False(svc.CanOpenChest);  // 充能已消耗
        Assert.Equal(0, _save.Current.ChestChargeSeconds, 1);
        Assert.NotEqual(0, _save.Current.LastChestTicks);

        int totalGranted = _save.Current.OwnedItems.Values.Sum();
        Assert.Equal(2, totalGranted);
    }

    [Fact]
    public void 宝箱_重复物品自动折算金币()
    {
        // 预先拥有全部 common 物品 → 开箱必为重复 → 拆解价 10 × 0.6 = 6 金币
        _save.Current.OwnedItems["hat_dev_cap"] = 1;
        _save.Current.OwnedItems["skin_cream"] = 1;
        _save.Current.OwnedItems["bongos_wood"] = 1;

        EconomyService svc = CreateService(MakeConfig(
            nothing: 1.0, coinPack: 0, item: 0,
            chargeSeconds: 100,
            chests: [new ChestRule("wooden", 150, 1, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 })]));
        svc.Initialize();

        _time.AdvanceMilliseconds(100_000);
        svc.Tick();

        Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));
        ChestOpenResult granted = Assert.Single(results);
        Assert.True(granted.WasDuplicate);
        Assert.Equal(6, granted.DuplicateCoins);
        Assert.Equal(6, svc.Coins);
    }

    [Theory]
    [InlineData("wooden", 0.9293)]
    [InlineData("silver", 0.9417)]
    [InlineData("gold", 0.8396)]
    public void 宝箱期望回收率_三类均低于100红线_固定种子模拟验证(string chestId, double expected)
    {
        // 回收率红线（GDD-02 §6.2）：期望回收率 = Σ(权重×拆解价)×件数 ÷ 售价，必须 < 100%
        // E5：保底会把部分箱子顶成 epic+，抬高实际回收率——本测试先隔离保底（阈值拉满），
        // 锁定 GDD §6.2 纯权重表的期望值；"含保底仍 < 100%"红线由 E5 新增测试单独锁定。
        EconomyConfig real = EconomyConfigLoader.LoadFromAssembly();
        EconomyConfig cfg = new()
        {
            Keystroke = real.Keystroke,
            Drip = real.Drip,
            IdleDrop = real.IdleDrop,
            Chests = real.Chests,
            Salvage = real.Salvage,
            DuplicateConversionRate = real.DuplicateConversionRate,
            ChestCharge = new ChestChargeRule(1, chestId), // 充能即满，专注开箱分布
            DropBucket = real.DropBucket,
            Pity = new PityRule(int.MaxValue / 2, ItemQuality.Epic), // 隔离保底
        };

        var inventory = new InventoryService(_save, _catalog);
        EconomyService svc = new(_save, inventory, cfg, new Random(7), _time);

        const int opens = 20000;
        long totalValue = 0;
        for (int i = 0; i < opens; i++)
        {
            _save.Current.ChestChargeSeconds = 1; // 每次直接充满
            Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));
            foreach (ChestOpenResult r in results)
            {
                totalValue += cfg.Salvage[r.Item.Quality];
            }
        }

        double ratio = totalValue / (double)(opens * cfg.Chests.Single(c => c.Id == chestId).Price);
        Assert.InRange(ratio, expected - 0.04, expected + 0.04);
        Assert.True(ratio < 1.0, $"{chestId} 期望回收率 {ratio:P1} 必须 < 100%（无买箱套利循环）");
    }

    // ── 记账纪律 ─────────────────────────────────────────────────────────

    [Fact]
    public void 金币入账_来源审计事件逐笔触发()
    {
        EconomyService svc = CreateService(MakeConfig(
            dailyBonus: 50, coinsPer: 1, nothing: 1.0, coinPack: 0, item: 0));
        var ledger = new List<(CoinSource Source, long Amount)>();
        svc.CoinsChanged += (source, amount) => ledger.Add((source, amount));

        svc.Initialize();
        svc.OnValidTap();
        _time.AdvanceMilliseconds(120_000);
        svc.Tick();

        Assert.Equal(
            [(CoinSource.DailyLogin, 50L), (CoinSource.Tap, 1L), (CoinSource.Drip, 1L)],
            ledger);
        Assert.Equal(52, svc.Coins);
    }
}
