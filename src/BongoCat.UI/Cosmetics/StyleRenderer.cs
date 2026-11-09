using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// 风格渲染器（GDD-08/GDD-09：唯一通用渲染入口）——主猫 MainWindow 与远程猫 RemoteCatWindow
/// 共用同一实现（单一数据源纪律）。职责：
/// ① ApplyStyle：往宿主画布装载指定风格×物种模板（未知 id 防御回退 clay/cat + 日志）；
///    hat 锚点 = 风格层偏移 + 物种层偏移相加合成（GDD-09 §2.3），并对合成结果做
///    画布上缘钳制（开放问题①的通用落地：越界自动下移并记日志）；
/// ② ApplyOutfit：经 CosmeticArt 三套换绑（色板单一数据源）后，再叠加风格语言转译
///    （皮肤填充透明度 / 奶牛斑笔法 / 风格默认鼓色 / 扁平爪 8% 明度差 / 物种附属形状换色）。
/// 换风格/换物种 = 重载模板 + 参数；换装只改 Fill/Stroke/Visibility（纯渲染属性，零 Layout 重算）。
/// </summary>
public static class StyleRenderer
{
    /// <summary>
    /// sprite 物种资产目录（GDD-09 §7）：启动时由 App 层注入（LoadFromAssembly 嵌入资源）。
    /// 未注入 / 资产缺失时 sprite 物种回退 vector cat 渲染（绝不崩溃）。
    /// </summary>
    public static SpriteSpeciesCatalog? SpriteSpecies { get; set; }

    /// <summary>
    /// 装载风格模板到宿主画布（含帽子画布预建 + hat 锚点偏移合成）。猫物种便捷入口。
    /// </summary>
    /// <returns>新模板的命名部件集合（调用方保存供动画/换绑使用）。</returns>
    public static StyleCatParts ApplyStyle(Canvas host, string? styleId, StyleCatalog catalog, Action<string>? log = null)
        => ApplyStyle(host, styleId, catalog, BongoCat.Core.Config.SpeciesCatalog.DefaultSpeciesId, log);

