using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BongoCat.Core.Cosmetics;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// 装扮美术库（E3-S3）：全部原创 XAML Geometry 简笔绘制，延续 MainWindow 黏土软萌语言
/// （平涂 + 白描边底版技法：每个彩色形状先画一遍白填充 + 白粗描边底版，恒留 ~4px 白边）。
///
/// 单一数据源：主窗口 PartHat 换绑、背包/图鉴缩略图都走这里的工厂，杜绝两套几何。
/// 换装性能纪律：皮肤/鼓只改已命名形状的 Fill/Stroke（纯渲染属性，不触发 Layout 重算）；
/// 帽子用预建画布切 Visibility（装备是用户低频动作，且预建后零几何重建）。
/// </summary>
public static class CosmeticArt
{
    /// <summary>主画布坐标空间（与 MainWindow.xaml CatCanvas 一致，帽子画布直接叠放对位）。</summary>
    private const double ArtWidth = 240;
    private const double ArtHeight = 200;

    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Ink = Color.FromRgb(0x71, 0x2B, 0x13);
    private static readonly Color Blush = Color.FromRgb(0xF4, 0xC0, 0xD1);
    private static readonly Color GrayFill = Color.FromRgb(0xC9, 0xC2, 0xB6);   // 未拥有剪影
    private static readonly Color GrayDark = Color.FromRgb(0xA8, 0xA1, 0x94);

    // ── 皮肤色板（artKey → 身体/耳/耳内/爪线）──────────────────────────
    // 默认皮肤（空 artKey）= E1 用户拍板的原创基础配色。
    private static readonly Dictionary<string, (Color Body, Color Ear, Color Inner, Color Line)> Skins = new(StringComparer.Ordinal)
    {
        [""] = (FromRgb(0xF5, 0xC4, 0xB3), FromRgb(0xF0, 0x99, 0x7B), FromRgb(0xE8, 0x82, 0x6B), FromRgb(0xD8, 0x5A, 0x30)),
        ["skin_cream"] = (FromRgb(0xF7, 0xE7, 0xC8), FromRgb(0xED, 0xD3, 0xA4), FromRgb(0xDF, 0xBA, 0x85), FromRgb(0xC7, 0x9B, 0x5F)),
        ["skin_orange"] = (FromRgb(0xF2, 0xA6, 0x5A), FromRgb(0xE2, 0x8C, 0x3F), FromRgb(0xC9, 0x6F, 0x2E), FromRgb(0xB4, 0x5E, 0x22)),
        ["skin_gray"] = (FromRgb(0xBF, 0xC3, 0xCC), FromRgb(0xA2, 0xA7, 0xB2), FromRgb(0x8A, 0x8F, 0x9B), FromRgb(0x76, 0x7B, 0x87)),
        ["skin_cow"] = (FromRgb(0xF6, 0xF3, 0xEC), FromRgb(0xED, 0xEA, 0xE2), FromRgb(0xD9, 0xD5, 0xCA), FromRgb(0xC9, 0xC4, 0xB8)),

        // GDD-09 §2.4：恐龙默认专属皮肤（speciesOnly=dino）——柚黄 + 米白吻腹 + 柚黄加深爪/角基。
        // 青绿点缀（#2EA98F）不在此表：物种固定色烘焙在形体层，不随皮肤色板变。
        ["dino_default"] = (FromRgb(0xEF, 0xD3, 0x5D), FromRgb(0xD9, 0xBC, 0x4F), FromRgb(0xF7, 0xF1, 0xE3), FromRgb(0xB8, 0x9A, 0x2E)),
    };

    private const string CowSkinKey = "skin_cow";

