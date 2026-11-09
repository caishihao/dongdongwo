using BongoCat.App.Economy;
using BongoCat.App.Exchange;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 兑换合成服务测试（epics E5-S1 + GDD-04 §5 规则全矩阵）：
/// 品质阶跃/同质校验/数量校验/装备中防御/legendary 三件不同/输出池排除输入/重复折算/审计事件/守恒，
/// 以及手动拆解（拆解价/unique 不可拆/装备中不可拆/图鉴不回灰）。
/// 自定义目录隔离品质池（真实 items.json unique 池为空），拆解价用真实 economy.json 数值。
/// </summary>
public class ExchangeServiceTests
{
    private readonly FakeSaveService _save = new();
    private readonly FakeTimeProvider _time = new();

    private static CosmeticItem Item(string id, ItemSlot slot, ItemQuality quality)
        => new() { Id = id, Slot = slot, Quality = quality, DisplayName = id, ArtKey = id };

    /// <summary>测试目录：帽子全品质阶跃链 + unique 终点；皮肤/鼓小池用于跨槽位与异质拒绝。</summary>
    private static readonly CosmeticCatalog TestCatalog = new(new[]
    {
        Item("h_c1", ItemSlot.Hat, ItemQuality.Common),
        Item("h_c2", ItemSlot.Hat, ItemQuality.Common),
        Item("h_c3", ItemSlot.Hat, ItemQuality.Common),
        Item("h_u1", ItemSlot.Hat, ItemQuality.Uncommon),
        Item("h_u2", ItemSlot.Hat, ItemQuality.Uncommon),
        Item("h_r1", ItemSlot.Hat, ItemQuality.Rare),
        Item("h_e1", ItemSlot.Hat, ItemQuality.Epic),
        Item("h_l1", ItemSlot.Hat, ItemQuality.Legendary),
        Item("h_l2", ItemSlot.Hat, ItemQuality.Legendary),
        Item("h_l3", ItemSlot.Hat, ItemQuality.Legendary),
        Item("h_unique", ItemSlot.Hat, ItemQuality.Unique),
        Item("s_c1", ItemSlot.Skin, ItemQuality.Common),
        Item("s_u1", ItemSlot.Skin, ItemQuality.Uncommon),
        Item("b_c1", ItemSlot.Bongos, ItemQuality.Common),
        Item("b_u1", ItemSlot.Bongos, ItemQuality.Uncommon),
    });

    /// <summary>拆解价 = GDD-02 §6.1 真实数值表。</summary>
    private static EconomyConfig MakeConfig(int maxPerItem = 9)
        => new()
        {
            Salvage = new Dictionary<ItemQuality, long>
            {
                [ItemQuality.Common] = 10,
                [ItemQuality.Uncommon] = 40,
                [ItemQuality.Rare] = 150,
                [ItemQuality.Epic] = 600,
                [ItemQuality.Legendary] = 2500,
            },
            DuplicateConversionRate = 0.60,
            InventoryLimit = new InventoryLimitRule(maxPerItem),
            Pity = new PityRule(15, ItemQuality.Epic),
        };

    private (InventoryService Inventory, EconomyService Economy, ExchangeService Exchange) CreateStack(
        EconomyConfig? config = null,
        Action<string, long>? overflowSink = null)
    {
        EconomyConfig cfg = config ?? MakeConfig();
        var inventory = new InventoryService(_save, TestCatalog, cfg, overflowSink);
        var economy = new EconomyService(_save, inventory, cfg, new Random(42), _time);
        var exchange = new ExchangeService(inventory, economy, cfg, new Random(7));
        _save.Current.InventoryInitialized = true; // 跳过新手装授予，按用例自行授予
        return (inventory, economy, exchange);
    }

