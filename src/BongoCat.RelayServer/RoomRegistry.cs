namespace BongoCat.RelayServer;

/// <summary>服务器限额配置（命令行可覆盖默认值；防资源耗尽的最小权威集）。</summary>
public sealed class RelayLimits
{
    /// <summary>单房间成员上限（06-multiplayer.md：上限 8 只猫）。</summary>
    public int MaxMembersPerRoom { get; init; } = 8;

    /// <summary>并发房间上限（防资源耗尽）。</summary>
    public int MaxRooms { get; init; } = 64;

    /// <summary>单连接每秒消息数上限，超限断开（06-multiplayer.md §7：防刷）。</summary>
    public int MaxMessagesPerSecond { get; init; } = 50;

    /// <summary>心跳超时秒数：60s 未收到任何帧即踢出（任务拍板：60s 无心跳）。</summary>
    public int HeartbeatTimeoutSeconds { get; init; } = 60;

    /// <summary>房间空闲回收分钟数：全员离线超过该时长销毁房间（释放房间码）。</summary>
    public int RoomIdleMinutes { get; init; } = 10;

    /// <summary>显示名字节上限（与 Protocol.HelloMessage.DisplayNameMaxBytes 对齐）。</summary>
    public int DisplayNameMaxBytes { get; init; } = 48;
}

/// <summary>房间内成员状态（服务器只登记身份与活跃时间，不理解消息语义）。</summary>
public sealed class RoomMember
{
    public RoomMember(ushort id, string displayName, long lastSeenUtcTicks)
    {
        Id = id;
        DisplayName = displayName;
        LastSeenUtcTicks = lastSeenUtcTicks;
    }

    public ushort Id { get; }

    public string DisplayName { get; }

    /// <summary>
    /// 成员物种字节（GDD-09 §4：从最新 CatState 观测缓存，Welcome 名册快照用；
    /// 未发过 CatState / 旧客户端 → 默认猫 0）。服务器只透传不解释语义。
    /// </summary>
    public byte Species { get; set; }

    /// <summary>最后收到任意帧的 UTC ticks（心跳保活依据；60s 无帧即踢）。</summary>
    public long LastSeenUtcTicks { get; set; }
}

/// <summary>加入失败原因（映射 Protocol.ByeReason 下发）。</summary>
public enum JoinFailure
{
    RoomNotFound,
    RoomFull,
    ServerBusy,
    InvalidField,
}

/// <summary>
/// 一个房间：房间码 + 成员表 + 最近活跃时间。纯数据 + 纯逻辑，不含 Socket——
/// 配合 <see cref="RoomRegistry"/> 全部可在单测中脱离网络验证（满员/回收/码生成）。
/// </summary>
public sealed class Room
{
    private readonly Dictionary<ushort, RoomMember> _members = new();

    public Room(string code, long createdUtcTicks)
    {
        Code = code;
        LastActivityUtcTicks = createdUtcTicks;
    }

    public string Code { get; }

    public IReadOnlyDictionary<ushort, RoomMember> Members => _members;

    /// <summary>最后有成员（或房间创建）的时间。全员离线后满 IdleMinutes 即回收。</summary>
    public long LastActivityUtcTicks { get; set; }

    public bool IsEmpty => _members.Count == 0;

    public bool IsFull(int maxMembers) => _members.Count >= maxMembers;

    public void Add(RoomMember member)
    {
        _members.Add(member.Id, member);
        LastActivityUtcTicks = member.LastSeenUtcTicks;
    }

    public bool Remove(ushort memberId) => _members.Remove(memberId);

    public bool TryGet(ushort memberId, out RoomMember? member) => _members.TryGetValue(memberId, out member);
}

/// <summary>
/// 房间登记处（服务器权威最小集的纯逻辑核心）：房间创建/加入/满员拒绝/空闲回收判定。
/// 独立成类的原因：这些规则可脱离网络在单测中穷举，连接管理（RelayServerHost）只做 IO。
/// </summary>
public sealed class RoomRegistry
{
    /// <summary>无歧义字符集：去掉 0/O、1/I/L（口头告知房间码不误读，06-multiplayer.md 房间码节）。</summary>
    public const string RoomCodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public const int RoomCodeLength = 6;

