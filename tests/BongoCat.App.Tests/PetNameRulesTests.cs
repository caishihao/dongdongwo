using System.Text;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// E9 宠物名字清洗规则：trim / 去控制符 / 12 字素 / UTF-8 ≤24 字节 / 空回退默认名。
/// CJK 按 1 个字素；emoji 整簇计入、绝不拆代理对或组合符（防乱码）。
/// </summary>
public class PetNameRulesTests
{
    [Theory]
    [InlineData(null, "猫猫")]
    [InlineData("", "猫猫")]
    [InlineData("   ", "猫猫")]
    [InlineData("\u0000\u0007\r\n\t", "猫猫")]
    public void 空或全控制符_回退默认名(string? raw, string expected)
    {
        Assert.Equal(expected, PetNameRules.Sanitize(raw, "猫猫"));
    }

    [Fact]
    public void 按物种取默认名_未知物种回退猫()
    {
        Assert.Equal("猫猫", PetNameRules.DefaultNameForSpecies("cat"));
        Assert.Equal("小恐龙", PetNameRules.DefaultNameForSpecies("dino"));
        Assert.Equal("阿瓜", PetNameRules.DefaultNameForSpecies("agua"));
        Assert.Equal("猫猫", PetNameRules.DefaultNameForSpecies("unknown"));
        Assert.Equal("猫猫", PetNameRules.DefaultNameForSpecies((string?)null));
        Assert.Equal("小恐龙", PetNameRules.DefaultNameForSpecies(BongoCat.Protocol.CatSpeciesByte.Dino));
    }

    [Fact]
    public void 前后空白裁剪_内部控制符移除()
    {
        Assert.Equal("布丁", PetNameRules.Sanitize("  布丁  "));
        Assert.Equal("布丁", PetNameRules.Sanitize("布\u0000丁\u0007"));
        Assert.Equal("布 丁", PetNameRules.Sanitize(" 布 丁 ")); // 普通空格保留
    }

    [Fact]
    public void 十二个ASCII字_原样保留()
    {
        Assert.Equal("abcdefghijkl", PetNameRules.Sanitize("abcdefghijklmnop"));
    }

    [Fact]
    public void 二十四字节上限_CJK最多八个()
    {
        // 12 个 CJK 字素 = 36 字节，超过 24 字节上限 → 收敛为 8 个（24B）
        string raw = "一二三四五六七八九十十一十二";
        string result = PetNameRules.Sanitize(raw);

        Assert.Equal("一二三四五六七八", result);
        Assert.Equal(24, Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void 表情按字素计_不拆代理对()
    {
        // 7 个 🐱（各 4B）= 28B > 24B → 保留 6 个完整表情
        string result = PetNameRules.Sanitize("🐱🐱🐱🐱🐱🐱🐱");

        Assert.Equal("🐱🐱🐱🐱🐱🐱", result);
        Assert.Equal(24, Encoding.UTF8.GetByteCount(result));
        Assert.All(result.EnumerateRunes(), rune => Assert.Equal(0x1F431, rune.Value));
    }

    [Fact]
    public void 组合字素不被截断_保留完整重音()
    {
        // e + 组合重音为一个字素（3B）；12 个 = 36B → 字节上限截到 8 个完整字素
        string raw = string.Concat(Enumerable.Repeat("e\u0301", 12));
        string result = PetNameRules.Sanitize(raw);

        Assert.Equal(string.Concat(Enumerable.Repeat("e\u0301", 8)), result);
        Assert.Equal(24, Encoding.UTF8.GetByteCount(result));
        Assert.Equal('\u0301', result[^1]); // 末位仍是完整字素的一部分，不是被截断的裸组合符
    }

    [Fact]
    public void 孤立代理项被丢弃_不产生非法UTF8()
    {
        string raw = "布丁" + '\uD83D' + "糯米"; // 孤立高代理
        string result = PetNameRules.Sanitize(raw);

        Assert.Equal("布丁糯米", result);
        _ = Encoding.UTF8.GetBytes(result); // 严格编码不抛
    }
}
