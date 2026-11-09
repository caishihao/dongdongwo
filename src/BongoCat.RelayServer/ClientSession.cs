using System.Threading.Channels;
using BongoCat.Protocol;

namespace BongoCat.RelayServer;

/// <summary>会话状态（未加入/已加入房间）。</summary>
public enum SessionState
{
    AwaitingHello,
    Joined,
}

/// <summary>
/// 单连接会话：收帧（限速/缓冲/保活计时）+ 发帧 Channel 化的独立写任务。
/// 消息语义（加入/转发/广播）全部由 <see cref="RelayServerHost"/> 处理——本类只做
/// 连接层纪律：限速、MemberId 归属校验、缓冲管理。服务器不理解房间消息的 payload 语义。
/// </summary>
public sealed class ClientSession
{
    private readonly RelayLimits _limits;
    private readonly RelayLog _log;

    private readonly Channel<byte[]> _outgoing =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private byte[] _recvBuffer = new byte[64 * 1024]; // 初始 64KB，超长帧按需扩容至上限
    private int _buffered;

    // 滑动 1s 窗口限速（06-multiplayer.md §7：>50/s 断开；取 RelayLimits 配置值）
    private long _rateWindowStartTicks;
    private int _rateCount;

    public ClientSession(RelayLimits limits, RelayLog log, long nowUtcTicks)
    {
        _limits = limits;
        _log = log;
        LastSeenUtcTicks = nowUtcTicks;
        _rateWindowStartTicks = nowUtcTicks;
    }

    public SessionState State { get; private set; } = SessionState.AwaitingHello;

    public Room? Room { get; private set; }

    public ushort MemberId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public long LastSeenUtcTicks { get; private set; }

    /// <summary>会话归属的远端描述（日志用，不参与逻辑）。</summary>
    public string RemoteDescription { get; private set; } = string.Empty;

    public ChannelReader<byte[]> OutgoingReader => _outgoing.Reader;

    /// <summary>host 注入的消息处理器（返回 false = 断开连接）。</summary>
    public Func<ProtocolMessage, bool>? MessageHandler { get; set; }

    /// <summary>
    /// 入帧处理。返回 false = 需要断开（非法帧/限速超限/归属校验失败）。
    /// 任何帧都刷新保活计时（心跳是客户端保底，数据帧同样证明连接活着）。
    /// </summary>
    public bool HandleInboundFrame(FrameParseResult frame, long nowUtcTicks)
    {
        LastSeenUtcTicks = nowUtcTicks;

        if (nowUtcTicks - _rateWindowStartTicks >= TimeSpan.FromSeconds(1).Ticks)
        {
            _rateWindowStartTicks = nowUtcTicks;
            _rateCount = 0;
        }

        if (++_rateCount > _limits.MaxMessagesPerSecond)
        {
            _log.Warn($"限速触发：{RemoteDescription} 一秒内 {_rateCount} 帧（上限 {_limits.MaxMessagesPerSecond}），断开");
            return false;
        }

        ProtocolMessage message = frame.Message!;

        // 归属校验（ADR-005：来包按不可信输入处理）——伪造他人 id 一律断开
        if ((message is CatStateMessage cat && cat.MemberId != MemberId) ||
            (message is TapMessage tap && tap.MemberId != MemberId) ||
            (message is CosmeticSyncMessage cosmetics && cosmetics.MemberId != MemberId))
        {
            return false;
        }

        return MessageHandler?.Invoke(message) ?? false;
    }

    // ── 接收缓冲管理（host 读网络字节进来，这里维护"未解析余量"）────────────

    public Memory<byte> FreeBufferSpace => _recvBuffer.AsMemory(_buffered);

    public ReadOnlySpan<byte> BufferedBytes => _recvBuffer.AsSpan(0, _buffered);

    public void NoteBytesReceived(int count) => _buffered += count;

    public void ConsumeBuffered(int frameLength)
    {
        _recvBuffer.AsSpan(frameLength, _buffered - frameLength).CopyTo(_recvBuffer);
        _buffered -= frameLength;
    }

    /// <summary>缓冲区已满仍解析不出完整帧 → 扩容；已达协议帧上限仍不够 → 非法断开。</summary>
    public bool TryGrowBuffer()
    {
        if (_recvBuffer.Length >= MultiplayerProtocol.MaxFrameBytes)
        {
            _log.Warn($"超长帧：{RemoteDescription} 缓冲已满 {_recvBuffer.Length}B 仍无完整帧，断开");
            return false;
        }

        int newSize = Math.Min(_recvBuffer.Length * 2, MultiplayerProtocol.MaxFrameBytes);
        var grown = new byte[newSize];
        _recvBuffer.AsSpan(0, _buffered).CopyTo(grown);
        _recvBuffer = grown;
        return true;
    }

    // ── 会话身份与发送 ──────────────────────────────────────────────────

    public void SetRemoteDescription(string value) => RemoteDescription = value;

    public void SetRoom(Room room, ushort memberId, string displayName)
    {
        Room = room;
        MemberId = memberId;
        DisplayName = displayName;
        State = SessionState.Joined;
    }

    /// <summary>排队一帧到发送通道（false = 通道已关闭，调用方按断开处理）。</summary>
    public bool TryEnqueue(byte[] frame) => _outgoing.Writer.TryWrite(frame);

    /// <summary>排队一条消息（内部编码；编码失败视为编程错误，记日志不断开排队者）。</summary>
    public bool TryEnqueue(ProtocolMessage message)
    {
        try
        {
            return TryEnqueue(MultiplayerProtocol.Encode(message));
        }
        catch (InvalidOperationException ex)
        {
            _log.Error($"编码失败（type={message.Type}）：{ex.Message}");
            return false;
        }
    }

    public void CloseOutgoing() => _outgoing.Writer.TryComplete();
}
