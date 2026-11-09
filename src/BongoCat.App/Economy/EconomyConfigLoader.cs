using System.Reflection;
using BongoCat.Core.Config;

namespace BongoCat.App.Economy;

/// <summary>
/// economy.json 装载器：从本程序集（BongoCat.App）的嵌入资源读入并解析。
/// 与 items.json 同模式（ItemCatalogLoader remarks）：嵌入资源随程序集分发、
/// 无文件路径依赖，测试项目引用 App 即可读到与生产同一份真实数值。
/// </summary>
public static class EconomyConfigLoader
{
    /// <summary>嵌入资源全名（默认命名空间 + 相对路径）。</summary>
    public const string ResourceName = "BongoCat.App.assets.economy.json";

    public static EconomyConfig LoadFromAssembly(Action<string>? log = null)
    {
        Assembly assembly = typeof(EconomyConfigLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"嵌入资源缺失：{ResourceName}（检查 csproj EmbeddedResource）");

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        EconomyConfig config = EconomyConfig.ParseJson(json);
        log?.Invoke(
            $"[Economy] economy.json 已载入：敲击 {config.Keystroke.CoinsPer}/币(日上限 {config.Keystroke.DailyCap})、" +
            $"滴灌 {config.Drip.SecondsPerCoin:0}s/币、掉落 {config.IdleDrop.IntervalMinutes:0}min/次、" +
            $"宝箱充能 {config.ChestCharge.SecondsPerChest / 3600.0:0.##}h/{config.ChestCharge.ChestId}、" +
            $"掉落桶 {config.DropBucket.MaxItems} 件/{config.DropBucket.WindowHours:0}h");
        return config;
    }
}
