namespace BongoCat.Protocol;

/// <summary>
/// 宠物名字线上契约（E9 取名）：名册条目复用既有 UTF-8 长度前缀的 DisplayName 字段承载
/// 宠物名，**不新增线字段、不 bump 帧版本**——旧服务器按显示名原样转发，旧客户端只把它
/// 当普通名字展示，天然双向兼容（ADR-005 / 06-multiplayer.md §6.3）。
/// 24 字节是发送侧上限；接收侧仍容忍旧客户端的 48 字节显示名（超长由 Core 展示层清洗）。
/// </summary>
public static class PetNameLimits
{
    /// <summary>新客户端发送的宠物名 UTF-8 字节上限（旧客户端的 48 字节显示名仍可接收）。</summary>
    public const int MaxUtf8Bytes = 24;
}
