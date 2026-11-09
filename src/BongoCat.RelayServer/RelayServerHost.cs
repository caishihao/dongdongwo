using System.Net;
using System.Net.Sockets;
using System.Text;
using BongoCat.Protocol;

namespace BongoCat.RelayServer;

/// <summary>
/// 中继服务器宿主：TcpListener 接受连接 → 每会话读/写两个异步任务。
/// 服务器权威最小集：成员上下线广播、心跳超时踢出、房间满员拒绝、空闲房间回收。
/// 服务器不理解房间消息的 payload 语义（哑中继 + 房间登记）。
/// </summary>
public sealed class RelayServerHost : IAsyncDisposable
{
    private readonly RelayLimits _limits;
    private readonly RoomRegistry _registry;
    private readonly RelayLog _log;
    private readonly List<ClientSession> _sessions = [];
    private readonly object _sessionsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _maintenanceTask;

    public RelayServerHost(RelayLimits limits, RoomRegistry registry, RelayLog log)
    {
        _limits = limits;
        _registry = registry;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    /// <summary>实际监听端口（port=0 时由系统分配；集成测试用）。</summary>
    public int? LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    /// <summary>当前在线会话数（测试/诊断用）。</summary>
    public int SessionCount
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.Count;
            }
        }
    }

    public async Task StartAsync(int port)
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;
        _log.Info($"中继服务器监听 0.0.0.0:{port}（房间上限 {_limits.MaxRooms}，单房 {_limits.MaxMembersPerRoom} 人，" +
                  $"限速 {_limits.MaxMessagesPerSecond} 帧/s，心跳超时 {_limits.HeartbeatTimeoutSeconds}s，空闲回收 {_limits.RoomIdleMinutes}min）");

        _acceptTask = AcceptLoopAsync(_cts.Token);
        _maintenanceTask = MaintenanceLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>优雅关闭：广播 Bye(ServerShutdown) → 停止接受 → 等待任务收尾。</summary>
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _log.Info("关闭中：广播 ServerShutdown 后断开全部会话");

        lock (_sessionsLock)
        {
            foreach (ClientSession session in _sessions)
            {
                session.TryEnqueue(new ByeMessage(ByeReason.ServerShutdown));
                session.CloseOutgoing();
            }
        }

        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // 已停止：忽略
        }

        if (_acceptTask is not null)
        {
            await Task.WhenAny(_acceptTask, Task.Delay(drainTimeout));
        }

        if (_maintenanceTask is not null)
        {
            await Task.WhenAny(_maintenanceTask, Task.Delay(TimeSpan.FromSeconds(1)));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener 已停止
            }

            _ = AcceptSessionAsync(tcpClient, ct); // 每连接独立任务；异常在其内部消化
        }
    }

    private async Task AcceptSessionAsync(TcpClient tcpClient, CancellationToken ct)
    {
        tcpClient.NoDelay = true; // ADR-005：关 Nagle，敲击转发正确性优先
        var session = new ClientSession(_limits, _log, TimeProvider.System.GetUtcNow().UtcTicks);
        session.MessageHandler = message => HandleMessage(session, message);
        session.SetRemoteDescription(tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown");
        _log.Info($"连接建立：{session.RemoteDescription}");

        lock (_sessionsLock)
        {
            _sessions.Add(session);
        }

        try
        {
            NetworkStream stream = tcpClient.GetStream();
            Task sendTask = SendLoopAsync(session, stream, ct);
            Task recvTask = ReceiveLoopAsync(session, stream, ct, () => tcpClient);
            await Task.WhenAny(sendTask, recvTask);
        }
        catch (Exception ex)
        {
            _log.Warn($"会话 IO 异常 {session.RemoteDescription}: {ex.Message}");
        }
        finally
        {
            await DisconnectAsync(session, tcpClient);
        }
    }

    /// <summary>读循环：网络字节 → 会话缓冲 → 逐帧解析分发。</summary>
    private async Task ReceiveLoopAsync(ClientSession session, NetworkStream stream, CancellationToken ct, Func<TcpClient> client)
    {
        while (!ct.IsCancellationRequested && client().Connected)
        {
            int read = await stream.ReadAsync(session.FreeBufferSpace, ct);
            if (read == 0)
            {
                return; // 对端关闭
            }

            session.NoteBytesReceived(read);

            while (true)
            {
                FrameParseResult result = MultiplayerProtocol.ParseFrame(session.BufferedBytes);
                if (result.Status == FrameParseStatus.NeedMoreData)
                {
                    break;
                }

                if (result.Status == FrameParseStatus.Invalid || !session.HandleInboundFrame(result, TimeProvider.System.GetUtcNow().UtcTicks))
                {
                    _log.Warn($"断开会话 {session.RemoteDescription}（帧={result.Status}）");
                    return;
                }

                session.ConsumeBuffered(result.FrameLength);
            }

            if (session.FreeBufferSpace.Length == 0 && !session.TryGrowBuffer())
            {
                return; // 帧超限
            }
        }
    }

    /// <summary>写循环：发送 Channel → 网络（单写者，多会话转发天然串行化到各会话自己的队列）。</summary>
    private static async Task SendLoopAsync(ClientSession session, NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (await session.OutgoingReader.WaitToReadAsync(ct))
            {
                while (session.OutgoingReader.TryRead(out byte[]? frame))
                {
                    await stream.WriteAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // 对端断开：读循环也会退出，统一在 finally 清理
        }
    }

    // ── 消息语义（服务器权威最小集）──────────────────────────────────────

    private bool HandleMessage(ClientSession session, ProtocolMessage message)
    {
        switch (message)
        {
            case HelloMessage hello:
                return HandleHello(session, hello);

            case HeartbeatMessage:
                return true; // 保活已由 HandleInboundFrame 刷新 LastSeen

            case ByeMessage:
                return false; // 客户端告别 → 正常断开

            case CatStateMessage cat:
                if (session.State != SessionState.Joined || session.Room is null)
                {
                    return false; // 未加入就发状态：协议错序
                }

                // 服务器权威校验：坐标钳制到 [0,1]（NaN 收敛 0.5）再转发，杜绝非法值传播
                double x = double.IsNaN(cat.NormX) ? 0.5 : Math.Clamp(cat.NormX, 0.0, 1.0);
                double y = double.IsNaN(cat.NormY) ? 0.5 : Math.Clamp(cat.NormY, 0.0, 1.0);
                // GDD-08：style 字节随 CatState 转发（Decode 已收敛非法值，服务器只透传）
                Broadcast(session.Room, session, new CatStateMessage(session.MemberId, x, y, cat.StyleId, cat.SpeciesId));

                // GDD-09 §4：观测缓存成员物种（Welcome 名册快照用；Decode 已收敛非法值）
                if (session.Room.TryGet(session.MemberId, out RoomMember? member))
                {
                    member!.Species = cat.SpeciesId;
                }

                return true;

            case TapMessage tap:
                if (session.State != SessionState.Joined || session.Room is null)
                {
                    return false;
                }

                Broadcast(session.Room, session, new TapMessage(session.MemberId, tap.Side, tap.TimestampMs));
                return true;

            case CosmeticSyncMessage cosmetics:
                if (session.State != SessionState.Joined || session.Room is null)
                {
                    return false;
                }

                // 哑中继不理解 artKey 语义，只重写归属 id 转发；未知/伪造 id 由接收方目录校验回退默认（E8 防御渲染）
                Broadcast(session.Room, session, new CosmeticSyncMessage(session.MemberId, cosmetics.HatId, cosmetics.SkinId, cosmetics.BongosId));
                return true;

            // Welcome / MemberJoin / MemberLeave 是服务器下行消息，客户端发来即协议错序
            default:
                _log.Warn($"协议错序：{session.RemoteDescription} 发送下行消息 type={message.Type}，断开");
                return false;
        }
    }

    private bool HandleHello(ClientSession session, HelloMessage hello)
    {
        if (session.State != SessionState.AwaitingHello)
        {
            return false; // 重复 Hello
        }

        if (Encoding.UTF8.GetByteCount(hello.DisplayName) > _limits.DisplayNameMaxBytes || hello.DisplayName.Length == 0)
        {
            session.TryEnqueue(new ByeMessage(ByeReason.InvalidField));
            return false;
        }

        if (!_registry.TryJoin(hello.RoomCode, hello.DisplayName, out Room? room, out ushort memberId, out JoinFailure failure))
        {
            ByeReason reason = failure switch
            {
                JoinFailure.RoomNotFound => ByeReason.RoomNotFound,
                JoinFailure.RoomFull => ByeReason.RoomFull,
                JoinFailure.ServerBusy => ByeReason.ServerBusy,
                _ => ByeReason.InvalidField,
            };
            _log.Info($"加入被拒：{session.RemoteDescription} code={hello.RoomCode} 原因={failure}");
            session.TryEnqueue(new ByeMessage(reason));
            return false;
        }

        session.SetRoom(room!, memberId, hello.DisplayName);

        // Welcome：分配 id + 房间码 + 房间内其他成员快照（不含自己；species = 观测缓存，GDD-09 §4）
        var roster = room!.Members.Values
            .Where(m => m.Id != memberId)
            .Select(m => new RosterEntry(m.Id, m.DisplayName, m.Species))
            .ToList();
        session.TryEnqueue(new WelcomeMessage(memberId, room.Code, roster));
        _log.Info($"加入成功：{session.RemoteDescription} → 房间 {room.Code} 成员 #{memberId}「{hello.DisplayName}」（{room.Members.Count}/{_limits.MaxMembersPerRoom}）");

        // 房间内其他成员收到上线广播
        Broadcast(room, session, new MemberJoinMessage(memberId, hello.DisplayName));
        return true;
    }

    /// <summary>向房间内除 sender 外的所有会话转发一条消息（哑中继路由的唯一动作）。</summary>
    private void Broadcast(Room room, ClientSession sender, ProtocolMessage message)
    {
        lock (_sessionsLock)
        {
            foreach (ClientSession session in _sessions)
            {
                if (!ReferenceEquals(session, sender) &&
                    session.State == SessionState.Joined &&
                    ReferenceEquals(session.Room, room))
                {
                    session.TryEnqueue(message);
                }
            }
        }
    }

    /// <summary>会话收尾：从房间移除成员（广播下线）→ 从会话表移除 → 关闭连接。</summary>
    private async Task DisconnectAsync(ClientSession session, TcpClient tcpClient)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
        }

        if (session.State == SessionState.Joined && session.Room is not null)
        {
            _registry.RemoveMember(session.Room, session.MemberId);
            Broadcast(session.Room, session, new MemberLeaveMessage(session.MemberId));
            _log.Info($"离开房间 {session.Room.Code}：#{session.MemberId}「{session.DisplayName}」（余 {session.Room.Members.Count} 人）");
        }

        session.CloseOutgoing();
        try
        {
            tcpClient.Close();
        }
        catch (SocketException)
        {
        }

        await Task.CompletedTask;
    }

    /// <summary>IAsyncDisposable（await using 场景）：转发到优雅关闭。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>维护循环（30s 周期）：心跳超时踢出 + 空闲房间回收。</summary>
    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            long now = TimeProvider.System.GetUtcNow().UtcTicks;
            long timeoutTicks = TimeSpan.FromSeconds(_limits.HeartbeatTimeoutSeconds).Ticks;
            List<ClientSession> stale;
            lock (_sessionsLock)
            {
                stale = _sessions.Where(s => now - s.LastSeenUtcTicks > timeoutTicks).ToList();
            }

            foreach (ClientSession session in stale)
            {
                _log.Info($"心跳超时踢出：{session.RemoteDescription}（{_limits.HeartbeatTimeoutSeconds}s 无任何帧）");
                session.TryEnqueue(new ByeMessage(ByeReason.Normal));
                session.CloseOutgoing();
            }

            foreach (string code in _registry.CollectIdleRooms())
            {
                _log.Info($"空闲回收：房间 {code}（全员离线超 {_limits.RoomIdleMinutes} 分钟）");
            }
        }
    }
}
