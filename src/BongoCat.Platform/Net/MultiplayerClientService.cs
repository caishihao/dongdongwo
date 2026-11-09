using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using BongoCat.Core.Net;
using BongoCat.Protocol;

namespace BongoCat.Platform.Net;

/// <summary>
/// <see cref="IMultiplayerClient"/> 的 TCP 实现（ADR-005 决定 C）。
/// 一个会话 = 连接 → Hello → 收循环 + 发循环（Channel 化，模式同 KeyPollingService 的投递解耦）。
/// 断线重连指数退避在会话循环外层实现；心跳 20s 在发循环内定时注入。
/// </summary>
public sealed class MultiplayerClientService : IMultiplayerClient
{
    /// <summary>心跳间隔（20s；服务器 60s 无帧即踢，3 次容错余量）。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    /// <summary>TCP 连接超时。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>重连退避封顶（指数 1s→2s→4s…30s）。</summary>
    public const int MaxBackoffMs = 30_000;

    private readonly object _gate = new();
    private readonly Channel<byte[]> _outgoing =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
    private readonly Action<string>? _log;

    private CancellationTokenSource? _sessionCts;
    private TcpClient? _client;
    private Task? _sessionTask;
    private volatile bool _stopRequested;
    private MpConnectionState _state = MpConnectionState.Disconnected;

    private string _serverAddress = string.Empty;
    private string _displayName = string.Empty;
    private string _roomCode = string.Empty;
    private int _reconnectAttempt;

    public MultiplayerClientService(Action<string>? log = null) => _log = log;

    public MpConnectionState State => _state;

    public ushort MyMemberId { get; private set; }

    // ── 事件（后台线程触发，消费方自行跳线程）────────────────────────────

    public event Action<MpConnectionState>? StateChanged;
    public event Action<string, IReadOnlyList<RosterEntry>>? Joined;
    public event Action<ushort, string>? MemberJoined;
    public event Action<ushort>? MemberLeft;
    public event Action<ushort, double, double, byte, byte>? RemoteCatState;
    public event Action<ushort, TapSide, long>? RemoteTap;
    public event Action<ushort, string, string, string>? CosmeticSyncReceived;
    public event Action<ByeReason>? JoinRejected;
    public event Action<string>? Disconnected;

    // ── 控制面 ──────────────────────────────────────────────────────────

    public void Start(string serverAddress, string displayName, string roomCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        lock (_gate)
        {
            if (_sessionTask is not null)
            {
                return; // 幂等：已运行（UI 侧按钮态防重复点击）
            }

            _serverAddress = serverAddress.Trim();
            _displayName = displayName.Trim();
            _roomCode = roomCode.Trim().ToUpperInvariant();
            _stopRequested = false;
            _reconnectAttempt = 0;
            _sessionCts = new CancellationTokenSource();
            SetStateLocked(MpConnectionState.Connecting);
            _sessionTask = Task.Run(() => SessionLoopAsync(_sessionCts.Token));
        }

        _log?.Invoke($"[Mp] 启动：{_serverAddress} 房间={(_roomCode.Length == 0 ? "(新建)" : _roomCode)}");
    }

    public void Stop()
    {
        Task? task;
        lock (_gate)
        {
            if (_sessionTask is null)
            {
                return;
            }

            _stopRequested = true;
            task = _sessionTask;
        }

        _sessionCts?.Cancel();
        try
        {
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 会话循环内已记录；关闭流程不再传播
        }

        lock (_gate)
        {
            _sessionTask = null;
            SetStateLocked(MpConnectionState.Disconnected);
        }

        _log?.Invoke("[Mp] 已主动离开房间");
    }

