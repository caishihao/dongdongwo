using System.Text.Json;

namespace BongoCat.Core.Config;

/// <summary>
/// 单个物种的形体参数包（GDD-09 §1/§2：物种 = 形体层，species.json 反序列化产物）。
/// 物种回答"什么形体"（剪影/部件几何/锚点偏移/专属静态特征）；渲染模板本身在 UI 层
/// StyleArt 按物种×风格构建，本记录只承载数据驱动的参数（锚点/字节映射/默认皮肤）。
/// </summary>
public sealed record SpeciesPack
{
    /// <summary>物种 id（小写字母；存档 SpeciesId 与多人渲染键；"cat"/"dino"）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（选择器页签 / 托盘菜单）。</summary>
    public required string Name { get; init; }

    /// <summary>多人 species 字节枚举（0~1，与 Protocol.CatSpeciesByte 对齐）。</summary>
    public required byte ByteId { get; init; }

    /// <summary>
    /// hat 锚点 Y 偏移增量（px，负 = 上移；与 styles.json 风格层 HatAnchorOffsetY 相加合成，
    /// GDD-09 §2.3。恐龙 = -14（落位双角之间）；渲染侧对合成结果做窗体上缘钳制）。
    /// </summary>
    public required double HatAnchorOffsetY { get; init; }

    /// <summary>
    /// 该物种的默认专属皮肤 id（items.json speciesOnly 物品；空串 = 无专属皮肤，用通用默认）。
    /// 恐龙 = dino_default（柚黄+青绿三点，GDD-09 §2.4）。
    /// </summary>
    public string DefaultSkinId { get; init; } = string.Empty;
}

/// <summary>
/// 物种目录（species.json 解析产物）：id / byte 双索引 + 防御查询。
/// 加载即 fail-fast（模式同 StyleCatalog），运行期查询只做 TryGet 防御回退，绝不抛异常。
/// </summary>
public sealed class SpeciesCatalog
{
    private readonly Dictionary<string, SpeciesPack> _byId;
    private readonly Dictionary<byte, SpeciesPack> _byByte;

    private SpeciesCatalog(IReadOnlyList<SpeciesPack> species)
    {
        _byId = new Dictionary<string, SpeciesPack>(StringComparer.Ordinal);
        _byByte = new Dictionary<byte, SpeciesPack>();
        foreach (SpeciesPack pack in species)
        {
            _byId[pack.Id] = pack;
            _byByte[pack.ByteId] = pack;
        }

        Species = species;
    }

    /// <summary>全部物种（加载顺序 = 选择器页签顺序：cat 在首）。</summary>
    public IReadOnlyList<SpeciesPack> Species { get; }

    /// <summary>默认物种 id（GDD-09 §5：非法/空 SpeciesId 回退 cat；首个物种恒为 cat）。</summary>
    public const string DefaultSpeciesId = "cat";

    /// <summary>按 id 查询（未知 id 返回 false —— 调用方回退 cat 并记日志，GDD-09 §5）。</summary>
    public bool TryGet(string? speciesId, out SpeciesPack species)
        => _byId.TryGetValue(speciesId ?? string.Empty, out species!);

    /// <summary>按多人 species 字节查询（未知字节返回 false → 调用方回退 cat）。</summary>
    public bool TryGetByByte(byte speciesByte, out SpeciesPack species)
        => _byByte.TryGetValue(speciesByte, out species!);

    /// <summary>id → 多人字节（未知 id 回退 cat 字节 0；发送侧防御）。</summary>
    public byte ToByte(string? speciesId)
        => _byId.TryGetValue(speciesId ?? string.Empty, out SpeciesPack? species) ? species.ByteId : (byte)0;

    /// <summary>
    /// 从 species.json 解析（fail-fast：缺失字段 / 非法值 / 重复 id / 字节冲突 /
    /// 首物种不是 cat 一律抛 InvalidOperationException——坏配置宁可启动失败也不带病运行）。
    /// </summary>
    public static SpeciesCatalog ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("species", out JsonElement speciesEl) ||
            speciesEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("species.json 缺少 species 数组");
        }

        var species = new List<SpeciesPack>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenBytes = new HashSet<byte>();
        foreach (JsonElement el in speciesEl.EnumerateArray())
        {
            SpeciesPack pack = ParseSpecies(el);
            if (!seenIds.Add(pack.Id))
            {
                throw new InvalidOperationException($"species.json 物种 id 重复：{pack.Id}");
            }

            if (!seenBytes.Add(pack.ByteId))
            {
                throw new InvalidOperationException($"species.json 物种字节冲突：{pack.ByteId}（{pack.Id}）");
            }

            if (pack.ByteId > 15)
            {
                throw new InvalidOperationException($"species.json {pack.Id} 字节 {pack.ByteId} 超出预留段（0~15，GDD-09 §7）");
            }

            species.Add(pack);
        }

        if (species.Count == 0)
        {
            throw new InvalidOperationException("species.json species 数组为空");
        }

        if (species[0].Id != DefaultSpeciesId)
        {
            throw new InvalidOperationException("species.json 首个物种必须是 cat（默认回退语义依赖此约定）");
        }

        return new SpeciesCatalog(species);
    }

    private static SpeciesPack ParseSpecies(JsonElement el)
    {
        return new SpeciesPack
        {
            Id = RequireString(el, "id"),
            Name = RequireString(el, "name"),
            ByteId = (byte)RequireInt(el, "byteId", min: 0, max: 255),
            HatAnchorOffsetY = RequireDouble(el, "hatAnchorOffsetY", min: -64, max: 64),
            DefaultSkinId = OptionalString(el, "defaultSkinId"),
        };
    }

    // ── JSON 字段读取辅助（缺失即抛——fail-fast 纪律，与 StyleCatalog 同签名）──

    private static string RequireString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"species.json 缺少字符串字段：{name}");

    private static string OptionalString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : string.Empty;

    private static int RequireInt(JsonElement el, string name, int min, int max)
    {
        if (!el.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number ||
            !v.TryGetInt32(out int value) || value < min || value > max)
        {
            throw new InvalidOperationException($"species.json 字段 {name} 缺失或越界 [{min},{max}]");
        }

        return value;
    }

    private static double RequireDouble(JsonElement el, string name, double min, double max)
    {
        if (!el.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double value) ||
            double.IsNaN(value) || value < min || value > max)
        {
            throw new InvalidOperationException($"species.json 字段 {name} 缺失或越界 [{min},{max}]");
        }

        return value;
    }
}
