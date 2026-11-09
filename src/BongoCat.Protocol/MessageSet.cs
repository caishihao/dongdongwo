namespace BongoCat.Protocol;

using System.Text;

/// <summary>
/// E8 八种消息的具体实现（字段语义见 <see cref="MsgType"/> 各注释）。
/// 编码纪律：字段顺序即线上格式，只增不改（历史兼容不在 V1 范围，ADR-005：
/// protocolVersion 拒绝不匹配客户端，消息不向前兼容）。
/// </summary>
public sealed record HelloMessage(string DisplayName, string RoomCode) : ProtocolMessage(MsgType.Hello)
{
    /// <summary>显示名字节上限（u8 前缀内的服务器校验上限；UI 输入框另做长度限制）。</summary>
    public const int DisplayNameMaxBytes = 48;

    /// <summary>E9：新客户端宠物名发送上限（与 <see cref="PetNameLimits.MaxUtf8Bytes"/> 同源）。</summary>
    public const int PetNameMaxBytes = PetNameLimits.MaxUtf8Bytes;

    /// <summary>E9：宠物名（与线上 DisplayName 同源；Hello 的显示名字段即宠物名）。</summary>
    public string PetName => DisplayName;

    /// <summary>房间码固定 6 字符。</summary>
    public const int RoomCodeLength = 6;

    public override int MeasurePayload() => StringSize(DisplayName) + StringSize(RoomCode);

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        if (!w.TryWriteString(DisplayName) || !w.TryWriteString(RoomCode))
        {
            throw new InvalidOperationException("Hello payload 超出编码缓冲区（检查字段长度上限）");
        }
    }

    /// <summary>解码；字段非法（超长字符串/UTF8 非法）返回 null。</summary>
    public static HelloMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        if (!r.TryReadString(out string? name) || !r.TryReadString(out string? room))
        {
            return null;
        }

        if (name.Length == 0 || name.Length > DisplayNameMaxBytes)
        {
            return null;
        }

        return new HelloMessage(name, room);
    }
}

public sealed record WelcomeMessage(
    ushort YourId,
    string RoomCode,
    IReadOnlyList<RosterEntry> Members) : ProtocolMessage(MsgType.Welcome)
{
    public override int MeasurePayload()
        => 2 /* yourId */ + StringSize(RoomCode) + 1 /* count */
           + Members.Sum(m => 2 + StringSize(m.DisplayName) + 1 /* species（GDD-09）*/);

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        w.WriteU16(YourId);
        if (!w.TryWriteString(RoomCode) || Members.Count > byte.MaxValue)
        {
            throw new InvalidOperationException("Welcome payload 超出编码缓冲区");
        }

        w.WriteU8((byte)Members.Count);
        foreach (RosterEntry member in Members)
        {
            w.WriteU16(member.MemberId);
            if (!w.TryWriteString(member.DisplayName))
            {
                throw new InvalidOperationException("Welcome 成员名超出编码缓冲区");
            }

            w.WriteU8(CatSpeciesByte.Normalize(member.Species));
        }
    }

    public static WelcomeMessage? Decode(ReadOnlySpan<byte> payload)
    {
        // GDD-09 §4 新旧互操作：成员条目从 v2（id+name）扩为 v3（id+name+species，species
        // 交错在每成员条目内）。species 字节与下一条目的 id 首字节无法靠位置区分，
        // 必须"先按一种布局完整解析并精确耗尽 payload"来判别：先试新格式，失败或
        // 剩余字节则回退旧格式（全部成员按猫渲染）。两趟解析，payload 极小无开销。
        int pos = 0;
        if (!TryReadU16(payload, ref pos, out ushort yourId))
        {
            return null;
        }

        if (!TryReadLengthPrefixedString(payload, ref pos, out string? room))
        {
            return null;
        }

        if (pos >= payload.Length)
        {
            return null;
        }

        byte count = payload[pos++];

        // 新格式：每成员 [u16 id][u8 len][name][u8 species]
        var members = new List<RosterEntry>(count);
        int newPos = pos;
        if (TryParseMembers(payload, ref newPos, count, withSpecies: true, members) && newPos == payload.Length)
        {
            return new WelcomeMessage(yourId, room, members);
        }

        // 旧格式（GDD-09 之前）：每成员 [u16 id][u8 len][name]，species 全部回退猫
        int oldPos = pos;
        members.Clear();
        if (TryParseMembers(payload, ref oldPos, count, withSpecies: false, members) && oldPos == payload.Length)
        {
            return new WelcomeMessage(yourId, room, members);
        }

        return null;
    }

    /// <summary>按指定布局顺序解析成员条目（调用方校验是否精确耗尽 payload）。</summary>
    private static bool TryParseMembers(ReadOnlySpan<byte> payload, ref int pos, int count, bool withSpecies, List<RosterEntry> members)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TryReadU16(payload, ref pos, out ushort id) ||
                !TryReadLengthPrefixedString(payload, ref pos, out string? name) ||
                name.Length == 0)
            {
                return false;
            }

            byte species = CatSpeciesByte.Cat;
            if (withSpecies)
            {
                if (pos + 1 > payload.Length)
                {
                    return false;
                }

                species = CatSpeciesByte.Normalize(payload[pos]);
                pos += 1;
            }

            members.Add(new RosterEntry(id, name, species));
        }

        return true;
    }

    private static bool TryReadU16(ReadOnlySpan<byte> payload, ref int pos, out ushort value)
    {
        value = 0;
        if (pos + 2 > payload.Length)
        {
            return false;
        }

        value = (ushort)(payload[pos] | (payload[pos + 1] << 8));
        pos += 2;
        return true;
    }

    /// <summary>读取 u8 长度前缀 UTF8 字符串（非法序列 → false，严格 schema 纪律）。</summary>
    private static bool TryReadLengthPrefixedString(ReadOnlySpan<byte> payload, ref int pos,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = null;
        if (pos + 1 > payload.Length)
        {
            return false;
        }

        byte length = payload[pos++];
        if (pos + length > payload.Length)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.Encoding.GetString(payload.Slice(pos, length));
            pos += length;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed record MemberJoinMessage(ushort MemberId, string DisplayName) : ProtocolMessage(MsgType.MemberJoin)
{
    /// <summary>E9：宠物名（与线上 DisplayName 同源；旧客户端也把它当普通名字展示）。</summary>
    public string PetName => DisplayName;

    public override int MeasurePayload() => 2 + StringSize(DisplayName);

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        w.WriteU16(MemberId);
        if (!w.TryWriteString(DisplayName))
        {
            throw new InvalidOperationException("MemberJoin payload 超出编码缓冲区");
        }
    }

    public static MemberJoinMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        if (!r.TryReadU16(out ushort id) || !r.TryReadString(out string? name) || name.Length == 0)
        {
            return null;
        }

        return new MemberJoinMessage(id, name);
    }
}

