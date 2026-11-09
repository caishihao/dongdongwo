using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.UI.Cosmetics;

namespace BongoCat.StylePreview;

/// <summary>
/// 风格包预览渲染工具（烟测辅助，GDD-08 §6 / GDD-09 同款生产同路径）：
/// 逐"风格 × 物种"组合 StyleRenderer.ApplyStyle 装载模板 + ApplyOutfit 叠圣诞帽（hat_santa）+
/// 物种默认皮肤（猫=skin_cream，龙=dino_default），离屏 RenderTargetBitmap 渲染 240×200
/// 逻辑画布，PNG 覆盖写到 analysis/style-previews/preview_&lt;id&gt;.png / preview_dino_&lt;id&gt;.png。
/// 12 张渲染条件完全一致（同装扮、同背景、同分辨率），供风格间对照与烟测文档引用。
/// 另实测 GDD-09 开放问题①：chibi 龙 hat 合成锚点 -24px 是否越出画布上缘（钳制结论打到 stdout）。
/// 用法：dotnet run --project scripts/style-preview
/// </summary>
internal static class Program
{
    private const string OutDir = @"D:\BongoCat\analysis\style-previews";

    /// <summary>渲染分辨率倍率（1 = 240×200 原生逻辑像素，与烟测文档逐像素检查口径一致）。</summary>
    private const double Scale = 1.0;

    /// <summary>预览底色（中性灰：白贴纸描边/白色底版在图上可辨）。</summary>
    private static readonly Color Backdrop = Color.FromRgb(0x80, 0x80, 0x80);

    [STAThread]
    private static void Main()
    {
        StyleCatalog catalog = StyleCatalogLoader.LoadFromAssembly(_ => { });
        SpeciesCatalog speciesCatalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });
        Directory.CreateDirectory(OutDir);

        foreach (SpeciesPack species in speciesCatalog.Species)
        {
            // 物种默认皮肤（猫 = 奶油小猫；龙 = 柚柚小恐龙 dino_default，GDD-09 §2.4）
            string skinId = species.DefaultSkinId.Length > 0 ? species.DefaultSkinId : "skin_cream";

            foreach (StylePack style in catalog.Styles)
            {
                var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };

                // 生产同路径：模板装载（含 hat 预建与锚点合成）+ 装扮换绑（圣诞帽 + 物种默认皮肤，鼓用风格默认）
                StyleCatParts parts = StyleRenderer.ApplyStyle(host, style.Id, catalog, species.Id, Console.WriteLine);
                StyleRenderer.ApplyOutfit(parts, style, "hat_santa", skinId, bongosId: null);

                // 背景层（ApplyStyle 会 Clear 宿主，故在其后插到最底：仅预览可见性参照，
                // 中性灰让白描边/白色底版可辨）
                host.Children.Insert(0, new System.Windows.Shapes.Rectangle
                {
                    Width = StyleArt.ArtWidth,
                    Height = StyleArt.ArtHeight,
                    Fill = new SolidColorBrush(Backdrop),
                });

                host.Measure(new Size(StyleArt.ArtWidth, StyleArt.ArtHeight));
                host.Arrange(new Rect(0, 0, StyleArt.ArtWidth, StyleArt.ArtHeight));

                int pxW = (int)(StyleArt.ArtWidth * Scale);
                int pxH = (int)(StyleArt.ArtHeight * Scale);
                var rtb = new RenderTargetBitmap(pxW, pxH, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                rtb.Render(host);

                string prefix = species.Id == SpeciesCatalog.DefaultSpeciesId ? "" : $"{species.Id}_";
                string path = Path.Combine(OutDir, $"preview_{prefix}{style.Id}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using FileStream fs = File.Create(path);
                encoder.Save(fs);

                Console.WriteLine($"[preview] {species.Id}/{style.Id} → {path} ({pxW}×{pxH})");
            }
        }

        Console.WriteLine("[preview] 全部 12 张预览（6 猫 + 6 龙）已重出。");
        Console.WriteLine("[preview] hat 锚点钳制日志见上方 [Style] 行（GDD-09 开放问题①：chibi 龙合成 -24px 是否越界）。");
    }
}