    private void Grant(InventoryService inventory, string itemId, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            inventory.GrantItem(itemId);
        }
    }

    // ── 合成：成功路径 ───────────────────────────────────────────────────

    [Fact]
    public void 合成_common帽子三件_产出uncommon帽子_守恒负3正1()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1", 3);

        bool ok = exchange.TryExchange(["h_c1", "h_c1", "h_c1"], out ExchangeResult result, out string? reason);

        Assert.True(ok, reason);
        Assert.Equal(ItemQuality.Uncommon, result.OutputItem.Quality); // 品质阶跃
        Assert.Equal(ItemSlot.Hat, result.OutputItem.Slot);            // 结果槽位 = 输入槽位
        Assert.Equal(0, inventory.Owned.GetValueOrDefault("h_c1"));    // 3 件同名全部消耗
        Assert.Equal(1, inventory.Owned.Values.Sum(v => v));           // 守恒：-3 + 1
        Assert.False(result.WasDuplicate);                              // h_u* 未拥有 → 新物品
    }

    [Fact]
    public void 合成_三件不同common_输出排除输入id()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1");
        Grant(inventory, "h_c2");
        Grant(inventory, "h_c3");

        bool ok = exchange.TrySynthesizeAuto("h_c1", out ExchangeResult result, out string? reason);

        Assert.True(ok, reason);
        Assert.Equal(ItemQuality.Uncommon, result.OutputItem.Quality);
        Assert.DoesNotContain(result.OutputItem.Id, (IReadOnlyList<string>)result.InputIds); // 排除输入（GDD-04 §8-1）
        Assert.Equal(1, inventory.Owned.Values.Sum(v => v));
    }

    [Fact]
    public void 一键自动选取_优先消耗anchor重复件()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1", 2);
        Grant(inventory, "h_c2");

        Assert.True(exchange.CanSynthesize("h_c1", out _));
        Assert.True(exchange.TrySynthesizeAuto("h_c1", out ExchangeResult result, out string? reason));

        // 自动选取优先 anchor 重复件：h_c1 剩 0，h_c2 剩 0
        Assert.Equal(0, inventory.Owned.GetValueOrDefault("h_c1"));
        Assert.Equal(0, inventory.Owned.GetValueOrDefault("h_c2"));
        Assert.Equal(["h_c1", "h_c1", "h_c2"], result.InputIds);
    }

    [Fact]
    public void 合成输出为重复品_按拆解价60折算金币()
    {
        var (inventory, economy, exchange) = CreateStack();
        Grant(inventory, "h_c1", 3);
        Grant(inventory, "h_u1"); // 预置 uncommon 帽 → 合出它即重复

        bool ok = exchange.TryExchange(["h_c1", "h_c1", "h_c1"], out ExchangeResult result, out _);

        Assert.True(ok);
        Assert.True(result.WasDuplicate);
        Assert.Equal((long)Math.Floor(40 * 0.60), result.DuplicateCoins); // uncommon 拆解价 40 × 60%
        Assert.Equal(24, economy.Coins);                                  // 金币经经济层入账（审计来源 DuplicateConversion）
    }

    [Fact]
    public void 合成_金币零成本_无重复时余额不变()
    {
        var (inventory, economy, exchange) = CreateStack();
        Grant(inventory, "h_c1", 3);
        long before = economy.Coins;

        exchange.TryExchange(["h_c1", "h_c1", "h_c1"], out _, out _);

        Assert.Equal(before, economy.Coins); // GDD-04 §2：消耗物品即成本，零金币
    }

    [Fact]
    public void 合成成功_触发审计事件_输入输出全记录()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1", 3);

        (IReadOnlyList<string> Inputs, CosmeticItem Output)? audited = null;
        exchange.ExchangeOccurred += (inputs, output) => audited = (inputs, output);

        exchange.TryExchange(["h_c1", "h_c1", "h_c1"], out _, out _);

        Assert.NotNull(audited);
        Assert.Equal(3, audited!.Value.Inputs.Count);
        Assert.All(audited.Value.Inputs, id => Assert.Equal("h_c1", id));
        Assert.Equal(ItemQuality.Uncommon, audited.Value.Output.Quality);
    }

    [Fact]
    public void legendary三件不同_合成unique()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_l1");
        Grant(inventory, "h_l2");
        Grant(inventory, "h_l3");

        bool ok = exchange.TrySynthesizeAuto("h_l1", out ExchangeResult result, out string? reason);

        Assert.True(ok, reason);
        Assert.Equal(ItemQuality.Unique, result.OutputItem.Quality); // 唯一 unique 获取途径
    }

    // ── 合成：拒绝矩阵（失败路径零入库，epics E5-S2 验收）────────────────

    [Fact]
    public void 拒绝矩阵_异品质_异槽位_不足三件_库存不足()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1", 3);
        Grant(inventory, "s_c1", 2);
        Grant(inventory, "h_l1");

        // 异品质（2 common 帽 + 1 uncommon 帽）
        Grant(inventory, "h_u1");
        Assert.False(exchange.TryExchange(["h_c1", "h_c1", "h_u1"], out _, out string? reason1));
        Assert.Equal("合成物品必须同品质", reason1);

        // 异槽位（2 帽 + 1 皮肤）
        Assert.False(exchange.TryExchange(["h_c1", "h_c1", "s_c1"], out _, out string? reason2));
        Assert.Contains("同槽位", reason2);

        // 不足 3 件（参数层面）
        Assert.False(exchange.TryExchange(["h_c1", "h_c1"], out _, out string? reason3));
        Assert.Contains("3 件", reason3);

        // 库存不足（3 件但只拥有 1 件）
        Assert.False(exchange.TryExchange(["s_c1", "s_c1", "s_c1"], out _, out string? reason5));
        Assert.Contains("库存不足", reason5);

        // 全部拒绝路径零入库：库存总量不变
        Assert.Equal(3 + 2 + 1 + 1, inventory.Owned.Values.Sum(v => v));
    }

    [Fact]
    public void 装备中的物品_合成与拆解都拒绝_先卸下才能操作()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_c1");
        Grant(inventory, "h_c2", 2);
        inventory.Equip("h_c1");

        Assert.False(exchange.TryExchange(["h_c1", "h_c2", "h_c2"], out _, out string? reason));
        Assert.Contains("装备中", reason);
        Assert.False(exchange.TrySynthesizeAuto("h_c1", out _, out string? autoReason));
        Assert.Contains("装备中", autoReason);
        Assert.False(exchange.TrySalvage("h_c1", out _, out string? salvageReason));
        Assert.Contains("装备中", salvageReason);
    }

    [Fact]
    public void legendary三件同名_拒绝_需要三件不同()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_l1", 3);

        Assert.False(exchange.TryExchange(["h_l1", "h_l1", "h_l1"], out _, out string? reason));
        Assert.Contains("3 件不同", reason);
        Assert.Equal(3, inventory.Owned.Values.Sum(v => v)); // 零入库
    }

    [Fact]
    public void unique物品_不可再合成_不可拆解()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_unique");

        Assert.False(exchange.CanSynthesize("h_unique", out string? synthReason));
        Assert.Contains("顶阶", synthReason);
        Assert.False(exchange.TrySalvage("h_unique", out _, out string? salvageReason));
        Assert.Contains("顶阶", salvageReason);
        Assert.False(exchange.TryGetSalvageValue("h_unique", out _)); // 无拆解价
    }

    [Fact]
    public void 目标品质池为空_合成入口置灰并给原因()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "b_c1", 3); // common 鼓 → uncommon 鼓池有 b_u1：可行
        Grant(inventory, "b_u1", 3); // uncommon 鼓 → rare 鼓池为空：置灰

        Assert.True(exchange.CanSynthesize("b_c1", out _));
        Assert.False(exchange.CanSynthesize("b_u1", out string? reason));
        Assert.Contains("暂无", reason);
    }

    [Fact]
    public void 未知id与未拥有_拒绝()
    {
        var (_, _, exchange) = CreateStack();

        Assert.False(exchange.TryExchange(["nonexistent", "h_c1", "h_c2"], out _, out string? reason1));
        Assert.Contains("未知", reason1);
        Assert.False(exchange.CanSynthesize("h_c1", out string? reason2)); // 未拥有
        Assert.NotNull(reason2);
    }

    // ── 手动拆解 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("h_c1", 10)]    // common
    [InlineData("h_u1", 40)]    // uncommon
    [InlineData("h_r1", 150)]   // rare
    [InlineData("h_e1", 600)]   // epic
    [InlineData("h_l1", 2500)]  // legendary
    public void 拆解价_按GDD02数值表全额返还(string itemId, long expectedCoins)
    {
        var (inventory, economy, exchange) = CreateStack();
        Grant(inventory, itemId);
        long before = economy.Coins;

        Assert.True(exchange.TrySalvage(itemId, out long coins, out _));

        Assert.Equal(expectedCoins, coins);
        Assert.Equal(before + expectedCoins, economy.Coins);
        Assert.Equal(0, inventory.Owned.GetValueOrDefault(itemId)); // 归零移除
    }

    [Fact]
    public void 拆解后_图鉴不回灰_显示曾获得()
    {
        var (inventory, _, exchange) = CreateStack();
        Grant(inventory, "h_e1"); // epic 帽唯一件
        Assert.True(inventory.IsDiscovered("h_e1"));

        exchange.TrySalvage("h_e1", out _, out _);

        Assert.False(inventory.IsOwned("h_e1"));        // 库存已空
        Assert.True(inventory.IsDiscovered("h_e1"));    // 图鉴永久点亮（E5）
        CollectopediaEntry entry = inventory.GetCollectopedia().Single(e => e.Item.Id == "h_e1");
        Assert.False(entry.Owned);
        Assert.True(entry.Discovered);
        Assert.Equal("曾获得", entry.OwnedText);
        Assert.Equal("h_e1", entry.DisplayName);        // 不打码
    }

    [Fact]
    public void 拆解未拥有_拒绝()
    {
        var (_, _, exchange) = CreateStack();

        Assert.False(exchange.TrySalvage("h_c1", out _, out string? reason));
        Assert.Contains("未拥有", reason);
    }
}