    /// <summary>
    /// 装载风格 × 物种模板到宿主画布（GDD-09：物种=形体层；§7 sprite 物种走位图路径）。
    /// 未知风格/物种 id → 回退 clay/cat 并记日志（绝不抛异常——存档脏数据不崩溃）；
    /// sprite 物种缺资产/anchors → 回退 cat 渲染。
    /// </summary>
    public static StyleCatParts ApplyStyle(Canvas host, string? styleId, StyleCatalog catalog,
        string? speciesId, Action<string>? log = null)
    {
        string resolvedSpecies = speciesId ?? BongoCat.Core.Config.SpeciesCatalog.DefaultSpeciesId;
        bool isSprite = SpriteSpeciesCatalog.IsSpriteSpecies(resolvedSpecies);
        if (!isSprite && resolvedSpecies is not ("cat" or "dino"))
        {
            log?.Invoke($"[Species] 物种 '{speciesId}' 未知，回退 cat（GDD-09 §5）");
            resolvedSpecies = BongoCat.Core.Config.SpeciesCatalog.DefaultSpeciesId;
        }

        if (!catalog.TryGet(styleId, out StylePack style))
        {
            log?.Invoke($"[Style] 风格 '{styleId}' 未知，回退 clay（GDD-08 §6 边界 1）");
            catalog.TryGet(StyleCatalog.DefaultStyleId, out style!);
        }

        host.Children.Clear();

        // GDD-09 §7：sprite 物种（阿瓜）走位图路径；缺资产/未注入目录 → 回退 cat
        bool spriteApplied = false;
        double hatOffsetY;
        double hatOffsetX = 0;
        StyleCatParts parts;
        if (isSprite && SpriteSpecies is { } sprites)
        {
            StyleCatParts? spriteParts = SpriteArt.Build(style, resolvedSpecies, sprites, log);
            if (spriteParts is not null)
            {
                parts = spriteParts;
                spriteApplied = true;

                // sprite 帽锚（anchors.json 220×180 逻辑坐标 → art 坐标 +10 居中偏移）：
                // dy = 阿瓜头顶 art y − 矢量猫头顶基准 37（帽形状贴合 clay 头顶的基线）+ 风格层偏移
                sprites.TryGetAnchors(style.Id, out SpriteAnchors anchors);
                hatOffsetY = (anchors.HatAnchorY + SpriteArt.OffsetY) - SpriteArt.CatHatBaselineArtY + style.HatAnchorOffsetY;
                hatOffsetX = anchors.HatAnchorX - SpriteArt.SpriteWidth / 2;
            }
            else
            {
                resolvedSpecies = BongoCat.Core.Config.SpeciesCatalog.DefaultSpeciesId;
                parts = StyleArt.Build(style, resolvedSpecies);
                hatOffsetY = style.HatAnchorOffsetY;
            }
        }
        else
        {
            if (isSprite)
            {
                log?.Invoke("[Species] sprite 资产目录未注入，物种回退 cat（GDD-09 §7）");
                resolvedSpecies = BongoCat.Core.Config.SpeciesCatalog.DefaultSpeciesId;
            }

            parts = StyleArt.Build(style, resolvedSpecies);
            hatOffsetY = style.HatAnchorOffsetY + (resolvedSpecies == "dino" ? DinoHatAnchorOffsetY : 0);
        }

        host.Children.Add(parts.Root);

        // 帽子画布预建（E3 纪律：装备切换只切 Visibility，不重建几何；每模板实例独立预建）
        foreach ((string _, Canvas hatCanvas) in CosmeticArt.CreateHatCanvases())
        {
            parts.PartHat.Children.Add(hatCanvas);
        }

        // hat 锚点偏移合成（GDD-09 §2.3：风格层 + 物种层相加；Q版龙 = -10 + -14 = -24）
        if (hatOffsetY != 0 || hatOffsetX != 0)
        {
            parts.PartHat.RenderTransform = new TranslateTransform(hatOffsetX, hatOffsetY);
        }

        // 开放问题①（GDD-09 §9）：合成锚点使帽位图越过画布上缘 → 钳制下移并记日志
        //（窗体 220×180/画布 240×200 的可见上缘 = 画布 y=0）。
        // 容差 10px：Geometry.Bounds 含贝塞尔控制点外扩（描边宽未计入），帽顶真实墨迹比量测值
        // 低约 4~8px（实测 hat_santa 量测顶 6.5 / 真实 ~10.5；全帽最坏量测顶 4）。容差内不钳制：
        // 保住既有批准视觉（chibi 猫 -10）与 GDD-09 规格（恐龙 -14 全风格精确生效），
        // 只拦真实越界（Q版龙合成 -24 → 钳制为 -14，帽落双角之间）。
        const double ClampTopTolerance = 10;
        if (TryMeasureHatTop(parts.PartHat, out double hatTop) && hatTop + hatOffsetY < -ClampTopTolerance)
        {
            double clamped = -ClampTopTolerance - hatTop;
            log?.Invoke($"[Style] hat 锚点合成 {hatOffsetY:0.#}px 越出画布上缘（量测帽顶 {hatTop + hatOffsetY:0.#}px，" +
                        $"真实墨迹约 {hatTop + hatOffsetY + ClampTopTolerance:0.#}px），钳制为 {clamped:0.#}px（GDD-09 开放问题①）");
            parts.PartHat.RenderTransform = new TranslateTransform(hatOffsetX, clamped);
        }

        string renderPath = spriteApplied ? "sprite" : "vector";
        log?.Invoke($"[Style] 已装载风格 '{style.Id}' × 物种 '{resolvedSpecies}'（{renderPath} 路径；压深 {style.TapDownPx:0.#}px，" +
                    $"回弹 {style.ReboundMs:0}ms {style.Easing}，hat 锚点 ({hatOffsetX:0.#},{hatOffsetY:0.#})px）");
        return parts;
    }

