using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.Core.Save;
using BongoCat.Protocol;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 风格包系统配置单测（GDD-08）：styles.json 解析与 6 风格参数完整性（对照 §2.6 参数总表）、
/// 皮肤转译表映射、非法值 fail-fast 防御、id/byte 双向映射、选择器触发矩阵、托盘风格命令持久化。
/// 测试读同一份嵌入资源（与生产同数据，防回退清单 5）。
/// </summary>
public class StyleConfigTests
{
    private static StyleCatalog Load() => StyleCatalogLoader.LoadFromAssembly(_ => { });

    [Fact]
    public void styles_json_解析出6风格_顺序与id正确()
    {
        StyleCatalog catalog = Load();

        Assert.Equal(6, catalog.Styles.Count);
        Assert.Equal(["clay", "sticker", "flat", "sketch", "chibi", "papercut"],
            catalog.Styles.Select(s => s.Id).ToArray());
        Assert.Equal("黏土", catalog.Styles[0].Name);
    }

    [Fact]
    public void gdd08_参数总表_六风格动画参数逐项对照()
    {
        StyleCatalog catalog = Load();

        var clay = AssertStyle(catalog, "clay");
        Assert.Equal(8, clay.TapDownPx);
        Assert.Equal(120, clay.ReboundMs);
        Assert.Equal("back", clay.Easing);
        Assert.Equal(0.5, clay.Amplitude);
        Assert.Equal(0.94, clay.SquashScaleY);
        Assert.Equal(0, clay.PawRotationDeg);

        var sticker = AssertStyle(catalog, "sticker");
        Assert.Equal(9, sticker.TapDownPx);
        Assert.Equal(130, sticker.ReboundMs);
        Assert.Equal(0.7, sticker.Amplitude);
        Assert.Equal(0.93, sticker.SquashScaleY);
        Assert.Equal(5, sticker.OutlineWidth);
        Assert.Equal("#FFFFFF", sticker.OutlineColor);
        Assert.Equal(0.22, sticker.ShadowOpacity);
        Assert.Equal(1.8, sticker.InnerOutlineWidth);       // 双层描边：白框内侧细深棕线（ai-sticker 参照）
        Assert.Equal("#6B4A32", sticker.InnerOutlineColor);

        var flat = AssertStyle(catalog, "flat");
        Assert.Equal(6, flat.TapDownPx);
        Assert.Equal(110, flat.ReboundMs);
        Assert.Equal("quadratic", flat.Easing);
        Assert.Equal(1.0, flat.SquashScaleY); // 无挤压
        Assert.Equal(0, flat.OutlineWidth);   // 无描边
        Assert.Equal(0, flat.ShadowOpacity);  // 无投影

        var sketch = AssertStyle(catalog, "sketch");
        Assert.Equal(7, sketch.TapDownPx);
        Assert.Equal(130, sketch.ReboundMs);
        Assert.Equal(0.4, sketch.Amplitude);
        Assert.Equal(2, sketch.PawRotationDeg);        // ±2° 手绘颤感
        Assert.Equal(2.2, sketch.OutlineWidth);
        Assert.Equal("#4A403A", sketch.OutlineColor);  // 墨色恒定（V1）
        Assert.Equal(0.12, sketch.SkinFillOpacity);    // 12% 淡填充
        Assert.Equal("#FDF8EF", sketch.PaperBaseColor); // 纸底色

        var chibi = AssertStyle(catalog, "chibi");
        Assert.Equal(11, chibi.TapDownPx);             // 五风格最大
        Assert.Equal(150, chibi.ReboundMs);
        Assert.Equal("elastic", chibi.Easing);
        Assert.Equal(1, chibi.Oscillations);
        Assert.Equal(3, chibi.Springiness);
        Assert.Equal(0.90, chibi.SquashScaleY);
        Assert.Equal(4, chibi.PawRotationDeg);
        Assert.Equal(3, chibi.BongoSinkPx);            // 鼓沉 3px
        Assert.Equal(9, chibi.OutlineWidth);           // 厚描边
        Assert.Equal("#2B2B2B", chibi.OutlineColor);
        Assert.True(chibi.HighlightDots);              // 两点高光
        Assert.True(chibi.HatAnchorOffsetY < 0);       // hat 锚点上移（画布内，见烟测文档）

        var papercut = AssertStyle(catalog, "papercut");
        Assert.Equal(8, papercut.TapDownPx);
        Assert.Equal(140, papercut.ReboundMs);
        Assert.Equal(0.5, papercut.Amplitude);
        Assert.Equal(2, papercut.LayerBouncePx);       // hat/face 反弹 2px
        Assert.Equal(80, papercut.LayerBounceMs);
        Assert.Equal(0.30, papercut.ShadowOpacity);    // 层错位阴影
        Assert.Equal("paper", papercut.SpotMode);
        Assert.Equal(7, papercut.Layers);              // 奶牛 +1 纹理层
    }

