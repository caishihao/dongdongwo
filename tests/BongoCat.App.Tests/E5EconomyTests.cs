using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// E5 经济扩展测试（epics E5 交接单 §3/§4）：金币支出路径（TrySpendCoins 记账不可为负）、
/// 金币买箱（不足拒绝/扣币/复用开箱链路）、宝箱保底（计数/触发/重置）、
/// 含保底的三类宝箱期望回收率仍 &lt; 100%（GDD-02 §6.2 红线不被保底击穿）。
/// </summary>
public class E5EconomyTests
{
    private readonly FakeSaveService _save = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private static EconomyConfig MakeConfig(
        string chargeChestId = "wooden",
        IReadOnlyList<ChestRule>? chests = null,
        PityRule? pity = null)
        => new()
        {
            Keystroke = new KeystrokeRule(250, 500),
            Drip = new DripRule(120),
            IdleDrop = new IdleDropRule(15, 0.5, 0.3, 0.2, 15, 35, DefaultQualityWeights),
            Chests = chests ?? RealChests,
            Salvage = DefaultSalvage,
            DuplicateConversionRate = 0.60,
            ChestCharge = new ChestChargeRule(1, chargeChestId),
            DropBucket = new DropBucketRule(24, 40),
            // 测试用 wooden 箱验证保底机制 → 排除表清空（生产默认排除 wooden，见红线测试）
            Pity = pity ?? new PityRule(15, ItemQuality.Epic, Array.Empty<string>()),
            InventoryLimit = new InventoryLimitRule(9),
        };