public sealed record MemberLeaveMessage(ushort MemberId) : ProtocolMessage(MsgType.MemberLeave)
{
    public override int MeasurePayload() => 2;

    public override void WritePayload(Span<byte> dest) => new SpanWriter(dest).WriteU16(MemberId);

    public static MemberLeaveMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        return r.TryReadU16(out ushort id) ? new MemberLeaveMessage(id) : null;
    }
}

/// <summary>
/// 归一化摆放位置。坐标为发送方"主屏工作区左上角为原点"的比例（0~1）；
/// 接收方按自己工作区物理像素换算，跨分辨率一致（06-multiplayer.md §5 槽位协商节）。
/// 收到越界值由消费方 Clamp，协议层只管忠实传输。
/// </summary>
/// <remarks>
/// GDD-08 §4 风格同步：payload 尾部追加 1 字节 StyleId（CatStyleByte 枚举 0~5），
/// payloadVersion +1（v2）。GDD-09 §4 物种同步：再追加 1 字节 Species（CatSpeciesByte 枚举 0~1），
/// payloadVersion +1（v3）。兼容规则：旧客户端 payload 只有 18 字节（读到坐标即止），
/// 解码端对短 payload 忽略缺失尾字节回退 Clay/Cat；新客户端非法枚举值同样收敛 Clay/Cat
/// （不返回 null——坏字节绝不断开会话）。帧结构与消息类型表完全不变，服务器透明转发。
/// </remarks>
public sealed record CatStateMessage(
    ushort MemberId,
    double NormX,
    double NormY,
    byte StyleId = CatStyleByte.Clay,
    byte SpeciesId = CatSpeciesByte.Cat)
    : ProtocolMessage(MsgType.CatState)
{
    /// <summary>旧版 payload 字节数（无 style/species 尾字节，GDD-08 之前 v1）。</summary>
    public const int LegacyPayloadBytes = 2 + 8 + 8;

    /// <summary>当前 payload 字节数（尾部 +1 style +1 species，payloadVersion 3）。</summary>
    public const int PayloadBytes = LegacyPayloadBytes + 2;

    /// <summary>v2 payload 字节数（只有 style 尾字节，GDD-08）。</summary>
    public const int V2PayloadBytes = LegacyPayloadBytes + 1;

    /// <summary>payload 小版本（GDD-09：species 尾字节为 v3；仅作元数据标识，帧头协议版本不变）。</summary>
    public const int PayloadVersion = 3;

    public override int MeasurePayload() => PayloadBytes;

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        w.WriteU16(MemberId);
        w.WriteF64(NormX);
        w.WriteF64(NormY);
        w.WriteU8(StyleId);
        w.WriteU8(SpeciesId);
    }

    public static CatStateMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        if (!r.TryReadU16(out ushort id) || !r.TryReadF64(out double x) || !r.TryReadF64(out double y))
        {
            return null;
        }

        byte style = CatStyleByte.Clay;
        byte species = CatSpeciesByte.Cat;
        if (payload.Length >= PayloadBytes)
        {
            _ = r.TryReadU8(out style);
            style = CatStyleByte.Normalize(style); // 非法枚举回退 clay，不断连
            _ = r.TryReadU8(out species);
            species = CatSpeciesByte.Normalize(species); // 非法枚举回退 cat，不断连（GDD-09 §4）
        }
        else if (payload.Length >= V2PayloadBytes)
        {
            _ = r.TryReadU8(out style);
            style = CatStyleByte.Normalize(style);
        }

        // payload < 19 = 旧客户端：忽略缺失尾字节，回退 clay + cat（GDD-08 §6 边界 3 / GDD-09 §4）

        return new CatStateMessage(id, x, y, style, species);
    }
}

