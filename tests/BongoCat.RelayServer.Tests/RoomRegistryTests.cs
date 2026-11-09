using BongoCat.Protocol;
using BongoCat.RelayServer;

using Xunit;

namespace BongoCat.RelayServer.Tests;

/// <summary>可控时钟（Core 规则：所有时间可注入；空闲回收/超时判定用它推进）。</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// 房间登记纯逻辑测试：创建/房间码/加入/满员/并发上限/空闲回收/成员 id 分配。
/// 服务器权威最小集里全部"登记"语义在这里穷举；连接管理不在此测试范围。
/// </summary>
public class RoomRegistryTests
{
    private static RelayLimits Limits(int maxRooms = 64, int maxMembers = 8) => new()
    {
        MaxRooms = maxRooms,
        MaxMembersPerRoom = maxMembers,
        MaxMessagesPerSecond = 50,
        HeartbeatTimeoutSeconds = 60,
        RoomIdleMinutes = 10,
    };

    [Fact]
    public void 创建房间_生成6位无歧义房间码_成员id从1分配()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new RoomRegistry(Limits(), new Random(1), time);

        Assert.True(registry.TryJoin(string.Empty, "猫猫", out Room? room, out ushort id, out JoinFailure _));

        Assert.Equal(6, room!.Code.Length);
        Assert.All(room.Code, c => Assert.Contains(c, RoomRegistry.RoomCodeAlphabet));
        Assert.DoesNotContain(room.Code, "0OLI1");
        Assert.Single(room.Members);
        Assert.Equal((ushort)1, id);
        Assert.Equal("猫猫", room.Members[id].DisplayName);
    }

    [Fact]
    public void 凭房间码加入_拿全房间快照_成员id递增不冲突()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new RoomRegistry(Limits(), new Random(1), time);

        Assert.True(registry.TryJoin(string.Empty, "房主", out Room? room, out ushort hostId, out _));
        Assert.Equal((ushort)1, hostId);

        Assert.True(registry.TryJoin(room!.Code, "客人", out Room? joined, out ushort guestId, out _));
        Assert.Same(room, joined);
        Assert.Equal((ushort)2, guestId);
        Assert.Equal(2, room.Members.Count);
    }

    [Fact]
    public void 加入不存在的房间码_拒绝RoomNotFound()
    {
        var registry = new RoomRegistry(Limits(), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.False(registry.TryJoin("A2C4E6", "猫猫", out _, out _, out JoinFailure failure));
        Assert.Equal(JoinFailure.RoomNotFound, failure);
    }

    [Fact]
    public void 加入格式非法的房间码_按RoomNotFound拒绝_不泄露存在性()
    {
        var registry = new RoomRegistry(Limits(), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        // 小写 / 含歧义字符 / 长度不符：一律 RoomNotFound（减少探测面）
        Assert.False(registry.TryJoin("a2c4e6", "猫", out _, out _, out JoinFailure f1));
        Assert.Equal(JoinFailure.RoomNotFound, f1);
        Assert.False(registry.TryJoin("0O1ILI", "猫", out _, out _, out JoinFailure f2));
        Assert.Equal(JoinFailure.RoomNotFound, f2);
        Assert.False(registry.TryJoin("A2C4", "猫", out _, out _, out JoinFailure f3));
        Assert.Equal(JoinFailure.RoomNotFound, f3);
    }

    [Fact]
    public void 房间满员_第9人拒绝RoomFull_前8人不受影响()
    {
        var registry = new RoomRegistry(Limits(maxMembers: 8), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        Assert.True(registry.TryJoin(string.Empty, "host", out Room? room, out _, out _));

        for (ushort i = 2; i <= 8; i++)
        {
            Assert.True(registry.TryJoin(room!.Code, $"m{i}", out _, out ushort id, out _));
            Assert.Equal(i, id);
        }

        Assert.False(registry.TryJoin(room!.Code, "第9人", out _, out _, out JoinFailure failure));
        Assert.Equal(JoinFailure.RoomFull, failure);
        Assert.Equal(8, room.Members.Count);
    }

    [Fact]
    public void 并发房间上限_超出后拒绝创建_ServerBusy_既有房间照常加入()
    {
        var registry = new RoomRegistry(Limits(maxRooms: 2), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.True(registry.TryJoin(string.Empty, "a", out Room? r1, out _, out _));
        Assert.True(registry.TryJoin(string.Empty, "b", out Room? r2, out _, out _));
        Assert.False(registry.TryJoin(string.Empty, "c", out _, out _, out JoinFailure failure));
        Assert.Equal(JoinFailure.ServerBusy, failure);

        // 既有房间不受影响
        Assert.True(registry.TryJoin(r1!.Code, "d", out _, out _, out _));
        Assert.True(registry.TryJoin(r2!.Code, "e", out _, out _, out _));
    }

    [Fact]
    public void 空显示名_拒绝InvalidField()
    {
        var registry = new RoomRegistry(Limits(), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.False(registry.TryJoin(string.Empty, string.Empty, out _, out _, out JoinFailure failure));
        Assert.Equal(JoinFailure.InvalidField, failure);
    }

    [Fact]
    public void 成员离开_房间保留_全员离线超时后回收_未到期不回收()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new RoomRegistry(Limits(), new Random(1), time);

        Assert.True(registry.TryJoin(string.Empty, "猫", out Room? room, out ushort id, out _));
        string code = room!.Code;
        registry.RemoveMember(room, id);
        Assert.True(room.IsEmpty);

        // 到期前：保留（重连窗口内房间码仍有效）
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(registry.CollectIdleRooms());
        Assert.Equal(1, registry.RoomCount);

        // 到期后：回收并释放房间码
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal([code], registry.CollectIdleRooms());
        Assert.Equal(0, registry.RoomCount);
    }

    [Fact]
    public void 有人的房间_永远不会被空闲回收()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new RoomRegistry(Limits(), new Random(1), time);

        Assert.True(registry.TryJoin(string.Empty, "常住猫", out Room? room, out _, out _));
        time.Advance(TimeSpan.FromDays(30));

        Assert.Empty(registry.CollectIdleRooms());
        Assert.Equal(1, registry.RoomCount);
        Assert.Single(room!.Members);
    }

    [Fact]
    public void 房间码生成_字符集合规且不与既有房间冲突()
    {
        var registry = new RoomRegistry(Limits(), new Random(7), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        // 生成器保证不撞既有房间（确定性随机下连打 50 个码全部互异且合规）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 50; i++)
        {
            string code = registry.GenerateRoomCode();
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.Contains(c, RoomRegistry.RoomCodeAlphabet));
            Assert.True(seen.Add(code), $"房间码重复：{code}");
        }
    }

    [Fact]
    public void 成员重进房间_获得未占用id_不与在房成员冲突()
    {
        var registry = new RoomRegistry(Limits(maxMembers: 3), new Random(1), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.True(registry.TryJoin(string.Empty, "a", out Room? room, out ushort id1, out _));
        Assert.True(registry.TryJoin(room!.Code, "b", out _, out ushort id2, out _));
        registry.RemoveMember(room, id1); // 1 号离开 → 空洞
        Assert.True(registry.TryJoin(room.Code, "c", out _, out ushort id3, out _));

        Assert.Equal((ushort)1, id3); // 最小未占用 id 复用
        Assert.Equal((ushort)2, id2);
        Assert.Equal(2, room.Members.Count);
    }
}

/// <summary>
/// 会话层纪律测试：帧率限制 / 缓冲上限（无 Socket，纯状态机调用）。
/// </summary>
public class ClientSessionTests
{
    private static RelayLimits Limits(int maxPerSec = 5) => new() { MaxMessagesPerSecond = maxPerSec };

    private static FrameParseResult HeartbeatFrame() =>
        new(FrameParseStatus.Ok, new HeartbeatMessage(), MultiplayerProtocol.HeaderSize);

    [Fact]
    public void 帧率限制_窗口内超限断开_窗口滚动后恢复()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        long ticks = time.GetUtcNow().UtcTicks;
        var session = new ClientSession(Limits(maxPerSec: 5), RelayLogStub(), ticks)
        {
            MessageHandler = _ => true,
        };

        for (int i = 0; i < 5; i++)
        {
            Assert.True(session.HandleInboundFrame(HeartbeatFrame(), ticks));
        }

        // 第 6 帧：同窗口超限 → 断开
        Assert.False(session.HandleInboundFrame(HeartbeatFrame(), ticks));

        // 窗口滚动后：新连接语义（这里仅验证计数器重置路径；实际超限即断连不会再来帧）
        ticks = time.GetUtcNow().UtcTicks;
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.HandleInboundFrame(HeartbeatFrame(), time.GetUtcNow().UtcTicks));
    }

    [Fact]
    public void 任意帧都刷新保活时间戳()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var session = new ClientSession(Limits(), RelayLogStub(), time.GetUtcNow().UtcTicks)
        {
            MessageHandler = _ => true,
        };

        time.Advance(TimeSpan.FromSeconds(30));
        session.HandleInboundFrame(HeartbeatFrame(), time.GetUtcNow().UtcTicks);

        Assert.Equal(time.GetUtcNow().UtcTicks, session.LastSeenUtcTicks);
    }

    [Fact]
    public void 缓冲扩容_直至帧上限_超限拒绝()
    {
        var session = new ClientSession(Limits(), RelayLogStub(), 0);

        Assert.Equal(64 * 1024, session.FreeBufferSpace.Length); // 起步 64KB
        Assert.True(session.TryGrowBuffer());  // 128KB
        Assert.True(session.TryGrowBuffer());  // 256KB
        Assert.True(session.TryGrowBuffer());  // 262152 = MaxFrameBytes（8 + 256KB）
        Assert.Equal(MultiplayerProtocol.MaxFrameBytes, session.FreeBufferSpace.Length);
        Assert.False(session.TryGrowBuffer()); // 已达上限：超长帧断开
    }

    [Fact]
    public void 会话身份_加入后状态与名册登记()
    {
        var room = new Room("A2C4E6", 0);
        var session = new ClientSession(Limits(), RelayLogStub(), 0);

        Assert.Equal(SessionState.AwaitingHello, session.State);
        session.SetRoom(room, 3, "布丁");
        Assert.Equal(SessionState.Joined, session.State);
        Assert.Equal((ushort)3, session.MemberId);
        Assert.Equal("布丁", session.DisplayName);
        Assert.Same(room, session.Room);
    }

    /// <summary>日志桩：单测不写真实日志文件（路径指向一次性临时文件）。</summary>
    private static RelayLog RelayLogStub()
        => RelayLog.CreateDefault(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "relay-tests-stub.log"));
}
