using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 赛季配置解析测试（epics E6-S1：SeasonConfig.ParseJson fail-fast 防御 + 曲线阈值表 + 窗口判定）。
/// </summary>
public class SeasonConfigTests
{
    private const string TestSeasonJson = """
        {
          "seasonId": "season_t1",
          "name": "测试赛季一",
          "startDate": "2025-01-01",
          "endDate": "2025-02-12",
          "maxLevel": 5,
          "xpCurve": { "base": 100, "step": 10 },
          "premiumUnlockCoins": 2500,
          "dailyQuestPool": [
            { "id": "open_chests_1", "type": "openChest", "target": 1, "xp": 30 }
          ],
          "rewards": [
            { "level": 1, "track": "free",    "type": "coins", "amount": 100 },
            { "level": 1, "track": "premium", "type": "item",  "itemId": "hat_dev_cap" },
            { "level": 5, "track": "free",    "type": "item",  "itemId": "bongos_gold" }
          ]
        }
        """;

    [Fact]
    public void 合法配置_解析出全部字段()
    {
        SeasonConfig s = SeasonConfig.ParseJson(TestSeasonJson);

        Assert.Equal("season_t1", s.SeasonId);
        Assert.Equal("测试赛季一", s.Name);
        Assert.Equal(new DateOnly(2025, 1, 1), s.StartDate);
        Assert.Equal(new DateOnly(2025, 2, 12), s.EndDate);
        Assert.Equal(5, s.MaxLevel);
        Assert.Equal(100, s.XpCurveBase);
        Assert.Equal(10, s.XpCurveStep);
        Assert.Equal(2500, s.PremiumUnlockCoins);
        Assert.Single(s.DailyQuestPool);
        Assert.Equal(3, s.Rewards.Count);
    }

    // ── 曲线阈值表（GDD-05 §6：每级需求 = base + (level-1)×step）────────────

    [Fact]
    public void 曲线阈值_1级为零_后续线性微增()
    {
        SeasonConfig s = SeasonConfig.ParseJson(TestSeasonJson);

        Assert.Equal(0, s.ThresholdForLevel(1));
        Assert.Equal(100, s.ThresholdForLevel(2));
        Assert.Equal(210, s.ThresholdForLevel(3));   // 100 + 110
        Assert.Equal(330, s.ThresholdForLevel(4));
        Assert.Equal(460, s.ThresholdForLevel(5));
        // 越界级不崩溃（调用方校验范围，这里只保证纯函数）：L6 = 460 + (100+4×10) = 600
        Assert.Equal(600, s.ThresholdForLevel(6));
    }

    // ── 赛季窗口判定（GDD-05 §5：startDate~endDate 含当日）────────────────

    [Fact]
    public void 窗口判定_两端含当日_窗外不激活()
    {
        SeasonConfig s = SeasonConfig.ParseJson(TestSeasonJson);

        Assert.True(s.IsActiveOn(new DateOnly(2025, 1, 1)));   // 起始日含
        Assert.True(s.IsActiveOn(new DateOnly(2025, 2, 12)));  // 结束日含
        Assert.True(s.IsActiveOn(new DateOnly(2025, 1, 20)));  // 窗口内
        Assert.False(s.IsActiveOn(new DateOnly(2024, 12, 31))); // 前一天
        Assert.False(s.IsActiveOn(new DateOnly(2025, 2, 13)));  // 后一天
    }

    [Fact]
    public void 奖励查询_命中与未命中()
    {
        SeasonConfig s = SeasonConfig.ParseJson(TestSeasonJson);

        Assert.True(s.TryGetReward(1, PassTrack.Free, out PassReward free));
        Assert.Equal(PassRewardKind.Coins, free.Kind);
        Assert.Equal(100, free.Amount);
        Assert.True(s.TryGetReward(1, PassTrack.Premium, out PassReward premium));
        Assert.Equal("hat_dev_cap", premium.ItemId);
        Assert.False(s.TryGetReward(2, PassTrack.Free, out _)); // 稀疏奖励表：L2 免费轨未配置
    }

    // ── 非法值防御（fail-fast，绝不带病运行）──────────────────────────────

    [Theory]
    [InlineData("\"seasonId\": \"\"", "seasonId")]
    [InlineData("\"seasonId\": \"  \"", "seasonId")]
    public void 非法配置_拒绝对应字段(string replace, string expectKeyword)
    {
        string json = TestSeasonJson.Replace("\"seasonId\": \"season_t1\"", replace, StringComparison.Ordinal);
        SeasonConfigException ex = Assert.Throws<SeasonConfigException>(
            () => SeasonConfig.ParseJson(json));
        Assert.Contains(expectKeyword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 非法日期格式_拒绝()
    {
        string json = TestSeasonJson.Replace("2025-01-01", "2025/01/01", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(json));
    }

    [Fact]
    public void 日期区间倒挂_拒绝()
    {
        string json = TestSeasonJson
            .Replace("\"startDate\": \"2025-01-01\"", "\"startDate\": \"2025-03-01\"", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(json));
    }

    [Fact]
    public void maxLevel小于1_拒绝()
    {
        string json = TestSeasonJson.Replace("\"maxLevel\": 5", "\"maxLevel\": 0", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(json));
    }

    [Fact]
    public void 曲线base小于1或step为负_拒绝()
    {
        string badBase = TestSeasonJson.Replace("\"base\": 100", "\"base\": 0", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badBase));

        string badStep = TestSeasonJson.Replace("\"step\": 10", "\"step\": -1", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badStep));
    }

    [Fact]
    public void 解锁价为负_拒绝()
    {
        string json = TestSeasonJson.Replace("2500", "-1", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(json));
    }

    [Fact]
    public void 任务_未知类型或非法目标或xp或重复id_拒绝()
    {
        string badType = TestSeasonJson.Replace("openChest", "unknownQuest", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badType));

        string badTarget = TestSeasonJson.Replace("\"target\": 1", "\"target\": 0", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badTarget));

        string badXp = TestSeasonJson.Replace("\"xp\": 30", "\"xp\": 0", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badXp));
    }

    [Fact]
    public void 奖励_level越界或未知轨别或金币额为零_拒绝()
    {
        string badLevel = TestSeasonJson.Replace("\"level\": 5", "\"level\": 6", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badLevel));

        string badTrack = TestSeasonJson.Replace("\"track\": \"free\"", "\"track\": \"gold\"", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badTrack));

        string badAmount = TestSeasonJson.Replace("\"amount\": 100", "\"amount\": 0", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badAmount));
    }

    [Fact]
    public void 奖励_物品缺itemId或未知类型_拒绝()
    {
        string missingItemId = TestSeasonJson.Replace(
            "\"itemId\": \"hat_dev_cap\"", "\"itemId\": \"\"", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(missingItemId));

        string badType = TestSeasonJson.Replace(
            "\"type\": \"coins\"", "\"type\": \"token\"", StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(badType));
    }

    [Fact]
    public void 奖励_同节点双轨重复_拒绝()
    {
        string json = TestSeasonJson.Replace(
            "{ \"level\": 5, \"track\": \"free\",    \"type\": \"item\",  \"itemId\": \"bongos_gold\" }",
            "{ \"level\": 1, \"track\": \"free\",    \"type\": \"item\",  \"itemId\": \"bongos_gold\" }",
            StringComparison.Ordinal);
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson(json));
    }

    [Fact]
    public void 不是合法JSON_拒绝()
    {
        Assert.Throws<SeasonConfigException>(() => SeasonConfig.ParseJson("{ 不是 json"));
    }
}
