using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.UI.Cosmetics;

using Xunit;

namespace BongoCat.UI.Tests;

/// <summary>
/// 恐龙模板渲染完整性测试（GDD-09 §2）：
/// ① 6 风格 × 恐龙模板全部可构建且命名换绑目标齐全（主猫/远程猫共用契约）；
/// ② 恐龙专属特征齐备（5 枚棘刺 / 双角 / 青绿固定点缀 Tag=dino_accent）；
/// ③ 物种附属形状随皮肤"耳"槽换色（SpeciesAppendages）；猫模板无附属形状（回归防御）；
/// ④ hat 锚点合成：风格层 + 物种层相加（恐龙 -14），超界钳制（开放问题①）。
/// WPF 视觉对象在 STA 线程构建。
/// </summary>
public class DinoRenderTests
{
    private static readonly StyleCatalog Styles = StyleCatalogLoader.LoadFromAssembly(_ => { });
    private static readonly SpeciesCatalog Species = SpeciesCatalogLoader.LoadFromAssembly(_ => { });

    /// <summary>xunit 并行测试线程非 STA：视觉树构建统一投递到专用 STA 线程执行。</summary>
    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    public static TheoryData<string> StyleIds => [.. Styles.Styles.Select(s => s.Id)];

    [Theory]
    [MemberData(nameof(StyleIds))]
    public void 恐龙模板_全风格构建成功_命名换绑目标齐全(string styleId)
    {
        StyleCatParts parts = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            return StyleRenderer.ApplyStyle(host, styleId, Styles, "dino");
        });

        Assert.NotNull(parts.Root);
        Assert.NotNull(parts.BodyFur);
        Assert.NotNull(parts.TailFur);
        Assert.NotNull(parts.EarLFur);
        Assert.NotNull(parts.EarRFur);
        Assert.NotNull(parts.NoseFill);
        Assert.NotNull(parts.PawLFur);
        Assert.NotNull(parts.PawRFur);
        Assert.NotNull(parts.CowSpots);
        Assert.NotEmpty(parts.CowSpotShapes);
        Assert.NotNull(parts.PawLTranslate);
        Assert.NotNull(parts.BodySquash);
        Assert.NotEmpty(parts.SpeciesAppendages); // 恐龙必有附属形状（棘/角基座）
    }

    [Theory]
    [MemberData(nameof(StyleIds))]
    public void 恐龙模板_五枚背棘齐备_青绿点缀非线稿必有且颜色固定(string styleId)
    {
        // GDD-09 §2.5：线稿龙青绿点缀以墨线语言表达（省略点缀色），其余风格必有青绿固定点缀
        bool isSketch = styleId == "sketch";
        (int spikeCount, List<Color> accentColors) = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleCatParts parts = StyleRenderer.ApplyStyle(host, styleId, Styles, "dino");
            var spikes = new List<Shape>();
            var accents = new List<Shape>();
            CollectByTag(parts.PartBody, "dino_spike", spikes);
            CollectByTag(parts.PartBody, "dino_accent", accents);
            return (spikes.Count, accents.Select(a => ((SolidColorBrush)a.Fill).Color).ToList());
        });

        Assert.Equal(5, spikeCount); // GDD-09 §2.2：背中线 5 枚圆钝棘刺

        if (isSketch)
        {
            Assert.Empty(accentColors); // 线稿：点缀并入墨线语言（小尺寸省略规则，GDD-09 §2.5）
        }
        else
        {
            Assert.NotEmpty(accentColors);
            foreach (Color color in accentColors)
            {
                // 青绿固定色 #2EA98F（物种固定，不随皮肤色板——GDD-09 §2.2/§6 差异 6）
                Assert.Equal(Color.FromRgb(0x2E, 0xA9, 0x8F), color);
            }
        }
    }

    [Fact]
    public void 恐龙模板_附属形状随皮肤耳槽换色_猫模板无附属形状()
    {
        (Color dinoEarColor, int dinoAppendageCount, int catAppendageCount) = OnSta(() =>
        {
            var dinoHost = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleCatParts dino = StyleRenderer.ApplyStyle(dinoHost, "clay", Styles, "dino");
            StyleRenderer.ApplyOutfit(dino, Styles.Styles[0], null, "skin_cream", null);
            var dinoBrush = (SolidColorBrush)dino.SpeciesAppendages[0].Fill;

            var catHost = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleCatParts cat = StyleRenderer.ApplyStyle(catHost, "clay", Styles, "cat");
            StyleRenderer.ApplyOutfit(cat, Styles.Styles[0], null, "skin_cream", null);

            return (dinoBrush.Color, dino.SpeciesAppendages.Count, cat.SpeciesAppendages.Count);
        });

        // skin_cream 的"耳"槽色 #EDD3A4 —— 棘/角基座随皮肤换绑（GDD-09：皮肤转译规则不变）
        Assert.Equal(Color.FromRgb(0xED, 0xD3, 0xA4), dinoEarColor);
        Assert.True(dinoAppendageCount >= 7); // 5 棘刺基座 + 2 角基座
        Assert.Equal(0, catAppendageCount);   // 猫模板无附属形状（回归防御）
    }

    [Fact]
    public void Hat锚点合成_恐龙等于风格加物种偏移_Q版龙超界被钳制()
    {
        (double clayDinoOffset, double chibiDinoOffset, double chibiCatOffset) = OnSta(() =>
        {
            double GetOffset(string styleId, string speciesId)
            {
                var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
                StyleCatParts parts = StyleRenderer.ApplyStyle(host, styleId, Styles, speciesId);
                return (parts.PartHat.RenderTransform as TranslateTransform)?.Y ?? 0;
            }

            return (GetOffset("clay", "dino"), GetOffset("chibi", "dino"), GetOffset("chibi", "cat"));
        });

        // GDD-09 §2.3：恐龙 hat 锚点 = 风格层 + 物种层(-14) 相加
        Assert.Equal(-14, clayDinoOffset);
        Assert.Equal(-10, chibiCatOffset); // 猫既有行为不变（回归防御）

        // 开放问题①：Q版龙合成 -24 实测越出画布上缘 → 钳制（介于 -12~ -20，帽落双角之间且不出界）
        Assert.NotEqual(-24, chibiDinoOffset);
        Assert.InRange(chibiDinoOffset, -20, -8);
    }

    [Fact]
    public void 未知物种id_回退cat渲染_模板照常可用()
    {
        StyleCatParts parts = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            return StyleRenderer.ApplyStyle(host, "clay", Styles, "dragon");
        });

        Assert.NotNull(parts.Root);
        Assert.Empty(parts.SpeciesAppendages); // 回退猫 = 无附属形状
    }

    private static void CollectByTag(DependencyObject root, string tag, List<Shape> results)
    {
        // 纯逻辑树遍历（不依赖视觉树连接/布局状态，跨线程安全）
        if (root is Canvas canvas)
        {
            if (root is Shape shape && string.Equals(shape.Tag as string, tag, StringComparison.Ordinal))
            {
                results.Add(shape);
            }

            foreach (object? child in canvas.Children)
            {
                if (child is DependencyObject d)
                {
                    CollectByTag(d, tag, results);
                }
            }

            return;
        }

        if (root is Shape s && string.Equals(s.Tag as string, tag, StringComparison.Ordinal))
        {
            results.Add(s);
        }
    }
}
