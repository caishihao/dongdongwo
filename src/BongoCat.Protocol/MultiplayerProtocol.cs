namespace BongoCat.Protocol;

/// <summary>帧解析结果（流式读取端每轮把已缓冲字节交给 <see cref="MultiplayerProtocol.ParseFrame"/>）。</summary>
public enum FrameParseStatus
{
    /// <summary>缓冲区数据不足一帧，等待更多字节。</summary>
    NeedMoreData,

    /// <summary>解析出一帧。</summary>
    Ok,

    /// <summary>非法帧（魔数/版本/payloadLen 超限/未知消息类型），调用方应断开连接。</summary>
    Invalid,
}

/// <summary>一帧的解析产物。</summary>
public readonly struct FrameParseResult
{
    public FrameParseResult(FrameParseStatus status, ProtocolMessage? message, int frameLength)
    {
        Status = status;
        Message = message;
        FrameLength = frameLength;
    }

    public FrameParseStatus Status { get; }

    /// <summary>Status == Ok 时的解码消息（恒非 null）。</summary>
    public ProtocolMessage? Message { get; }

    /// <summary>Status != NeedMoreData 时的整帧字节数（供消费方从流缓冲移除）。</summary>
    public int FrameLength { get; }
}

/// <summary>
/// E8 云中继协议编解码入口（ADR-005 决定 C 的帧格式落地）：
/// <code>
/// [u16 magic][u8 version][u8 msgType][u32 payloadLen(little-endian)][payload]
/// </code>
/// 帧上限 256KB（超限即非法帧断开，防恶意包）；Socket 层设 NoDelay 关 Nagle（正确性优先）。
/// 客户端与 RelayServer 两端共用本项目编解码，杜绝序列化漂移（architecture.md §1）。
/// </summary>
public static class MultiplayerProtocol
{
    /// <summary>帧魔数 "BC"（BongoCat 缩写）；错包/串流注入的第一道闸。</summary>
    public const ushort Magic = 0x4243;

    /// <summary>协议版本（ADR-005：不匹配即拒绝，不做向前兼容）。</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>帧头大小 = 2 magic + 1 version + 1 type + 4 payloadLen。</summary>
    public const int HeaderSize = 8;

    /// <summary>payload 上限（ADR-005：256KB，超限断开）。</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>整帧上限 = 头 + payload（校验在读取 payloadLen 之后、分配缓冲之前）。</summary>
    public const int MaxFrameBytes = HeaderSize + MaxPayloadBytes;

    /// <summary>编码一条完整帧（header + payload，单次精确分配）。</summary>
    public static byte[] Encode(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int payloadSize = message.MeasurePayload();
        if (payloadSize > MaxPayloadBytes)
        {
            throw new InvalidOperationException($"payload {payloadSize}B 超过上限 {MaxPayloadBytes}B");
        }

        var frame = new byte[HeaderSize + payloadSize];
        frame[0] = (byte)(Magic & 0xFF);
        frame[1] = (byte)(Magic >> 8);
        frame[2] = ProtocolVersion;
        frame[3] = (byte)message.Type;

        var lenWriter = new SpanWriter(frame.AsSpan(4, 4));
        lenWriter.WriteU32((uint)payloadSize);

        message.WritePayload(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// 从流缓冲解析一帧。缓冲数据不足返回 NeedMoreData（不消费）；
    /// 魔数/版本/长度/消息类型非法返回 Invalid（调用方断开该连接，ADR-005 安全边界）。
    /// </summary>
    public static FrameParseResult ParseFrame(ReadOnlySpan<byte> available)
    {
        if (available.Length < HeaderSize)
        {
            return new FrameParseResult(FrameParseStatus.NeedMoreData, null, 0);
        }

        ushort magic = (ushort)(available[0] | (available[1] << 8));
        if (magic != Magic)
        {
            return new FrameParseResult(FrameParseStatus.Invalid, null, 0);
        }

        byte version = available[2];
        if (version != ProtocolVersion)
        {
            return new FrameParseResult(FrameParseStatus.Invalid, null, 0);
        }

        var lenReader = new SpanReader(available.Slice(4, 4));
        if (!lenReader.TryReadU32(out uint payloadLen) || payloadLen > MaxPayloadBytes)
        {
            return new FrameParseResult(FrameParseStatus.Invalid, null, 0);
        }

        int frameLength = HeaderSize + (int)payloadLen;
        if (available.Length < frameLength)
        {
            return new FrameParseResult(FrameParseStatus.NeedMoreData, null, 0);
        }

        byte typeValue = available[3];
        ReadOnlySpan<byte> payload = available.Slice(HeaderSize, (int)payloadLen);

        ProtocolMessage? message = typeValue switch
        {
            (byte)MsgType.Hello => HelloMessage.Decode(payload),
            (byte)MsgType.Welcome => WelcomeMessage.Decode(payload),
            (byte)MsgType.MemberJoin => MemberJoinMessage.Decode(payload),
            (byte)MsgType.MemberLeave => MemberLeaveMessage.Decode(payload),
            (byte)MsgType.CatState => CatStateMessage.Decode(payload),
            (byte)MsgType.Tap => TapMessage.Decode(payload),
            (byte)MsgType.Heartbeat => HeartbeatMessage.Decode(payload),
            (byte)MsgType.Bye => ByeMessage.Decode(payload),
            (byte)MsgType.CosmeticSync => CosmeticSyncMessage.Decode(payload),
            _ => null, // 0x10~0x1F 扩展段未实现：未知类型同样按非法帧断开
        };

        return message is null
            ? new FrameParseResult(FrameParseStatus.Invalid, null, frameLength)
            : new FrameParseResult(FrameParseStatus.Ok, message, frameLength);
    }
}
