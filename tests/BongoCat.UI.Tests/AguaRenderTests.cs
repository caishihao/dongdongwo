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
/// 阿瓜 sprite 渲染测试（GDD-09 §7）：
/// ① 6 风格 sprite 模板全部可构建（= 选择器卡片预览渲染不抛异常），六层契约齐全，
///    鼓换绑目标为真实程序化鼓形状（双鼓共用），脸烘焙进 body（v1 无表情层）；
/// ② 缺资产目录（未注入）/ anchors 缺风格 → 回退 vector cat 渲染，绝不抛异常；
/// ③ sprite 换绑哑形状不接色板（Fill=null），ApplyOutfit 全链路不抛；
/// ④ 帽子锚点合成：anchors.hatAnchor 驱动 PartHat 平移（跨渲染路径混排）。
/// WPF 视觉对象在 STA 线程构建。
/// </summary>
public class AguaRenderTests
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
    public void agua模板_全风格构建成功_六层契约与程序化鼓齐全(string styleId)
    {
        (bool ok, string detail) = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleRenderer.SpriteSpecies = SpriteSpeciesCatalog.LoadFromAssembly(_ => { });
            StyleCatParts p = StyleRenderer.ApplyStyle(host, styleId, Styles, "agua");

            // 断言在 STA 线程内执行（DependencyObject 跨线程不可访问）
            if (p.Root is null) return (false, "Root null");
            if (p.BodyFur is null || p.PawLFur is null) return (false, "哑换绑形状缺失");
            if (p.PawLTranslate is null || p.PawLRotate is null) return (false, "爪动画句柄缺失");
            if (p.BodySquash is null) return (false, "BodySquash 缺失");
            if (p.BongoBodyL is null || p.BongoRimR is null) return (false, "程序化鼓层缺失（双鼓共用）");
            if (p.PartFace.Children.Count != 0) return (false, "脸应烘焙进 body（v1 无表情层）");
            if (p.PartHat.Children.Count == 0) return (false, "矢量帽子未预建（跨路径混排）");
            if (p.BodyFur.Fill is not null) return (false, "sprite 哑形状不应接色板");
            if (p.SpeciesAppendages.Count != 0) return (false, "sprite 无附属矢量形状");
            return (true, "");
        });
        StyleRenderer.SpriteSpecies = null;

        Assert.True(ok, detail);
    }

    [Fact]
    public void agua模板_hat锚点由anchors_json驱动_平移到头顶()
    {
        (double dx, double dy) = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleRenderer.SpriteSpecies = SpriteSpeciesCatalog.LoadFromAssembly(_ => { });
            StyleCatParts p = StyleRenderer.ApplyStyle(host, "clay", Styles, "agua");
            var t = Assert.IsType<TranslateTransform>(p.PartHat.RenderTransform);
            return (t.X, t.Y);
        });
        StyleRenderer.SpriteSpecies = null;

        // anchors.json clay 帽锚 x=110（居中）→ dx≈0；y≈9.4 → dy = (9.4+10) - 37 ≈ -17.6
        Assert.InRange(dx, -2, 2);
        Assert.InRange(dy, -25, -10);
    }

    [Fact]
    public void sprite目录未注入_agua回退cat渲染_不抛异常()
    {
        StyleCatParts parts = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleRenderer.SpriteSpecies = null; // 模拟未注入（缺资产防御路径）
            return StyleRenderer.ApplyStyle(host, "clay", Styles, "agua");
        });

        Assert.NotNull(parts.Root);
        Assert.Empty(parts.SpeciesAppendages); // 回退猫 = 无附属形状
    }

    [Fact]
    public void agua应用装扮_全链路不抛异常_鼓色可换绑()
    {
        (bool ok, string detail) = OnSta(() =>
        {
            var host = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight };
            StyleRenderer.SpriteSpecies = SpriteSpeciesCatalog.LoadFromAssembly(_ => { });
            StyleCatParts p = StyleRenderer.ApplyStyle(host, "clay", Styles, "agua");
            Styles.TryGet("clay", out StylePack s);
            StyleRenderer.ApplyOutfit(p, s, "hat_santa", "agua_default", "bongos_wood"); // 三槽全装备
            return (p.BongoBodyL.Fill is not null, ""); // 鼓层真实换绑（木色鼓），断言在 STA 内
        });
        StyleRenderer.SpriteSpecies = null;

        Assert.True(ok, detail);
    }

    [Fact]
    public void 物种目录_选择器页签数据源含三物种_agua在末位()
    {
        // StylePickerWindow 页签按 SpeciesCatalog.Species 顺序构建（数据驱动）
        Assert.Equal(3, Species.Species.Count);
        Assert.Equal("agua", Species.Species[^1].Id);
    }
}
