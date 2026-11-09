using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BongoCat.App.Styles;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// sprite 物种模板美术库（GDD-09 §7：阿瓜 = 位图 sprite 渲染路径）。
/// 与 <see cref="StyleArt"/>（矢量程序化）产出同一 <see cref="StyleCatParts"/> 契约：
/// 六层结构一致（body → face → bongos → pawL → pawR → hat），动画句柄语义一致，
/// 敲击动画（CatTapAnimator）与装扮换绑（StyleRenderer.ApplyOutfit）零改动复用。
///
/// 构成：
/// - body/pawL/pawR = 嵌入资源位图（440×360 @2x，缩放到 220×180 逻辑，居中挂在 240×200 画布）；
///   脸烘焙进 body（v1 不做表情切换——矢量物种有 face 层，sprite 物种省略，GDD-09 §7）；
/// - 双鼓不拆：复用 StyleArt.BuildSpriteBongos 程序化鼓层（按风格配色）；
/// - 装扮换绑目标（SkinTargets 的 Shape 组）为哑形状（透明、挂 collapsed 层）——
///   sprite 位图不接受色板换绑，但契约字段必须非空（ApplyOutfit 直接访问）；
/// - 帽子：矢量帽子经 PartHat 直接叠画（跨渲染路径混排），锚点由 anchors.json 提供。
/// 任一部件缺资源 / anchors 缺风格 → 返回 null（调用方回退 vector cat + 日志，绝不崩溃）。
/// </summary>
public static class SpriteArt
{
    /// <summary>sprite 资源逻辑画布（锚点坐标系；2x 资产 440×360 缩放显示）。</summary>
    public const double SpriteWidth = 220;
    public const double SpriteHeight = 180;

    /// <summary>sprite 在 240×200 渲染画布中的居中偏移。</summary>
    public const double OffsetX = (StyleArt.ArtWidth - SpriteWidth) / 2;
    public const double OffsetY = (StyleArt.ArtHeight - SpriteHeight) / 2;

    /// <summary>矢量模板的猫头顶基准 art Y（clay 头顶 37——帽锚 dy 以此为基线，GDD-08 构图约定）。</summary>
    public const double CatHatBaselineArtY = 37;

    /// <summary>位图缓存（Frozen；同 (species,style,part) 跨窗口/重载共享解码结果）。</summary>
    private static readonly Dictionary<string, BitmapSource> ImageCache = new();

    /// <summary>
    /// 构建 sprite 模板。资产/锚点缺失返回 null（调用方回退 cat）。
    /// </summary>
    public static StyleCatParts? Build(Core.Config.StylePack style, string speciesId,
        SpriteSpeciesCatalog sprites, Action<string>? log)
    {
        if (!sprites.TryGetAnchors(style.Id, out SpriteAnchors anchors))
        {
            log?.Invoke($"[Sprite] 物种 '{speciesId}' 风格 '{style.Id}' 缺 anchors，回退 cat（GDD-09 §7）");
            return null;
        }

        BitmapSource? body = LoadPart(speciesId, style.Id, "body", sprites, log);
        BitmapSource? pawL = LoadPart(speciesId, style.Id, "pawL", sprites, log);
        BitmapSource? pawR = LoadPart(speciesId, style.Id, "pawR", sprites, log);
        if (body is null || pawL is null || pawR is null)
        {
            log?.Invoke($"[Sprite] 物种 '{speciesId}' 风格 '{style.Id}' 缺 sprite 部件，回退 cat（GDD-09 §7）");
            return null;
        }

        var (root, bodyLayer, face, bongos, pawLLayer, pawRLayer, hat) = StyleArt.NewRoot();

        // ① body sprite（居中位；squash 以脚底为轴——与矢量层同轴语义）
        var bodyImage = new Image
        {
            Source = body,
            Width = SpriteWidth,
            Height = SpriteHeight,
        };
        Canvas.SetLeft(bodyImage, OffsetX);
        Canvas.SetTop(bodyImage, OffsetY);
        StyleArt.Add(bodyLayer, bodyImage);
        var squash = StyleArt.NewSquash(bodyLayer);

        // ② 脸层：v1 脸烘焙进 body，face 层保持空（契约层位保留）

        // ③ 程序化邦戈鼓层（双鼓不拆，按风格配色复用矢量绘制）
        var (bongoBodyL, bongoRimL, bongoBodyR, bongoRimR) = StyleArt.BuildSpriteBongos(bongos, style);
        var bongoSink = StyleArt.NewTranslate(bongos);

        // ④⑤ 双爪 sprite（与 body 同位对齐；旋转轴 = anchors.pawAxis 爪根）
        var pawLTranslate = StyleArt.NewTranslate(pawLLayer);
        var pawLRotate = NewRotate(pawLLayer, axisX: anchors.PawAxisLX, axisY: anchors.PawAxisLY);
        StyleArt.Add(pawLLayer, PartImage(pawL));
        var pawRTranslate = StyleArt.NewTranslate(pawRLayer);
        var pawRRotate = NewRotate(pawRLayer, axisX: anchors.PawAxisRX, axisY: anchors.PawAxisRY);
        StyleArt.Add(pawRLayer, PartImage(pawR));

        // ⑥ hat 层（矢量帽子跨路径混排；锚点平移由 StyleRenderer 合成后统一钳制）
        var hatBounce = StyleArt.NewTranslate(hat);
        var faceBounce = StyleArt.NewTranslate(face);

        // 哑换绑形状（挂 collapsed 层；SkinTargets 契约字段必须非空，sprite 位图不接受色板）
        var dummies = CreateDummyShapes();
        root.Children.Add(dummies.Canvas);

        StyleArt.ApplyShadow(root, style);
        return new StyleCatParts
        {
            Root = root,
            PartBody = bodyLayer,
            PartFace = face,
            PartBongos = bongos,
            PartPawL = pawLLayer,
            PartPawR = pawRLayer,
            PartHat = hat,
            BodyFur = dummies.BodyFur,
            TailFur = dummies.TailFur,
            EarLFur = dummies.EarLFur,
            EarRFur = dummies.EarRFur,
            EarInnerL = dummies.EarInnerL,
            EarInnerR = dummies.EarInnerR,
            NoseFill = dummies.NoseFill,
            PawLFur = dummies.PawLFur,
            PawRFur = dummies.PawRFur,
            PawLineL = dummies.PawLineL,
            PawLineR = dummies.PawLineR,
            CowSpots = dummies.CowSpots,
            CowSpotShapes = [],
            BongoBodyL = bongoBodyL,
            BongoRimL = bongoRimL,
            BongoBodyR = bongoBodyR,
            BongoRimR = bongoRimR,
            PawLTranslate = pawLTranslate,
            PawRTranslate = pawRTranslate,
            PawLRotate = pawLRotate,
            PawRRotate = pawRRotate,
            BodySquash = squash,
            BongoSink = bongoSink,
            HatBounce = hatBounce,
            FaceBounce = faceBounce,
        };
    }

