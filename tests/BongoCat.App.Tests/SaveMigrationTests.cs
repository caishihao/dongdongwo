using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 存档迁移测试（epics E2/E3：v1→v2 尺寸、v2→v3 库存，ADR-004 规则 2 链式迁移）。
/// </summary>
public class SaveMigrationTests
{
    [Fact]
    public void v1存档_链式迁移到当前版本_新增尺寸字段并保留既有字段()
    {
        var v1 = new SaveModel
        {
            SchemaVersion = 1,
            CatPositionRelX = 0.62,
            CatPositionRelY = 0.17,
            CatMirrored = true,
            MasterVolume = 0.35f,
            Muted = true,
            // 模拟 v1 反序列化：没有 WindowScale / 库存字段（保持默认值）
        };

        SaveMigrator.MigrateToCurrent(v1);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v1.SchemaVersion);
        Assert.Equal(1.0, v1.WindowScale);
        // 既有字段一律不动（迁移只增不改）
        Assert.Equal(0.62, v1.CatPositionRelX);
        Assert.Equal(0.17, v1.CatPositionRelY);
        Assert.True(v1.CatMirrored);
        Assert.Equal(0.35f, v1.MasterVolume);
        Assert.True(v1.Muted);
        // 链式补齐的 v3 字段默认值
        Assert.Empty(v1.OwnedItems);
        Assert.Equal(string.Empty, v1.EquippedHat);
        Assert.False(v1.InventoryInitialized);
    }

    [Fact]
    public void v2存档_链式迁移到当前版本_库存与经济字段为默认值且不动既有设置()
    {
        var v2 = new SaveModel
        {
            SchemaVersion = 2,
            WindowScale = 1.5,
            CatPositionRelX = 0.1,
            MasterVolume = 0.6f,
        };

        SaveMigrator.MigrateToCurrent(v2);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v2.SchemaVersion);
        Assert.Equal(1.5, v2.WindowScale);   // 既有设置不动
        Assert.Equal(0.6f, v2.MasterVolume);
        Assert.Empty(v2.OwnedItems);          // v3 新增字段：空库存、全部卸下、未初始化
        Assert.Equal(string.Empty, v2.EquippedHat);
        Assert.Equal(string.Empty, v2.EquippedSkin);
        Assert.Equal(string.Empty, v2.EquippedBongos);
        Assert.False(v2.InventoryInitialized);
        // v4 新增字段（链式补齐）：经济默认零值（DailyCoinDate 置空 → 首次进入 E4 按当日首次启动发奖励）
        Assert.Equal(0, v2.Coins);
        Assert.Equal(0, v2.DailyTapCount);
        Assert.Equal(string.Empty, v2.DailyCoinDate);
        Assert.Equal(0, v2.IdleCoinAccumulator);
        Assert.Empty(v2.DropBucket);
        Assert.Equal(0, v2.LastChestTicks);
        Assert.Equal(0, v2.ChestChargeSeconds);
    }

    [Fact]
    public void v3存档_链式迁移到当前版本_经济与E5字段默认值且库存装备不动()
    {
        var v3 = new SaveModel
        {
            SchemaVersion = 3,
            WindowScale = 0.75,
            OwnedItems = new Dictionary<string, int> { ["hat_beret"] = 1 },
            EquippedHat = "hat_beret",
            InventoryInitialized = true,
        };

        SaveMigrator.MigrateToCurrent(v3);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v3.SchemaVersion);
        Assert.Equal(1, v3.OwnedItems["hat_beret"]);  // 库存不动
        Assert.Equal("hat_beret", v3.EquippedHat);    // 装备不动
        Assert.True(v3.InventoryInitialized);
        Assert.Equal(0, v3.Coins);                    // E4 字段零值起步
        Assert.Equal(0, v3.DailyTapCount);
        Assert.Equal(string.Empty, v3.DailyCoinDate);
        Assert.Empty(v3.DropBucket);
        Assert.Equal(0, v3.ChestChargeSeconds);
        // E5 字段（链式补齐）：图鉴由库存 keys 点亮 / 预设空表 / 保底计数清零
        Assert.Equal(["hat_beret"], v3.DiscoveredItems);
        Assert.Empty(v3.Presets);
        Assert.Equal(0, v3.ChestPityCount);
    }

    [Fact]
    public void v4存档_链式迁移到当前版本_图鉴由库存keys点亮且经济状态不动()
    {
        var v4 = new SaveModel
        {
            SchemaVersion = 4,
            OwnedItems = new Dictionary<string, int>
            {
                ["hat_dev_cap"] = 3,
                ["skin_cream"] = 1,
            },
            Coins = 777,
            ChestChargeSeconds = 3600,
        };

        SaveMigrator.MigrateToCurrent(v4);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v4.SchemaVersion);
        Assert.Equal(["hat_dev_cap", "skin_cream"], v4.DiscoveredItems); // 老玩家图鉴不回灰
        Assert.Empty(v4.Presets);
        Assert.Equal(0, v4.ChestPityCount);
        Assert.Equal(777, v4.Coins);            // 经济状态不动
        Assert.Equal(3600, v4.ChestChargeSeconds);
        // E6 字段（链式补齐）：赛季零值起步（SeasonId 空 → 首次进入按当前赛季惰性装载）
        Assert.Equal(string.Empty, v4.SeasonId);
        Assert.Equal(0, v4.SeasonXP);
        Assert.Empty(v4.ClaimedMilestones);
        Assert.False(v4.PremiumUnlocked);
    }

    [Fact]
    public void v5存档_迁移到当前版本_赛季字段零值起步且E5状态保留()
    {
        var v5 = new SaveModel
        {
            SchemaVersion = 5,
            OwnedItems = new Dictionary<string, int>(), // 已拆解光：库存空
            DiscoveredItems = ["hat_dev_cap", "skin_cream"],
            Presets = [new CosmeticPreset("日常", "hat_dev_cap", "skin_cream", "bongos_wood")],
            ChestPityCount = 12,
            Coins = 500,
        };

        SaveMigrator.MigrateToCurrent(v5);
        SaveMigrator.MigrateToCurrent(v5); // 幂等重放（如 .bak 回读）

        Assert.Equal(SaveModel.CurrentSchemaVersion, v5.SchemaVersion);
        // 拆解光后图鉴不回灰（库存空 ≠ 图鉴清空，迁移只增不改）
        Assert.Equal(["hat_dev_cap", "skin_cream"], v5.DiscoveredItems);
        Assert.Single(v5.Presets);
        Assert.Equal(12, v5.ChestPityCount);   // 保底进度不因迁移丢失
        Assert.Equal(500, v5.Coins);
        // E6 字段零值起步且幂等重放不变化
        Assert.Equal(string.Empty, v5.SeasonId);
        Assert.Equal(0, v5.SeasonXP);
        Assert.Empty(v5.ClaimedMilestones);
        Assert.False(v5.PremiumUnlocked);
        Assert.Equal(string.Empty, v5.DailyXpDate);
        Assert.Equal(0, v5.DailyXpEarned);
    }

    [Fact]
    public void v6存档_链式迁移到当前版本_教程与主题字段默认值且既有状态保留()
    {
        var v6 = new SaveModel
        {
            SchemaVersion = 6,
            Coins = 888,
            SeasonId = "season_001",
            SeasonXP = 500,
            OwnedItems = new Dictionary<string, int> { ["hat_beret"] = 1 },
            DiscoveredItems = ["hat_beret"],
        };
        // 模拟 v6 反序列化：没有教程/主题字段（v7 新增，JSON 缺失保持类型默认）
        v6.UiThemeId = string.Empty;

        SaveMigrator.MigrateToCurrent(v6);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v6.SchemaVersion);
        // E7 v7 新增字段：教程未完成（老玩家也会看一次引导）、步进度 0、主题回默认奶油
        Assert.False(v6.TutorialCompleted);
        Assert.Equal(0, v6.TutorialStep);
        Assert.Equal("cream", v6.UiThemeId);
        // 既有状态一律不动（迁移只增不改）
        Assert.Equal(888, v6.Coins);
        Assert.Equal("season_001", v6.SeasonId);
        Assert.Equal(500, v6.SeasonXP);
        Assert.Equal(1, v6.OwnedItems["hat_beret"]);
        Assert.Equal(["hat_beret"], v6.DiscoveredItems);
    }

    [Fact]
    public void 已是当前版本_迁移为幂等_不覆盖用户已有库存装备经济与教程主题状态()
    {
        var v7 = new SaveModel
        {
            SchemaVersion = SaveModel.CurrentSchemaVersion,
            WindowScale = 1.5,
            CatPositionRelX = 0.1,
            OwnedItems = new Dictionary<string, int> { ["hat_beret"] = 2 },
            EquippedHat = "hat_beret",
            EquippedSkin = "skin_orange",
            InventoryInitialized = true,
            Coins = 1234,
            DailyTapCount = 5000,
            DailyCoinDate = "2025-06-01",
            IdleCoinAccumulator = 42.5,
            DropBucket = [1000, 2000],
            LastChestTicks = 999,
            ChestChargeSeconds = 3600,
            DiscoveredItems = ["hat_beret", "hat_dev_cap"],
            Presets = [new CosmeticPreset("节日", "hat_santa", "skin_cow", "bongos_gold")],
            ChestPityCount = 7,
            SeasonId = "season_001",
            SeasonXP = 4321,
            ClaimedMilestones = ["season_001:1:free", "season_001:1:premium"],
            PremiumUnlocked = true,
            DailyXpDate = "2025-06-01",
            DailyXpEarned = 280,
            DailyChestCount = 2,
            DailySynthCount = 1,
            DailyCompletedQuests = ["open_chests_2"],
            TutorialCompleted = true,
            TutorialStep = 2,
            UiThemeId = "ink",
        };

        SaveMigrator.MigrateToCurrent(v7);
        SaveMigrator.MigrateToCurrent(v7); // 重复调用（如 .bak 回读）

        Assert.Equal(SaveModel.CurrentSchemaVersion, v7.SchemaVersion);
        Assert.Equal(1.5, v7.WindowScale); // 绝不因幂等调用重置
        Assert.Equal(2, v7.OwnedItems["hat_beret"]); // 库存绝不因幂等调用清空
        Assert.Equal("hat_beret", v7.EquippedHat);
        Assert.True(v7.InventoryInitialized);
        Assert.Equal(1234, v7.Coins);                 // 经济状态绝不因幂等调用清零
        Assert.Equal(5000, v7.DailyTapCount);
        Assert.Equal("2025-06-01", v7.DailyCoinDate);
        Assert.Equal(42.5, v7.IdleCoinAccumulator);
        Assert.Equal([1000, 2000], v7.DropBucket);
        Assert.Equal(999, v7.LastChestTicks);
        Assert.Equal(3600, v7.ChestChargeSeconds);
        Assert.Equal(["hat_beret", "hat_dev_cap"], v7.DiscoveredItems); // 图鉴不回灰
        Assert.Single(v7.Presets);
        Assert.Equal("节日", v7.Presets[0].Name);
        Assert.Equal(7, v7.ChestPityCount);
        // E6 赛季状态绝不因幂等调用清零
        Assert.Equal("season_001", v7.SeasonId);
        Assert.Equal(4321, v7.SeasonXP);
        Assert.Equal(["season_001:1:free", "season_001:1:premium"], v7.ClaimedMilestones);
        Assert.True(v7.PremiumUnlocked);
        Assert.Equal(280, v7.DailyXpEarned);
        Assert.Equal(2, v7.DailyChestCount);
        Assert.Equal(1, v7.DailySynthCount);
        Assert.Equal(["open_chests_2"], v7.DailyCompletedQuests);
        // E7 教程/主题状态绝不因幂等调用重置
        Assert.True(v7.TutorialCompleted);
        Assert.Equal(2, v7.TutorialStep);
        Assert.Equal("ink", v7.UiThemeId);
    }

    [Fact]
    public void v7存档_链式迁移到当前版本_多人字段默认值且既有状态保留()
    {
        var v7 = new SaveModel
        {
            SchemaVersion = 7,
            Coins = 777,
            TutorialCompleted = true,
            TutorialStep = 1,
            UiThemeId = "bamboo",
            OwnedItems = new Dictionary<string, int> { ["skin_orange"] = 1 },
            // 模拟 v7 反序列化：没有 Mp* 字段（v8 新增，JSON 缺失保持类型默认）
        };

        SaveMigrator.MigrateToCurrent(v7);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v7.SchemaVersion);
        // E8 v8 新增字段：功能未配置态（地址/房间码空，显示名默认）
        Assert.Equal(string.Empty, v7.MpServerAddress);
        Assert.Equal("猫猫", v7.MpDisplayName);
        Assert.Equal(string.Empty, v7.MpLastRoomCode);
        // 既有状态一律不动（迁移只增不改）
        Assert.Equal(777, v7.Coins);
        Assert.True(v7.TutorialCompleted);
        Assert.Equal("bamboo", v7.UiThemeId);
        Assert.Equal(1, v7.OwnedItems["skin_orange"]);
    }

    [Fact]
    public void 已是当前版本_迁移为幂等_多人字段不因重复迁移被重置()
    {
        var v8 = new SaveModel
        {
            SchemaVersion = SaveModel.CurrentSchemaVersion,
            MpServerAddress = "relay.example.com:9437",
            MpDisplayName = "布丁",
            MpLastRoomCode = "X9T4CB",
            Coins = 10,
        };

        SaveMigrator.MigrateToCurrent(v8);
        SaveMigrator.MigrateToCurrent(v8); // 重复调用（如 .bak 回读）

        Assert.Equal("relay.example.com:9437", v8.MpServerAddress);
        Assert.Equal("布丁", v8.MpDisplayName);
        Assert.Equal("X9T4CB", v8.MpLastRoomCode);
        Assert.Equal(10, v8.Coins);
    }

    [Fact]
    public void v8存档_链式迁移到当前版本_风格字段默认值且既有状态保留()
    {
        var v8 = new SaveModel
        {
            SchemaVersion = 8,
            Coins = 888,
            TutorialCompleted = true,
            TutorialStep = 3,
            UiThemeId = "ink",
            MpServerAddress = "your.server.ip:9437",
            MpDisplayName = "糯米",
            MpLastRoomCode = "A7K2MQ",
            // 模拟 v8 反序列化：没有 CatStyleId / StyleChosen（v9 新增，JSON 缺失保持默认）
        };

        SaveMigrator.MigrateToCurrent(v8);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v8.SchemaVersion);
        // GDD-08 v9 新增字段：风格待选择态（首启选择器触发矩阵的输入）
        Assert.Equal(string.Empty, v8.CatStyleId);
        Assert.False(v8.StyleChosen);
        // 既有状态一律不动（迁移只增不改）
        Assert.Equal(888, v8.Coins);
        Assert.True(v8.TutorialCompleted);
        Assert.Equal("ink", v8.UiThemeId);
        Assert.Equal("your.server.ip:9437", v8.MpServerAddress);
        Assert.Equal("糯米", v8.MpDisplayName);
        Assert.Equal("A7K2MQ", v8.MpLastRoomCode);
    }

    [Fact]
    public void 当前版本存档_风格选择幂等_重复迁移不重置()
    {
        var v9 = new SaveModel
        {
            SchemaVersion = SaveModel.CurrentSchemaVersion,
            CatStyleId = "papercut",
            StyleChosen = true,
            Coins = 10,
        };

        SaveMigrator.MigrateToCurrent(v9);
        SaveMigrator.MigrateToCurrent(v9);

        Assert.Equal("papercut", v9.CatStyleId);
        Assert.True(v9.StyleChosen);
    }

    // ── GDD-09：v9 → v10 物种迁移 ──────────────────────────────────────

    [Fact]
    public void v9存档_链式迁移到v10_物种默认cat且风格标记刻意清空()
    {
        // GDD-09 §5 设计决策：迁移清 CatStyleId + StyleChosen=false → 全体老玩家
        // 重看一次升级版第 0 步选择器（顺便获得选龙机会），与 v8→v9 迁移策略同构
        var v9 = new SaveModel
        {
            SchemaVersion = 9,
            Coins = 777,
            CatStyleId = "chibi",
            StyleChosen = true,
            TutorialCompleted = true,
            UiThemeId = "ink",
        };

        SaveMigrator.MigrateToCurrent(v9);

        Assert.Equal(SaveModel.CurrentSchemaVersion, v9.SchemaVersion);
        Assert.Equal("cat", v9.SpeciesId);        // v10 新增字段：默认猫
        Assert.Equal(string.Empty, v9.CatStyleId); // 刻意清空（重看选择器）
        Assert.False(v9.StyleChosen);              // 刻意清空（重看选择器）
        // 既有状态一律不动
        Assert.Equal(777, v9.Coins);
        Assert.True(v9.TutorialCompleted);
        Assert.Equal("ink", v9.UiThemeId);
    }

    [Fact]
    public void v10存档_幂等迁移_物种与风格选择不重置()
    {
        var v10 = new SaveModel
        {
            SchemaVersion = SaveModel.CurrentSchemaVersion,
            SpeciesId = "dino",
            CatStyleId = "sticker",
            StyleChosen = true,
            Coins = 10,
        };

        SaveMigrator.MigrateToCurrent(v10);
        SaveMigrator.MigrateToCurrent(v10); // 重复调用（如 .bak 回读）

        Assert.Equal("dino", v10.SpeciesId);
        Assert.Equal("sticker", v10.CatStyleId);
        Assert.True(v10.StyleChosen);
    }

    [Fact]
    public void 非法尺寸档位_无论来源版本_收敛到100()
    {
        var bad = new SaveModel { SchemaVersion = 4, WindowScale = 2.7 };

        SaveMigrator.MigrateToCurrent(bad);

        Assert.Equal(SaveModel.CurrentSchemaVersion, bad.SchemaVersion);
        Assert.Equal(1.0, bad.WindowScale);
    }

    [Fact]
    public void 负金币_无论来源版本_收敛到0_记账不可为负()
    {
        var bad = new SaveModel { SchemaVersion = 4, Coins = -5 };

        SaveMigrator.MigrateToCurrent(bad);

        Assert.Equal(0, bad.Coins);
    }

    [Fact]
    public void 未来版本_拒绝加载由加载侧守门_迁移器不降级()
    {
        // 契约：SchemaVersion > Current 的模型按 ADR-004 规则 2 "只读不写"，
        // 由 JsonSaveService 拒绝加载；迁移器对 <= 当前版本生效即可。
        var future = new SaveModel { SchemaVersion = SaveModel.CurrentSchemaVersion + 1, WindowScale = 0.75 };

        SaveMigrator.MigrateToCurrent(future);

        Assert.Equal(SaveModel.CurrentSchemaVersion + 1, future.SchemaVersion);
        Assert.Equal(0.75, future.WindowScale);
    }

    [Fact]
    public void 档位表_只含用户拍板的三档()
    {
        Assert.Equal([0.75, 1.0, 1.5], SaveModel.ValidWindowScales);
        Assert.True(SaveModel.IsValidWindowScale(0.75));
        Assert.True(SaveModel.IsValidWindowScale(1.0));
        Assert.True(SaveModel.IsValidWindowScale(1.5));
        Assert.False(SaveModel.IsValidWindowScale(1.2));
        Assert.False(SaveModel.IsValidWindowScale(0.5));
    }
}
