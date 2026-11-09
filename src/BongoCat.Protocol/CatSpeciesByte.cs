namespace BongoCat.Protocol;

/// <summary>
/// 物种枚举字节（GDD-09 §4：CAT_STATE payload 尾部追加 1 字节 species；Welcome 名册条目同表）。
/// 枚举表写进 Protocol（两端共用常量，杜绝序列化漂移）；语义 id（"cat"/"dino"/"agua"）
/// 由 Core 的 SpeciesCatalog 按 <see cref="ByteId"/> 对齐映射，Protocol 不依赖 Core。
/// </summary>
public static class CatSpeciesByte
{
    /// <summary>猫（基线物种，默认值 = 旧客户端无 species 字节时的回退）。</summary>
    public const byte Cat = 0;

    /// <summary>原创小恐龙（GDD-09 第二物种）。</summary>
    public const byte Dino = 1;

    /// <summary>阿瓜（GDD-09 §7 第三物种，sprite 位图渲染路径）。</summary>
    public const byte Agua = 2;

    /// <summary>小黄（GDD-09 §8，sprite 位图渲染路径）。</summary>
    public const byte Xiaohuang = 3;

    /// <summary>小白（GDD-09 §8，sprite 位图渲染路径）。</summary>
    public const byte Xiaobai = 4;

    /// <summary>最大合法枚举值（5~15 预留）。</summary>
    public const byte Max = Xiaobai;

    /// <summary>枚举是否合法（0~1）。</summary>
    public static bool IsValid(byte value) => value <= Max;

    /// <summary>
    /// 非法值防御收敛：&gt;2 一律回退猫（GDD-09 §4"非法 species 回退 cat 渲染并记日志，
    /// 不断连接"——decode 端不返回 null，绝不因坏字节断开会话）。
    /// </summary>
    public static byte Normalize(byte value) => IsValid(value) ? value : Cat;
}