    /// <summary>解码一个 sprite 部件（缓存 + Freeze；缺资源/解码失败返回 null——防御不抛）。</summary>
    private static BitmapSource? LoadPart(string speciesId, string styleId, string part,
        SpriteSpeciesCatalog sprites, Action<string>? log)
    {
        string key = $"{speciesId}/{styleId}/{part}";
        if (ImageCache.TryGetValue(key, out BitmapSource? cached))
        {
            return cached;
        }

        try
        {
            if (!sprites.TryOpenPart(speciesId, styleId, part, out Stream stream))
            {
                log?.Invoke($"[Sprite] 嵌入资源缺失：{key}（sprite 渲染回退 vector）");
                return null;
            }

            using (stream)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // 流关闭后仍可用
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze(); // 跨线程冻结（ADR-003 渲染纪律）
                ImageCache[key] = bmp;
                return bmp;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Sprite] 位图解码失败：{key}（{ex.Message}；sprite 渲染回退 vector）");
            return null;
        }
    }

    /// <summary> clay body 缩略图（选择器页签图标；缺资源返回 null 由调用方降级 emoji）。</summary>
    public static BitmapSource? TryGetTabThumbnail(SpriteSpeciesCatalog sprites, string speciesId, string styleId)
        => LoadPart(speciesId, styleId, "body", sprites, null);

    private static Image PartImage(BitmapSource source) => new()
    {
        Source = source,
        Width = SpriteWidth,
        Height = SpriteHeight,
    };

    /// <summary>爪旋转（绕 anchors.pawAxis 爪根轴；origin 归一化到 220×180 元素局部坐标）。</summary>
    private static RotateTransform NewRotate(Canvas layer, double axisX, double axisY)
    {
        var r = new RotateTransform { Angle = 0 };
        ((TransformGroup)layer.RenderTransform).Children.Add(r);
        layer.RenderTransformOrigin = new Point(axisX / SpriteWidth, axisY / SpriteHeight);
        return r;
    }

    /// <summary>哑换绑形状组（0×0 透明，挂 collapsed 画布；契约字段非空即可，涂色无视觉效应）。</summary>
    private static (Canvas Canvas, Shape BodyFur, Shape TailFur, Shape EarLFur, Shape EarRFur,
        Shape EarInnerL, Shape EarInnerR, Shape NoseFill, Shape PawLFur, Shape PawRFur,
        Shape PawLineL, Shape PawLineR, FrameworkElement CowSpots) CreateDummyShapes()
    {
        var canvas = new Canvas { Visibility = Visibility.Collapsed };
        Shape Dummy() => new Rectangle { Width = 0, Height = 0, Fill = null };
        var bodyFur = Dummy();
        var tailFur = Dummy();
        var earL = Dummy();
        var earR = Dummy();
        var innerL = Dummy();
        var innerR = Dummy();
        var nose = Dummy();
        var pawL = Dummy();
        var pawR = Dummy();
        var lineL = Dummy();
        var lineR = Dummy();
        var cowSpots = new Canvas { Visibility = Visibility.Collapsed };
        foreach (Shape s in new[] { bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawL, pawR, lineL, lineR })
        {
            canvas.Children.Add(s);
        }

        canvas.Children.Add(cowSpots);
        return (canvas, bodyFur, tailFur, earL, earR, innerL, innerR, nose, pawL, pawR, lineL, lineR, cowSpots);
    }
}
