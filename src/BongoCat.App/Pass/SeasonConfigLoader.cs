using System.Reflection;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Pass;

/// <summary>
/// seasons/*.json 装载器：从本程序集（BongoCat.App）的嵌入资源读入全部赛季配置并解析。
/// 与 items/economy 同模式（ItemCatalogLoader/EconomyConfigLoader remarks）：
/// 嵌入资源随程序集分发、无文件路径依赖，测试项目引用 App 即可读到与生产同一份真实赛季数据。
/// GDD-05 §1"可随版本热更"= 随版本发布更新配置 → EmbeddedResource 语义完全吻合；
/// 若未来需要免发版调参（%APPDATA% 覆盖目录），在此加载器加"文件系统优先"分支即可，接口不变。
/// </summary>
public static class SeasonConfigLoader
{
    /// <summary>嵌入资源名前缀（默认命名空间 + assets\seasons 目录）。</summary>
    public const string ResourcePrefix = "BongoCat.App.assets.seasons.";

    /// <summary>
    /// 载入全部赛季配置（按 StartDate 升序）。
    /// 每个奖励 itemId 必须在物品目录中存在（GDD-05 §7 边界：引用失效在加载侧 fail-fast，
    /// 不留到领取时才暴露）；无任何赛季文件 → 返回空表（通行证 UI 显示"暂无赛季"，进度保留不删）。
    /// </summary>
    public static IReadOnlyList<SeasonConfig> LoadFromAssembly(
        CosmeticCatalog catalog, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        Assembly assembly = typeof(SeasonConfigLoader).Assembly;
        var seasons = new List<SeasonConfig>();
        foreach (string resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"嵌入资源缺失：{resourceName}");
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();

            SeasonConfig season = SeasonConfig.ParseJson(json);
            ValidateRewardItems(season, catalog);
            seasons.Add(season);
            log?.Invoke($"[Pass] {resourceName[ResourcePrefix.Length..]} 已载入：{season.Name}" +
                        $"（{season.StartDate:yyyy-MM-dd} ~ {season.EndDate:yyyy-MM-dd}，" +
                        $"{season.Rewards.Count} 项奖励，任务池 {season.DailyQuestPool.Count} 条）");
        }

        var ordered = seasons.OrderBy(s => s.StartDate).ToList();
        log?.Invoke(ordered.Count > 0
            ? $"[Pass] 共 {ordered.Count} 个赛季配置（当前激活：{DescribeActive(ordered)}）"
            : "[Pass] 无赛季配置：通行证 UI 将显示「暂无赛季」（进度保留）");
        return ordered;
    }

    private static void ValidateRewardItems(SeasonConfig season, CosmeticCatalog catalog)
    {
        foreach (Core.Config.PassReward reward in season.Rewards)
        {
            if (reward.Kind == PassRewardKind.Item && !catalog.TryGet(reward.ItemId, out _))
            {
                throw new SeasonConfigException(
                    $"赛季 {season.SeasonId} 奖励 L{reward.Level} {reward.Track} " +
                    $"引用未知物品：{reward.ItemId}（不在 items.json 中）");
            }
        }
    }

    private static string DescribeActive(IReadOnlyList<SeasonConfig> seasons)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now); // 仅日志展示用，判定逻辑在 PassService
        SeasonConfig? active = seasons.FirstOrDefault(s => s.IsActiveOn(today));
        return active?.Name ?? "今日无激活赛季";
    }
}