    private static readonly IReadOnlyDictionary<ItemQuality, double> DefaultQualityWeights =
        new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 1.0, // 测试用全 common，隔离保底与品质 roll
        };

    private static readonly IReadOnlyList<ChestRule> RealChests =
    [
        new ChestRule("wooden", 150, 2, new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.55,
            [ItemQuality.Uncommon] = 0.28,
            [ItemQuality.Rare] = 0.13,
            [ItemQuality.Epic] = 0.035,
            [ItemQuality.Legendary] = 0.005,
        }),
        new ChestRule("silver", 600, 2, new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.10,
            [ItemQuality.Uncommon] = 0.35,
            [ItemQuality.Rare] = 0.35,
            [ItemQuality.Epic] = 0.15,
            [ItemQuality.Legendary] = 0.05,
        }),
        new ChestRule("gold", 2400, 3, new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Uncommon] = 0.10,
            [ItemQuality.Rare] = 0.35,
            [ItemQuality.Epic] = 0.40,
            [ItemQuality.Legendary] = 0.15,
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

    private InventoryService? _inventory;

    private EconomyService CreateService(EconomyConfig config, int seed = 42)
    {
        _inventory = new InventoryService(_save, _catalog, config);
        return new EconomyService(_save, _inventory, config, new Random(seed), _time);
    }

    // ── 金币支出（E5：记账不可为负）──────────────────────────────────────

    [Fact]
    public void 支出_余额不足_拒绝且零状态变化()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 100;

        Assert.False(svc.TrySpendCoins(150, CoinSource.Purchase));

        Assert.Equal(100, svc.Coins); // 绝不变负
        Assert.Empty(_coinSpent);
    }

    private readonly List<(CoinSource Source, long Amount)> _coinSpent = [];

    [Fact]
    public void 支出_成功_扣款并广播CoinsSpent事件()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 500;
        svc.CoinsSpent += (source, amount) => _coinSpent.Add((source, amount));

        Assert.True(svc.TrySpendCoins(150, CoinSource.Purchase));

        Assert.Equal(350, svc.Coins);
        Assert.Equal([(CoinSource.Purchase, 150)], _coinSpent); // 来源审计
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void 支出_非正数额_拒绝(long amount)
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 1000;

        Assert.False(svc.TrySpendCoins(amount, CoinSource.Purchase));
        Assert.Equal(1000, svc.Coins);
    }

    // ── 金币买箱（E5：扣币走支出路径 + 复用开箱链路）──────────────────────

    [Fact]
    public void 买箱_金币不足_拒绝且零状态变化()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 149;

        Assert.False(svc.TryBuyChest("wooden", out IReadOnlyList<ChestOpenResult> results));

        Assert.Empty(results);
        Assert.Equal(149, svc.Coins);
        Assert.Empty(_inventory!.Owned); // 零入库
    }

    [Fact]
    public void 买箱_未知箱id_拒绝()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 10000;

        Assert.False(svc.TryBuyChest("diamond", out _));
        Assert.Equal(10000, svc.Coins);
    }

    [Fact]
    public void 买箱_成功_扣币并开出物品入库()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 1000;
        svc.CoinsSpent += (source, amount) => _coinSpent.Add((source, amount));

        Assert.True(svc.TryBuyChest("wooden", out IReadOnlyList<ChestOpenResult> results));

        Assert.Equal(2, results.Count);                                     // wooden = 2 件
        Assert.Equal(1000 - 150, svc.Coins);                                // 扣币（wooden 150）
        Assert.Equal([(CoinSource.Purchase, 150)], _coinSpent);
        Assert.Equal(results.Count, _inventory!.Owned.Values.Sum());        // 全部入库（初始空库存）
    }

    [Fact]
    public void 买箱_不影响免费充能_充能与买箱互相独立()
    {
        EconomyService svc = CreateService(MakeConfig());
        svc.Initialize();
        _save.Current.Coins = 10000;
        _save.Current.ChestChargeSeconds = 1; // 充能即满

        Assert.True(svc.TryBuyChest("wooden", out _));

        Assert.True(svc.CanOpenChest); // 买箱不消耗免费充能
        Assert.True(svc.TryOpenChest(out _));
        Assert.False(svc.CanOpenChest); // 免费开箱才消耗充能
    }

    // ── 宝箱保底（E4 遗留项并入，GDD-02 §5）──────────────────────────────

    [Fact]
    public void 开箱未出epic加_保底计数递增()
    {
        // 全 common 权重 → 必无 epic+，每箱 +1
        EconomyConfig cfg = MakeConfig(chests:
        [
            new ChestRule("wooden", 150, 2, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 }),
        ]);
        EconomyService svc = CreateService(cfg);
        svc.Initialize();
        _save.Current.ChestChargeSeconds = 1;

        Assert.True(svc.TryOpenChest(out _));

        Assert.Equal(1, _save.Current.ChestPityCount);
    }

    [Fact]
    public void 连续14箱无epic加_第15箱必出epic加_计数清零()
    {
        EconomyConfig cfg = MakeConfig(chests:
        [
            new ChestRule("wooden", 150, 1, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 }),
        ]);
        EconomyService svc = CreateService(cfg);
        svc.Initialize();
        _save.Current.ChestPityCount = 14;   // 已连续 14 箱无 epic+（GDD-02 §5：第 15 箱触发）
        _save.Current.ChestChargeSeconds = 1;

        Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));

        Assert.Equal(ItemQuality.Epic, results[0].Item.Quality); // 必出 epic（minQuality）
        Assert.Equal(0, _save.Current.ChestPityCount);           // 触发即清零
    }

    [Fact]
    public void 保底计数_未触发期间递增到阈值减一封顶()
    {
        EconomyConfig cfg = MakeConfig(chests:
        [
            new ChestRule("wooden", 150, 1, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 }),
        ]);
        EconomyService svc = CreateService(cfg);
        svc.Initialize();
        _save.Current.ChestPityCount = 5;

        for (int i = 0; i < 9; i++) // 9 箱：5+9=14 = threshold-1（再开一箱即触发，由触发测试覆盖）
        {
            _save.Current.ChestChargeSeconds = 1;
            Assert.True(svc.TryOpenChest(out _));
        }

        Assert.Equal(14, _save.Current.ChestPityCount); // 封顶 threshold-1，不越过
    }

    [Fact]
    public void 开箱出epic加_计数清零()
    {
        EconomyConfig cfg = MakeConfig(chests:
        [
            new ChestRule("wooden", 150, 1, new Dictionary<ItemQuality, double>
            {
                [ItemQuality.Common] = 0.0,
                [ItemQuality.Epic] = 1.0,
            }),
        ]);
        EconomyService svc = CreateService(cfg);
        svc.Initialize();
        _save.Current.ChestPityCount = 9;
        _save.Current.ChestChargeSeconds = 1;

        Assert.True(svc.TryOpenChest(out _));

        Assert.Equal(0, _save.Current.ChestPityCount); // 出 epic+ 即重置
    }

    [Fact]
    public void 保底触发_品阶不低于minQuality()
    {
        // minQuality=legendary 的保底：第 15 箱必出 legendary+
        EconomyConfig cfg = MakeConfig(
            chests: [new ChestRule("wooden", 150, 1, new Dictionary<ItemQuality, double> { [ItemQuality.Common] = 1.0 })],
            pity: new PityRule(15, ItemQuality.Legendary, Array.Empty<string>()));
        EconomyService svc = CreateService(cfg);
        svc.Initialize();
        _save.Current.ChestPityCount = 14;
        _save.Current.ChestChargeSeconds = 1;

        Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));

        Assert.Equal(ItemQuality.Legendary, results[0].Item.Quality);
    }

    // ── 含保底的期望回收率红线（保底不得击穿"无套利循环"）────────────────
    // GDD-02 §5：保底只对银/金箱（excludeChestIds 默认排除 wooden——wooden 售价低，
    // 保底会抬升回收率越线形成买箱套利，模拟数据验证了这一点）。

    [Theory]
    [InlineData("silver", 600)]
    [InlineData("gold", 2400)]
    public void 含保底的宝箱期望回收率_仍低于100红线_固定种子模拟验证(string chestId, int price)
    {
        EconomyConfig real = EconomyConfigLoader.LoadFromAssembly(); // 真实数值 + 真实保底 15
        EconomyConfig cfg = new()
        {
            Keystroke = real.Keystroke,
            Drip = real.Drip,
            IdleDrop = real.IdleDrop,
            Chests = real.Chests,
            Salvage = real.Salvage,
            DuplicateConversionRate = real.DuplicateConversionRate,
            ChestCharge = new ChestChargeRule(1, chestId),
            DropBucket = real.DropBucket,
            Pity = real.Pity,
        };

        EconomyService svc = CreateService(cfg, seed: 7);

        const int opens = 20000;
        long totalValue = 0;
        for (int i = 0; i < opens; i++)
        {
            _save.Current.ChestChargeSeconds = 1;
            Assert.True(svc.TryOpenChest(out IReadOnlyList<ChestOpenResult> results));
            foreach (ChestOpenResult r in results)
            {
                totalValue += cfg.Salvage[r.Item.Quality];
            }
        }

        double ratio = totalValue / (double)(opens * price);
        Assert.True(ratio < 1.0, $"{chestId} 含保底期望回收率 {ratio:P1} 必须 < 100%（无买箱套利循环）");
    }
}
