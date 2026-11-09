namespace BongoCat.Core.Cosmetics;

/// <summary>
/// 装扮槽位类型（GDD-03 §5；E3 先开放 hat / skin / bongos 三槽，emote/uitheme/consumable 留枚举位）。
/// 每类槽位同时只能装备一件（槽位互斥）。
/// </summary>
public enum ItemSlot
{
    Hat,
    Skin,
    Bongos,
}

/// <summary>
/// 物品品质（GDD-03 §6 品质分级；品质即独立物品——同一形象按品质区分）。
/// 颜色为自设计色板（<see cref="QualityPalette"/>），不使用原作色值。
/// </summary>
public enum ItemQuality
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary,
    Unique,
}

/// <summary>单件装扮物品定义（GDD-03 §6：全部由 items.json 驱动，程序不硬编码）。</summary>
public sealed class CosmeticItem
{
    /// <summary>物品唯一 id（如 hat_dev_cap）。</summary>
    public required string Id { get; init; }

    /// <summary>装备槽位。</summary>
    public required ItemSlot Slot { get; init; }

    /// <summary>品质。</summary>
    public required ItemQuality Quality { get; init; }

    /// <summary>显示名（原创中文命名）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>美术引用键（UI 层据此换绑 Geometry / 填色组）。</summary>
    public required string ArtKey { get; init; }

    /// <summary>
    /// 物种限定（GDD-09 §1：items.json 新增 speciesOnly 字段；空串 = 全物种可穿；
    /// "cat"/"dino" = 仅该物种可装备——如恐龙默认专属皮肤 dino_default 仅龙可穿，
    /// 猫玩家不可穿；四色板恐龙可穿 = 四色板不带 speciesOnly）。跨物种穿装由
    /// InventoryService 换装时防御拒绝（存档清洗模式同失效引用卸下）。
    /// </summary>
    public string SpeciesOnly { get; init; } = string.Empty;

    public override string ToString() => $"{Id}({Slot}/{Quality})";
}

/// <summary>
/// 品质色板（自设计，延续黏土软萌 UI 的奶油底 + 高饱和品质点缀）。
/// Core 层只存 HEX 字符串（纯数据可测），Brush 转换在 UI 层。
/// </summary>
public static class QualityPalette
{
    /// <summary>品质 → 自设计 HEX 色值（#RRGGBB）。</summary>
    public static readonly IReadOnlyDictionary<ItemQuality, string> Hex = new Dictionary<ItemQuality, string>
    {
        [ItemQuality.Common] = "#9E9E9E",
        [ItemQuality.Uncommon] = "#4CAF50",
        [ItemQuality.Rare] = "#2196F3",
        [ItemQuality.Epic] = "#9C27B0",
        [ItemQuality.Legendary] = "#FF9800",
        [ItemQuality.Unique] = "#E91E63",
    };

    /// <summary>取品质色 HEX，未知品质返回灰（防御枚举扩展忘配色的兜底）。</summary>
    public static string GetHex(ItemQuality quality)
        => Hex.TryGetValue(quality, out string? hex) ? hex : "#9E9E9E";
}
