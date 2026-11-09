using System.Reflection;
using BongoCat.Core.Cosmetics;

namespace BongoCat.App.Inventory;

/// <summary>
/// items.json 装载器：从本程序集（BongoCat.App）的嵌入资源读入并解析。
/// 放 App 层的理由（见 InventoryService remarks）：物品定义是库存逻辑的消费数据；
/// 嵌入资源非文件系统 IO，随程序集分发、测试可直接读同一份真实数据。
/// </summary>
public static class ItemCatalogLoader
{
    /// <summary>嵌入资源全名（默认命名空间 + 相对路径）。</summary>
    public const string ResourceName = "BongoCat.App.assets.items.json";

    public static CosmeticCatalog LoadFromAssembly(Action<string>? log = null)
    {
        Assembly assembly = typeof(ItemCatalogLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"嵌入资源缺失：{ResourceName}（检查 csproj EmbeddedResource）");

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        CosmeticCatalog catalog = CosmeticCatalog.ParseJson(json);
        log?.Invoke($"[Inventory] items.json 已载入：{catalog.Count} 件物品");
        return catalog;
    }
}
