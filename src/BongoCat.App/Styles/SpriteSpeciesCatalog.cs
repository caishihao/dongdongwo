using System.Reflection;
using System.Text.Json;

namespace BongoCat.App.Styles;

/// <summary>
/// 单风格 sprite 锚点（GDD-09 §7：220×180 逻辑坐标）。
/// pawAxis = 爪子旋转轴（爪根/臂根切口中心，敲击旋转绕此点）；hatAnchor = 头顶帽锚。
/// </summary>
public sealed record SpriteAnchors
{
    public required double PawAxisLX { get; init; }
    public required double PawAxisLY { get; init; }
    public required double PawAxisRX { get; init; }
    public required double PawAxisRY { get; init; }
    public required double HatAnchorX { get; init; }
    public required double HatAnchorY { get; init; }

    /// <summary>帽子整体缩放（阿瓜头围与猫不同；1 = 原尺寸）。</summary>
    public double HatScale { get; init; } = 1.0;
}

/// <summary>
/// sprite 物种资产目录（GDD-09 §7）：从 BongoCat.App 嵌入资源读取 anchors.json 与
/// assets/species/&lt;speciesId&gt;/&lt;styleId&gt;/{body,pawL,pawR}.png 资源流。
/// 嵌入资源随程序集分发、无文件路径依赖；解析/缺文件一律 TryGet 防御返回 false
/// （调用方回退 vector 物种渲染并记日志，绝不抛异常——sprite 物种缺资产不崩溃）。
/// </summary>
public sealed class SpriteSpeciesCatalog
{
    /// <summary>sprite 块的部分（与产出文件名对应）。</summary>
    /// <summary>v5 双帧部件（idle/tap 整绘；v4 及更早的 body/pawL/pawR 已废弃）。</summary>
    public static readonly string[] Parts = ["idle", "tap"];

    private readonly Dictionary<string, Dictionary<string, SpriteAnchors>> _anchorsBySpecies;

    /// <summary>缺 anchors 时的默认锚点（双帧模式不依赖锚点，仅满足契约）。</summary>
    private static SpriteAnchors DefaultAnchors => new()
    {
        PawAxisLX = 76, PawAxisLY = 104, PawAxisRX = 144, PawAxisRY = 104,
        HatAnchorX = 110, HatAnchorY = 4.5, HatScale = 1.0,
    };
    private readonly Assembly _assembly;

    private SpriteSpeciesCatalog(Dictionary<string, Dictionary<string, SpriteAnchors>> anchorsBySpecies, Assembly assembly)
    {
        _anchorsBySpecies = anchorsBySpecies;
        _assembly = assembly;
    }

    /// <summary>默认 sprite 物种 id（v1 仅 agua；新增 sprite 物种需扩展资源目录与本表）。</summary>
    /// <summary>sprite 物种 id 集（assets/species/&lt;id&gt;/ 有资产的物种）。</summary>
    public static readonly string[] SpriteSpeciesIds = ["agua", "xiaohuang", "xiaobai"];

    /// <summary>历史单物种常量（保留兼容）。</summary>
    public const string DefaultSpriteSpeciesId = "agua";

    /// <summary>该物种是否为 sprite 渲染路径物种。</summary>
    public static bool IsSpriteSpecies(string? speciesId)
        => speciesId is not null && Array.IndexOf(SpriteSpeciesIds, speciesId) >= 0;

    /// <summary>按风格查询锚点（未知风格返回 false → 调用方回退 vector）。</summary>
    public bool TryGetAnchors(string? speciesId, string? styleId, out SpriteAnchors anchors)
    {
        if (_anchorsBySpecies.TryGetValue(speciesId ?? string.Empty, out var byStyle)
            && byStyle.TryGetValue(styleId ?? string.Empty, out anchors!))
        {
            return true;
        }
        anchors = DefaultAnchors; // 缺锚点物种（新 sprite 物种）走默认值（双帧模式不依赖）
        return true;
    }