    // ── 鼓色板（artKey → 鼓身/鼓沿；空 = E1 基础陶土鼓）────────────────
    private static readonly Dictionary<string, (Color Body, Color Rim)> Bongos = new(StringComparer.Ordinal)
    {
        [""] = (FromRgb(0xD8, 0x5A, 0x30), FromRgb(0x99, 0x3C, 0x1D)),
        ["bongos_wood"] = (FromRgb(0xC9, 0x8A, 0x4B), FromRgb(0x8A, 0x5A, 0x2B)),
        ["bongos_neon"] = (FromRgb(0x38, 0xC7, 0xDE), FromRgb(0x1B, 0x7E, 0x93)),
        ["bongos_gold"] = (FromRgb(0xE4, 0xB5, 0x4A), FromRgb(0xA8, 0x7C, 0x1C)),
    };

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze(); // 跨渲染冻结：零变更通知开销（ADR-003 渲染纪律）
        return b;
    }

    /// <summary>取皮肤色板；未知 artKey 防御回落默认（存档脏数据不崩溃）。</summary>
    private static (Color Body, Color Ear, Color Inner, Color Line) GetSkin(string? artKey)
        => Skins.TryGetValue(artKey ?? string.Empty, out var p) ? p : Skins[""];

    private static (Color Body, Color Rim) GetBongos(string? artKey)
        => Bongos.TryGetValue(artKey ?? string.Empty, out var p) ? p : Bongos[""];

    // ── 皮肤应用（只改 Fill/Stroke + 奶牛纹斑点可见性）────────────────

    /// <summary>主窗口皮肤换绑目标（MainWindow.xaml 中已命名的毛色形状）。</summary>
    public sealed class SkinTargets
    {
        public required Shape BodyFur { get; init; }
        public required Shape TailFur { get; init; }      // 尾巴彩色描边
        public required Shape EarLFur { get; init; }
        public required Shape EarRFur { get; init; }
        public required Shape EarInnerL { get; init; }
        public required Shape EarInnerR { get; init; }
        public required Shape NoseFill { get; init; }
        public required Shape PawLFur { get; init; }
        public required Shape PawRFur { get; init; }
        public required Shape PawLineL { get; init; }     // 趾线描边
        public required Shape PawLineR { get; init; }
        public required FrameworkElement CowSpots { get; init; } // 奶牛纹斑点组（默认 Collapsed）

        /// <summary>
        /// 物种附属形状组（GDD-09：恐龙棘刺/双角基座随"耳"同槽换色；null = 猫模板无附属形状）。
        /// </summary>
        public IReadOnlyList<Shape>? BodyAppendages { get; init; }
    }

    public static void ApplySkin(string? artKey, SkinTargets t)
    {
        var (body, ear, inner, line) = GetSkin(artKey);
        t.BodyFur.Fill = Brush(body);
        t.TailFur.Stroke = Brush(ear);
        t.EarLFur.Fill = Brush(ear);
        t.EarRFur.Fill = Brush(ear);
        t.EarInnerL.Fill = Brush(inner);
        t.EarInnerR.Fill = Brush(inner);
        t.NoseFill.Fill = Brush(inner);
        t.PawLFur.Fill = Brush(ear);
        t.PawRFur.Fill = Brush(ear);
        t.PawLineL.Stroke = Brush(line);
        t.PawLineR.Stroke = Brush(line);
        t.CowSpots.Visibility = artKey == CowSkinKey ? Visibility.Visible : Visibility.Collapsed;

        // GDD-09：恐龙附属形状（棘刺/双角基座）与耳同槽换色（色板转译规则不变）
        if (t.BodyAppendages is { } appendages)
        {
            foreach (Shape shape in appendages)
            {
                shape.Fill = Brush(ear);
            }
        }
    }

    // ── 鼓应用（只改 Fill）────────────────────────────────────────────

    public sealed class BongosTargets
    {
        public required Shape BodyL { get; init; }
        public required Shape RimL { get; init; }
        public required Shape BodyR { get; init; }
        public required Shape RimR { get; init; }
    }

    public static void ApplyBongos(string? artKey, BongosTargets t)
    {
        var (body, rim) = GetBongos(artKey);
        t.BodyL.Fill = Brush(body);
        t.BodyR.Fill = Brush(body);
        t.RimL.Fill = Brush(rim);
        t.RimR.Fill = Brush(rim);
    }

    // ── 帽子（原创简笔，白底版 + 平涂，坐标同主画布 240×200）──────────

    /// <summary>主窗口 PartHat 预建的 5 顶帽子画布（全部 Collapsed，装备时切 Visibility；Tag=artKey）。</summary>
    public static IReadOnlyDictionary<string, Canvas> CreateHatCanvases()
    {
        var hats = new Dictionary<string, Canvas>
        {
            ["hat_dev_cap"] = BuildHatDevCap(),
            ["hat_beret"] = BuildHatBeret(),
            ["hat_santa"] = BuildHatSanta(),
            ["hat_graduation"] = BuildHatGraduation(),
            ["hat_flower_crown"] = BuildHatFlowerCrown(),
        };

        foreach ((string key, Canvas canvas) in hats)
        {
            canvas.Tag = key;
            canvas.Visibility = Visibility.Collapsed;
        }

        return hats;
    }

    private static Path P(string data, Color? fill, double strokeThickness = 0)
    {
        var path = new Path { Data = Geometry.Parse(data) };
        if (fill is not null)
        {
            path.Fill = Brush(fill.Value);
        }

        if (strokeThickness > 0)
        {
            path.Stroke = Brush(White);
            path.StrokeThickness = strokeThickness;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }

        return path;
    }

    /// <summary>描边路径（w 嘴等线条笔画；非白底版）。</summary>
    private static Path PS(string data, Color stroke, double thickness)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = Brush(stroke),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        return path;
    }

    private static Ellipse E(double left, double top, double w, double h, Color fill, double strokeThickness = 0, double angle = 0)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = Brush(fill) };
        if (strokeThickness > 0)
        {
            e.Stroke = Brush(White);
            e.StrokeThickness = strokeThickness;
        }

        if (angle != 0)
        {
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            e.RenderTransform = new RotateTransform(angle);
        }

        Canvas.SetLeft(e, left);
        Canvas.SetTop(e, top);
        return e;
    }

    private static Canvas NewArtCanvas()
        => new() { Width = ArtWidth, Height = ArtHeight, Background = Brushes.Transparent };

    /// <summary>程序员鸭舌帽（common）：灰蓝圆顶 + 前伸帽檐 + 顶扣。</summary>
    private static Canvas BuildHatDevCap()
    {
        var c = NewArtCanvas();
        c.Children.Add(P("M 74,56 A 46,40 0 1 1 166,56 Z", White, 8));          // 圆顶白底版
        c.Children.Add(P("M 74,56 A 46,40 0 1 1 166,56 Z", FromRgb(0x7E, 0x93, 0xAB)));
        c.Children.Add(P("M 64,56 Q 120,42 176,56 Q 120,70 64,56 Z", White, 6)); // 帽檐白底版
        c.Children.Add(P("M 64,56 Q 120,42 176,56 Q 120,70 64,56 Z", FromRgb(0x64, 0x7A, 0x93)));
        c.Children.Add(E(112, 8, 16, 16, White));                                // 顶扣白底版
        c.Children.Add(E(115, 11, 10, 10, FromRgb(0xA5, 0xB7, 0xC9)));
        return c;
    }

    /// <summary>画家贝雷帽（uncommon）：酒红斜戴软塌 + 小帽蒂。</summary>
    private static Canvas BuildHatBeret()
    {
        var c = NewArtCanvas();
        c.Children.Add(E(66, 22, 108, 40, White, 8, -8));                        // 帽体白底版
        c.Children.Add(E(66, 22, 108, 40, FromRgb(0xC0, 0x57, 0x6B), 0, -8));
        c.Children.Add(E(80, 12, 76, 30, White, 6, -8));                         // 软塌鼓包白底版
        c.Children.Add(E(80, 12, 76, 30, FromRgb(0xC0, 0x57, 0x6B), 0, -8));
        c.Children.Add(E(113, 4, 14, 14, White, 4));                             // 帽蒂白底版
        c.Children.Add(E(115, 6, 9, 9, FromRgb(0x8E, 0x3D, 0x4E)));
        return c;
    }

    /// <summary>圣诞尖尖帽（rare）：红尖垂右 + 白绒球 + 白绒边。</summary>
    private static Canvas BuildHatSanta()
    {
        var c = NewArtCanvas();
        c.Children.Add(P("M 82,54 Q 88,10 132,14 Q 158,16 148,34", White, 7));   // 尖锥白底版
        c.Children.Add(P("M 82,54 Q 88,10 132,14 Q 158,16 148,34", FromRgb(0xD9, 0x53, 0x4F)));
        var pom = E(139, 25, 22, 22, White);
        pom.Stroke = Brush(FromRgb(0xE3, 0xDC, 0xD2));
        pom.StrokeThickness = 1.5;
        c.Children.Add(pom);                                                     // 绒球
        c.Children.Add(E(66, 46, 108, 20, White));                               // 白绒边（自白，盖住锥底）
        return c;
    }

    /// <summary>学士方帽（epic）：墨蓝方板 + 帽筒 + 金流苏。</summary>
    private static Canvas BuildHatGraduation()
    {
        var c = NewArtCanvas();
        c.Children.Add(E(78, 38, 84, 28, White, 7));                             // 帽筒白底版
        c.Children.Add(E(78, 38, 84, 28, FromRgb(0x5A, 0x5A, 0x70)));
        c.Children.Add(P("M 120,14 L 180,38 L 120,62 L 60,38 Z", White, 8));     // 方板白底版
        c.Children.Add(P("M 120,14 L 180,38 L 120,62 L 60,38 Z", FromRgb(0x45, 0x45, 0x5A)));
        c.Children.Add(P("M 120,38 L 176,38 L 176,52", White, 6));               // 流苏白底版
        c.Children.Add(P("M 120,38 L 176,38 L 176,52", FromRgb(0xE8, 0xB2, 0x3A)));
        c.Children.Add(E(114, 32, 12, 12, White));                               // 板心扣白底版
        c.Children.Add(E(116, 34, 8, 8, FromRgb(0xE8, 0xB2, 0x3A)));
        return c;
    }

    /// <summary>春日花环（legendary）：五朵小花沿头顶弧线 + 两片绿叶。</summary>
    private static Canvas BuildHatFlowerCrown()
    {
        var c = NewArtCanvas();
        var leaf = FromRgb(0x7F, 0xA8, 0x6B);
        var pink = FromRgb(0xF2, 0xA7, 0xC3);
        var yellow = FromRgb(0xF6, 0xC4, 0x53);
        var gold = FromRgb(0xE8, 0xB2, 0x3A);

        // 叶（白底版 + 绿）
        c.Children.Add(E(62, 52, 18, 10, White, 4, -25));
        c.Children.Add(E(62, 52, 18, 10, leaf, 0, -25));
        c.Children.Add(E(160, 52, 18, 10, White, 4, 25));
        c.Children.Add(E(160, 52, 18, 10, leaf, 0, 25));

        // 花：白底版 r10 → 花瓣 r7 → 花心 r2.8（粉黄花交替，花心反色）
        (double x, double y, Color petal, Color center)[] flowers =
        {
            (78, 50, pink, gold),
            (96, 40, yellow, FromRgb(0xC0, 0x57, 0x6B)),
            (120, 36, pink, gold),
            (144, 40, yellow, FromRgb(0xC0, 0x57, 0x6B)),
            (162, 50, pink, gold),
        };

        foreach ((double x, double y, Color petal, Color center) in flowers)
        {
            c.Children.Add(E(x - 10, y - 10, 20, 20, White, 5));
            c.Children.Add(E(x - 7, y - 7, 14, 14, petal));
            c.Children.Add(E(x - 2.8, y - 2.8, 5.6, 5.6, center));
        }

        return c;
    }

    // ── 主窗口换装应用 ────────────────────────────────────────────────

    /// <summary>按 artKey 切 PartHat 内预建帽子的可见性（空 = 全部隐藏，默认无帽）。</summary>
    public static void ApplyHat(Canvas partHat, string? artKey)
    {
        foreach (object child in partHat.Children)
        {
            if (child is Canvas { Tag: string key } hat)
            {
                hat.Visibility = key == artKey ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ── 背包/图鉴缩略图标（48×48，复用同一几何，未拥有=灰剪影）────────

    /// <summary>
    /// 物品缩略图标：帽子复用帽画布几何，皮肤画迷你猫头，鼓画迷你双鼓。
    /// 未拥有 → 全形状灰剪影 + "未获得"文案由列表项渲染（GDD-03 §7）。
    /// </summary>
    public static Canvas CreateItemIcon(CosmeticItem item, bool owned)
    {
        const double size = 48;
        var icon = new Canvas { Width = size, Height = size };

        // 内容画布统一用主画布坐标，再 Viewbox 等比缩进 48 盒
        Canvas content;
        switch (item.Slot)
        {
            case ItemSlot.Hat:
                content = BuildHatCanvas(item.ArtKey, owned);
                break;

            case ItemSlot.Skin:
                content = BuildSkinHead(item.ArtKey, owned);
                break;

            case ItemSlot.Bongos:
                content = BuildBongosIcon(item.ArtKey, owned);
                break;

            default:
                content = NewArtCanvas();
                break;
        }

        var host = new Viewbox
        {
            Width = size,
            Height = size,
            Child = content,
            Stretch = Stretch.Uniform,
        };
        icon.Children.Add(host);
        return icon;
    }

    /// <summary>按 artKey 建帽子画布（icon 用；owned=false 全灰）。</summary>
    private static Canvas BuildHatCanvas(string artKey, bool owned)
    {
        Canvas c = artKey switch
        {
            "hat_beret" => BuildHatBeret(),
            "hat_santa" => BuildHatSanta(),
            "hat_graduation" => BuildHatGraduation(),
            "hat_flower_crown" => BuildHatFlowerCrown(),
            _ => BuildHatDevCap(),
        };

        if (!owned)
        {
            GrayOut(c);
        }

        return c;
    }

    /// <summary>迷你猫头（皮肤 icon）：白底版头 + 双耳 + 豆豆眼/腮红/w 嘴；奶牛纹加一斑。</summary>
    private static Canvas BuildSkinHead(string artKey, bool owned)
    {
        var (body, ear, inner, _) = GetSkin(owned ? artKey : null);
        if (!owned)
        {
            body = GrayFill;
            ear = GrayFill;
            inner = GrayDark;
        }

        var c = NewArtCanvas();

        // 耳（白底版 + 色 + 耳内）
        c.Children.Add(P("M 78,62 L 66,14 L 114,50 Z", White, 8));
        c.Children.Add(P("M 78,62 L 66,14 L 114,50 Z", ear));
        c.Children.Add(P("M 82,52 L 76,30 L 102,48 Z", inner));
        c.Children.Add(P("M 162,62 L 174,14 L 126,50 Z", White, 8));
        c.Children.Add(P("M 162,62 L 174,14 L 126,50 Z", ear));
        c.Children.Add(P("M 158,52 L 164,30 L 138,48 Z", inner));

        // 头（白底版 + 色）
        c.Children.Add(E(50, 46, 140, 122, White, 8));
        c.Children.Add(E(50, 46, 140, 122, body));

        // 奶牛纹斑点（仅 cow）
        if (owned && artKey == CowSkinKey)
        {
            c.Children.Add(P("M 70,120 Q 86,96 108,110 Q 112,134 88,142 Q 68,140 70,120 Z", FromRgb(0x4B, 0x4B, 0x57)));
            c.Children.Add(P("M 148,70 Q 166,64 172,84 Q 162,98 146,90 Q 140,78 148,70 Z", FromRgb(0x4B, 0x4B, 0x57)));
        }

        // 脸：豆豆眼 + 腮红 + w 嘴（头顶高光省略——icon 太小）
        c.Children.Add(E(88, 92, 16, 16, Ink));
        c.Children.Add(E(136, 92, 16, 16, Ink));
        c.Children.Add(E(70, 110, 20, 12, Blush));
        c.Children.Add(E(150, 110, 20, 12, Blush));
        c.Children.Add(PS("M 108,120 Q 113,127 118,120 Q 123,127 128,120", Ink, 4));
        return c;
    }

    /// <summary>迷你双鼓（鼓 icon）：白底版鼓身 + 鼓沿。</summary>
    private static Canvas BuildBongosIcon(string artKey, bool owned)
    {
        var (body, rim) = GetBongos(owned ? artKey : null);
        if (!owned)
        {
            body = GrayFill;
            rim = GrayDark;
        }

        var c = NewArtCanvas();
        foreach (double x in new[] { 30.0, 126.0 })
        {
            c.Children.Add(E2(x, 56, 84, 104, 20, White, 8));       // 鼓身白底版
            c.Children.Add(E2(x, 56, 84, 104, 20, body, 0));
            c.Children.Add(E2(x, 56, 84, 30, 15, White, 6));        // 鼓沿白底版
            c.Children.Add(E2(x, 56, 84, 30, 15, rim, 0));
            c.Children.Add(E(x + 12, 62, 7, 7, White, 0, 0));       // 高光
        }

        return c;
    }

    /// <summary>圆角矩形 Shape（RoundedRect Path，白描边技法用）。</summary>
    private static Path E2(double x, double y, double w, double h, double radius, Color fill, double strokeThickness)
        => P($"M {x + radius},{y} H {x + w - radius} A {radius},{radius} 0 0 1 {x + w},{y + radius} " +
             $"V {y + h - radius} A {radius},{radius} 0 0 1 {x + w - radius},{y + h} " +
             $"H {x + radius} A {radius},{radius} 0 0 1 {x},{y + h - radius} V {y + radius} A {radius},{radius} 0 0 1 {x + radius},{y} Z",
             fill, strokeThickness);

    /// <summary>未拥有剪影：递归把所有形状涂灰（Geometry 保留轮廓 → 灰剪影效果）。</summary>
    private static void GrayOut(Canvas canvas)
    {
        foreach (object child in canvas.Children)
        {
            switch (child)
            {
                case Shape { } s:
                    if (s.Fill is not null)
                    {
                        s.Fill = Brush(GrayFill);
                    }

                    if (s.Stroke is not null && !ReferenceEquals(s.Stroke, s.Fill))
                    {
                        s.Stroke = Brush(GrayDark);
                    }

                    if (s.Fill is null && s.Stroke is not null)
                    {
                        s.Stroke = Brush(GrayDark);
                    }

                    break;
                case Canvas { } inner:
                    GrayOut(inner);
                    break;
            }
        }
    }
}