    public bool SendTap(TapSide side)
        => TrySend(new TapMessage(MyMemberId, side, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public bool SendCatState(double normX, double normY, byte styleByte, byte speciesByte)
        => TrySend(new CatStateMessage(
            MyMemberId,
            Math.Clamp(normX, 0.0, 1.0),
            Math.Clamp(normY, 0.0, 1.0),
            CatStyleByte.Normalize(styleByte),
            CatSpeciesByte.Normalize(speciesByte)));

    public bool SendCosmetics(string hatId, string skinId, string bongosId)
        => TrySend(new CosmeticSyncMessage(MyMemberId, hatId, skinId, bongosId));

    private bool TrySend(ProtocolMessage message)
    {
        if (_state != MpConnectionState.Joined)
        {
            return false; // 未在房间：丢弃（调用方节流策略下不缓存旧敲击）
        }

        try
        {
            return _outgoing.Writer.TryWrite(MultiplayerProtocol.Encode(message));
        }
        catch (InvalidOperationException ex)
        {
            _log?.Invoke($"[Mp] 编码失败 type={message.Type}: {ex.Message}");
            return false;
        }
    }

    // ── 会话循环（连接 + 断线重连退避）───────────────────────────────────

    private async Task SessionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            string? failure = await ConnectOnceAsync(ct);
            if (ct.IsCancellationRequested || _stopRequested)
            {
                return;
            }

            if (_joinRejected is not null)
            {
                // 加入被拒：服务器权威答复，重连也无济于事（房间码错/满员/忙）
                ByeReason reason = _joinRejected.Value;
                _joinRejected = null;
                SetState(MpConnectionState.Failed);
                JoinRejected?.Invoke(reason);
                return;
            }

            _reconnectAttempt++;
            int delayMs = ComputeBackoffDelayMs(_reconnectAttempt);
            SetState(MpConnectionState.Reconnecting);
            _log?.Invoke($"[Mp] 断线（{failure ?? "未知"}），{delayMs}ms 后第 {_reconnectAttempt} 次重连");
            Disconnected?.Invoke(failure ?? "连接断开");

            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            SetState(MpConnectionState.Connecting);
        }
    }

    /// <summary>指数退避：attempt 从 1 起 → 1s/2s/4s/8s/16s/30s…封顶 30s（可注入可测）。</summary>
    public static int ComputeBackoffDelayMs(int attempt)
        => (int)Math.Min(1000L * (1L << Math.Clamp(attempt - 1, 0, 30)), MaxBackoffMs);

    /// <summary>单次连接生命周期。返回断开原因描述（null = 无描述）。</summary>
    private async Task<string?> ConnectOnceAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TcpClient? client = null;
        try
        {
            if (!TryParseEndpoint(_serverAddress, out IPAddress[] addresses, out int port))
            {
                return "服务器地址无法解析";
            }

            client = new TcpClient();
            _client = client;

            // 带超时连接：逐个候选地址尝试（域名解析出多 IP 时自动换下一个）
            Exception? lastError = null;
            bool connected = false;
            foreach (IPAddress address in addresses)
            {
                linked.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(address, port, linked.Token);
                    connected = true;
                    break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = new TimeoutException($"连接 {address} 超时 {ConnectTimeout.TotalSeconds:0}s");
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                }
            }

            if (!connected)
            {
                return lastError?.Message ?? "连接失败";
            }

            client.NoDelay = true; // ADR-005：关 Nagle
            _log?.Invoke($"[Mp] TCP 已连接 {_serverAddress}");

            // 握手：Hello → 等 Welcome（收循环内首帧校验）
            _awaitingWelcome = true;
            await client.GetStream().WriteAsync(
                MultiplayerProtocol.Encode(new HelloMessage(_displayName, _roomCode)), ct);

            Task send = SendLoopAsync(client.GetStream(), ct);
            Task recv = ReceiveLoopAsync(client.GetStream(), ct);
            await Task.WhenAny(send, recv);

