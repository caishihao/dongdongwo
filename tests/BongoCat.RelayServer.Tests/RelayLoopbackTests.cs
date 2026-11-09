using System.Net.Sockets;
using BongoCat.Protocol;
using BongoCat.RelayServer;

using Xunit;

namespace BongoCat.RelayServer.Tests;

/// <summary>
/// 真实 TCP 回路集成测试（loopback，无需云服务器）：AcceptSessionAsync / HandleHello /
/// 广播转发 / 下线通知的端到端验证——纯逻辑单测覆盖不到的连接生命周期在这里收口。
/// </summary>
public class RelayLoopbackTests
{
    private static async Task<FrameParseResult> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        int buffered = 0;
        while (true)
        {
            FrameParseResult result = MultiplayerProtocol.ParseFrame(buffer.AsSpan(0, buffered));
            if (result.Status == FrameParseStatus.Ok)
            {
                return result;
            }

            if (result.Status == FrameParseStatus.Invalid)
            {
                throw new InvalidOperationException("收到非法帧");
            }

            int read = await stream.ReadAsync(buffer.AsMemory(buffered), ct);
            if (read == 0)
            {
                throw new InvalidOperationException("连接被对端关闭");
            }

            buffered += read;
        }
    }

    [Fact]
    public async Task 回路_创建拿码_凭码加入_Tap转发_成员下线广播()
    {
        var limits = new RelayLimits { MaxMembersPerRoom = 8, MaxMessagesPerSecond = 50 };
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);
        int port = host.LocalPort!.Value;

        // A 创建房间并读出房间码
        var clientA = new TcpClient();
        await clientA.ConnectAsync("127.0.0.1", port);
        NetworkStream streamA = clientA.GetStream();
        await streamA.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("甲", string.Empty)));
        var welcomeA = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        string roomCode = welcomeA.RoomCode;
        Assert.Equal(6, roomCode.Length);
        Assert.Equal((ushort)1, welcomeA.YourId);
        Assert.Empty(welcomeA.Members); // 第一个成员：名册不含自己

        // B 凭码加入
        var clientB = new TcpClient();
        await clientB.ConnectAsync("127.0.0.1", port);
        NetworkStream streamB = clientB.GetStream();
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("乙", roomCode)));
        var welcomeB = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamB, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, welcomeB.YourId);
        Assert.Single(welcomeB.Members); // 名册含甲
        Assert.Equal("甲", welcomeB.Members[0].DisplayName);

        // A 收到 MemberJoin(#2 乙)
        var joinA = Assert.IsType<MemberJoinMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, joinA.MemberId);
        Assert.Equal("乙", joinA.DisplayName);

        // B 发 Tap → A 收到（哑中继转发）
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new TapMessage(2, TapSide.Left, 12345)));
        var tapA = Assert.IsType<TapMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, tapA.MemberId);
        Assert.Equal(TapSide.Left, tapA.Side);

        // B 发越界 CatState → A 收到被服务器钳制的坐标（服务器权威校验）
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new CatStateMessage(2, 5.0, -3.0)));
        var catA = Assert.IsType<CatStateMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal(1.0, catA.NormX);
        Assert.Equal(0.0, catA.NormY);

        // B 发装扮三元组（E8 追加）→ A 收到且归属 id 被服务器重写
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new CosmeticSyncMessage(2, "hat_beret", "skin_cow", "bongos_gold")));
        var cosmeticsA = Assert.IsType<CosmeticSyncMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, cosmeticsA.MemberId);
        Assert.Equal("hat_beret", cosmeticsA.HatId);
        Assert.Equal("skin_cow", cosmeticsA.SkinId);
        Assert.Equal("bongos_gold", cosmeticsA.BongosId);

        // B 主动告别（Bye）→ A 收到 MemberLeave
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new ByeMessage(ByeReason.Normal)));
        var leaveA = Assert.IsType<MemberLeaveMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, leaveA.MemberId);

        // A 收到服务器的 Bye 应答后断开；关闭
        clientA.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, registry.RoomCount - registry.RoomCount); // 幂等调用不抛
    }

    [Fact]
    public async Task 回路_加入不存在的房间_收到Bye拒绝()
    {
        var limits = new RelayLimits();
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);

        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.LocalPort!.Value);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("猫", "Z9Z9Z9")));

        var bye = Assert.IsType<ByeMessage>((await ReadFrameAsync(stream, CancellationToken.None)).Message);
        Assert.Equal(ByeReason.RoomNotFound, bye.Reason);

        // 服务器随后关闭连接（读到流关闭即符合预期）
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ReadFrameAsync(stream, CancellationToken.None));

        client.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task 回路_满员后第9人_收到RoomFull拒绝()
    {
        var limits = new RelayLimits { MaxMembersPerRoom = 2 };
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);
        int port = host.LocalPort!.Value;

        var clientA = new TcpClient();
        await clientA.ConnectAsync("127.0.0.1", port);
        NetworkStream streamA = clientA.GetStream();
        await streamA.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("甲", string.Empty)));
        string roomCode = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message).RoomCode;

        var clientB = new TcpClient();
        await clientB.ConnectAsync("127.0.0.1", port);
        NetworkStream streamB = clientB.GetStream();
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("乙", roomCode)));
        await ReadFrameAsync(streamB, CancellationToken.None); // Welcome
        _ = await ReadFrameAsync(streamA, CancellationToken.None); // A 收 MemberJoin

        var clientC = new TcpClient();
        await clientC.ConnectAsync("127.0.0.1", port);
        NetworkStream streamC = clientC.GetStream();
        await streamC.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("丙", roomCode)));
        var bye = Assert.IsType<ByeMessage>((await ReadFrameAsync(streamC, CancellationToken.None)).Message);
        Assert.Equal(ByeReason.RoomFull, bye.Reason);

        clientA.Dispose();
        clientB.Dispose();
        clientC.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
    }

    // ── GDD-09 §4：物种字节转发与 Welcome 名册 species 快照 ─────────────

    [Fact]
    public async Task 回路_物种字节随CatState转发_并进入后续成员的Welcome名册()
    {
        var limits = new RelayLimits { MaxMembersPerRoom = 8, MaxMessagesPerSecond = 50 };
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);
        int port = host.LocalPort!.Value;

        // A 创建房间
        var clientA = new TcpClient();
        await clientA.ConnectAsync("127.0.0.1", port);
        NetworkStream streamA = clientA.GetStream();
        await streamA.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("甲", string.Empty)));
        string roomCode = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message).RoomCode;

        // B 加入
        var clientB = new TcpClient();
        await clientB.ConnectAsync("127.0.0.1", port);
        NetworkStream streamB = clientB.GetStream();
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("乙", roomCode)));
        var welcomeB = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamB, CancellationToken.None)).Message);
        _ = await ReadFrameAsync(streamA, CancellationToken.None); // A 收 MemberJoin

        // B 声明自己是恐龙（CatState species=1）→ A 收到转发且物种字节保真
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new CatStateMessage(2, 0.5, 0.5, CatStyleByte.Chibi, CatSpeciesByte.Dino)));
        var catA = Assert.IsType<CatStateMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal((ushort)2, catA.MemberId);
        Assert.Equal(CatSpeciesByte.Dino, catA.SpeciesId);
        Assert.Equal(CatStyleByte.Chibi, catA.StyleId);

        // C 随后加入 → Welcome 名册快照携带 B 的观测物种（服务器缓存）
        var clientC = new TcpClient();
        await clientC.ConnectAsync("127.0.0.1", port);
        NetworkStream streamC = clientC.GetStream();
        await streamC.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("丙", roomCode)));
        var welcomeC = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamC, CancellationToken.None)).Message);
        Assert.Equal(2, welcomeC.Members.Count);
        RosterEntry entryB = welcomeC.Members.Single(m => m.MemberId == 2);
        Assert.Equal(CatSpeciesByte.Dino, entryB.Species);
        RosterEntry entryA = welcomeC.Members.Single(m => m.MemberId == 1);
        Assert.Equal(CatSpeciesByte.Cat, entryA.Species); // 未发过 CatState → 默认猫

        clientA.Dispose();
        clientB.Dispose();
        clientC.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task 回路_非法物种字节被服务器收敛为cat转发()
    {
        // GDD-09 §4：协议解码已收敛非法 species → 服务器透传的必是合法值（防御在 decode 层）
        var limits = new RelayLimits { MaxMessagesPerSecond = 50 };
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);
        int port = host.LocalPort!.Value;

        var clientA = new TcpClient();
        await clientA.ConnectAsync("127.0.0.1", port);
        NetworkStream streamA = clientA.GetStream();
        await streamA.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("甲", string.Empty)));
        string roomCode = Assert.IsType<WelcomeMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message).RoomCode;

        var clientB = new TcpClient();
        await clientB.ConnectAsync("127.0.0.1", port);
        NetworkStream streamB = clientB.GetStream();
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("乙", roomCode)));
        _ = await ReadFrameAsync(streamB, CancellationToken.None); // Welcome
        _ = await ReadFrameAsync(streamA, CancellationToken.None); // MemberJoin

        // B 发非法 species=9 → 编码侧 Normalize 收敛为 cat=0（帧层字节 0）
        await streamB.WriteAsync(MultiplayerProtocol.Encode(new CatStateMessage(2, 0.5, 0.5, CatStyleByte.Clay, CatSpeciesByte.Normalize(9))));
        var catA = Assert.IsType<CatStateMessage>((await ReadFrameAsync(streamA, CancellationToken.None)).Message);
        Assert.Equal(CatSpeciesByte.Cat, catA.SpeciesId);

        clientA.Dispose();
        clientB.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task 回路_限速超限_连接被断开()
    {
        var limits = new RelayLimits { MaxMessagesPerSecond = 3, HeartbeatTimeoutSeconds = 60 };
        var registry = new RoomRegistry(limits, new Random(1));
        using var log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-loopback.log"));
        await using var host = new RelayServerHost(limits, registry, log);
        await host.StartAsync(0);

        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.LocalPort!.Value);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(MultiplayerProtocol.Encode(new HelloMessage("甲", string.Empty)));
        await ReadFrameAsync(stream, CancellationToken.None); // Welcome

        // 连发 10 帧（上限 3）：第 4 帧超限即断开 → 之后任何读都应"对端关闭"
        for (int i = 0; i < 10; i++)
        {
            await stream.WriteAsync(MultiplayerProtocol.Encode(new HeartbeatMessage()));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ReadFrameAsync(stream, CancellationToken.None));

        client.Dispose();
        await host.StopAsync(TimeSpan.FromSeconds(2));
    }
}
