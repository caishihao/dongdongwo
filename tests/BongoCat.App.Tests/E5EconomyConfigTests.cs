using BongoCat.App.Economy;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// economy.json E5 新增段解析与防御测试：pity（保底阈值/最低品阶）、inventory.maxPerItem（单件上限）。
/// 非法值 fail-fast 拒绝，绝不带病运行（与 E4 防御纪律一致）。
/// </summary>
public class E5EconomyConfigTests
{
    [Fact]
    public void 嵌入economy_json_E5段与设计值一致()
    {
        EconomyConfig cfg = EconomyConfigLoader.LoadFromAssembly();

        // 保底（GDD-02 §5：连续 15 箱未出 epic+ → 必出 epic+；只对银/金箱，排除 wooden）
        Assert.Equal(15, cfg.Pity.Threshold);
        Assert.Equal(ItemQuality.Epic, cfg.Pity.MinQuality);
        Assert.Equal(["wooden"], cfg.Pity.ExcludedChestIds);

        // 单件上限（E5 任务书：单件 9 件，超出自动折算）
        Assert.Equal(9, cfg.InventoryLimit.MaxPerItem);
    }

    private static string BaseJson(string extra) => $$"""
        {
          "keystroke": { "coinsPer": 250, "dailyCap": 500 },
          "drip": { "secondsPerCoin": 120 },
          "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                        "weights": { "common": 1.0 } },
          "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
          "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
          "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
          "dropBucket": { "windowHours": 24, "maxItems": 40 },
          {{extra}}
        }
        """;

    [Fact]
    public void pity段_合法值解析()
    {
        EconomyConfig cfg = EconomyConfig.ParseJson(BaseJson(
            "\"pity\": { \"threshold\": 10, \"minQuality\": \"legendary\" }"));

        Assert.Equal(10, cfg.Pity.Threshold);
        Assert.Equal(ItemQuality.Legendary, cfg.Pity.MinQuality);
    }

    [Fact]
    public void pity段_缺省时用默认15_epic()
    {
        EconomyConfig cfg = EconomyConfig.ParseJson(BaseJson("\"inventory\": { \"maxPerItem\": 9 }"));

        Assert.Equal(15, cfg.Pity.Threshold);
        Assert.Equal(ItemQuality.Epic, cfg.Pity.MinQuality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void pity_threshold小于1_拒绝(int threshold)
    {
        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(BaseJson(
            $"\"pity\": {{ \"threshold\": {threshold}, \"minQuality\": \"epic\" }}")));
    }

    [Theory]
    [InlineData("common")]
    [InlineData("uncommon")]
    [InlineData("rare")]
    [InlineData("nonsense")]
    public void pity_minQuality低于epic或非法_拒绝(string quality)
    {
        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(BaseJson(
            $"\"pity\": {{ \"threshold\": 15, \"minQuality\": \"{quality}\" }}")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void inventory_maxPerItem小于1_拒绝(int maxPerItem)
    {
        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(BaseJson(
            $"\"inventory\": {{ \"maxPerItem\": {maxPerItem} }}")));
    }

    [Fact]
    public void inventory段_合法值解析()
    {
        EconomyConfig cfg = EconomyConfig.ParseJson(BaseJson("\"inventory\": { \"maxPerItem\": 5 }"));

        Assert.Equal(5, cfg.InventoryLimit.MaxPerItem);
    }
}