    [Fact]
    public void 全部风格_层预算不超8_字节枚举与协议对齐()
    {
        StyleCatalog catalog = Load();

        foreach (StylePack style in catalog.Styles)
        {
            Assert.InRange(style.Layers, 1, 8); // GDD-08 图层预算铁律
            Assert.InRange(style.ByteId, 0, CatStyleByte.Max);
        }

        // byte 0..5 与 Protocol.CatStyleByte 一一对应（多人枚举常量表对齐）
        Assert.Equal(CatStyleByte.Clay, AssertStyle(catalog, "clay").ByteId);
        Assert.Equal(CatStyleByte.Sticker, AssertStyle(catalog, "sticker").ByteId);
        Assert.Equal(CatStyleByte.Flat, AssertStyle(catalog, "flat").ByteId);
        Assert.Equal(CatStyleByte.Sketch, AssertStyle(catalog, "sketch").ByteId);
        Assert.Equal(CatStyleByte.Chibi, AssertStyle(catalog, "chibi").ByteId);
        Assert.Equal(CatStyleByte.Papercut, AssertStyle(catalog, "papercut").ByteId);
    }

    [Fact]
    public void 皮肤转译表_按GDD08_1_1映射_内容层色板恒定()
    {
        StyleCatalog catalog = Load();

        // 贴纸/扁平/Q版/剪纸：色块实填充（GDD-08 §1.1）
        Assert.Equal(1.0, AssertStyle(catalog, "sticker").SkinFillOpacity);
        Assert.Equal(1.0, AssertStyle(catalog, "flat").SkinFillOpacity);
        Assert.Equal(1.0, AssertStyle(catalog, "chibi").SkinFillOpacity);
        Assert.Equal(1.0, AssertStyle(catalog, "papercut").SkinFillOpacity);

        // 线稿：奶油=纸底不填充、橘/灰=12% 淡填充（风格级统一 12%，纸底承担"本色"）
        var sketch = AssertStyle(catalog, "sketch");
        Assert.Equal(0.12, sketch.SkinFillOpacity);
        Assert.Equal("#FDF8EF", sketch.PaperBaseColor);

        // 奶牛斑转译：solid（贴纸/扁平/Q版）/ outline（线稿）/ paper（剪纸）
        Assert.Equal("solid", AssertStyle(catalog, "clay").SpotMode);
        Assert.Equal("solid", AssertStyle(catalog, "sticker").SpotMode);
        Assert.Equal("solid", AssertStyle(catalog, "flat").SpotMode);
        Assert.Equal("outline", sketch.SpotMode);
        Assert.Equal("paper", AssertStyle(catalog, "papercut").SpotMode);

        // 扁平默认鼓 = #5B8DEF 单色（GDD-08 §2.2），其余风格沿用内容层默认鼓
        Assert.Equal("#5B8DEF", AssertStyle(catalog, "flat").BongosDefaultBody);
        Assert.Equal(string.Empty, AssertStyle(catalog, "clay").BongosDefaultBody);

        // 内描边仅贴纸风启用（其余风格缺省 0 = 不生效）
        Assert.Equal(0, AssertStyle(catalog, "clay").InnerOutlineWidth);
        Assert.Equal(0, AssertStyle(catalog, "chibi").InnerOutlineWidth);
    }

    [Fact]
    public void 双向映射_TryGet与TryGetByByte_未知值防御回退()
    {
        StyleCatalog catalog = Load();

        Assert.True(catalog.TryGet("chibi", out StylePack byId));
        Assert.Equal("chibi", byId.Id);

        Assert.True(catalog.TryGetByByte(CatStyleByte.Papercut, out StylePack byByte));
        Assert.Equal("papercut", byByte.Id);

        // 未知 id / 字节 → false（调用方回退 clay + 日志，GDD-08 §6 边界 1）
        Assert.False(catalog.TryGet("not_a_style", out _));
        Assert.False(catalog.TryGet(null, out _));
        Assert.False(catalog.TryGetByByte(200, out _));

        // ToByte：未知 id 回退 clay 字节 0（发送侧防御）
        Assert.Equal(CatStyleByte.Clay, catalog.ToByte("not_a_style"));
        Assert.Equal(CatStyleByte.Clay, catalog.ToByte(string.Empty));
        Assert.Equal(CatStyleByte.Sticker, catalog.ToByte("sticker"));
    }