    /// <summary>恐龙物种层 hat 锚点增量（GDD-09 §2.3：-14px，落位双角之间；由 species.json 数据驱动值在此对齐）。</summary>
    public const double DinoHatAnchorOffsetY = -14;

    /// <summary>
    /// 量测帽子内容在宿主画布坐标系中的最高点（纯几何、零布局依赖——可在模板装载后
    /// 立即调用，不要求 Measure/Arrange 已运行）。量测失败返回 false（不钳制）。
    /// </summary>
    private static bool TryMeasureHatTop(Canvas hatLayer, out double top)
    {
        top = double.PositiveInfinity;
        bool found = false;
        foreach (UIElement child in hatLayer.Children)
        {
            CollectTop(child, 0, 0, ref top, ref found);
        }

        return found && !double.IsPositiveInfinity(top);
    }

    private static void CollectTop(System.Windows.DependencyObject node, double offsetX, double offsetY, ref double top, ref bool found)
    {
        switch (node)
        {
            case Shape shape:
            {
                double x = Canvas.GetLeft(shape);
                double y = Canvas.GetTop(shape);
                Rect geo = shape is Path { Data: not null } path
                    ? path.Data.Bounds
                    : new Rect(0, 0, shape.Width, shape.Height);
                Rect local = new(
                    offsetX + (double.IsNaN(x) ? 0 : x) + geo.X,
                    offsetY + (double.IsNaN(y) ? 0 : y) + geo.Y,
                    geo.Width,
                    geo.Height);
                top = Math.Min(top, local.Top);
                found = true;
                break;
            }

            case Canvas canvas:
            {
                double x = Canvas.GetLeft(canvas);
                double y = Canvas.GetTop(canvas);
                double nx = offsetX + (double.IsNaN(x) ? 0 : x);
                double ny = offsetY + (double.IsNaN(y) ? 0 : y);
                foreach (UIElement child in canvas.Children)
                {
                    CollectTop(child, nx, ny, ref top, ref found);
                }

                break;
            }
        }
    }

    /// <summary>
    /// 按当前装扮 + 风格换绑全部渲染层（主猫/远程猫共用）。
    /// 先走 CosmeticArt 三套换绑（内容层色板），再叠加风格语言转译（语言层）。
    /// </summary>
    public static void ApplyOutfit(StyleCatParts parts, StylePack style, string? hatId, string? skinId, string? bongosId)
    {
        CosmeticArt.ApplyHat(parts.PartHat, hatId);
        CosmeticArt.ApplySkin(skinId, new CosmeticArt.SkinTargets
        {
            BodyFur = parts.BodyFur,
            TailFur = parts.TailFur,
            EarLFur = parts.EarLFur,
            EarRFur = parts.EarRFur,
            EarInnerL = parts.EarInnerL,
            EarInnerR = parts.EarInnerR,
            NoseFill = parts.NoseFill,
            PawLFur = parts.PawLFur,
            PawRFur = parts.PawRFur,
            PawLineL = parts.PawLineL,
            PawLineR = parts.PawLineR,
            CowSpots = parts.CowSpots,
            BodyAppendages = parts.SpeciesAppendages, // GDD-09：恐龙棘/角基座随"耳"槽换色
        });
        ApplyBongosStyled(parts, style, bongosId);

        ApplySkinTranslation(parts, style, skinId);
    }