/// <summary>敲击事件。TimestampMs 为发送方墙钟毫秒（仅供 UI 展示节奏，不做时钟同步——正确性靠 TCP 有序到达）。</summary>
public sealed record TapMessage(ushort MemberId, TapSide Side, long TimestampMs) : ProtocolMessage(MsgType.Tap)
{
    public override int MeasurePayload() => 2 + 1 + 8;

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        w.WriteU16(MemberId);
        w.WriteU8((byte)Side);
        w.WriteI64(TimestampMs);
    }

    public static TapMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        if (!r.TryReadU16(out ushort id) || !r.TryReadU8(out byte side) || !r.TryReadI64(out long ts))
        {
            return null;
        }

        return side > (byte)TapSide.Right ? null : new TapMessage(id, (TapSide)side, ts);
    }
}

public sealed record HeartbeatMessage() : ProtocolMessage(MsgType.Heartbeat)
{
    public override int MeasurePayload() => 0;

    public override void WritePayload(Span<byte> dest)
    {
    }

    public static HeartbeatMessage? Decode(ReadOnlySpan<byte> payload) => payload.IsEmpty
        ? new HeartbeatMessage()
        : null; // 心跳必须空 payload：多余字节即非法（协议纪律：严格 schema）
}

public sealed record ByeMessage(ByeReason Reason) : ProtocolMessage(MsgType.Bye)
{
    public override int MeasurePayload() => 1;

    public override void WritePayload(Span<byte> dest) => new SpanWriter(dest).WriteU8((byte)Reason);

    public static ByeMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        return r.TryReadU8(out byte reason) && reason <= (byte)ByeReason.InvalidField
            ? new ByeMessage((ByeReason)reason)
            : null;
    }
}

/// <summary>
/// 装扮三元组同步（E8 追加：摆放松绑为独立消息而非并入 Hello/MemberJoin 的理由）——
/// ① Hello 发生在服务器分配 memberId 之前，Hello 里带 memberId 是自相矛盾的；
/// ② 装扮在会话中会变（背包换装），独立消息可复用"客户端上行→服务器改写 id→转发"的
///    既有管线，Hello/Welcome/MemberJoin 的编码完全不动（协议只增不改纪律的延伸）；
/// ③ 只传 3 个字符串 id，美术资源全部由接收方本地 items.json 映射，零资产传输。
/// </summary>
public sealed record CosmeticSyncMessage(
    ushort MemberId,
    string HatId,
    string SkinId,
    string BongosId) : ProtocolMessage(MsgType.CosmeticSync)
{
    /// <summary>单 id 字节上限（与目录 id 量级匹配；服务器/接收方对未知 id 做防御回退，不崩溃）。</summary>
    public const int ArtKeyMaxBytes = 64;

    public override int MeasurePayload() => 2 + StringSize(HatId) + StringSize(SkinId) + StringSize(BongosId);

    public override void WritePayload(Span<byte> dest)
    {
        var w = new SpanWriter(dest);
        w.WriteU16(MemberId);
        if (!w.TryWriteString(HatId) || !w.TryWriteString(SkinId) || !w.TryWriteString(BongosId))
        {
            throw new InvalidOperationException("CosmeticSync payload 超出编码缓冲区（检查 id 长度上限）");
        }
    }

    public static CosmeticSyncMessage? Decode(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        return r.TryReadU16(out ushort id)
            && r.TryReadString(out string? hat)
            && r.TryReadString(out string? skin)
            && r.TryReadString(out string? bongos)
            && hat.Length <= ArtKeyMaxBytes
            && skin.Length <= ArtKeyMaxBytes
            && bongos.Length <= ArtKeyMaxBytes
            ? new CosmeticSyncMessage(id, hat, skin, bongos)
            : null;
    }
}
