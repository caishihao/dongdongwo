namespace BongoCat.Protocol;

/// <summary>
/// E8 云中继协议消息集（语义基础：design/systems/06-multiplayer.md §6.3，云化裁剪版）。
/// 保留：加入/离开/成员列表/摆放位置同步/敲击事件/心跳/房间码；
/// 砍掉：礼物、表情互发、Battle Royale（0x10~0x1F 为扩展保留段，未来玩法从 0x10 起分配）。
/// </summary>
public enum MsgType : byte
{
    /// <summary>C→S：加入握手。RoomCode 空 = 创建新房间（服务器生成 6 位房间码）。</summary>
    Hello = 0x01,

    /// <summary>S→C：加入成功。服务器分配成员 id + 房间码 + 全量名册快照。</summary>
    Welcome = 0x02,

    /// <summary>S→All：成员上线广播（成员 id + 显示名）。</summary>
    MemberJoin = 0x03,

    /// <summary>S→All：成员下线广播（含心跳超时被踢）。</summary>
    MemberLeave = 0x04,

    /// <summary>C→S→All：归一化摆放位置（0~1，乘接收方工作区尺寸还原跨分辨率位置）。</summary>
    CatState = 0x05,

    /// <summary>C→S→All：敲击事件（侧别 + 毫秒时间戳；事件驱动可丢帧，不重传）。</summary>
    Tap = 0x06,

    /// <summary>双向：存活心跳（客户端 20s 间隔；服务器 60s 无任何帧即踢出）。</summary>
    Heartbeat = 0x07,

    /// <summary>双向：优雅断开（服务器拒绝加入/关闭时下发原因码）。</summary>
    Bye = 0x08,

    /// <summary>
    /// C→S→All：装扮三元组同步（hat/skin/bongos 的 artKey，空串=该槽默认/未装备）。
    /// 加入房间成功后立即发一次；换装后由客户端 500ms 防抖合并再发（E8 追加，用户拍板）。
    /// </summary>
    CosmeticSync = 0x09,
}

/// <summary>Bye 原因码（服务器权威语义；客户端收到后向用户展示对应文案）。</summary>
public enum ByeReason : byte
{
    /// <summary>正常退出（客户端主动离开房间）。</summary>
    Normal = 0,

    /// <summary>房间码不存在（服务器上没有这个房间）。</summary>
    RoomNotFound = 1,

    /// <summary>房间已满（成员数达上限）。</summary>
    RoomFull = 2,

    /// <summary>并发房间数达服务器上限，拒绝创建新房间。</summary>
    ServerBusy = 3,

    /// <summary>服务器正在关闭/维护。</summary>
    ServerShutdown = 4,

    /// <summary>显示名/房间码等字段非法（长度/字符集校验失败）。</summary>
    InvalidField = 5,
}

/// <summary>敲击侧别（与 Core.PawSide 数值对齐：0=左 1=右；Protocol 不依赖 Core 保持两端共用纯净）。</summary>
public enum TapSide : byte
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// 名册条目（Welcome 快照 / MemberJoin 广播共用）。
/// GDD-09 §4：追加 species 字节（CatSpeciesByte 枚举，默认猫）——旧服务器下行 Welcome 无该
/// 字节时解码端按猫回退（见 WelcomeMessage.Decode 的长短 payload 分支）。
/// E9 取名：<see cref="PetName"/> 即宠物名（≤24B 新发），线上复用既有 UTF-8 长度前缀字段
/// （字段名保留 DisplayName 语义别名），不新增线字段、不 bump 帧版本；旧服务器原样转发，
/// 旧客户端当普通显示名展示——这是"零服务器改动 + 双向兼容"的最小侵入方案。
/// </summary>
public sealed record RosterEntry(ushort MemberId, string PetName, byte Species = CatSpeciesByte.Cat)
{
    /// <summary>线上字段名（历史兼容别名，与 <see cref="PetName"/> 同源）。</summary>
    public string DisplayName => PetName;
}

/// <summary>协议消息基类：子类实现 payload 度量与写入（ADR-005：手写编码，不用反射序列化）。</summary>
public abstract record ProtocolMessage(MsgType Type)
{
    /// <summary>精确计算 payload 字节数（Encode 据此一次性分配，不做二次扩容）。</summary>
    public abstract int MeasurePayload();

    /// <summary>把 payload 写入 dest。dest 必须不小于 <see cref="MeasurePayload"/>。</summary>
    public abstract void WritePayload(Span<byte> dest);

    /// <summary>字符串字节数辅助（u8 前缀 1 字节 + UTF8 内容；与 SpanWriter 同一编码器，计数必然一致）。</summary>
    protected static int StringSize(string value) => StrictUtf8.Encoding.GetByteCount(value) + 1;
}
