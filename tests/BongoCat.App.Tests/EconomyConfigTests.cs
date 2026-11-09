using BongoCat.App.Economy;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// economy.json 解析与防御测试（epics E4-S4：数值全部数据驱动 + 非法值防御）。
/// 真实嵌入数据与 GDD-02 §6 数值表逐项对账，防止"配置漂移"破坏已验算的回收率红线。
/// </summary>
public class EconomyConfigTests
{
    [Fact]
    public void 嵌入economy_json_解析成功且与GDD02数值表一致()
    {
        EconomyConfig cfg = EconomyConfigLoader.LoadFromAssembly();

        // 敲击产币（日上限 500 为主理人拍板值，GDD-02 §8 开放问题 3）
        Assert.Equal(250, cfg.Keystroke.CoinsPer);
        Assert.Equal(500, cfg.Keystroke.DailyCap);

        // 滴灌 / 每日奖励
        Assert.Equal(120, cfg.Drip.SecondsPerCoin);
        Assert.Equal(50, cfg.DailyLoginBonus);

        // 挂机掉落（50% 无 / 30% 金币包 / 20% 物品，15 分钟一次）
        Assert.Equal(15, cfg.IdleDrop.IntervalMinutes);
        Assert.Equal(0.50, cfg.IdleDrop.Nothing, 6);
        Assert.Equal(0.30, cfg.IdleDrop.CoinPack, 6);
        Assert.Equal(0.20, cfg.IdleDrop.Item, 6);
        Assert.Equal(15, cfg.IdleDrop.CoinPackMin);
        Assert.Equal(35, cfg.IdleDrop.CoinPackMax);
        Assert.Equal(0.70, cfg.IdleDrop.Weights[ItemQuality.Common], 6);
        Assert.Equal(0.22, cfg.IdleDrop.Weights[ItemQuality.Uncommon], 6);
        Assert.Equal(0.07, cfg.IdleDrop.Weights[ItemQuality.Rare], 6);
        Assert.Equal(0.009, cfg.IdleDrop.Weights[ItemQuality.Epic], 6);
        Assert.Equal(0.001, cfg.IdleDrop.Weights[ItemQuality.Legendary], 6);

        // 宝箱（GDD-02 §6.2 数值表）
        Assert.Equal(3, cfg.Chests.Count);
        Assert.Equal("wooden", cfg.Chests[0].Id);
        Assert.Equal(150, cfg.Chests[0].Price);
        Assert.Equal(2, cfg.Chests[0].Items);
        Assert.Equal(0.55, cfg.Chests[0].Weights[ItemQuality.Common], 6);
        Assert.Equal(0.005, cfg.Chests[0].Weights[ItemQuality.Legendary], 6);
        Assert.Equal("silver", cfg.Chests[1].Id);
        Assert.Equal(600, cfg.Chests[1].Price);
        Assert.Equal(0.15, cfg.Chests[1].Weights[ItemQuality.Epic], 6);
        Assert.Equal("gold", cfg.Chests[2].Id);
        Assert.Equal(2400, cfg.Chests[2].Price);
        Assert.Equal(3, cfg.Chests[2].Items);
        Assert.Equal(0.15, cfg.Chests[2].Weights[ItemQuality.Legendary], 6);
        Assert.False(cfg.Chests[2].Weights.ContainsKey(ItemQuality.Common)); // 金箱 0% common

        // 拆解价 / 重复折算 / 宝箱充能 / 掉落桶
        Assert.Equal(10, cfg.Salvage[ItemQuality.Common]);
        Assert.Equal(40, cfg.Salvage[ItemQuality.Uncommon]);
        Assert.Equal(150, cfg.Salvage[ItemQuality.Rare]);
        Assert.Equal(600, cfg.Salvage[ItemQuality.Epic]);
        Assert.Equal(2500, cfg.Salvage[ItemQuality.Legendary]);
        Assert.Equal(0.60, cfg.DuplicateConversionRate, 6);
        Assert.Equal(7200, cfg.ChestCharge.SecondsPerChest); // 2h 一箱（首箱约 2h 体验锚点）
        Assert.Equal("wooden", cfg.ChestCharge.ChestId);
        Assert.Equal(24, cfg.DropBucket.WindowHours);
        Assert.Equal(40, cfg.DropBucket.MaxItems);
    }

    [Fact]
    public void 权重和不等于1_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.1, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 1.0 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 权重表含unique_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 0.99, "unique": 0.01 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 未知品质_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 0.9, "mythic": 0.1 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 宝箱权重和不等于1_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 1.0 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 0.9 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 宝箱引用不存在_chestCharge_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 1.0 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "silver" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Theory]
    [InlineData(-150)]
    public void 负价格_拒绝(int price)
    {
        string json = $$"""
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 1.0 } },
              "chests": [ { "id": "wooden", "price": {{price}}, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 重复折算率越界_拒绝()
    {
        const string json = """
            {
              "keystroke": { "coinsPer": 250, "dailyCap": 500 },
              "drip": { "secondsPerCoin": 120 },
              "idleDrop": { "intervalMinutes": 15, "nothing": 0.5, "coinPack": 0.3, "item": 0.2, "coinPackMin": 15, "coinPackMax": 35,
                            "weights": { "common": 1.0 } },
              "chests": [ { "id": "wooden", "price": 150, "items": 2, "weights": { "common": 1.0 } } ],
              "salvage": { "common": 10, "uncommon": 40, "rare": 150, "epic": 600, "legendary": 2500 },
              "duplicateConversionRate": 1.5,
              "chestCharge": { "secondsPerChest": 7200, "chestId": "wooden" },
              "dropBucket": { "windowHours": 24, "maxItems": 40 }
            }
            """;

        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson(json));
    }

    [Fact]
    public void 非法JSON_拒绝()
    {
        Assert.Throws<EconomyConfigException>(() => EconomyConfig.ParseJson("{ not json "));
    }
}
