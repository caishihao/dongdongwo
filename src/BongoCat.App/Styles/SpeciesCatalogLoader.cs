using System.Reflection;
using BongoCat.Core.Config;

namespace BongoCat.App.Styles;

/// <summary>
/// species.json 装载器：从本程序集（BongoCat.App）的嵌入资源读入并解析。
/// 与 styles.json 同模式（StyleCatalogLoader remarks）：嵌入资源随程序集分发、
/// 无文件路径依赖，测试项目引用 App 即可读到与生产同一份真实数据。
/// 解析 fail-fast（SpeciesCatalog.ParseJson）：坏配置启动即失败，绝不带病运行。
/// </summary>
public static class SpeciesCatalogLoader
{
    /// <summary>嵌入资源全名（默认命名空间 + 相对路径）。</summary>
    public const string ResourceName = "BongoCat.App.assets.species.json";

    public static SpeciesCatalog LoadFromAssembly(Action<string>? log = null)
    {
        Assembly assembly = typeof(SpeciesCatalogLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"嵌入资源缺失：{ResourceName}（检查 csproj EmbeddedResource）");

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        SpeciesCatalog catalog = SpeciesCatalog.ParseJson(json);
        log?.Invoke($"[Species] species.json 已载入：{catalog.Species.Count} 物种 " +
                    $"({string.Join("/", catalog.Species.Select(s => s.Id))})");
        return catalog;
    }
}
