namespace BongoCat.Protocol;

/// <summary>
/// 猫风格枚举字节（GDD-08 §4：CAT_STATE payload 尾部追加 1 字节 style）。
/// 枚举表写进 Protocol（两端共用常量，杜绝序列化漂移）；语义 id（"clay" 等）
/// 由 Core 的 StyleCatalog 按 <see cref="ByteId"/> 对齐映射，Protocol 不依赖 Core。
/// </summary>
public static class CatStyleByte
{
    /// <summary>黏土（基线风格，默认值 = 旧客户端无 style 字节时的回退）。</summary>
    public const byte Clay = 0;

    /// <summary>贴纸风。</summary>
    public const byte Sticker = 1;

    /// <summary>极简扁平。</summary>
    public const byte Flat = 2;

    /// <summary>手绘线稿。</summary>
    public const byte Sketch = 3;

    /// <summary>Q版厚描边。</summary>
    public const byte Chibi = 4;

    /// <summary>剪纸分层。</summary>
    public const byte Papercut = 5;

    /// <summary>最大合法枚举值。</summary>
    public const byte Max = Papercut;

    /// <summary>枚举是否合法（0~5）。</summary>
    public static bool IsValid(byte value) => value <= Max;

    /// <summary>
    /// 非法值防御收敛：&gt;5 一律回退黏土（GDD-08 §4"非法 styleId 回退 clay 渲染并记日志，
    /// 不断连接"——decode 端不返回 null，绝不因坏字节断开会话）。
    /// </summary>
    public static byte Normalize(byte value) => IsValid(value) ? value : Clay;
}
