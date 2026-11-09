using System.Reflection;
using BongoCat.Core.Config;

namespace BongoCat.App.Styles;

/// <summary>
/// styles.json 装载器：从本程序集（BongoCat.App）的嵌入资源读入并解析。
/// 与 economy.json 同模式（EconomyConfigLoader remarks）：嵌入资源随程序集分发、
/// 无文件路径依赖，测试项目引用 App 即可读到与生产同一份真实数值。
/// 解析 fail-fast（StyleCatalog.ParseJson）：坏配置启动即失败，绝不带病运行。
/// </summary>
public static class StyleCatalogLoader
{
    /// <summary>嵌入资源全名（默认命名空间 + 相对路径）。</summary>
    public const string ResourceName = "BongoCat.App.assets.styles.json";

    public static StyleCatalog LoadFromAssembly(Action<string>? log = null)
    {
        Assembly assembly = typeof(StyleCatalogLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"嵌入资源缺失：{ResourceName}（检查 csproj EmbeddedResource）");

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        StyleCatalog catalog = StyleCatalog.ParseJson(json);
        log?.Invoke($"[Styles] styles.json 已载入：{catalog.Styles.Count} 风格 " +
                    $"({string.Join("/", catalog.Styles.Select(s => s.Id))})，层预算 ≤8 全部达标");
        return catalog;
    }
}
