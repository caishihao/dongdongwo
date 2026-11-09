using System.Globalization;
using System.Text;
using BongoCat.Protocol;

namespace BongoCat.Core.Save;

/// <summary>
/// 宠物名字清洗规则（存档 / 首登输入 / 托盘重命名 / 联机名册共用单一实现）。
/// 规则：trim → 去控制字符与孤立代理项 → 按字素簇（grapheme）取前 12 个"字" →
/// 再按 UTF-8 ≤ 24 字节收敛（CJK 3B/字，最多 8 个；emoji 整簇计入，绝不拆代理对/组合符）。
/// 空/全脏输入回退物种默认名（猫猫 / 小恐龙 / 阿瓜）。
/// </summary>
public static class PetNameRules
{
    /// <summary>显示层"字数"上限（CJK 按 1 字，表情/组合字符按 1 个字素簇）。</summary>
    public const int MaxTextElements = 12;

    /// <summary>线上 UTF-8 字节上限（与 <see cref="PetNameLimits.MaxUtf8Bytes"/> 同源）。</summary>
    public const int MaxUtf8Bytes = PetNameLimits.MaxUtf8Bytes;

    public const string DefaultCatName = "猫猫";
    public const string DefaultDinoName = "小恐龙";
    public const string DefaultAguaName = "阿瓜";
    public const string DefaultXiaohuangName = "小黄";
    public const string DefaultXiaobaiName = "小白";

    /// <summary>按存档物种 id 取默认名（未知/空 → 猫；旧档迁移与 UI 回退共用）。</summary>
    public static string DefaultNameForSpecies(string? speciesId) => speciesId switch
    {
        "dino" => DefaultDinoName,
        "agua" => DefaultAguaName,
        "xiaohuang" => DefaultXiaohuangName,
        "xiaobai" => DefaultXiaobaiName,
        _ => DefaultCatName,
    };

    /// <summary>按协议 species 字节取默认名（0=猫 1=恐龙 2=阿瓜；未知 → 猫）。</summary>
    public static string DefaultNameForSpecies(byte speciesByte) => speciesByte switch
    {
        CatSpeciesByte.Dino => DefaultDinoName,
        CatSpeciesByte.Agua => DefaultAguaName,
        _ => DefaultCatName,
    };

    /// <summary>
    /// 清洗一个用户输入的名字。raw 为空/仅空白/仅控制符 → fallback；fallback 也无效 →
    /// 最终回退 <see cref="DefaultCatName"/>（保证返回值永远可安全上线）。
    /// </summary>
    public static string Sanitize(string? raw, string? fallback = null)
    {
        string cleaned = RemoveControlAndLoneSurrogates(StripWhitespaceEdges(raw));
        if (cleaned.Length == 0)
        {
            cleaned = RemoveControlAndLoneSurrogates(StripWhitespaceEdges(fallback));
        }

        if (cleaned.Length == 0)
        {
            return DefaultCatName;
        }

        var builder = new StringBuilder(cleaned.Length);
        int textElements = 0;
        int utf8Bytes = 0;
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(cleaned);
        while (enumerator.MoveNext())
        {
            if (textElements >= MaxTextElements)
            {
                break;
            }

            string element = (string)enumerator.Current!;
            int elementBytes = Encoding.UTF8.GetByteCount(element);
            if (utf8Bytes + elementBytes > MaxUtf8Bytes)
            {
                break;
            }

            builder.Append(element);
            textElements++;
            utf8Bytes += elementBytes;
        }

        return builder.Length > 0 ? builder.ToString() : DefaultCatName;
    }

    /// <summary>去除首尾空白（null 视作空串）。</summary>
    private static string StripWhitespaceEdges(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>去掉 C0/C1 控制符与孤立 UTF-16 代理项（保留成对代理 = 正常 emoji）。</summary>
    private static string RemoveControlAndLoneSurrogates(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(c);
                    builder.Append(value[i + 1]);
                    i++;
                }

                continue; // 孤立高代理：丢弃
            }

            if (char.IsLowSurrogate(c) || char.IsControl(c))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
