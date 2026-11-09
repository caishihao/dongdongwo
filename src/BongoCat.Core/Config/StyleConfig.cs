using System.Text.Json;

namespace BongoCat.Core.Config;

/// <summary>
/// 单个风格的渲染参数包（GDD-08 §2 全部数值数据驱动；styles.json 反序列化产物）。
/// 风格 = 渲染语言层：描边策略 / 配色转译 / 动画手感 / 层错位；不改猫姿势结构
/// （坐姿、鼓位、爪锚点与黏土猫一致）。数值出处 = GDD-08 §2.6 参数总表。
/// </summary>
public sealed record StylePack
{
    /// <summary>风格 id（小写字母；存档 CatStyleId 与多人渲染键）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（选择器卡片 / 托盘菜单）。</summary>
    public required string Name { get; init; }

    /// <summary>多人 style 字节枚举（0~5，与 Protocol.CatStyleByte 对齐）。</summary>
    public required byte ByteId { get; init; }

    /// <summary>图层数（预算 ≤8，GDD-08 §2；剪纸 7）。</summary>
    public required int Layers { get; init; }

    // ── 描边规格（模板笔法；0 = 无描边）────────────────────────────
    /// <summary>轮廓描边宽（逻辑 px；clay 8 / sticker 5 / flat 0 / sketch 2.2 / chibi 9 / papercut 0）。</summary>
    public required double OutlineWidth { get; init; }

    /// <summary>轮廓描边色（#RRGGBB；sketch 墨色 #4A403A、chibi 厚描边 #2B2B2B、贴纸白等）。</summary>
    public required string OutlineColor { get; init; }

    /// <summary>
    /// 内描边宽（逻辑 px；贴纸风白描边内侧的细深棕线 1.8，参照 ai-sticker 双层描边；0 = 无）。
    /// 实现语义：内描边画在填充形状自身边缘（居中描边，内外各半），叠于白色底版描边之上。
    /// </summary>
    public double InnerOutlineWidth { get; init; }

    /// <summary>内描边色（#RRGGBB；贴纸 #6B4A32；仅 InnerOutlineWidth &gt; 0 时生效）。</summary>
    public string InnerOutlineColor { get; init; } = string.Empty;

    // ── 投影（不占层：DropShadowEffect 挂层实现）────────────────────
    /// <summary>投影不透明度 0~1（0 = 无投影；sticker 0.22 / papercut 0.30）。</summary>
    public required double ShadowOpacity { get; init; }

    /// <summary>投影 X 偏移（px；sticker 0 / papercut 2 错位阴影）。</summary>
    public required double ShadowOffsetX { get; init; }

    /// <summary>投影 Y 偏移（px；sticker 6 / papercut 3）。</summary>
    public required double ShadowOffsetY { get; init; }

    /// <summary>投影模糊半径（px）。</summary>
    public required double ShadowBlur { get; init; }

    // ── 敲击动画（GDD-08 §2.6 总表；reboundMs 受用户设置全局系数缩放）──
    /// <summary>下压深度（逻辑 px）。</summary>
    public required double TapDownPx { get; init; }

    /// <summary>回弹时长基线（ms；实际 = 本值 × 用户 ReboundMs/120 系数）。</summary>
    public required double ReboundMs { get; init; }

    /// <summary>回弹缓动类型（back / quadratic / elastic；fail-fast 校验白名单）。</summary>
    public required string Easing { get; init; }

    /// <summary>BackEase 幅度（back 专用；quadratic/elastic 为 0）。</summary>
    public required double Amplitude { get; init; }

    /// <summary>ElasticEase 振荡次数（elastic 专用）。</summary>
    public required int Oscillations { get; init; }

    /// <summary>ElasticEase 弹性（elastic 专用）。</summary>
    public required double Springiness { get; init; }

    /// <summary>身体挤压 scaleY（1 = 无挤压；clay 0.94 / sticker 0.93 / chibi 0.90）。</summary>
    public required double SquashScaleY { get; init; }

    /// <summary>爪落下旋转量（度；sketch 2 / chibi 4，方向按左右爪取反）。</summary>
    public required double PawRotationDeg { get; init; }

    /// <summary>敲中时鼓下沉量（px；chibi 3，其余 0）。</summary>
    public required double BongoSinkPx { get; init; }

    /// <summary>层错位反弹量（px；papercut hat/face 2，其余 0）。</summary>
    public required double LayerBouncePx { get; init; }

    /// <summary>层错位归位时长（ms；papercut 80）。</summary>
    public required double LayerBounceMs { get; init; }

    // ── 皮肤色板风格转译（GDD-08 §1.1：内容层色板恒定，风格用自身语言转译）──
    /// <summary>皮肤填充不透明度（sketch 0.12 = 12% 淡填充，其余 1.0）。</summary>
    public required double SkinFillOpacity { get; init; }

    /// <summary>纸底色（sketch 线稿本色 #FDF8EF；空串 = 无纸底）。</summary>
    public required string PaperBaseColor { get; init; }

