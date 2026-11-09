using BongoCat.App.Economy;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 库存服务 E5 扩展测试：GrantItem 图鉴永久点亮、单件上限自动折算（economy.json 参数化）、
/// RemoveOne（合成/拆解消耗路径）、ApplyOutfit（预设三槽一次切换）。
/// </summary>
public class E5InventoryTests
{
    private readonly FakeSaveService _save = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private static EconomyConfig MakeConfig(int maxPerItem = 9)
        => new() { InventoryLimit = new InventoryLimitRule(maxPerItem) };

    private InventoryService Create(EconomyConfig? config = null, Action<string, long>? sink = null)
    {
        var svc = new InventoryService(_save, _catalog, config, sink);
        _save.Current.InventoryInitialized = true; // 跳过新手装授予
        return svc;
    }

    // ── 图鉴永久点亮（DiscoveredItems）────────────────────────────────────

    [Fact]
    public void GrantItem_写入图鉴点亮_拆解归零后不回灰()
    {
        InventoryService svc = Create();

        svc.GrantItem("hat_santa");
        Assert.True(svc.IsDiscovered("hat_santa"));

        svc.RemoveOne("hat_santa"); // 模拟拆解消耗

        Assert.False(svc.IsOwned("hat_santa"));     // 库存空
        Assert.True(svc.IsDiscovered("hat_santa")); // 图鉴永久点亮（E5）
        CollectopediaEntry entry = svc.GetCollectopedia().Single(e => e.Item.Id == "hat_santa");
        Assert.True(entry.Discovered);
        Assert.Equal("曾获得", entry.OwnedText);
    }

    [Fact]
    public void GrantItem_幂等点亮_不重复写DiscoveredItems()
    {
        InventoryService svc = Create();

        svc.GrantItem("hat_dev_cap");
        svc.GrantItem("hat_dev_cap");

        Assert.Contains("hat_dev_cap", _save.Current.DiscoveredItems);
        Assert.Single(_save.Current.DiscoveredItems);
    }

    [Fact]
    public void 新手装授予_同样点亮图鉴()
    {
        _save.Current.InventoryInitialized = false;
        var svc = new InventoryService(_save, _catalog);

        svc.Initialize();

        Assert.All(InventoryService.StarterKitIds, id => Assert.Contains(id, _save.Current.DiscoveredItems));
        Assert.Equal(5, _save.Current.DiscoveredItems.Count);
    }

    // ── 单件上限自动折算（E3 遗留项并入）─────────────────────────────────

    [Fact]
    public void 超出单件上限_截断到上限_超出部分按拆解价折算()
    {
        long sinkCoins = 0;
        string? sinkItem = null;
        InventoryService svc = Create(MakeConfig(maxPerItem: 2), (id, coins) => { sinkItem = id; sinkCoins = coins; });

        svc.GrantItem("hat_dev_cap"); // 1
        ItemGrantResult r2 = svc.GrantItem("hat_dev_cap"); // 2 = 上限
        ItemGrantResult r3 = svc.GrantItem("hat_dev_cap"); // 第 3 件 → 超出 1 件

        Assert.Equal(2, r2.NewCount);
        Assert.Equal(0, r2.OverflowCoins);
        Assert.Equal(2, r3.NewCount);        // 封顶不突破
        Assert.Equal(10, r3.OverflowCoins);  // common 拆解价 10 × 超出 1 件
        Assert.Equal("hat_dev_cap", sinkItem);
        Assert.Equal(10, sinkCoins);         // 折算金额经 sink 交经济层记账
        Assert.Equal(2, svc.Owned["hat_dev_cap"]);
        Assert.Single(svc.Owned);
    }

    [Fact]
    public void 未配置上限约束_economy为null_不截断()
    {
        InventoryService svc = Create(); // economy = null（老测试/兜底路径）

        for (int i = 0; i < 15; i++)
        {
            svc.GrantItem("hat_dev_cap");
        }

        Assert.Equal(15, svc.Owned["hat_dev_cap"]);
    }

    // ── RemoveOne（合成消耗/拆解路径）────────────────────────────────────

    [Fact]
    public void RemoveOne_多件递减_归零移除键()
    {
        InventoryService svc = Create();
        svc.GrantItem("hat_dev_cap");
        svc.GrantItem("hat_dev_cap");

        Assert.True(svc.RemoveOne("hat_dev_cap"));
        Assert.Equal(1, svc.Owned["hat_dev_cap"]);
        Assert.True(svc.RemoveOne("hat_dev_cap"));
        Assert.False(svc.Owned.ContainsKey("hat_dev_cap")); // 归零移除键
        Assert.False(svc.RemoveOne("hat_dev_cap"));         // 已空 → 拒绝
    }

    [Fact]
    public void RemoveOne_未知id或未拥有_拒绝且零状态变化()
    {
        InventoryService svc = Create();

        Assert.False(svc.RemoveOne("nonexistent"));
        Assert.False(svc.RemoveOne("hat_santa")); // 未拥有
        Assert.Equal(0, _save.SaveCount);         // 拒绝路径不落盘
    }

    // ── ApplyOutfit（预设三槽一次切换）────────────────────────────────────

    [Fact]
    public void ApplyOutfit_合法id三槽一次切换_单次落盘()
    {
        var svc = new InventoryService(_save, _catalog); // 未预置标记 → Initialize 授予新手装
        svc.Initialize();
        int saves = _save.SaveCount;
        int fired = 0;
        svc.Changed += () => fired++;

        svc.ApplyOutfit("hat_beret", "skin_orange", "bongos_wood");

        Assert.Equal("hat_beret", svc.EquippedHat);
        Assert.Equal("skin_orange", svc.EquippedSkin);
        Assert.Equal("bongos_wood", svc.EquippedBongos);
        Assert.Equal(1, _save.SaveCount - saves); // 单次落盘（不三连闪）
        Assert.Equal(1, fired);                   // 单次广播
    }

    [Fact]
    public void ApplyOutfit_空串卸下_非法id保留原装备()
    {
        var svc = new InventoryService(_save, _catalog);
        svc.Initialize();
        svc.ApplyOutfit("hat_beret", "skin_orange", "bongos_wood");

        // 空串 = 卸下；hat_deleted 不在目录；hat_santa 是帽子但传给皮肤槽（槽位不符）
        svc.ApplyOutfit(string.Empty, "hat_deleted", "hat_santa");

        Assert.Equal(string.Empty, svc.EquippedHat);          // 空串 → 卸下
        Assert.Equal("skin_orange", svc.EquippedSkin);        // 非法 id → 保留
        Assert.Equal("bongos_wood", svc.EquippedBongos);      // 槽位不符的合法 id → 保留
    }
}