    private readonly RelayLimits _limits;
    private readonly Random _random;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    public RoomRegistry(RelayLimits limits, Random? random = null, TimeProvider? time = null)
    {
        _limits = limits;
        _random = random ?? Random.Shared;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyDictionary<string, Room> Rooms => _rooms;

    public int RoomCount => _rooms.Count;

    /// <summary>生成一个不与现有房间冲突的 6 位房间码（冲突重摇；字符集 31 → 空间 31^6 ≈ 8.9 亿）。</summary>
    public string GenerateRoomCode()
    {
        Span<char> chars = stackalloc char[RoomCodeLength];
        while (true)
        {
            for (int i = 0; i < RoomCodeLength; i++)
            {
                chars[i] = RoomCodeAlphabet[_random.Next(RoomCodeAlphabet.Length)];
            }

            string code = new string(chars);
            if (!_rooms.ContainsKey(code))
            {
                return code;
            }
        }
    }

    /// <summary>
    /// 处理加入请求。code 为空 = 创建新房间（受并发房间上限约束）；
    /// 非空 = 加入现有房间（受存在性与满员约束）。成功返回房间与分配的成员 id。
    /// </summary>
    public bool TryJoin(
        string code,
        string displayName,
        out Room? room,
        out ushort memberId,
        out JoinFailure failure)
    {
        room = null;
        memberId = 0;
        failure = default;

        if (string.IsNullOrEmpty(displayName))
        {
            failure = JoinFailure.InvalidField;
            return false;
        }

        long now = _time.GetUtcNow().UtcTicks;

        if (string.IsNullOrEmpty(code))
        {
            if (_rooms.Count >= _limits.MaxRooms)
            {
                failure = JoinFailure.ServerBusy;
                return false;
            }

            string newCode = GenerateRoomCode();
            room = new Room(newCode, now);
            _rooms.Add(newCode, room);
            memberId = AllocateMemberId(room);
            room.Add(new RoomMember(memberId, displayName, now));
            return true;
        }

        if (code.Length != RoomCodeLength || !code.All(RoomCodeAlphabet.Contains))
        {
            failure = JoinFailure.RoomNotFound; // 格式非法按"找不到房间"对外（不区分，减少探测面）
            return false;
        }

        if (!_rooms.TryGetValue(code, out Room? existing))
        {
            failure = JoinFailure.RoomNotFound;
            return false;
        }

        if (existing.IsFull(_limits.MaxMembersPerRoom))
        {
            failure = JoinFailure.RoomFull;
            return false;
        }

        room = existing;
        memberId = AllocateMemberId(existing);
        existing.Add(new RoomMember(memberId, displayName, now));
        return true;
    }

    /// <summary>成员离开（含被踢）。房间保留（全员离线后等空闲回收，重连窗口内房间码仍有效）。</summary>
    public void RemoveMember(Room room, ushort memberId)
    {
        if (room.Remove(memberId) && room.IsEmpty)
        {
            // 最后一人离开：把活跃时间锚定到现在，空闲回收倒计时从此刻起算
            room.LastActivityUtcTicks = _time.GetUtcNow().UtcTicks;
        }
    }

    /// <summary>回收全员离线超过 idleMinutes 的房间。返回被回收的房间码列表（日志用）。</summary>
    public List<string> CollectIdleRooms()
    {
        long now = _time.GetUtcNow().UtcTicks;
        var idleAfterTicks = TimeSpan.FromMinutes(_limits.RoomIdleMinutes).Ticks;
        List<string> removed = [];

        foreach (Room room in _rooms.Values.Where(r => r.IsEmpty && now - r.LastActivityUtcTicks > idleAfterTicks).ToList())
        {
            _rooms.Remove(room.Code);
            removed.Add(room.Code);
        }

        return removed;
    }

    /// <summary>从 1 起找房间内未占用的最小成员 id（断连重进不复用刚释放的 id 之前的空洞，简单安全）。</summary>
    private static ushort AllocateMemberId(Room room)
    {
        for (ushort id = 1; ; id++)
        {
            if (!room.Members.ContainsKey(id))
            {
                return id;
            }
        }
    }
}