    /// <summary>奶牛斑转译模式（solid 色块 / outline 轮廓线不填充 / paper 独立色纸片）。</summary>
    public required string SpotMode { get; init; }

    /// <summary>是否加两点高光（chibi true，其余 false）。</summary>
    public required bool HighlightDots { get; init; }

    /// <summary>该风格下默认鼓外观（空 = 沿用内容层默认陶土鼓；flat #5B8DEF 单色）。</summary>
    public required string BongosDefaultBody { get; init; }

    /// <summary>该风格下默认鼓沿色（与 BongosDefaultBody 配套；空 = 内容层默认）。</summary>
    public required string BongosDefaultRim { get; init; }

    /// <summary>hat 锚点 Y 偏移（px，负 = 上移；chibi 头身比 1.4:1 上移 12，其余 0）。</summary>
    public required double HatAnchorOffsetY { get; init; }

    // ── 品质 token（GDD-08 §1.3 跨风格统一 token 的转译参数）─────────
    /// <summary>legendary 金描边色（#E8B33C）。</summary>
    public required string LegendaryStrokeColor { get; init; }

    /// <summary>unique 彩虹描边标识（"rainbow" = 渐变描边 + 微光尘 token）。</summary>
    public required string UniqueStrokeColor { get; init; }

    /// <summary>缓动类型白名单（fail-fast）。</summary>
    public static readonly IReadOnlyList<string> ValidEasings = ["back", "quadratic", "elastic"];

    /// <summary>奶牛斑转译模式白名单。</summary>
    public static readonly IReadOnlyList<string> ValidSpotModes = ["solid", "outline", "paper"];
}

/// <summary>
/// 风格目录（styles.json 解析产物）：id / byte 双索引 + 防御查询。
/// 加载即 fail-fast（模式同 EconomyConfigLoader/EconomyConfig.ParseJson），
/// 运行期查询只做 TryGet 防御回退，绝不抛异常。
/// </summary>
public sealed class StyleCatalog
{
    private readonly Dictionary<string, StylePack> _byId;
    private readonly Dictionary<byte, StylePack> _byByte;

    private StyleCatalog(IReadOnlyList<StylePack> styles)
    {
        _byId = new Dictionary<string, StylePack>(StringComparer.Ordinal);
        _byByte = new Dictionary<byte, StylePack>();
        foreach (StylePack style in styles)
        {
            _byId[style.Id] = style;
            _byByte[style.ByteId] = style;
        }

        Styles = styles;
    }

    /// <summary>全部风格（加载顺序 = 选择器卡片顺序：clay 在首）。</summary>
    public IReadOnlyList<StylePack> Styles { get; }

    /// <summary>默认风格 id（GDD-08 §3：跳过选择器 = clay；首个风格恒为 clay）。</summary>
    public const string DefaultStyleId = "clay";

    /// <summary>按 id 查询（未知 id 返回 false —— 调用方回退 clay 并记日志，GDD-08 §6 边界 1）。</summary>
    public bool TryGet(string? styleId, out StylePack style)
        => _byId.TryGetValue(styleId ?? string.Empty, out style!);

    /// <summary>按多人 style 字节查询（未知字节返回 false → 调用方回退 clay）。</summary>
    public bool TryGetByByte(byte styleByte, out StylePack style)
        => _byByte.TryGetValue(styleByte, out style!);

    /// <summary>id → 多人字节（未知 id 回退 clay 字节 0；发送侧防御）。</summary>
    public byte ToByte(string? styleId)
        => _byId.TryGetValue(styleId ?? string.Empty, out StylePack? style) ? style.ByteId : (byte)0;

