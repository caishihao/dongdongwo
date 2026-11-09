using System.Text.Json;

namespace BongoCat.Core.Cosmetics;

/// <summary>items.json 结构非法（id 重复 / 未知槽位 / 未知品质等）。</summary>
public sealed class ItemCatalogException : Exception
{
    public ItemCatalogException(string message) : base(message) { }
}

/// <summary>
/// 物品总目录：items.json 解析结果的只读索引（GDD-03 §6 数据驱动）。
/// 纯内存数据结构，零 IO；解析失败 fail-fast（<see cref="ItemCatalogException"/>），
/// 保证带病目录不会流进库存/存档（GDD-03 §7 边界：失效引用在加载侧校验）。
/// </summary>
public sealed class CosmeticCatalog
{
    private readonly Dictionary<string, CosmeticItem> _byId;

    /// <summary>全部物品（items.json 顺序）。</summary>
    public IReadOnlyList<CosmeticItem> Items { get; }

    public int Count => Items.Count;

    public CosmeticCatalog(IEnumerable<CosmeticItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list = items.ToList();
        _byId = new Dictionary<string, CosmeticItem>(list.Count, StringComparer.Ordinal);
        foreach (CosmeticItem item in list)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                throw new ItemCatalogException("物品 id 不能为空");
            }

            if (!_byId.TryAdd(item.Id, item))
            {
                throw new ItemCatalogException($"物品 id 重复：{item.Id}");
            }
        }

        Items = list;
    }

    public bool TryGet(string itemId, out CosmeticItem item)
        => _byId.TryGetValue(itemId, out item!);

    /// <summary>
    /// 从 JSON 文本解析物品目录（纯逻辑、可单测；文件/嵌入资源读取由上层负责）。
    /// 校验规则：items 非空；id 非空且唯一；displayName/artKey 非空；slot/quality 必须是已知枚举名（忽略大小写）。
    /// </summary>
    public static CosmeticCatalog ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ItemFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ItemFileDto>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new ItemCatalogException($"items.json 不是合法 JSON：{ex.Message}");
        }

        if (dto?.Items is null || dto.Items.Count == 0)
        {
            throw new ItemCatalogException("items.json 缺少 items 数组或为空");
        }

        var items = new List<CosmeticItem>(dto.Items.Count);
        foreach (ItemDto? e in dto.Items)
        {
            if (e is null)
            {
                throw new ItemCatalogException("items 数组含 null 元素");
            }

            if (string.IsNullOrWhiteSpace(e.Id))
            {
                throw new ItemCatalogException("物品 id 不能为空");
            }

            if (string.IsNullOrWhiteSpace(e.DisplayName))
            {
                throw new ItemCatalogException($"物品 {e.Id} 缺少 displayName");
            }

            if (string.IsNullOrWhiteSpace(e.ArtKey))
            {
                throw new ItemCatalogException($"物品 {e.Id} 缺少 artKey");
            }

            if (!Enum.TryParse<ItemSlot>(e.Slot, ignoreCase: true, out ItemSlot slot))
            {
                throw new ItemCatalogException($"物品 {e.Id} 未知槽位：{e.Slot}");
            }

            if (!Enum.TryParse<ItemQuality>(e.Quality, ignoreCase: true, out ItemQuality quality))
            {
                throw new ItemCatalogException($"物品 {e.Id} 未知品质：{e.Quality}");
            }

            items.Add(new CosmeticItem
            {
                Id = e.Id,
                Slot = slot,
                Quality = quality,
                DisplayName = e.DisplayName,
                ArtKey = e.ArtKey,
                SpeciesOnly = e.SpeciesOnly ?? string.Empty, // GDD-09：物种限定（缺省 = 全物种可穿）
            });
        }

        return new CosmeticCatalog(items);
    }

    /// <summary>items.json 属性名用小驼峰（id/slot/…），解析忽略大小写并容忍未知字段。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, // 容忍人工注释（items.json 首行有说明）
        AllowTrailingCommas = true,
    };

    private sealed class ItemFileDto
    {
        public List<ItemDto>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public string? Id { get; set; }
        public string? Slot { get; set; }
        public string? Quality { get; set; }
        public string? DisplayName { get; set; }
        public string? ArtKey { get; set; }
        public string? SpeciesOnly { get; set; }
    }
}