    /// <summary>鼓换绑：未装备（空 artKey）时用风格默认鼓色（如扁平 #5B8DEF 单色），装备时走内容层色板。</summary>
    private static void ApplyBongosStyled(StyleCatParts parts, StylePack style, string? bongosId)
    {
        if (string.IsNullOrEmpty(bongosId) && style.BongosDefaultBody.Length > 0)
        {
            var body = Frozen(style.BongosDefaultBody);
            var rim = Frozen(style.BongosDefaultRim.Length > 0 ? style.BongosDefaultRim : style.BongosDefaultBody);
            parts.BongoBodyL.Fill = body;
            parts.BongoBodyR.Fill = body;
            parts.BongoRimL.Fill = rim;
            parts.BongoRimR.Fill = rim;
            return;
        }

        CosmeticArt.ApplyBongos(bongosId, new CosmeticArt.BongosTargets
        {
            BodyL = parts.BongoBodyL,
            RimL = parts.BongoRimL,
            BodyR = parts.BongoBodyR,
            RimR = parts.BongoRimR,
        });
    }

    /// <summary>皮肤色板的风格语言转译（GDD-08 §1.1；按 StylePack 参数数据驱动）。</summary>
    private static void ApplySkinTranslation(StyleCatParts parts, StylePack style, string? skinId)
    {
        bool isCow = skinId == "skin_cow";

        // ① 皮肤填充透明度（线稿 12% 淡填充）：作用于内容层涂色的形状（不动模板静态装饰）
        ApplyFillOpacity(parts.BodyFur, style.SkinFillOpacity);
        ApplyFillOpacity(parts.EarLFur, style.SkinFillOpacity);
        ApplyFillOpacity(parts.EarRFur, style.SkinFillOpacity);
        ApplyFillOpacity(parts.EarInnerL, style.SkinFillOpacity);
        ApplyFillOpacity(parts.EarInnerR, style.SkinFillOpacity);
        ApplyFillOpacity(parts.NoseFill, style.SkinFillOpacity);
        ApplyFillOpacity(parts.PawLFur, style.SkinFillOpacity);
        ApplyFillOpacity(parts.PawRFur, style.SkinFillOpacity);

        // ② 奶牛斑笔法转译（solid 色块 / outline 轮廓线不填充 / paper 独立色纸片）
        foreach (Shape spot in parts.CowSpotShapes)
        {
            switch (style.SpotMode)
            {
                case "outline":
                    spot.Fill = null;
                    if (spot.Stroke is null || ReferenceEquals(spot.Stroke, Frozen("transparent")))
                    {
                        spot.Stroke = Frozen("#4A403A");
                        spot.StrokeThickness = 2.2;
                    }

                    break;

                default: // solid / paper：保留模板画法（色块或带投影色纸片）
                    break;
            }
        }

        // ③ 扁平爪 8% 明度差（GDD-08 §2.2：爪比身体深 8%）
        if (style.Id == "flat")
        {
            parts.PawLFur.Fill = Darken(parts.PawLFur.Fill, 0.08);
            parts.PawRFur.Fill = Darken(parts.PawRFur.Fill, 0.08);
        }

        _ = isCow; // isCow 仅文档语义（可见性已由 CosmeticArt.ApplySkin 控制），保留参数完整性
    }

    private static void ApplyFillOpacity(Shape shape, double opacity)
    {
        if (shape.Fill is not null && opacity < 1.0)
        {
            shape.Opacity = opacity;
        }
        else if (opacity >= 1.0)
        {
            shape.Opacity = 1.0;
        }
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>按比例压暗画刷颜色（扁平 8% 明度差；HSL L 分量直接缩放）。</summary>
    private static Brush? Darken(Brush? brush, double ratio)
    {
        if (brush is SolidColorBrush { } solid && !solid.IsFrozen)
        {
            Color c = solid.Color;
            var dark = Color.FromRgb(
                (byte)(c.R * (1 - ratio)),
                (byte)(c.G * (1 - ratio)),
                (byte)(c.B * (1 - ratio)));
            var b = new SolidColorBrush(dark);
            b.Freeze();
            return b;
        }

        return brush;
    }
}