    /// <summary>
    /// 从 styles.json 解析（fail-fast：缺失字段 / 非法值 / 重复 id / 字节冲突 / 超层预算一律抛
    /// InvalidOperationException，模式同 EconomyConfig.ParseJson——坏配置宁可启动失败也不带病运行）。
    /// </summary>
    public static StyleCatalog ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("styles", out JsonElement stylesEl) ||
            stylesEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("styles.json 缺少 styles 数组");
        }

        var styles = new List<StylePack>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenBytes = new HashSet<byte>();
        foreach (JsonElement el in stylesEl.EnumerateArray())
        {
            StylePack style = ParseStyle(el);
            if (!seenIds.Add(style.Id))
            {
                throw new InvalidOperationException($"styles.json 风格 id 重复：{style.Id}");
            }

            if (!seenBytes.Add(style.ByteId))
            {
                throw new InvalidOperationException($"styles.json 风格字节冲突：{style.ByteId}（{style.Id}）");
            }

            if (style.Layers is < 1 or > 8)
            {
                throw new InvalidOperationException($"styles.json {style.Id} 图层数 {style.Layers} 超预算（≤8）");
            }

            if (!StylePack.ValidEasings.Contains(style.Easing, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"styles.json {style.Id} 缓动类型非法：{style.Easing}");
            }

            if (!StylePack.ValidSpotModes.Contains(style.SpotMode, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"styles.json {style.Id} 奶牛斑转译模式非法：{style.SpotMode}");
            }

            if (style.TapDownPx < 0 || style.ReboundMs <= 0 || style.SquashScaleY <= 0 ||
                style.SkinFillOpacity is < 0 or > 1)
            {
                throw new InvalidOperationException($"styles.json {style.Id} 动画/皮肤参数越界（压深/时长/挤压/透明度）");
            }

            // 内描边 fail-fast：开了宽度就必须给颜色，且宽度在描边规格同一量程内
            if (style.InnerOutlineWidth is < 0 or > 64 ||
                (style.InnerOutlineWidth > 0 && style.InnerOutlineColor.Length == 0))
            {
                throw new InvalidOperationException($"styles.json {style.Id} 内描边参数非法（宽度越界或缺颜色）");
            }

            styles.Add(style);
        }

        if (styles.Count == 0)
        {
            throw new InvalidOperationException("styles.json styles 数组为空");
        }

        if (styles[0].Id != DefaultStyleId)
        {
            throw new InvalidOperationException("styles.json 首个风格必须是 clay（默认回退语义依赖此约定）");
        }

        return new StyleCatalog(styles);
    }

    private static StylePack ParseStyle(JsonElement el)
    {
        return new StylePack
        {
            Id = RequireString(el, "id"),
            Name = RequireString(el, "name"),
            ByteId = (byte)RequireInt(el, "byteId", min: 0, max: 255),
            Layers = RequireInt(el, "layers", min: 1, max: 255),
            OutlineWidth = RequireDouble(el, "outlineWidth", min: 0, max: 64),
            OutlineColor = RequireString(el, "outlineColor"),
            InnerOutlineWidth = OptionalDouble(el, "innerOutlineWidth"),
            InnerOutlineColor = OptionalString(el, "innerOutlineColor"),
            ShadowOpacity = RequireDouble(el, "shadowOpacity", min: 0, max: 1),
            ShadowOffsetX = RequireDouble(el, "shadowOffsetX", min: -64, max: 64),
            ShadowOffsetY = RequireDouble(el, "shadowOffsetY", min: -64, max: 64),
            ShadowBlur = RequireDouble(el, "shadowBlur", min: 0, max: 128),
            TapDownPx = RequireDouble(el, "tapDownPx", min: 0, max: 128),
            ReboundMs = RequireDouble(el, "reboundMs", min: 1, max: 2000),
            Easing = RequireString(el, "easing"),
            Amplitude = OptionalDouble(el, "amplitude"),
            Oscillations = OptionalInt(el, "oscillations"),
            Springiness = OptionalDouble(el, "springiness"),
            SquashScaleY = RequireDouble(el, "squashScaleY", min: 0.1, max: 1),
            PawRotationDeg = RequireDouble(el, "pawRotationDeg", min: 0, max: 45),
            BongoSinkPx = RequireDouble(el, "bongoSinkPx", min: 0, max: 64),
            LayerBouncePx = RequireDouble(el, "layerBouncePx", min: 0, max: 64),
            LayerBounceMs = RequireDouble(el, "layerBounceMs", min: 0, max: 2000),
            SkinFillOpacity = RequireDouble(el, "skinFillOpacity", min: 0, max: 1),
            PaperBaseColor = OptionalString(el, "paperBaseColor"),
            SpotMode = RequireString(el, "spotMode"),
            HighlightDots = OptionalBool(el, "highlightDots"),
            BongosDefaultBody = OptionalString(el, "bongosDefaultBody"),
            BongosDefaultRim = OptionalString(el, "bongosDefaultRim"),
            HatAnchorOffsetY = RequireDouble(el, "hatAnchorOffsetY", min: -64, max: 64),
            LegendaryStrokeColor = OptionalString(el, "legendaryStrokeColor", fallback: "#E8B33C"),
            UniqueStrokeColor = OptionalString(el, "uniqueStrokeColor", fallback: "rainbow"),
        };
    }

    // ── JSON 字段读取辅助（缺失即抛——fail-fast 纪律）──────────────────

    private static string RequireString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"styles.json 缺少字符串字段：{name}");

    private static string OptionalString(JsonElement el, string name, string fallback = "")
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : fallback;

    private static int RequireInt(JsonElement el, string name, int min, int max)
    {
        if (!el.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number ||
            !v.TryGetInt32(out int value) || value < min || value > max)
        {
            throw new InvalidOperationException($"styles.json 字段 {name} 缺失或越界 [{min},{max}]");
        }

        return value;
    }

    private static int OptionalInt(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int value)
            ? value
            : 0;

    private static double RequireDouble(JsonElement el, string name, double min, double max)
    {
        if (!el.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double value) ||
            double.IsNaN(value) || value < min || value > max)
        {
            throw new InvalidOperationException($"styles.json 字段 {name} 缺失或越界 [{min},{max}]");
        }

        return value;
    }

    private static double OptionalDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double value)
            ? value
            : 0;

    private static bool OptionalBool(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
