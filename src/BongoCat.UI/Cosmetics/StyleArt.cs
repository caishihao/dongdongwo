using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// 风格模板美术库（GDD-08 §2：6 风格 × 6 层原创绘制，铁律：不复制任何现有产品形象）。
/// 全部为纯 XAML 矢量（Path/Ellipse/Rectangle），坐标空间 = 主画布 240×200，
/// 部件锚点跨风格一致（坐姿、鼓位、爪锚点与黏土猫相同——GDD-08 §1 敲击判定与音画同步零改动）。
///
/// 通用构图约定（跨 6 风格统一，换绑目标命名与 E3 MainWindow.xaml 完全一致）：
/// - 身体锚点：头身一体大椭圆 42,37 156×132（黏土；头身比放大一档后，底缘 169 与鼓/爪锚点不变）；
///   其余风格沿用基线椭圆 46,41 148×128；耳 (82,52)-(72,8)-(116,40) / 镜像（黏土）；
///   尾巴 M 194,118 Q 226,106 216,80；鼓 L(22,136 70×50) R(148,136 70×50)；
///   爪 L(41,94 34×46) R(165,94 34×46)。
/// - 换绑纪律：全部 SkinTargets/BongosTargets 命名形状必须经 <see cref="Add"/> 挂入视觉树
///   （底色占位值仅供未换绑前的兜底渲染，StyleRenderer.ApplyOutfit 会经 CosmeticArt 重涂）。
/// - 动画句柄（Paw*Translate/Rotate、BodySquash、BongoSink、Hat/FaceBounce）全部
///   RenderTransform 层，参数由 StylePack 驱动（CatTapAnimator）。
/// </summary>
public static class StyleArt
{
    /// <summary>主画布坐标空间（与 MainWindow CatCanvas 一致）。</summary>
    public const double ArtWidth = 240;
    public const double ArtHeight = 200;

    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Ink = Color.FromRgb(0x71, 0x2B, 0x13);
    private static readonly Color SketchInk = Color.FromRgb(0x4A, 0x40, 0x3A);
    private static readonly Color ChibiOutline = Color.FromRgb(0x2B, 0x2B, 0x2B);
    private static readonly Color ClayBody = Color.FromRgb(0xF5, 0xC4, 0xB3);
    private static readonly Color ClayEar = Color.FromRgb(0xF0, 0x99, 0x7B);
    private static readonly Color ClayInner = Color.FromRgb(0xE8, 0x82, 0x6B);
    private static readonly Color ClayLine = Color.FromRgb(0xD8, 0x5A, 0x30);
    private static readonly Color ClayDrum = Color.FromRgb(0xD8, 0x5A, 0x30);
    private static readonly Color ClayRim = Color.FromRgb(0x99, 0x3C, 0x1D);
    private static readonly Color Blush = Color.FromRgb(0xF4, 0xC0, 0xD1);
    private static readonly Color StickerBlush = Color.FromRgb(0xF5, 0xA0, 0xB0);
    private static readonly Color ChibiBlush = Color.FromRgb(0xF5, 0x80, 0x8F);
    private static readonly Color FlatInk = Color.FromRgb(0x2F, 0x2F, 0x2F);
    private static readonly Color FlatBongo = Color.FromRgb(0x5B, 0x8D, 0xEF);
    private static readonly Color FlatBongoRim = Color.FromRgb(0x47, 0x70, 0x9F);
    private static readonly Color CowSpot = Color.FromRgb(0x4B, 0x4B, 0x57);

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze(); // 跨渲染冻结：零变更通知开销（ADR-003 渲染纪律）
        return b;
    }

    /// <summary>加入画布并返回（换绑命名形状的唯一挂树入口——防"建了没挂"漂移）。</summary>
    internal static T Add<T>(Canvas canvas, T element) where T : FrameworkElement
    {
        canvas.Children.Add(element);
        return element;
    }

    // ── 形状构造辅助（与 CosmeticArt 同签名，模板绘制专用）──────────────

    private static Path P(string data, Color? fill, double strokeThickness = 0, Color? stroke = null)
    {
        var path = new Path { Data = Geometry.Parse(data) };
        if (fill is not null)
        {
            path.Fill = Brush(fill.Value);
        }

        if (strokeThickness > 0)
        {
            path.Stroke = Brush(stroke ?? White);
            path.StrokeThickness = strokeThickness;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }

        return path;
    }

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

    private static Ellipse E(double left, double top, double w, double h, Color fill,
        double strokeThickness = 0, Color? stroke = null, double angle = 0)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = Brush(fill) };
        if (strokeThickness > 0)
        {
            e.Stroke = Brush(stroke ?? White);
            e.StrokeThickness = strokeThickness;
            e.StrokeLineJoin = PenLineJoin.Round;
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

    private static Rectangle R(double left, double top, double w, double h, double radius,
        Color fill, double strokeThickness = 0, Color? stroke = null)
    {
        var r = new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            Fill = Brush(fill),
        };
        if (strokeThickness > 0)
        {
            r.Stroke = Brush(stroke ?? White);
            r.StrokeThickness = strokeThickness;
            r.StrokeLineJoin = PenLineJoin.Round;
        }

        Canvas.SetLeft(r, left);
        Canvas.SetTop(r, top);
        return r;
    }

    internal static Canvas Layer()
        => new() { RenderTransform = new TransformGroup() };

    /// <summary>身体层挤压 Transform（以脚底为轴 scaleY）。</summary>
    internal static ScaleTransform NewSquash(Canvas layer)
    {
        var squash = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        ((TransformGroup)layer.RenderTransform).Children.Add(squash);
        layer.RenderTransformOrigin = new Point(0.5, 1);
        return squash;
    }

    internal static TranslateTransform NewTranslate(Canvas layer, double x = 0, double y = 0)
    {
        var t = new TranslateTransform { X = x, Y = y };
        ((TransformGroup)layer.RenderTransform).Children.Add(t);
        return t;
    }

    private static RotateTransform NewRotate(Canvas layer, double angle = 0)
    {
        var r = new RotateTransform { Angle = angle };
        ((TransformGroup)layer.RenderTransform).Children.Add(r);
        layer.RenderTransformOrigin = new Point(0.5, 0.5);
        return r;
    }

    /// <summary>组装六层根画布（绘制顺序 = 层序：body → face → bongos → pawL → pawR → hat）。</summary>
    internal static (Canvas Root, Canvas Body, Canvas Face, Canvas Bongos, Canvas PawL, Canvas PawR, Canvas Hat) NewRoot()
    {
        var root = new Canvas { Width = ArtWidth, Height = ArtHeight, Background = Brushes.Transparent };
        var body = Layer();
        var face = Layer();
        var bongos = Layer();
        var pawL = Layer();
        var pawR = Layer();
        var hat = Layer();
        root.Children.Add(body);
        root.Children.Add(face);
        root.Children.Add(bongos);
        root.Children.Add(pawL);
        root.Children.Add(pawR);
        root.Children.Add(hat);
        return (root, body, face, bongos, pawL, pawR, hat);
    }

    /// <summary>投影效果（不占层：DropShadowEffect 挂根画布，GDD-08 §2.1/§2.5）。</summary>
    internal static void ApplyShadow(Canvas root, Core.Config.StylePack style)
    {
        if (style.ShadowOpacity <= 0)
        {
            return;
        }

        root.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = style.ShadowOpacity,
            BlurRadius = style.ShadowBlur,
            ShadowDepth = style.ShadowOffsetY,
            Direction = style.ShadowOffsetX >= 0 ? 90 : 270, // 0=向上；90=向下（WPF Direction 角度制）
        };
    }

    /// <summary>
    /// 细内描边（贴纸风双层描边，参照 ai-sticker）：给填充形状自身加一圈细深棕居中描边，
    /// 叠在白色底版描边之上形成"白框 + 内侧细线"。由 styles.json innerOutlineWidth/Color 参数化，
    /// 其余风格宽度为 0 不生效。WPF Stroke 居中于形状边缘 → 白框可见段收窄约半线宽，视觉即双层。
    /// </summary>
    private static void ApplyInnerOutline(Core.Config.StylePack style, Shape shape)
    {
        if (style.InnerOutlineWidth > 0 && style.InnerOutlineColor.Length > 0)
        {
            shape.Stroke = Brush((Color)ColorConverter.ConvertFromString(style.InnerOutlineColor));
            shape.StrokeThickness = style.InnerOutlineWidth;
        }
    }

    private static StyleCatParts Assemble(
        Core.Config.StylePack style,
        Canvas root, Canvas body, Canvas face, Canvas bongos, Canvas pawL, Canvas pawR, Canvas hat,
        Shape bodyFur, Shape tailFur, Shape earL, Shape earR, Shape innerL, Shape innerR,
        Shape nose, Shape pawLFur, Shape pawRFur, Shape pawLineL, Shape pawLineR,
        Canvas cowSpots, IReadOnlyList<Shape> cowSpotShapes,
        Shape bongoBodyL, Shape bongoRimL, Shape bongoBodyR, Shape bongoRimR,
        TranslateTransform pawLTranslate, TranslateTransform pawRTranslate,
        RotateTransform pawLRotate, RotateTransform pawRRotate,
        ScaleTransform bodySquash, TranslateTransform bongoSink,
        TranslateTransform hatBounce, TranslateTransform faceBounce)
    {
        ApplyShadow(root, style);
        return new StyleCatParts
        {
            Root = root,
            PartBody = body,
            PartFace = face,
            PartBongos = bongos,
            PartPawL = pawL,
            PartPawR = pawR,
            PartHat = hat,
            BodyFur = bodyFur,
            TailFur = tailFur,
            EarLFur = earL,
            EarRFur = earR,
            EarInnerL = innerL,
            EarInnerR = innerR,
            NoseFill = nose,
            PawLFur = pawLFur,
            PawRFur = pawRFur,
            PawLineL = pawLineL,
            PawLineR = pawLineR,
            CowSpots = cowSpots,
            CowSpotShapes = cowSpotShapes,
            BongoBodyL = bongoBodyL,
            BongoRimL = bongoRimL,
            BongoBodyR = bongoBodyR,
            BongoRimR = bongoRimR,
            PawLTranslate = pawLTranslate,
            PawRTranslate = pawRTranslate,
            PawLRotate = pawLRotate,
            PawRRotate = pawRRotate,
            BodySquash = bodySquash,
            BongoSink = bongoSink,
            HatBounce = hatBounce,
            FaceBounce = faceBounce,
        };
    }

    // ══ 黏土（基线，与 E1-E8 MainWindow.xaml 原模板同几何原配色）════════

    public static StyleCatParts BuildClay(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        // 尾巴（白底版 + 彩色；TailFur 为换绑目标）
        Add(body, PS("M 194,118 Q 226,106 216,80", White, 15));
        var tailFur = Add(body, PS("M 194,118 Q 226,106 216,80", ClayEar, 8));

        // 左耳 / 右耳（白底版 + 黏土色 + 耳内）
        // 头身比放大一档（参照 ai-clay.jpg 比例感，2024 用户拍板）：头顶 41→37、横径 148→156、
        // 底缘 169 不动（坐姿/鼓/爪世界锚点零改动）；耳朵随头顶上移 4px 保持衔接。
        // hat 锚点 offsetY 保持 0：帽白绒边（y46-66）仍覆盖头冠衔接处，仅露冠 4→9px，视觉自然。
        Add(body, P("M 82,52 L 72,8 L 116,40 Z", White, 8));
        var earL = Add(body, P("M 82,52 L 72,8 L 116,40 Z", ClayEar));
        var innerL = Add(body, P("M 85,44 L 80,22 L 104,39 Z", ClayInner));
        Add(body, P("M 158,52 L 168,8 L 124,40 Z", White, 8));
        var earR = Add(body, P("M 158,52 L 168,8 L 124,40 Z", ClayEar));
        var innerR = Add(body, P("M 155,44 L 160,22 L 136,39 Z", ClayInner));

        // 头身一体（白底版 + 黏土色）+ 头顶高光（随头顶上移 4px）
        Add(body, E(42, 37, 156, 132, White, 8));
        var bodyFur = Add(body, E(42, 37, 156, 132, ClayBody));
        var highlight = E(72, 48, 26, 14, White, angle: -18);
        highlight.Opacity = 0.5;
        Add(body, highlight);

        // 奶牛纹斑点组（skin_cow 才可见）
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", CowSpot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", CowSpot);
        spot1.Opacity = 0.9;
        spot2.Opacity = 0.9;
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);

        // 脸（豆豆眼 + 高光 / 腮红 / 鼻 / w 嘴）
        Add(face, E(93, 72, 14, 14, Ink));
        Add(face, E(96, 75, 4.5, 4.5, White));
        Add(face, E(133, 72, 14, 14, Ink));
        Add(face, E(136, 75, 4.5, 4.5, White));
        Add(face, E(74, 88, 18, 11, Blush));
        Add(face, E(148, 88, 18, 11, Blush));
        var nose = Add(face, P("M 116,86 L 124,86 L 120,91 Z", ClayInner));
        Add(face, PS("M 110,95 Q 115,101 120,95 Q 125,101 130,95", Ink, 2.5));

        // 双鼓（白底版 + 陶土鼓身 + 深鼓沿）
        Add(bongos, R(22, 136, 70, 50, 14, White, 8));
        var bongoBodyL = Add(bongos, R(22, 136, 70, 50, 14, ClayDrum));
        var bongoRimL = Add(bongos, R(22, 136, 70, 14, 7, ClayRim));
        Add(bongos, R(148, 136, 70, 50, 14, White, 8));
        var bongoBodyR = Add(bongos, R(148, 136, 70, 50, 14, ClayDrum));
        var bongoRimR = Add(bongos, R(148, 136, 70, 14, 7, ClayRim));

        // 爪（白底版 + 黏土色肉爪 + 趾线；Transform 句柄供敲击动画）
        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        Add(pawL, E(41, 94, 34, 46, White, 8, angle: -14));
        var pawLFur = Add(pawL, E(41, 94, 34, 46, ClayEar, angle: -14));
        var pawLineL = Add(pawL, PS("M 52,126 L 58,126 M 63,126 L 69,126", ClayLine, 2));
        pawLineL.Opacity = 0.55;

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        Add(pawR, E(165, 94, 34, 46, White, 8, angle: 14));
        var pawRFur = Add(pawR, E(165, 94, 34, 46, ClayEar, angle: 14));
        var pawLineR = Add(pawR, PS("M 173,126 L 179,126 M 184,126 L 190,126", ClayLine, 2));
        pawLineR.Opacity = 0.55;

        var squash = NewSquash(body);
        var bongoSink = NewTranslate(bongos);
        var faceBounce = NewTranslate(face);
        var hatBounce = NewTranslate(hat);

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate, squash, bongoSink, hatBounce, faceBounce);
    }

    // ══ 贴纸风（GDD-08 §2.1：圆胖剪影 + 5px 白描边 + 底部投影）══════════

    public static StyleCatParts BuildSticker(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double outline = 5;

        // 部件间留 2px 白边间隙（贴纸剪影）：白色描边即"贴纸边框"
        // 尾巴内描边：白底加宽 9→10.5，白色与彩芯之间加一圈深棕（宽 = 彩芯 + 2×内描边宽）
        Add(body, P("M 196,118 Q 224,106 214,82", ClayEar, 10.5, White)); // 尾巴：彩色笔触自带白框
        if (style.InnerOutlineWidth > 0)
        {
            Add(body, PS("M 196,118 Q 224,106 214,82",
                (Color)ColorConverter.ConvertFromString(style.InnerOutlineColor), 4 + 2 * style.InnerOutlineWidth));
        }

        var tailFur = Add(body, PS("M 196,118 Q 224,106 214,82", ClayEar, 4));

        Add(body, P("M 82,56 L 73,14 L 114,44 Z", ClayEar, outline, White));
        var earL = Add(body, P("M 82,56 L 73,14 L 114,44 Z", ClayEar));
        ApplyInnerOutline(style, earL);
        var innerL = Add(body, P("M 86,48 L 81,28 L 102,43 Z", StickerBlush));
        Add(body, P("M 158,56 L 167,14 L 126,44 Z", ClayEar, outline, White));
        var earR = Add(body, P("M 158,56 L 167,14 L 126,44 Z", ClayEar));
        ApplyInnerOutline(style, earR);
        var innerR = Add(body, P("M 154,48 L 159,28 L 138,43 Z", StickerBlush));

        var bodyFur = Add(body, E(48, 43, 144, 124, ClayBody, outline, White));
        ApplyInnerOutline(style, bodyFur);

        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 68,118 Q 82,94 104,108 Q 108,134 86,142 Q 64,140 68,118 Z", CowSpot, 3, White);
        var spot2 = P("M 146,68 Q 164,60 172,80 Q 164,94 146,86 Q 140,76 146,68 Z", CowSpot, 3, White);
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);

        Add(face, E(95, 74, 13, 13, Ink));
        Add(face, E(132, 74, 13, 13, Ink));
        Add(face, E(76, 90, 17, 10, StickerBlush));
        Add(face, E(147, 90, 17, 10, StickerBlush));
        var nose = Add(face, P("M 116,88 L 124,88 L 120,93 Z", StickerBlush));
        Add(face, PS("M 110,97 Q 115,103 120,97 Q 125,103 130,97", Ink, 2.5));

        Add(bongos, R(24, 138, 66, 46, 16, ClayDrum, outline, White));
        var bongoBodyL = Add(bongos, R(24, 138, 66, 46, 16, ClayDrum));
        ApplyInnerOutline(style, bongoBodyL);
        var bongoRimL = Add(bongos, R(24, 138, 66, 12, 6, ClayRim));
        Add(bongos, R(150, 138, 66, 46, 16, ClayDrum, outline, White));
        var bongoBodyR = Add(bongos, R(150, 138, 66, 46, 16, ClayDrum));
        ApplyInnerOutline(style, bongoBodyR);
        var bongoRimR = Add(bongos, R(150, 138, 66, 12, 6, ClayRim));

        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        var pawLFur = Add(pawL, E(42, 96, 32, 42, ClayEar, outline, White, -14));
        ApplyInnerOutline(style, pawLFur);
        var pawLineL = Add(pawL, PS("M 52,124 L 58,124 M 63,124 L 69,124", ClayLine, 2));

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        var pawRFur = Add(pawR, E(166, 96, 32, 42, ClayEar, outline, White, 14));
        ApplyInnerOutline(style, pawRFur);
        var pawLineR = Add(pawR, PS("M 172,124 L 178,124 M 183,124 L 189,124", ClayLine, 2));

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
    }

    // ══ 极简扁平（GDD-08 §2.2：最少形状 / 无描边无投影 / 8% 明度差）══════

    public static StyleCatParts BuildFlat(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        // 身体 = 圆角梯形（无描边、无耳内细节、无白底版）
        var tailFur = Add(body, PS("M 196,116 Q 224,104 214,80", ClayEar, 8)); // 尾巴（胶囊笔触）

        // 简单三角耳（与身体同色 = "无耳内细节"）
        var earL = Add(body, P("M 84,52 L 74,14 L 112,42 Z", ClayEar));
        var innerL = Add(body, P("M 84,52 L 74,14 L 112,42 Z", ClayEar));
        var earR = Add(body, P("M 156,52 L 166,14 L 128,42 Z", ClayEar));
        var innerR = Add(body, P("M 156,52 L 166,14 L 128,42 Z", ClayEar));

        // 圆角梯形头身一体
        var bodyFur = Add(body, P(
            "M 62,50 Q 62,38 74,38 L 166,38 Q 178,38 178,50 L 190,140 Q 192,158 174,158 L 66,158 Q 48,158 50,140 Z",
            ClayBody));

        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        // 白底 + 圆角几何斑块（GDD-08 §1.1 扁平奶牛转译）
        var spotBg = Add(cowSpots, E(48, 43, 144, 112, White));
        var spot1 = Add(cowSpots, R(70, 100, 34, 26, 10, CowSpot));
        var spot2 = Add(cowSpots, R(144, 62, 26, 22, 10, CowSpot));
        Add(body, cowSpots);

        // 脸 = 两点一线（眼 6px 圆点、嘴 10px 弧线；鼻省略——NoseFill 隐藏占位）
        Add(face, E(96, 76, 6, 6, FlatInk));
        Add(face, E(130, 76, 6, 6, FlatInk));
        var nose = Add(face, P("M 116,86 L 124,86 L 120,91 Z", ClayInner));
        nose.Visibility = Visibility.Collapsed;
        Add(face, PS("M 110,96 Q 115,102 120,96 Q 125,102 130,96", FlatInk, 2.5));

        var bongoBodyL = Add(bongos, R(24, 138, 66, 46, 12, FlatBongo));
        var bongoRimL = Add(bongos, R(24, 138, 66, 12, 6, FlatBongoRim));
        var bongoBodyR = Add(bongos, R(150, 138, 66, 46, 12, FlatBongo));
        var bongoRimR = Add(bongos, R(150, 138, 66, 12, 6, FlatBongoRim));

        // 爪 = 简单胶囊（渲染层由 StyleRenderer 再压 8% 明度差）
        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        var pawLFur = Add(pawL, R(42, 98, 30, 40, 15, ClayEar));
        pawLFur.RenderTransform = new RotateTransform(-10);
        var pawLineL = Add(pawL, P("M 48,132 L 66,132", ClayEar)); // 扁平无趾线（隐藏占位）
        pawLineL.Visibility = Visibility.Collapsed;

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        var pawRFur = Add(pawR, R(168, 98, 30, 40, 15, ClayEar));
        pawRFur.RenderTransform = new RotateTransform(10);
        var pawLineR = Add(pawR, P("M 170,132 L 188,132", ClayEar));
        pawLineR.Visibility = Visibility.Collapsed;

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spotBg, spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
    }

    // ══ 手绘线稿（GDD-08 §2.3：墨线恒定 / 纸底 12% 淡填充 / 抖动路径）════

    public static StyleCatParts BuildSketch(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double ink = 2.2;

        // 纸底（线稿本色画纸；GDD-08 §1.1 奶油=纸底色不填充）
        if (!string.IsNullOrEmpty(style.PaperBaseColor))
        {
            root.Children.Insert(0, E(30, 26, 180, 156, (Color)ColorConverter.ConvertFromString(style.PaperBaseColor)));
        }

        // 尾巴：彩芯（12% 透明度由渲染层压）+ 墨线轮廓
        var tailFur = Add(body, PS("M 194,118 Q 226,106 216,80", ClayEar, 6));
        Add(body, PS("M 194,118 Q 226,106 216,80", SketchInk, ink));

        // 耳（抖动三角：路径点手工偏移 ±1.5px 烘焙离线抖动感）
        var earL = Add(body, P("M 83,55 L 73.5,13.5 L 115,44.5 Z", ClayEar));
        var innerL = Add(body, P("M 85,47 L 80.5,26.5 L 103,42.5 Z", ClayInner));
        Add(body, PS("M 83,55 L 73.5,13.5 L 115,44.5 L 82.4,55.6", SketchInk, ink));
        var earR = Add(body, P("M 155,55 L 164.5,13.5 L 125,44.5 Z", ClayEar));
        var innerR = Add(body, P("M 155,47 L 159.5,26.5 L 137,42.5 Z", ClayInner));
        Add(body, PS("M 157,55 L 166.5,13.5 L 125,44.5 L 157.6,55.6", SketchInk, ink));

        // 头身一体：手绘不规则圆（多段贝塞尔抖动）
        var bodyFur = Add(body, E(46, 41, 148, 128, ClayBody));
        Add(body, PS(
            "M 120,40.5 C 156,39 193,68 193.5,105 C 194,140 160,168.5 119,168.5 C 79,168.5 46.5,139 47,104 " +
            "C 47.5,69 84,42 120,40.5 Z", SketchInk, ink));

        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", CowSpot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", CowSpot);
        spot1.Stroke = Brush(SketchInk); // 斑块轮廓线不填充（GDD-08 §1.1 线稿奶牛转译；渲染层置 Fill=null）
        spot2.Stroke = Brush(SketchInk);
        spot1.StrokeThickness = ink;
        spot2.StrokeThickness = ink;
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);

        // 脸：空心圆圈眼 + 3 条斜线腮红 + "w" 墨线嘴
        var eyeL = new Ellipse { Width = 10, Height = 10, Stroke = Brush(SketchInk), StrokeThickness = 1.8 };
        Canvas.SetLeft(eyeL, 95);
        Canvas.SetTop(eyeL, 74);
        face.Children.Add(eyeL);
        var eyeR = new Ellipse { Width = 10, Height = 10, Stroke = Brush(SketchInk), StrokeThickness = 1.8 };
        Canvas.SetLeft(eyeR, 135);
        Canvas.SetTop(eyeR, 74);
        face.Children.Add(eyeR);

        foreach ((double bx, double by) in new[] { (76.0, 92.0), (80.0, 95.0), (84.0, 98.0) })
        {
            Add(face, PS($"M {bx},{by} L {bx + 6},{by - 4}", SketchInk, 1.4));
            Add(face, PS($"M {bx + 64},{by} L {bx + 58},{by - 4}", SketchInk, 1.4));
        }

        var nose = Add(face, P("M 116,86 L 124,86 L 120,91 Z", ClayInner));
        Add(face, PS("M 112,96 Q 116,100 120,96 Q 124,100 128,96", SketchInk, 1.8));

        // 鼓：纸片矩形 + 墨线
        var bongoBodyL = Add(bongos, R(24, 138, 66, 46, 8, ClayDrum));
        var bongoRimL = Add(bongos, R(24, 138, 66, 12, 6, ClayRim));
        Add(bongos, PS("M 24,138 L 90,138 M 24,184 L 90,184", SketchInk, ink));
        Add(bongos, PS("M 24,138 L 24,184 M 90,138 L 90,184", SketchInk, ink));
        var bongoBodyR = Add(bongos, R(150, 138, 66, 46, 8, ClayDrum));
        var bongoRimR = Add(bongos, R(150, 138, 66, 12, 6, ClayRim));
        Add(bongos, PS("M 150,138 L 216,138 M 150,184 L 216,184", SketchInk, ink));
        Add(bongos, PS("M 150,138 L 150,184 M 216,138 L 216,184", SketchInk, ink));

        // 爪：墨线轮廓 + 12% 淡填充 + 趾线（落下时 ±2° 手绘颤感由动画驱动）
        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        var pawLFur = Add(pawL, E(42, 96, 32, 42, ClayEar, angle: -14));
        Add(pawL, PS("M 60,96.5 C 78,98 78,132 58,137 C 42,133 38,102 60,96.5 Z", SketchInk, ink));
        var pawLineL = Add(pawL, PS("M 52,126 L 58,126 M 63,126 L 69,126", SketchInk, 1.6));

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        var pawRFur = Add(pawR, E(166, 96, 32, 42, ClayEar, angle: 14));
        Add(pawR, PS("M 180,96.5 C 200,100 198,134 178,137 C 162,133 160,98 180,96.5 Z", SketchInk, ink));
        var pawLineR = Add(pawR, PS("M 172,126 L 178,126 M 183,126 L 189,126", SketchInk, 1.6));

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
    }

    // ══ Q版厚描边（GDD-08 §2.4：头身比 1.4:1 / 9px 厚描边 / 两点高光）════

    public static StyleCatParts BuildChibi(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double outline = 9;
        var oc = ChibiOutline;

        // 尾巴（厚描边）
        Add(body, P("M 194,118 Q 226,106 216,80", ClayEar, 8, oc));
        var tailFur = Add(body, PS("M 194,118 Q 226,106 216,80", ClayEar, 4));

        // 耳（大耳 + 厚描边；头身比 1.4:1 → 头顶上移至 ~24）
        Add(body, P("M 88,46 L 74,4 L 120,34 Z", ClayEar, outline, oc));
        var earL = Add(body, P("M 88,46 L 74,4 L 120,34 Z", ClayEar));
        var innerL = Add(body, P("M 89,38 L 82,16 L 108,32 Z", ClayInner));
        Add(body, P("M 152,46 L 166,4 L 120,34 Z", ClayEar, outline, oc));
        var earR = Add(body, P("M 152,46 L 166,4 L 120,34 Z", ClayEar));
        var innerR = Add(body, P("M 151,38 L 158,16 L 132,32 Z", ClayInner));

        // 大头（1.4:1：头顶 24、头底 ~174，占满上半身；爪/鼓世界锚点不变）
        var bodyFur = Add(body, E(30, 24, 180, 150, ClayBody, outline, oc));

        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 60,110 Q 76,86 98,100 Q 102,124 80,132 Q 58,130 60,110 Z", CowSpot, 6, oc);
        var spot2 = P("M 148,60 Q 166,52 174,74 Q 164,88 146,80 Q 140,70 148,60 Z", CowSpot, 6, oc);
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);

        // 脸：14px 实心眼 + 3px 白高光点 + 大椭圆腮红（24×13@70% → 26×15@85%，加大一档强化情绪感，
        //     参照 ai-chibi 腮红浓度；其白贴纸边为 AI 参照错误，不采纳）
        Add(face, E(86, 82, 14, 14, Ink));
        Add(face, E(90, 85, 3, 3, White));
        Add(face, E(140, 82, 14, 14, Ink));
        Add(face, E(144, 85, 3, 3, White));
        var blushL = E(67, 99, 26, 15, ChibiBlush);
        blushL.Opacity = 0.85;
        Add(face, blushL);
        var blushR = E(147, 99, 26, 15, ChibiBlush);
        blushR.Opacity = 0.85;
        Add(face, blushR);
        var nose = Add(face, P("M 116,94 L 124,94 L 120,99 Z", ClayInner));
        Add(face, PS("M 114,104 Q 117,107 120,104 Q 123,107 126,104", Ink, 2.5));

        var bongoBodyL = Add(bongos, R(22, 136, 70, 50, 14, ClayDrum, outline, oc));
        var bongoRimL = Add(bongos, R(22, 136, 70, 14, 7, ClayRim));
        var bongoBodyR = Add(bongos, R(148, 136, 70, 50, 14, ClayDrum, outline, oc));
        var bongoRimR = Add(bongos, R(148, 136, 70, 14, 7, ClayRim));

        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        var pawLFur = Add(pawL, E(41, 94, 34, 46, ClayEar, outline, oc, -14));
        var pawLineL = Add(pawL, PS("M 52,126 L 58,126 M 63,126 L 69,126", ClayLine, 2));

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        var pawRFur = Add(pawR, E(165, 94, 34, 46, ClayEar, outline, oc, 14));
        var pawLineR = Add(pawR, PS("M 173,126 L 179,126 M 184,126 L 190,126", ClayLine, 2));

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
    }

    // ══ 剪纸分层（GDD-08 §2.5：色纸 + 撕纸边 + 层错位阴影，无描边）══════

    public static StyleCatParts BuildPapercut(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        // 撕纸边 = 轻度不规则多边形（离线烘焙路径点，运行时零特效开销）
        var tailFur = Add(body, PS("M 194,118 Q 224,107 215,81", ClayEar, 7));

        // 耳（撕纸三角：边缘锯齿 ±2px）
        var earL = Add(body, P("M 82,56 L 73,13 L 90,26 L 104,22 L 116,44 L 98,50 Z", ClayEar));
        var innerL = Add(body, P("M 85,46 L 81,26 L 100,41 Z", ClayInner));
        var earR = Add(body, P("M 158,56 L 167,13 L 150,26 L 136,22 L 124,44 L 142,50 Z", ClayEar));
        var innerR = Add(body, P("M 155,46 L 159,26 L 140,41 Z", ClayInner));

        // 头身色纸（不规则边缘）
        var bodyFur = Add(body, P(
            "M 120,41 C 150,40 178,52 190,76 C 196,92 195,110 190,124 C 196,136 190,152 176,158 " +
            "C 158,167 138,169 120,168 C 96,169 72,162 58,148 C 46,136 46,116 52,100 " +
            "C 58,72 88,42 120,41 Z", ClayBody));

        // 奶牛斑 = 独立小色纸片（占 1 纹理层，GDD-08 §2.5 层数核算）
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", CowSpot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", CowSpot);
        spot1.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
        spot2.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);

        // 脸色纸：眼/嘴直接"剪"在脸色纸上（深色纸片眼）
        Add(face, P("M 93,74 L 105,73 L 104,85 L 94,84 Z", Ink));
        Add(face, P("M 133,73 L 145,74 L 144,84 L 134,85 Z", Ink));
        Add(face, P("M 76,92 L 92,91 L 91,99 L 77,99 Z", Blush));
        Add(face, P("M 148,91 L 164,92 L 163,99 L 149,99 Z", Blush));
        var nose = Add(face, P("M 116,86 L 124,86 L 120,91 Z", ClayInner));
        Add(face, P("M 108,96 L 116,100 L 124,95 L 130,99", null, 2.5, Ink));

        var bongoBodyL = Add(bongos, R(22, 136, 70, 50, 10, ClayDrum));
        var bongoRimL = Add(bongos, R(22, 136, 70, 14, 7, ClayRim));
        var bongoBodyR = Add(bongos, R(148, 136, 70, 50, 10, ClayDrum));
        var bongoRimR = Add(bongos, R(148, 136, 70, 14, 7, ClayRim));

        // 爪色纸（撕纸边）
        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        var pawLFur = Add(pawL, P("M 45,96 L 72,94 L 75,132 L 62,139 L 48,134 Z", ClayEar));
        var pawLineL = Add(pawL, PS("M 52,126 L 58,126 M 63,126 L 69,126", ClayLine, 2));

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        var pawRFur = Add(pawR, P("M 168,94 L 195,96 L 192,134 L 178,139 L 165,132 Z", ClayEar));
        var pawLineR = Add(pawR, PS("M 173,126 L 179,126 M 184,126 L 190,126", ClayLine, 2));

        return Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, [spot1, spot2], bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLTranslate, pawRTranslate, pawLRotate, pawRRotate,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
    }

    // ══ 物种形体层：原创小恐龙（GDD-09 §2）══════════════════════════════
    //
    // IP 红线自检（GDD-09 §6 差异清单，实现即对照）：
    // ① 头顶两根圆钝小角（参考形象无角）② 背部 5 枚半圆棘刺列、棘尖青绿（参考无棘）
    // ③ 豆豆眼（实心圆，无猫的高光双点）+ 一笔短弧嘴（非 w 形）④ 圆钝凸吻 + 两点鼻孔
    // ⑤ 短圆尾（尾尖钝圆）⑥ 柚黄 #EFD35D + 青绿 #2EA98F 三点呼应（角尖/棘尖/尾尖固定色）
    // ⑦ 无腹部斑形、头身比与五官布局与参考形象不同构。
    //
    // 结构纪律（GDD-09 §2.1）：棘刺/双角/尾为静态特征"烘焙"进 body 层（零新增动画层）；
    // 敲击动画句柄/鼓/爪锚点与猫完全一致（同风格猫龙手感一致）；青绿点缀 = 物种固定色，
    // 打 Tag "dino_accent"（不参与皮肤换绑）；棘刺/双角基座随皮肤"耳"槽换色
    // （SpeciesAppendages），身体随 BodyFur 换绑，吻部随 NoseFill 换绑。

    private static readonly Color DinoBody = FromRgb(0xEF, 0xD3, 0x5D); // 柚黄（默认皮肤占位，换绑覆盖）
    private static readonly Color DinoDeep = FromRgb(0xD9, 0xBC, 0x4F); // 柚黄加深 8%（角/棘基座/爪）
    private static readonly Color DinoAccent = FromRgb(0x2E, 0xA9, 0x8F); // 青绿（物种固定点缀色）
    private static readonly Color DinoBelly = FromRgb(0xF7, 0xF1, 0xE3); // 米白（吻部/腹侧）
    private static readonly Color DinoLine = FromRgb(0xB8, 0x9A, 0x2E); // 趾线/爪线（柚黄系深线）

    /// <summary>恐龙专属几何：胖水滴身（重心靠后、底部更宽，GDD-09 §2.2）。</summary>
    private const string DinoBodyData =
        "M 118,38 C 150,38 172,58 181,92 C 189,124 190,150 172,162 C 154,171 82,171 64,162 " +
        "C 46,150 47,124 55,92 C 63,58 86,38 118,38 Z";

    /// <summary>恐龙专属几何：短圆三角尾（尾尖钝圆，右后方伸出；粗描边胶囊笔触 = 钝圆端点）。</summary>
    private const string DinoTailData = "M 176,124 Q 206,122 214,142";

    /// <summary>棘刺列（5 枚，背中线由大到小向尾排：半圆凸起 = 椭圆下半被身体遮盖；
    /// 圆心落在头部右侧→尾根的背轮廓线上，露出上半凸起）。</summary>
    private static readonly (double X, double Y, double Rx)[] DinoSpikes =
        [(148, 45, 13), (167, 58, 12), (179, 73, 11), (187, 90, 10), (190, 108, 9)];

    /// <summary>组装恐龙通用部件：尾/棘刺/双角（白底版可选）+ 身体 + 换绑目标登记。
    /// includeFeatures=false 时不画棘/角基座（剪纸龙用独立色纸条自有画法）。</summary>
    private static (Shape TailFur, List<Shape> Appendages, Shape BodyFur) BuildDinoCore(
        Canvas body,
        double outlineWidth, Color? outlineColor,
        bool tipAccentsVisible, Color tipAccentColor, double accentScale = 1.0,
        bool includeFeatures = true)
    {
        var appendages = new List<Shape>();

        // ── 尾（白底版 + 彩色描边；TailFur 换绑目标与猫同槽）──────────────
        if (outlineWidth > 0 && outlineColor is { } oc)
        {
            Add(body, PS(DinoTailData, oc, outlineWidth + 7));
        }

        var tailFur = Add(body, PS(DinoTailData, DinoDeep, outlineWidth + 4));
        if (tipAccentsVisible)
        {
            // 尾尖青绿点缀（物种固定；钝圆端点上的一枚小圆）
            var tailTip = E(205, 134, 10 * accentScale, 9 * accentScale, tipAccentColor);
            tailTip.Tag = "dino_accent";
            Add(body, tailTip);
        }

        // ── 背棘列（基座椭圆先画、身体后画遮出半圆凸起；基座换绑"耳"槽）──
        foreach ((double sx, double sy, double rx) in DinoSpikes)
        {
            double ry = rx * 1.1;
            if (!includeFeatures)
            {
                continue;
            }

            if (outlineWidth > 0 && outlineColor is { } oc2)
            {
                Add(body, E(sx - rx - 2, sy - ry - 2, rx * 2 + 4, ry * 2 + 4, White, outlineWidth, oc2));
            }

            Shape spike = Add(body, E(sx - rx, sy - ry, rx * 2, ry * 2, DinoDeep));
            spike.Tag = "dino_spike";
            appendages.Add(spike);

            if (tipAccentsVisible)
            {
                var tip = E(sx - rx * 0.55 * accentScale, sy - ry * 1.05 * accentScale,
                    rx * 1.1 * accentScale, ry * 1.1 * accentScale, tipAccentColor);
                tip.Tag = "dino_accent";
                Add(body, tip);
            }
        }

        // ── 双角（短圆柱+圆头，左右对称；基座换绑"耳"槽，角尖青绿固定）────
        foreach (double hx in new[] { 100.0, 122.0 })
        {
            if (!includeFeatures)
            {
                continue;
            }

            if (outlineWidth > 0 && outlineColor is { } oc3)
            {
                Add(body, R(hx - 2, 16, 16, 30, 8, White, outlineWidth, oc3));
            }

            Shape horn = Add(body, R(hx - 2, 16, 16, 30, 8, DinoDeep));
            horn.Tag = "dino_horn";
            appendages.Add(horn);

            if (tipAccentsVisible)
            {
                var tip = E(hx + 2, 14, 9 * accentScale, 8.5 * accentScale, tipAccentColor);
                tip.Tag = "dino_accent";
                Add(body, tip);
            }
        }

        // ── 头身一体（胖水滴；白底版 + 彩色填充）─────────────────────────
        Shape bodyFur;
        if (outlineWidth > 0 && outlineColor is { } oc4)
        {
            Add(body, P(DinoBodyData, White, outlineWidth, oc4));
        }

        bodyFur = Add(body, P(DinoBodyData, DinoBody));

        return (tailFur, appendages, bodyFur);
    }

    /// <summary>恐龙通用脸部：圆钝凸吻（返回 = NoseFill 换绑目标）+ 豆豆眼 + 两点鼻孔 + 一笔短弧嘴 + 风格腮红。</summary>
    private static Shape BuildDinoFace(Canvas face, Color ink, Color blush, double muzzleW = 36, double muzzleH = 21,
        bool drawBlush = true)
    {
        // 圆钝凸吻（椭圆微凸出；NoseFill 换绑目标 → 米白/皮肤 inner 色）
        Shape muzzle = Add(face, E(118 - muzzleW / 2, 88, muzzleW, muzzleH, DinoBelly));

        // 豆豆眼（实心圆，无猫的高光双点结构——GDD-09 §6 差异 3）
        Add(face, E(86, 72, 11, 11, ink));
        Add(face, E(129, 72, 11, 11, ink));

        // 腮红（沿用各风格语言；扁平无腮红）
        if (drawBlush)
        {
            Add(face, E(68, 86, 18, 11, blush));
            Add(face, E(150, 86, 18, 11, blush));
        }

        // 吻上两点鼻孔（2px 级别，GDD-09 §2.2）
        Add(face, E(112, 94, 3, 3, ink));
        Add(face, E(123, 94, 3, 3, ink));

        // 一笔短弧嘴（非 w 形）
        Add(face, PS("M 109,103 Q 118,109 127,103", ink, 2.5));
        return muzzle;
    }

    /// <summary>恐龙通用爪：圆钝短爪（比猫短 8%）+ 三指垫趾线；锚点世界坐标不变；返回全部换绑目标与动画句柄。</summary>
    private static (Shape PawLFur, Shape PawLineL, Shape PawRFur, Shape PawLineR,
        TranslateTransform PawLTranslate, TranslateTransform PawRTranslate,
        RotateTransform PawLRotate, RotateTransform PawRRotate) BuildDinoPaws(
        Canvas pawL, Canvas pawR,
        Color fur, Color line,
        double outlineWidth = 0, Color? outlineColor = null,
        bool flatStyle = false)
    {
        var pawLTranslate = NewTranslate(pawL);
        var pawLRotate = NewRotate(pawL);
        Shape pawLFur;
        if (flatStyle)
        {
            pawLFur = Add(pawL, R(42, 98, 30, 38, 15, fur));
            pawLFur.RenderTransform = new RotateTransform(-10);
        }
        else if (outlineWidth > 0 && outlineColor is { } oc)
        {
            pawLFur = Add(pawL, E(41, 94, 34, 42, fur, outlineWidth, oc, -14));
        }
        else
        {
            pawLFur = Add(pawL, E(41, 94, 34, 42, fur, angle: -14));
        }

        // 三指垫趾线（三根短竖线；GDD-09 §2.2 三指垫轮廓）
        var pawLineL = Add(pawL, PS("M 50,126 L 50,132 M 57,125 L 57,132 M 64,126 L 64,132", line, 2));

        var pawRTranslate = NewTranslate(pawR);
        var pawRRotate = NewRotate(pawR);
        Shape pawRFur;
        if (flatStyle)
        {
            pawRFur = Add(pawR, R(168, 98, 30, 38, 15, fur));
            pawRFur.RenderTransform = new RotateTransform(10);
        }
        else if (outlineWidth > 0 && outlineColor is { } oc2)
        {
            pawRFur = Add(pawR, E(165, 94, 34, 42, fur, outlineWidth, oc2, 14));
        }
        else
        {
            pawRFur = Add(pawR, E(165, 94, 34, 42, fur, angle: 14));
        }

        var pawLineR = Add(pawR, PS("M 172,126 L 172,132 M 179,125 L 179,132 M 186,126 L 186,132", line, 2));

        return (pawLFur, pawLineL, pawRFur, pawLineR, pawLTranslate, pawRTranslate, pawLRotate, pawRRotate);
    }

    /// <summary>
    /// sprite 物种鼓层（GDD-09 §7：双鼓不拆——sprite 物种只出角色，鼓共用程序化绘制）。
    /// 按当前风格分发鼓配色（与同风格矢量模板同色同几何同锚点；线稿排线笔法在
    /// sprite 路径省略——220×180 显示尺寸下描边鼓已够达意，已知简化）。
    /// </summary>
    public static (Shape BodyL, Shape RimL, Shape BodyR, Shape RimR) BuildSpriteBongos(
        Canvas bongos, Core.Config.StylePack style) => style.Id switch
    {
        "flat" => BuildDinoBongos(bongos, FlatBongo, FlatBongoRim, radius: 12),
        "sticker" => BuildDinoBongos(bongos, ClayDrum, ClayRim, 3, White, radius: 16),
        "chibi" => BuildDinoBongos(bongos, ClayDrum, ClayRim, 3, ChibiOutline, radius: 14),
        _ => BuildDinoBongos(bongos, ClayDrum, ClayRim, radius: 10),
    };

    /// <summary>恐龙通用双鼓（同一几何同一锚点，GDD-09 §2.2：鼓完全不变）。</summary>
    private static (Shape BodyL, Shape RimL, Shape BodyR, Shape RimR) BuildDinoBongos(
        Canvas bongos, Color drum, Color rim,
        double outlineWidth = 0, Color? outlineColor = null, double radius = 14)
    {        if (outlineWidth > 0 && outlineColor is { } oc)
        {
            Add(bongos, R(22, 136, 70, 50, radius, White, outlineWidth, oc));
            Add(bongos, R(148, 136, 70, 50, radius, White, outlineWidth, oc));
        }

        var bongoBodyL = Add(bongos, R(22, 136, 70, 50, radius, drum));
        var bongoRimL = Add(bongos, R(22, 136, 70, 14, 7, rim));
        var bongoBodyR = Add(bongos, R(148, 136, 70, 50, radius, drum));
        var bongoRimR = Add(bongos, R(148, 136, 70, 14, 7, rim));
        return (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR);
    }

    /// <summary>恐龙通用奶牛斑组（skin_cow 可见；几何与猫一致）。</summary>
    private static (Canvas CowSpots, IReadOnlyList<Shape> Shapes) BuildDinoCowSpots(Canvas body, Color spot)
    {
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", spot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", spot);
        spot1.Opacity = 0.9;
        spot2.Opacity = 0.9;
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);
        return (cowSpots, new Shape[] { spot1, spot2 });
    }

    // ── 黏土恐龙（GDD-09 §2.5：基线形体直出；棘/角黏土捏塑圆角，青绿为第二块黏土色）──

    public static StyleCatParts BuildDinoClay(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, 8, White, tipAccentsVisible: true, DinoAccent);
        var (cowSpots, cowSpotShapes) = BuildDinoCowSpots(body, CowSpot);

        // 头顶高光（随黏土语言）
        var highlight = E(70, 52, 26, 14, White, angle: -18);
        highlight.Opacity = 0.5;
        Add(body, highlight);

        Shape muzzle = BuildDinoFace(face, Ink, Blush);
        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, ClayDrum, ClayRim, 8, White);
        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, DinoLine, 8, White);

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, appendages[0], appendages[^1], appendages[0], appendages[^1],
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = appendages;
        return parts;
    }

    // ── 贴纸恐龙（整体含棘/角/尾白描边 5px 一起剪出；棘刺间隙与部件间隙一致）──

    public static StyleCatParts BuildDinoSticker(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double outline = 5;

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, outline, White, tipAccentsVisible: true, DinoAccent);
        ApplyInnerOutline(style, bodyFur);
        foreach (Shape horn in appendages.Where(s => (string?)s.Tag == "dino_horn"))
        {
            ApplyInnerOutline(style, horn);
        }

        var (cowSpots, cowSpotShapes) = BuildDinoCowSpots(body, CowSpot);

        Shape muzzle = BuildDinoFace(face, Ink, StickerBlush);
        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, ClayDrum, ClayRim, outline, White, radius: 16);
        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, DinoLine, outline, White);

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, appendages[0], appendages[^1], appendages[0], appendages[^1],
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = appendages;
        return parts;
    }

    // ── 极简扁平恐龙（棘 = 5 个纯色半圆几何；角 = 2 个圆头短柱；无描边 8% 明度差）──

    public static StyleCatParts BuildDinoFlat(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, 0, null, tipAccentsVisible: true, DinoAccent);
        var (cowSpots, cowSpotShapes) = BuildDinoCowSpotsFlat(body);

        Shape muzzle = BuildDinoFace(face, FlatInk, FlatInk, muzzleW: 34, muzzleH: 18, drawBlush: false);
        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, FlatBongo, FlatBongoRim, radius: 12);
        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, DinoDeep, flatStyle: true);
        pawLineL.Visibility = Visibility.Collapsed; // 扁平无趾线（隐藏占位）
        pawLineR.Visibility = Visibility.Collapsed;

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, appendages[0], appendages[^1], appendages[0], appendages[^1],
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = appendages;
        return parts;
    }

    /// <summary>扁平恐龙奶牛斑：白底 + 圆角几何斑块（与扁平猫同转译，GDD-08 §1.1）。</summary>
    private static (Canvas CowSpots, IReadOnlyList<Shape> Shapes) BuildDinoCowSpotsFlat(Canvas body)
    {
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spotBg = Add(cowSpots, E(48, 43, 144, 112, White));
        var spot1 = Add(cowSpots, R(70, 100, 34, 26, 10, CowSpot));
        var spot2 = Add(cowSpots, R(144, 62, 26, 22, 10, CowSpot));
        Add(body, cowSpots);
        return (cowSpots, new Shape[] { spotBg, spot1, spot2 });
    }

    // ── 手绘线稿恐龙（棘/角/尾只描线不填；青绿点缀以墨线小圆表达——
    //    GDD-09 §2.5 线稿"青绿点缀小尺寸省略"规则在 V1 的落地：点缀色并入墨线语言）──

    public static StyleCatParts BuildDinoSketch(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double ink = 2.2;

        // 纸底（线稿本色画纸；与线稿猫同规则）
        if (!string.IsNullOrEmpty(style.PaperBaseColor))
        {
            root.Children.Insert(0, E(30, 26, 180, 156, (Color)ColorConverter.ConvertFromString(style.PaperBaseColor)));
        }

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, 0, null, tipAccentsVisible: false, DinoAccent, includeFeatures: true);

        // 线稿：棘/角基座改描线不填充（12% 淡填充与 skinFillOpacity 同规则，GDD-09 §2.5）
        foreach (Shape appendage in appendages)
        {
            appendage.Stroke = Brush(SketchInk);
            appendage.StrokeThickness = ink;
            appendage.Opacity = 0.12;
        }

        // 身体墨线轮廓（手绘多段贝塞尔抖动感与线稿猫同语言）
        Add(body, PS(DinoBodyData, SketchInk, ink));
        // 尾巴墨线轮廓
        Add(body, PS(DinoTailData, SketchInk, ink));

        var (cowSpots, cowSpotShapes) = BuildDinoCowSpotsSketch(body, ink);

        Shape muzzle = BuildDinoFace(face, SketchInk, SketchInk, muzzleW: 34, muzzleH: 19, drawBlush: false);
        // 线稿腮红：3 条斜线（与线稿猫同语言）
        foreach ((double bx, double by) in new[] { (70.0, 88.0), (74.0, 91.0), (78.0, 94.0) })
        {
            Add(face, PS($"M {bx},{by} L {bx + 6},{by - 4}", SketchInk, 1.4));
            Add(face, PS($"M {bx + 96},{by} L {bx + 90},{by - 4}", SketchInk, 1.4));
        }

        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, ClayDrum, ClayRim);
        Add(bongos, PS("M 22,136 L 92,136 M 22,186 L 92,186", SketchInk, ink));
        Add(bongos, PS("M 22,136 L 22,186 M 92,136 L 92,186", SketchInk, ink));
        Add(bongos, PS("M 148,136 L 218,136 M 148,186 L 218,186", SketchInk, ink));
        Add(bongos, PS("M 148,136 L 148,186 M 218,136 L 218,186", SketchInk, ink));

        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, SketchInk);
        Add(pawL, PS("M 58,94 C 78,96 78,132 56,136 C 40,132 38,100 58,94 Z", SketchInk, ink));
        Add(pawR, PS("M 182,94 C 202,98 200,134 178,137 C 162,133 160,100 182,94 Z", SketchInk, ink));

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, appendages[0], appendages[^1], appendages[0], appendages[^1],
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = appendages;
        return parts;
    }

    /// <summary>线稿恐龙奶牛斑：轮廓线不填充（GDD-08 §1.1 线稿转译；渲染层置 Fill=null）。</summary>
    private static (Canvas CowSpots, IReadOnlyList<Shape> Shapes) BuildDinoCowSpotsSketch(Canvas body, double ink)
    {
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", CowSpot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", CowSpot);
        spot1.Stroke = Brush(SketchInk);
        spot2.Stroke = Brush(SketchInk);
        spot1.StrokeThickness = ink;
        spot2.StrokeThickness = ink;
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);
        return (cowSpots, new Shape[] { spot1, spot2 });
    }

    // ── Q版恐龙（头身比 1.3:1，比猫 Q版略收保龙形辨识；角变短圆墩；描边 9px 全部件）──

    public static StyleCatParts BuildDinoChibi(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();
        const double outline = 9;
        var oc = ChibiOutline;

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, outline, oc, tipAccentsVisible: true, DinoAccent);
        var (cowSpots, cowSpotShapes) = BuildDinoCowSpots(body, CowSpot);

        // Q版大脸：眼放大（实心，无高光双点）、腮红加大
        Shape muzzle = BuildDinoFace(face, Ink, ChibiBlush, muzzleW: 42, muzzleH: 25);
        Add(face, E(64, 88, 24, 14, ChibiBlush));
        Add(face, E(152, 88, 24, 14, ChibiBlush));

        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, ClayDrum, ClayRim, outline, oc);
        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, DinoLine, outline, oc);

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, appendages[0], appendages[^1], appendages[0], appendages[^1],
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = appendages;
        return parts;
    }

    // ── 剪纸恐龙（GDD-09 §2.3：棘刺列 = 独立色纸小条（+1 层）；角/尾烘焙；
    //    最坏 = 6 基础 + 棘刺条 1 + 奶牛斑块纸片 1 = 8 ≤ 8 预算内）──────────

    public static StyleCatParts BuildDinoPapercut(Core.Config.StylePack style)
    {
        var (root, body, face, bongos, pawL, pawR, hat) = NewRoot();

        var (tailFur, appendages, bodyFur) = BuildDinoCore(body, 0, null, tipAccentsVisible: false, DinoAccent, includeFeatures: false);
        // 剪纸龙的棘刺不走"基座椭圆"路径（独立色纸条自有画法）；双角也用撕纸边自有画法

        // 双角（撕纸边短柱）
        var hornL = Add(body, P("M 98,44 L 100,18 L 106,20 L 112,44 Z", DinoDeep));
        var hornR = Add(body, P("M 124,44 L 126,20 L 132,18 L 134,44 Z", DinoDeep));
        // 角尖青绿色纸小片（物种固定点缀）
        var hornTipL = Add(body, P("M 100,30 L 100,18 L 106,20 L 106,30 Z", DinoAccent));
        var hornTipR = Add(body, P("M 126,30 L 126,20 L 132,18 L 132,30 Z", DinoAccent));
        hornTipL.Tag = "dino_accent";
        hornTipR.Tag = "dino_accent";

        // 头身色纸（撕纸边，与剪纸猫同语言）
        Add(body, P(DinoBodyData, DinoBody));

        // 背棘列 = 独立色纸小条（+1 纹理层，GDD-09 §2.3；撕纸边 + 层错位阴影表达起伏）
        var spikeStrips = new List<Shape>();
        foreach ((double sx, double sy, double rx) in DinoSpikes)
        {
            var strip = P($"M {sx - rx:F1},{sy + 6:F1} Q {sx:F1},{sy - rx * 1.2:F1} {sx + rx:F1},{sy + 6:F1} Z", DinoAccent);
            strip.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
            strip.Tag = "dino_spike";
            spikeStrips.Add(strip);
            Add(body, strip);
        }

        // 尾尖青绿色纸小片
        var tailTip = Add(body, E(205, 134, 10, 9, DinoAccent));
        tailTip.Tag = "dino_accent";

        var (cowSpots, cowSpotShapes) = BuildDinoCowSpotsPapercut(body);

        // 脸色纸：眼/嘴直接"剪"在脸色纸上
        Shape muzzle = BuildDinoFace(face, Ink, Blush, muzzleW: 36, muzzleH: 21);
        muzzle.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };

        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = BuildDinoBongos(bongos, ClayDrum, ClayRim, radius: 10);
        var (pawLFur, pawLineL, pawRFur, pawLineR, pawLT, pawRT, pawLRot, pawRRot) =
            BuildDinoPaws(pawL, pawR, DinoDeep, DinoLine);
        pawLFur.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
        pawRFur.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };

        var parts = Assemble(style, root, body, face, bongos, pawL, pawR, hat,
            bodyFur, tailFur, hornL, hornR, hornL, hornR,
            muzzle, pawLFur, pawRFur, pawLineL, pawLineR,
            cowSpots, cowSpotShapes, bongoBodyL, bongoRimL, bongoBodyR, bongoRimR,
            pawLT, pawRT, pawLRot, pawRRot,
            NewSquash(body), NewTranslate(bongos), NewTranslate(hat), NewTranslate(face));
        parts.SpeciesAppendages = [hornL, hornR];
        return parts;
    }

    /// <summary>剪纸恐龙奶牛斑 = 独立小色纸片（与剪纸猫同转译）。</summary>
    private static (Canvas CowSpots, IReadOnlyList<Shape> Shapes) BuildDinoCowSpotsPapercut(Canvas body)
    {
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        var spot1 = P("M 66,120 Q 80,94 104,108 Q 110,136 86,144 Q 62,142 66,120 Z", CowSpot);
        var spot2 = P("M 146,66 Q 166,58 174,80 Q 166,96 146,88 Q 138,74 146,66 Z", CowSpot);
        spot1.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
        spot2.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 0, ShadowDepth = 3, Direction = 315 };
        cowSpots.Children.Add(spot1);
        cowSpots.Children.Add(spot2);
        Add(body, cowSpots);
        return (cowSpots, new Shape[] { spot1, spot2 });
    }

    /// <summary>
    /// 按 StylePack × 物种分发模板构建（未知物种/未知 id 均由 StyleRenderer 防御回退，
    /// 不会到达此处；物种 id 未知回退猫——GDD-09 §5）。
    /// </summary>
    public static StyleCatParts Build(Core.Config.StylePack style, string? speciesId = null) => speciesId switch
    {
        "dino" => style.Id switch
        {
            "clay" => BuildDinoClay(style),
            "sticker" => BuildDinoSticker(style),
            "flat" => BuildDinoFlat(style),
            "sketch" => BuildDinoSketch(style),
            "chibi" => BuildDinoChibi(style),
            "papercut" => BuildDinoPapercut(style),
            _ => BuildDinoClay(style), // 模板缺失 = 数据错误，防御回退黏土龙
        },
        _ => BuildCat(style),
    };

    /// <summary>猫模板分发（GDD-08 原入口；物种缺省 = 猫）。</summary>
    public static StyleCatParts BuildCat(Core.Config.StylePack style) => style.Id switch
    {
        "clay" => BuildClay(style),
        "sticker" => BuildSticker(style),
        "flat" => BuildFlat(style),
        "sketch" => BuildSketch(style),
        "chibi" => BuildChibi(style),
        "papercut" => BuildPapercut(style),
        _ => BuildClay(style), // 模板缺失 = 数据错误，防御回退黏土（GDD-08 §6 边界 4）
    };
}