    [Fact]
    public void ParseJson_非法值fail_fast_缺失字段与越界与未知缓动()
    {
        const string baseStyle = """
            {"styles":[{
              "id":"clay","name":"黏土","byteId":0,"layers":6,
              "outlineWidth":8,"outlineColor":"#FFFFFF",
              "shadowOpacity":0,"shadowOffsetX":0,"shadowOffsetY":0,"shadowBlur":0,
              "tapDownPx":8,"reboundMs":120,"easing":"back","squashScaleY":0.94,
              "pawRotationDeg":0,"bongoSinkPx":0,"layerBouncePx":0,"layerBounceMs":0,
              "skinFillOpacity":1.0,"spotMode":"solid","hatAnchorOffsetY":0
            }]}
            """;

        // 缺失必填字段
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"tapDownPx\":8,", string.Empty, StringComparison.Ordinal)));

        // 未知缓动类型
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"easing\":\"back\"", "\"easing\":\"wobbly\"", StringComparison.Ordinal)));

        // 图层超预算（>8）
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"layers\":6", "\"layers\":9", StringComparison.Ordinal)));

        // 非法奶牛斑模式
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"spotMode\":\"solid\"", "\"spotMode\":\"neon\"", StringComparison.Ordinal)));

        // 皮肤透明度越界
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"skinFillOpacity\":1.0", "\"skinFillOpacity\":1.5", StringComparison.Ordinal)));

        // 首风格不是 clay
        Assert.Throws<InvalidOperationException>(
            () => StyleCatalog.ParseJson(baseStyle.Replace("\"id\":\"clay\"", "\"id\":\"sticker\"", StringComparison.Ordinal)));

        // 合法基准可解析
        var catalog = StyleCatalog.ParseJson(baseStyle);
        Assert.Single(catalog.Styles);
    }

    // ── 首启选择器触发矩阵（GDD-08 §3.1：两标记独立存储）────────────────

    [Theory]
    [InlineData(false, false, true)]  // 首启（教程未做且没选过风格）：弹选择器
    [InlineData(false, true, false)]  // 选过风格没做完教程：不重复弹（独立标记防重复）
    [InlineData(true, false, false)]  // 教程已完成的老玩家：永不打扰（触发条件按 GDD-08 §3.1 字面口径）
    [InlineData(true, true, false)]   // 都完成：永不打扰
    public void 选择器触发矩阵_TutorialCompleted乘StyleChosen(bool tutorialDone, bool styleChosen, bool expected)
        => Assert.Equal(expected, StylePickerPolicy.ShouldShow(tutorialDone, styleChosen));

    // ── 托盘"形象"命令持久化（GDD-08 §3.3）──────────────────────────────

    [Fact]
    public void SetCatStyle_写入存档_置StyleChosen_广播事件并落盘()
    {
        var save = new FakeSaveService();
        var controller = new TrayCommandController(save, new FakeAudioService());
        string? changed = null;
        controller.StyleChanged += id => changed = id;

        controller.SetCatStyle("sketch");

        Assert.Equal("sketch", save.Current.CatStyleId);
        Assert.True(save.Current.StyleChosen);
        Assert.Equal("sketch", changed);
        Assert.Equal(1, save.SaveCount);
    }

    [Fact]
    public void SetCatStyle_空id_抛参数异常不写存档()
    {
        var save = new FakeSaveService();
        var controller = new TrayCommandController(save, new FakeAudioService());

        Assert.Throws<ArgumentNullException>(() => controller.SetCatStyle(null!));
        Assert.Throws<ArgumentException>(() => controller.SetCatStyle(" "));
        Assert.Equal(string.Empty, save.Current.CatStyleId);
    }

    private static StylePack AssertStyle(StyleCatalog catalog, string id)
    {
        Assert.True(catalog.TryGet(id, out StylePack style), $"缺少风格 {id}");
        return style;
    }
}