    /// <summary>
    /// 打开 sprite 部件资源流（调用方负责 Dispose）。缺文件返回 false。
    /// </summary>
    public bool TryOpenPart(string? speciesId, string? styleId, string part, out Stream stream)
    {
        stream = null!;
        if (!IsSpriteSpecies(speciesId) || !Parts.Contains(part, StringComparer.Ordinal))
        {
            return false;
        }

        string name = $"BongoCat.App.assets.species.{speciesId}.{styleId}.{part}.png";
        Stream? s = _assembly.GetManifestResourceStream(name);
        if (s is null)
        {
            return false;
        }

        stream = s;
        return true;
    }

    /// <summary>
    /// 从本程序集嵌入资源解析 anchors.json（防御式：资源缺失 / JSON 损坏 / 字段缺失 /
    /// 坐标越界 → 返回 null 并记日志——调用方对所有 sprite 查询回退 vector 渲染）。
    /// </summary>
    public static SpriteSpeciesCatalog? LoadFromAssembly(Action<string>? log = null)
    {
        Assembly assembly = typeof(SpriteSpeciesCatalog).Assembly;
        var bySpecies = new Dictionary<string, Dictionary<string, SpriteAnchors>>(StringComparer.Ordinal);
        foreach (string speciesId in SpriteSpeciesIds)
        {
            string resourceName = $"BongoCat.App.assets.species.{speciesId}.anchors.json";
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    bySpecies[speciesId] = []; // 缺 anchors → 默认锚点（双帧模式不依赖）
                    continue;
                }

                using var doc = JsonDocument.Parse(stream);
                JsonElement styles = doc.RootElement.GetProperty("styles");
                var anchors = new Dictionary<string, SpriteAnchors>(StringComparer.Ordinal);
                foreach (JsonProperty style in styles.EnumerateObject())
                {
                    SpriteAnchors? parsed = ParseAnchors(style.Value);
                    if (parsed is null)
                    {
                        log?.Invoke($"[Sprite] {speciesId} anchors 风格 '{style.Name}' 字段缺失/越界，用默认锚点");
                        continue;
                    }
                    anchors[style.Name] = parsed;
                }
                bySpecies[speciesId] = anchors;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Sprite] {speciesId} anchors.json 解析失败：{ex.Message}（用默认锚点）");
                bySpecies[speciesId] = [];
            }
        }

        int total = bySpecies.Values.Sum(d => d.Count);
        log?.Invoke($"[Sprite] anchors 已载入：{bySpecies.Count} 物种 / {total} 风格");
        return new SpriteSpeciesCatalog(bySpecies, assembly);
    }

    /// <summary>锚点字段解析（缺失/非数/越界返回 null；220×180 逻辑坐标，容差 4px）。</summary>
    private static SpriteAnchors? ParseAnchors(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? axisLX = PointField(el, "pawAxisL", "x");
        double? axisLY = PointField(el, "pawAxisL", "y");
        double? axisRX = PointField(el, "pawAxisR", "x");
        double? axisRY = PointField(el, "pawAxisR", "y");
        double? hatX = PointField(el, "hatAnchor", "x");
        double? hatY = PointField(el, "hatAnchor", "y");
        if (axisLX is null || axisLY is null || axisRX is null || axisRY is null || hatX is null || hatY is null)
        {
            return null;
        }

        double hatScale = 1.0;
        if (el.TryGetProperty("hatScale", out JsonElement hs) && hs.TryGetDouble(out double hv)
            && hv is >= 0.6 and <= 1.2)
        {
            hatScale = hv;
        }

        return new SpriteAnchors
        {
            PawAxisLX = axisLX.Value,
            PawAxisLY = axisLY.Value,
            PawAxisRX = axisRX.Value,
            PawAxisRY = axisRY.Value,
            HatAnchorX = hatX.Value,
            HatAnchorY = hatY.Value,
            HatScale = hatScale,
        };
    }

    private static double? PointField(JsonElement el, string objName, string fieldName)
    {
        if (!el.TryGetProperty(objName, out JsonElement obj) ||
            !obj.TryGetProperty(fieldName, out JsonElement v) ||
            v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double value))
        {
            return null;
        }

        return value is >= -4 and <= 224 ? value : null;
    }
}
