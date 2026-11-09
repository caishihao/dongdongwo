using BongoCat.App.Inventory;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 物品目录/品质色板测试（epics E3-S1：物品领域模型 + 品质分级全测试覆盖）。
/// </summary>
public class ItemCatalogTests
{
    [Fact]
    public void 嵌入items_json_解析出16件_槽位分布583且id唯一()
    {
        // GDD-09：新增恐龙专属皮肤 dino_default（speciesOnly=dino）→ 13 件；
        // GDD-09 §7：新增阿瓜专属皮肤 agua_default（speciesOnly=agua）→ 14 件
        CosmeticCatalog catalog = ItemCatalogLoader.LoadFromAssembly();

        Assert.Equal(16, catalog.Count);
        Assert.Equal(5, catalog.Items.Count(i => i.Slot == ItemSlot.Hat));
        Assert.Equal(8, catalog.Items.Count(i => i.Slot == ItemSlot.Skin));
        Assert.Equal(3, catalog.Items.Count(i => i.Slot == ItemSlot.Bongos));
        Assert.Equal(16, catalog.Items.Select(i => i.Id).Distinct().Count());
        Assert.All(catalog.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.ArtKey)));
    }

    [Fact]
    public void 新手装id_必须真实存在于目录_防发空()
    {
        CosmeticCatalog catalog = ItemCatalogLoader.LoadFromAssembly();

        Assert.All(InventoryService.StarterKitIds, id => Assert.True(catalog.TryGet(id, out _)));
    }

    [Fact]
    public void 未知槽位_解析失败抛ItemCatalogException()
    {
        const string json = """
            { "items": [ { "id": "x", "slot": "emote", "quality": "common", "displayName": "x", "artKey": "x" } ] }
            """;

        Assert.Throws<ItemCatalogException>(() => CosmeticCatalog.ParseJson(json));
    }

    [Fact]
    public void 未知品质_解析失败抛ItemCatalogException()
    {
        const string json = """
            { "items": [ { "id": "x", "slot": "hat", "quality": "mythic", "displayName": "x", "artKey": "x" } ] }
            """;

        Assert.Throws<ItemCatalogException>(() => CosmeticCatalog.ParseJson(json));
    }

    [Fact]
    public void 重复id_解析失败抛ItemCatalogException()
    {
        const string json = """
            { "items": [
                { "id": "dup", "slot": "hat", "quality": "common", "displayName": "a", "artKey": "a" },
                { "id": "dup", "slot": "hat", "quality": "rare", "displayName": "b", "artKey": "b" } ] }
            """;

        Assert.Throws<ItemCatalogException>(() => CosmeticCatalog.ParseJson(json));
    }

    [Fact]
    public void 空artKey_解析失败抛ItemCatalogException()
    {
        const string json = """
            { "items": [ { "id": "x", "slot": "hat", "quality": "common", "displayName": "x", "artKey": "" } ] }
            """;

        Assert.Throws<ItemCatalogException>(() => CosmeticCatalog.ParseJson(json));
    }

    [Fact]
    public void 空items数组_解析失败抛ItemCatalogException()
    {
        Assert.Throws<ItemCatalogException>(() => CosmeticCatalog.ParseJson("""{ "items": [] }"""));
    }

    [Fact]
    public void 品质色板_六色齐全且为合法HEX()
    {
        var all = Enum.GetValues<ItemQuality>();
        Assert.Equal(6, all.Length);

        foreach (ItemQuality q in all)
        {
            string hex = QualityPalette.GetHex(q);
            Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        }

        // 自设计色板锚点（防误改回灰阶/撞色）
        Assert.Equal("#9E9E9E", QualityPalette.GetHex(ItemQuality.Common));
        Assert.Equal("#E91E63", QualityPalette.GetHex(ItemQuality.Unique));
    }
}