            return "连接断开";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
        catch (SocketException ex)
        {
            return ex.Message;
        }
        finally
        {
            lock (_gate)
            {
                _client = null;
            }

            client?.Dispose();
            MyMemberId = 0;
            _awaitingWelcome = false;
            DrainingOutgoing();
        }
    }

    /// <summary>清空发送队列中的残留帧（断线重连后旧帧不跨会话重放——敲击/位置都是瞬时语义）。</summary>
    private void DrainingOutgoing()
    {
        while (_outgoing.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>
    /// 解析 "host" 或 "host:port"（IPv4/域名；IPv6 带冒号按裸地址处理不支持端口写法，V1 不覆盖）。
    /// </summary>
    internal static bool TryParseEndpoint(string address, out IPAddress[] addresses, out int port)
    {
        addresses = [];
        port = 9437;

        string host = address;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.Count(c => c == ':') == 1 && int.TryParse(address[(colon + 1)..], out int p) && p is > 0 and < 65536)
        {
            host = address[..colon];
            port = p;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        try
        {
            addresses = Dns.GetHostAddressesAsync(host).GetAwaiter().GetResult();
            return addresses.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // ── 收/发循环 ───────────────────────────────────────────────────────

    private bool _awaitingWelcome;
    private ByeReason? _joinRejected;

    /// <summary>收循环：累积缓冲 → 逐帧解析 → 事件分发。首帧必须是 Welcome。</summary>
    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        int buffered = 0;

        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(buffered), ct);
            if (read == 0)
            {
                return; // 对端关闭
            }

            buffered += read;
            while (true)
            {
                FrameParseResult result = MultiplayerProtocol.ParseFrame(buffer.AsSpan(0, buffered));
                if (result.Status == FrameParseStatus.NeedMoreData)
                {
                    break;
                }

                if (result.Status == FrameParseStatus.Invalid)
                {
                    _log?.Invoke("[Mp] 收到非法帧，断开");
                    return;
                }

                buffered -= result.FrameLength;
                if (result.FrameLength > 0 && buffered > 0)
                {
                    Buffer.BlockCopy(buffer, result.FrameLength, buffer, 0, buffered);
                }

                if (!Dispatch(result.Message!))
                {
                    return;
                }
            }
        }
    }

    /// <summary>消息分发（收线程）。返回 false = 断开。</summary>
    private bool Dispatch(ProtocolMessage message)
    {
        switch (message)
        {
            case WelcomeMessage welcome:
                if (!_awaitingWelcome)
                {
                    return false; // 会话中途重复 Welcome：协议错序
                }

                _awaitingWelcome = false;
                _joinRejected = null;
                MyMemberId = welcome.YourId;
                _reconnectAttempt = 0;
                SetState(MpConnectionState.Joined);
                _log?.Invoke($"[Mp] 加入成功：房间 {welcome.RoomCode}，成员 #{welcome.YourId}（{welcome.Members.Count} 位其他成员）");
                Joined?.Invoke(welcome.RoomCode, welcome.Members);
                return true;

            case MemberJoinMessage join:
                MemberJoined?.Invoke(join.MemberId, join.DisplayName);
                return true;

            case MemberLeaveMessage leave:
                MemberLeft?.Invoke(leave.MemberId);
                return true;

            case CatStateMessage cat when cat.MemberId != MyMemberId:
                RemoteCatState?.Invoke(cat.MemberId, cat.NormX, cat.NormY, cat.StyleId, cat.SpeciesId);
                return true;

            case TapMessage tap when tap.MemberId != MyMemberId:
                RemoteTap?.Invoke(tap.MemberId, tap.Side, tap.TimestampMs);
                return true;

            case CosmeticSyncMessage cosmetics when cosmetics.MemberId != MyMemberId:
                CosmeticSyncReceived?.Invoke(cosmetics.MemberId, cosmetics.HatId, cosmetics.SkinId, cosmetics.BongosId);
                return true;

            case ByeMessage bye:
                if (_awaitingWelcome)
                {
                    _joinRejected = bye.Reason; // 握手期被拒：不重连
                    return false;
                }

                _log?.Invoke($"[Mp] 服务器下发 Bye({bye.Reason})");
                return bye.Reason is ByeReason.Normal or ByeReason.ServerShutdown; // 踢出/关服都会走重连

            case HeartbeatMessage:
                return true; // 服务器不主动发心跳，容忍即可

            default:
                // 服务器伪造客户端上行消息（CatState/Tap with myId）或未知：按错序断开
                return false;
        }
    }

    /// <summary>发循环：发送 Channel + 20s 心跳注入。</summary>
    private async Task SendLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task heartbeat = HeartbeatLoopAsync(heartbeatCts.Token);

        try
        {
            while (true)
            {
                byte[] frame = await _outgoing.Reader.ReadAsync(ct);
                await stream.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // 断开：收循环同样退出，统一由 ConnectOnce 收尾
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, ct);
                if (State == MpConnectionState.Joined)
                {
                    _outgoing.Writer.TryWrite(MultiplayerProtocol.Encode(new HeartbeatMessage()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetState(MpConnectionState state)
    {
        lock (_gate)
        {
            SetStateLocked(state);
        }
    }

    private void SetStateLocked(MpConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        Stop();
        _sessionCts?.Dispose();
    }
}
