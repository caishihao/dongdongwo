using BongoCat.App.Styles;
using BongoCat.Core.Config;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 物种目录测试（GDD-09）：真实 species.json 解析（与生产同源）+ 非法配置防御（fail-fast）。
/// </summary>
public class SpeciesConfigTests
{
    [Fact]
    public void 真实speciesjson_解析出三物种_cat在前且dino字节为1_agua字节为2()
    {
        SpeciesCatalog catalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });

        Assert.Equal(3, catalog.Species.Count);
        Assert.Equal("cat", catalog.Species[0].Id);      // 首物种恒为 cat（默认回退语义）
        Assert.Equal("dino", catalog.Species[1].Id);
        Assert.Equal("agua", catalog.Species[2].Id);
        Assert.Equal(0, catalog.Species[0].ByteId);
        Assert.Equal(1, catalog.Species[1].ByteId);
        Assert.Equal(2, catalog.Species[2].ByteId);
        Assert.Equal(0, catalog.Species[0].HatAnchorOffsetY);
        Assert.Equal(-14, catalog.Species[1].HatAnchorOffsetY); // GDD-09 §2.3：恐龙 hat 锚点增量
        Assert.Equal(0, catalog.Species[2].HatAnchorOffsetY);   // sprite 物种帽锚由 anchors.json 提供
        Assert.Equal("dino_default", catalog.Species[1].DefaultSkinId); // GDD-09 §2.4
        Assert.Equal("agua_default", catalog.Species[2].DefaultSkinId); // GDD-09 §7：阿瓜专属默认皮肤
        Assert.Equal(string.Empty, catalog.Species[0].DefaultSkinId);
        Assert.Equal("sprite", catalog.Species[2].Kind);  // GDD-09 §7：位图渲染路径
        Assert.Equal("vector", catalog.Species[0].Kind);
    }

    [Fact]
    public void TryGet_合法id命中_未知id返回false()
    {
        SpeciesCatalog catalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });

        Assert.True(catalog.TryGet("dino", out SpeciesPack dino));
        Assert.Equal("小恐龙", dino.Name);
        Assert.False(catalog.TryGet("dragon", out _));
        Assert.False(catalog.TryGet("", out _));
        Assert.False(catalog.TryGet(null, out _));
    }

    [Fact]
    public void ToByte_合法id映射字节_未知id回退cat字节0()
    {
        SpeciesCatalog catalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });

        Assert.Equal(0, catalog.ToByte("cat"));
        Assert.Equal(1, catalog.ToByte("dino"));
        Assert.Equal(0, catalog.ToByte("not_a_species"));
        Assert.Equal(0, catalog.ToByte(null));
    }

    [Fact]
    public void TryGetByByte_合法字节命中_未知字节返回false()
    {
        SpeciesCatalog catalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });

        Assert.True(catalog.TryGetByByte(1, out SpeciesPack dino));
        Assert.Equal("dino", dino.Id);
        Assert.True(catalog.TryGetByByte(2, out SpeciesPack agua)); // GDD-09 §7：阿瓜
        Assert.Equal("agua", agua.Id);
        Assert.False(catalog.TryGetByByte(3, out _)); // 3~15 预留段未实现
        Assert.False(catalog.TryGetByByte(255, out _));
    }

    // ── 非法配置防御（fail-fast，模式同 StyleConfigTests）────────────────

    [Fact]
    public void 缺少species数组_抛异常()
    {
        Assert.Throws<InvalidOperationException>(
            () => SpeciesCatalog.ParseJson("""{ "version": 1 }"""));
    }

    [Fact]
    public void species数组为空_抛异常()
    {
        Assert.Throws<InvalidOperationException>(
            () => SpeciesCatalog.ParseJson("""{ "species": [] }"""));
    }

    [Fact]
    public void 首物种不是cat_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """{ "species": [ { "id": "dino", "name": "龙", "byteId": 1, "hatAnchorOffsetY": -14 } ] }"""));
    }

    [Fact]
    public void 物种id重复_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """
            { "species": [
              { "id": "cat", "name": "猫", "byteId": 0, "hatAnchorOffsetY": 0 },
              { "id": "cat", "name": "猫2", "byteId": 1, "hatAnchorOffsetY": 0 }
            ] }
            """));
    }

    [Fact]
    public void 物种字节冲突_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """
            { "species": [
              { "id": "cat", "name": "猫", "byteId": 0, "hatAnchorOffsetY": 0 },
              { "id": "dino", "name": "龙", "byteId": 0, "hatAnchorOffsetY": -14 }
            ] }
            """));
    }

    [Fact]
    public void 字节超出预留段_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """
            { "species": [
              { "id": "cat", "name": "猫", "byteId": 0, "hatAnchorOffsetY": 0 },
              { "id": "dino", "name": "龙", "byteId": 16, "hatAnchorOffsetY": -14 }
            ] }
            """));
    }

    [Fact]
    public void 缺少必填字段_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """{ "species": [ { "id": "cat", "name": "猫" } ] }"""));
    }

    [Fact]
    public void hat锚点越界_抛异常()
    {
        Assert.Throws<InvalidOperationException>(() => SpeciesCatalog.ParseJson(
            """
            { "species": [
              { "id": "cat", "name": "猫", "byteId": 0, "hatAnchorOffsetY": 0 },
              { "id": "dino", "name": "龙", "byteId": 1, "hatAnchorOffsetY": -100 }
            ] }
            """));
    }
}
