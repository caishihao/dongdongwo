using System.Buffers.Binary;
using System.Text;
using BongoCat.Protocol;

using Xunit;

namespace BongoCat.Protocol.Tests;

/// <summary>
/// E8 协议编解码测试：全部消息类型 Encode → ParseFrame 往返保真 + 非法帧拒绝 + 帧格式纪律。
/// 协议是客户端与 RelayServer 两端共用契约（ADR-005），任何漂移都是全线不通，roundtrip 是最低守门。
/// GDD-09 追加：species 字节（CAT_STATE v3 / Welcome 名册条目）roundtrip 与新旧互操作。
/// </summary>
public class MultiplayerProtocolRoundtripTests
{
    [Fact]
    public void Hello_往返保真()
    {
        var message = new HelloMessage("糯米团", "A7K2MQ");
        ProtocolMessage decoded = Roundtrip(message);

        var hello = Assert.IsType<HelloMessage>(decoded);
        Assert.Equal("糯米团", hello.DisplayName);
        Assert.Equal("A7K2MQ", hello.RoomCode);
    }

    [Fact]
    public void Hello_创建房间_房间码为空也能往返()
    {
        var hello = Assert.IsType<HelloMessage>(Roundtrip(new HelloMessage("猫猫", string.Empty)));
        Assert.Equal(string.Empty, hello.RoomCode);
    }

    [Fact]
    public void Welcome_往返保真_名册顺序不变()
    {
        var message = new WelcomeMessage(7, "X9T4CB",
        [
            new RosterEntry(1, "布丁"),
            new RosterEntry(3, "糯米"),
            new RosterEntry(6, "年糕"),
        ]);

        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(message));
        Assert.Equal((ushort)7, welcome.YourId);
        Assert.Equal("X9T4CB", welcome.RoomCode);
        Assert.Equal(3, welcome.Members.Count);
        Assert.Equal(new RosterEntry(1, "布丁"), welcome.Members[0]);
        Assert.Equal(new RosterEntry(6, "年糕"), welcome.Members[2]);
    }

    [Fact]
    public void Welcome_空名册_往返保真()
    {
        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(new WelcomeMessage(1, "A2C4E6", [])));
        Assert.Empty(welcome.Members);
    }

    [Fact]
    public void MemberJoin_往返保真()
    {
        var join = Assert.IsType<MemberJoinMessage>(Roundtrip(new MemberJoinMessage(5, "花卷")));
        Assert.Equal((ushort)5, join.MemberId);
        Assert.Equal("花卷", join.DisplayName);
    }

    [Fact]
    public void MemberLeave_往返保真()
    {
        var leave = Assert.IsType<MemberLeaveMessage>(Roundtrip(new MemberLeaveMessage(42)));
        Assert.Equal((ushort)42, leave.MemberId);
    }

    [Fact]
    public void CatState_往返保真_归一化坐标无精度损耗()
    {
        // 归一化坐标往返（双精度逐位保真）：发送方 rel → 线上 → 接收方 rel，跨分辨率换算的输入不能走样
        double x = 0.345678901234;
        double y = 0.999999999999;

        var cat = Assert.IsType<CatStateMessage>(Roundtrip(new CatStateMessage(3, x, y)));
        Assert.Equal((ushort)3, cat.MemberId);
        Assert.Equal(x, cat.NormX);
        Assert.Equal(y, cat.NormY);
    }

    [Fact]
    public void CatState_风格字节_往返保真()
    {
        // GDD-08 §4：style 字节随 CAT_STATE 尾部往返（0~5 全枚举覆盖）
        foreach (byte style in new byte[] { CatStyleByte.Clay, CatStyleByte.Sticker, CatStyleByte.Flat, CatStyleByte.Sketch, CatStyleByte.Chibi, CatStyleByte.Papercut })
        {
            var cat = Assert.IsType<CatStateMessage>(Roundtrip(new CatStateMessage(3, 0.5, 0.5, style)));
            Assert.Equal(style, cat.StyleId);
        }
    }

    [Fact]
    public void CatState_旧客户端短payload_缺尾字节回退clay()
    {
        // GDD-08 §6 边界 3：旧客户端 payload 只有 18 字节（u16+f64+f64），解码不失败、回退 clay
        var payload = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10), 0.75);

        var cat = CatStateMessage.Decode(payload);

        Assert.NotNull(cat);
        Assert.Equal((ushort)9, cat.MemberId);
        Assert.Equal(0.25, cat.NormX);
        Assert.Equal(0.75, cat.NormY);
        Assert.Equal(CatStyleByte.Clay, cat.StyleId);
    }

    [Fact]
    public void CatState_非法风格字节_收敛clay不断连()
    {
        // GDD-08 §4：非法 styleId（>5）→ 回退 clay 渲染并记日志，绝不因坏字节断开
        var payload = new byte[19];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10), 0.75);
        payload[18] = 99;

        var cat = CatStateMessage.Decode(payload);

        Assert.NotNull(cat);
        Assert.Equal(CatStyleByte.Clay, cat.StyleId);
    }

    [Fact]
    public void CatState_新payload字节数_20字节()
    {
        // GDD-09 v3 语义：当前 20 字节（+species），GDD-08 v2 19 字节，v1 18 字节
        Assert.Equal(20, CatStateMessage.PayloadBytes);
        Assert.Equal(18, CatStateMessage.LegacyPayloadBytes);
        Assert.Equal(20, new CatStateMessage(1, 0.5, 0.5, CatStyleByte.Chibi, CatSpeciesByte.Dino).MeasurePayload());
    }

    [Fact]
    public void CatStyleByte_枚举白名单_全枚举合法超界非法()
    {
        Assert.Equal(0, CatStyleByte.Clay);
        Assert.Equal(5, CatStyleByte.Papercut);
        for (byte b = 0; b <= CatStyleByte.Max; b++)
        {
            Assert.True(CatStyleByte.IsValid(b));
            Assert.Equal(b, CatStyleByte.Normalize(b));
        }

        Assert.False(CatStyleByte.IsValid(6));
        Assert.False(CatStyleByte.IsValid(255));
        Assert.Equal(CatStyleByte.Clay, CatStyleByte.Normalize(6));
        Assert.Equal(CatStyleByte.Clay, CatStyleByte.Normalize(255));
    }

    [Fact]
    public void CatState_边界值0与1_往返保真()
    {
        var cat = Assert.IsType<CatStateMessage>(Roundtrip(new CatStateMessage(1, 0.0, 1.0)));
        Assert.Equal(0.0, cat.NormX);
        Assert.Equal(1.0, cat.NormY);
    }

    [Fact]
    public void Tap_往返保真_侧别与时间戳()
    {
        var message = new TapMessage(9, TapSide.Right, 1_730_000_000_000);
        var tap = Assert.IsType<TapMessage>(Roundtrip(message));

        Assert.Equal((ushort)9, tap.MemberId);
        Assert.Equal(TapSide.Right, tap.Side);
        Assert.Equal(1_730_000_000_000, tap.TimestampMs);
    }

    [Fact]
    public void Heartbeat_往返_且要求空payload()
    {
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        var result = MultiplayerProtocol.ParseFrame(frame);

        Assert.Equal(FrameParseStatus.Ok, result.Status);
        Assert.IsType<HeartbeatMessage>(result.Message);
        Assert.Equal(MultiplayerProtocol.HeaderSize, frame.Length); // 空 payload 帧 = 纯帧头
    }

    [Fact]
    public void Heartbeat_带payload_视为非法帧()
    {
        // 严格 schema：心跳多余字节即非法（协议纪律，防语义漂移）
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        frame[^1] ^= 0xFF; // 破坏最后字节（仍保留帧头可解析出"payload 非空"）

        // 直接改 payloadLen 会破坏结构，改为手工构造带 1 字节 payload 的心跳帧
        var handMade = new byte[MultiplayerProtocol.HeaderSize + 1];
        handMade[0] = (byte)(MultiplayerProtocol.Magic & 0xFF);
        handMade[1] = (byte)(MultiplayerProtocol.Magic >> 8);
        handMade[2] = MultiplayerProtocol.ProtocolVersion;
        handMade[3] = (byte)MsgType.Heartbeat;
        handMade[4] = 1; // payloadLen = 1

        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(handMade).Status);
    }

    [Fact]
    public void Bye_往返保真()
    {
        var bye = Assert.IsType<ByeMessage>(Roundtrip(new ByeMessage(ByeReason.RoomFull)));
        Assert.Equal(ByeReason.RoomFull, bye.Reason);
    }

    [Fact]
    public void CosmeticSync_往返保真_三元组与成员id()
    {
        var message = new CosmeticSyncMessage(7, "hat_beret", "skin_cow", "bongos_gold");
        var sync = Assert.IsType<CosmeticSyncMessage>(Roundtrip(message));

        Assert.Equal((ushort)7, sync.MemberId);
        Assert.Equal("hat_beret", sync.HatId);
        Assert.Equal("skin_cow", sync.SkinId);
        Assert.Equal("bongos_gold", sync.BongosId);
    }

    [Fact]
    public void CosmeticSync_空串与默认装扮_往返保真()
    {
        // 空串 = 该槽默认/未装备（黏土色回退），协议层忠实传输不特殊化
        var sync = Assert.IsType<CosmeticSyncMessage>(Roundtrip(new CosmeticSyncMessage(3, string.Empty, string.Empty, string.Empty)));
        Assert.Equal(string.Empty, sync.HatId);
        Assert.Equal(string.Empty, sync.SkinId);
        Assert.Equal(string.Empty, sync.BongosId);
    }

    [Fact]
    public void CosmeticSync_超长artKey_解码拒绝()
    {
        string tooLong = new('x', CosmeticSyncMessage.ArtKeyMaxBytes + 1);
        var frame = MultiplayerProtocol.Encode(new CosmeticSyncMessage(1, tooLong, string.Empty, string.Empty));
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void 帧头_魔数版本与类型字段符合ADR005布局()
    {
        byte[] frame = MultiplayerProtocol.Encode(new MemberLeaveMessage(1));

        Assert.Equal(0x43, frame[0]); // 'C'（magic 0x4243 低字节 "BC"）
        Assert.Equal(0x42, frame[1]); // 'B'
        Assert.Equal(MultiplayerProtocol.ProtocolVersion, frame[2]);
        Assert.Equal((byte)MsgType.MemberLeave, frame[3]);
        // payloadLen little-endian u32 = 2
        Assert.Equal(2, frame[4]);
        Assert.Equal(0, frame[5]);
        Assert.Equal(0, frame[6]);
        Assert.Equal(0, frame[7]);
        Assert.Equal(MultiplayerProtocol.HeaderSize + 2, frame.Length);
    }

    [Theory]
    [InlineData(0)] // 缺帧头
    [InlineData(4)]
    [InlineData(7)]
    public void 缓冲不足帧头_返回NeedMoreData(int buffered)
    {
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        var result = MultiplayerProtocol.ParseFrame(frame.AsSpan(0, Math.Min(buffered, frame.Length)));
        Assert.Equal(FrameParseStatus.NeedMoreData, result.Status);
    }

    [Fact]
    public void 缓冲不足整帧_返回NeedMoreData()
    {
        byte[] frame = MultiplayerProtocol.Encode(new TapMessage(1, TapSide.Left, 5));
        var result = MultiplayerProtocol.ParseFrame(frame.AsSpan(0, frame.Length - 1));
        Assert.Equal(FrameParseStatus.NeedMoreData, result.Status);
    }

    [Fact]
    public void 魔数错误_返回Invalid()
    {
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        frame[0] = 0x00;
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void 版本不匹配_返回Invalid()
    {
        // ADR-005：protocolVersion 不匹配即拒绝，不做向前兼容
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        frame[2] = (byte)(MultiplayerProtocol.ProtocolVersion + 1);
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void payloadLen超上限_返回Invalid()
    {
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        var writer = new SpanWriterShim();
        writer.WriteU32(frame, MultiplayerProtocol.MaxPayloadBytes + 1);
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void 未知消息类型_返回Invalid_扩展段未实现不得解析()
    {
        byte[] frame = MultiplayerProtocol.Encode(new HeartbeatMessage());
        frame[3] = 0x10; // 扩展保留段（礼物/BR 未来玩法）
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void Hello_超长显示名_解码拒绝()
    {
        string tooLong = new('猫', HelloMessage.DisplayNameMaxBytes + 1);
        var frame = MultiplayerProtocol.Encode(new HelloMessage(tooLong, string.Empty));
        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void Hello_非法UTF8字节_解码拒绝不抛异常()
    {
        // 手工构造 Hello 帧：payload = [名字长=2][0xE4][0xB8]——UTF8 三字节序列被截断 → 严格解码必须拒绝
        var frame = new byte[MultiplayerProtocol.HeaderSize + 3];
        frame[0] = (byte)(MultiplayerProtocol.Magic & 0xFF);
        frame[1] = (byte)(MultiplayerProtocol.Magic >> 8);
        frame[2] = MultiplayerProtocol.ProtocolVersion;
        frame[3] = (byte)MsgType.Hello;
        frame[4] = 3; // payloadLen
        frame[5] = 2; // 名字字节长 = 2
        frame[6] = 0xE4; // "中"的 UTF8 首字节
        frame[7] = 0xB8; // 截断：缺第三字节 → 非法序列

        Assert.Equal(FrameParseStatus.Invalid, MultiplayerProtocol.ParseFrame(frame).Status);
    }

    [Fact]
    public void 帧缓冲分片到达_拼接后可完整解析()
    {
        // 网络分片：一半一半两次 ParseFrame，第二次应完整解析
        byte[] frame = MultiplayerProtocol.Encode(new TapMessage(2, TapSide.Left, 99));
        var first = MultiplayerProtocol.ParseFrame(frame.AsSpan(0, 5));
        Assert.Equal(FrameParseStatus.NeedMoreData, first.Status);

        var second = MultiplayerProtocol.ParseFrame(frame);
        Assert.Equal(FrameParseStatus.Ok, second.Status);
    }

    [Fact]
    public void 常量_与ADR005一致()
    {
        Assert.Equal(0x4243, MultiplayerProtocol.Magic);
        Assert.Equal(1, MultiplayerProtocol.ProtocolVersion);
        Assert.Equal(8, MultiplayerProtocol.HeaderSize);
        Assert.Equal(256 * 1024, MultiplayerProtocol.MaxPayloadBytes);
        Assert.Equal(6, HelloMessage.RoomCodeLength);
    }

    // ── GDD-09 §4：物种字节（CAT_STATE v3 / Welcome 名册 species）────────

    [Fact]
    public void CatState_物种字节_往返保真()
    {
        foreach (byte species in new byte[] { CatSpeciesByte.Cat, CatSpeciesByte.Dino })
        {
            var cat = Assert.IsType<CatStateMessage>(Roundtrip(new CatStateMessage(3, 0.5, 0.5, CatStyleByte.Chibi, species)));
            Assert.Equal(species, cat.SpeciesId);
        }
    }

    [Fact]
    public void CatState_旧客户端短payload18字节_回退clay与cat()
    {
        // GDD-09 §4 新旧互操作：v1 payload（无 style/species）→ 解码不失败，回退 clay + cat
        var payload = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10), 0.75);

        var cat = CatStateMessage.Decode(payload);

        Assert.NotNull(cat);
        Assert.Equal(CatStyleByte.Clay, cat.StyleId);
        Assert.Equal(CatSpeciesByte.Cat, cat.SpeciesId);
    }

    [Fact]
    public void CatState_风格包期v2payload19字节_物种回退cat()
    {
        // GDD-08 时代 payload（只有 style 尾字节）：物种缺省 = cat
        var payload = new byte[19];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10), 0.75);
        payload[18] = CatStyleByte.Papercut;

        var cat = CatStateMessage.Decode(payload);

        Assert.NotNull(cat);
        Assert.Equal(CatStyleByte.Papercut, cat.StyleId);
        Assert.Equal(CatSpeciesByte.Cat, cat.SpeciesId);
    }

    [Fact]
    public void CatState_非法物种字节_收敛cat不断连()
    {
        // GDD-09 §4：非法 species（>1）→ 回退 cat 渲染并记日志（协调器侧），绝不因坏字节断开
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10), 0.75);
        payload[18] = CatStyleByte.Chibi;
        payload[19] = 99;

        var cat = CatStateMessage.Decode(payload);

        Assert.NotNull(cat);
        Assert.Equal(CatStyleByte.Chibi, cat.StyleId);
        Assert.Equal(CatSpeciesByte.Cat, cat.SpeciesId);
    }

    [Fact]
    public void CatState_payloadVersion_升级为v3()
    {
        Assert.Equal(3, CatStateMessage.PayloadVersion);
        Assert.Equal(19, CatStateMessage.V2PayloadBytes);
    }

    [Fact]
    public void CatSpeciesByte_枚举白名单_全枚举合法超界非法()
    {
        Assert.Equal(0, CatSpeciesByte.Cat);
        Assert.Equal(1, CatSpeciesByte.Dino);
        Assert.Equal(2, CatSpeciesByte.Agua); // GDD-09 §7：第三物种（sprite 路径）
        for (byte b = 0; b <= CatSpeciesByte.Max; b++)
        {
            Assert.True(CatSpeciesByte.IsValid(b));
            Assert.Equal(b, CatSpeciesByte.Normalize(b));
        }

        Assert.False(CatSpeciesByte.IsValid(3));
        Assert.False(CatSpeciesByte.IsValid(255));
        Assert.Equal(CatSpeciesByte.Cat, CatSpeciesByte.Normalize(3));
        Assert.Equal(CatSpeciesByte.Cat, CatSpeciesByte.Normalize(255));
    }

    [Fact]
    public void Welcome_物种字节_往返保真_名册顺序不变()
    {
        var message = new WelcomeMessage(7, "X9T4CB",
        [
            new RosterEntry(1, "布丁", CatSpeciesByte.Cat),
            new RosterEntry(3, "糯米", CatSpeciesByte.Dino),
            new RosterEntry(6, "年糕"),
        ]);

        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(message));
        Assert.Equal(3, welcome.Members.Count);
        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[0].Species);
        Assert.Equal(CatSpeciesByte.Dino, welcome.Members[1].Species);
        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[2].Species); // 缺省参数 = 猫
    }

    [Fact]
    public void Welcome_旧服务器payload无species_解码回退cat不失败()
    {
        // 手工构造 GDD-09 之前的 Welcome payload：[u16 yourId][u8 len][房码][u8 count] + (id,name)*count
        var writer = new LegacyWelcomeWriter(4, "A7K2MQ", [("布丁", 1), ("糯米团", 2)]);
        byte[] legacyFrame = writer.Encode();

        FrameParseResult result = MultiplayerProtocol.ParseFrame(legacyFrame);
        Assert.Equal(FrameParseStatus.Ok, result.Status);
        var welcome = Assert.IsType<WelcomeMessage>(result.Message);
        Assert.Equal((ushort)4, welcome.YourId);
        Assert.Equal(2, welcome.Members.Count);
        Assert.All(welcome.Members, m => Assert.Equal(CatSpeciesByte.Cat, m.Species));
    }

    [Fact]
    public void Welcome_非法物种字节_收敛cat()
    {
        var message = new WelcomeMessage(7, "X9T4CB", [new RosterEntry(1, "布丁", 99)]);
        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(message));
        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[0].Species);
    }

    // ── E9：宠物名（名册名字复用 DisplayName，≤24B 新发；旧 48B 仍可收）────

    [Fact]
    public void Welcome_名册名字UTF8与表情_往返保真()
    {
        // 8 个 CJK = 24B（新客户端发送上限） + 6 个表情 = 24B + 普通中文名
        var message = new WelcomeMessage(2, "A7K2MQ",
        [
            new RosterEntry(1, "一二三四五六七八", CatSpeciesByte.Cat),
            new RosterEntry(3, "🐱🐱🐱🐱🐱🐱", CatSpeciesByte.Agua),
            new RosterEntry(5, "糯米团", CatSpeciesByte.Dino),
        ]);

        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(message));
        Assert.Equal("一二三四五六七八", welcome.Members[0].PetName);
        Assert.Equal("🐱🐱🐱🐱🐱🐱", welcome.Members[1].PetName);
        Assert.Equal("糯米团", welcome.Members[2].PetName);
        Assert.Equal("糯米团", welcome.Members[2].DisplayName); // 别名同源，线上字段不变
    }

    [Fact]
    public void MemberJoin_宠物名UTF8_往返保真()
    {
        var join = Assert.IsType<MemberJoinMessage>(Roundtrip(new MemberJoinMessage(5, "阿瓜二世")));
        Assert.Equal("阿瓜二世", join.DisplayName);
    }

    [Fact]
    public void Hello_新客户端24字节名字_旧解码布局仍可解析()
    {
        // 新客户端发送 24B 宠物名；旧客户端/旧服务器读取 [len][utf8] 的布局完全不变，
        // 也不需要理解"宠物名"语义——这就是不 bump 版本、不新增线字段的兼容方案。
        string name = "一二三四五六七八"; // 8 CJK = 24B
        byte[] frame = MultiplayerProtocol.Encode(new HelloMessage(name, "A7K2MQ"));

        Assert.Equal(FrameParseStatus.Ok, MultiplayerProtocol.ParseFrame(frame).Status);
        var decoded = Assert.IsType<HelloMessage>(MultiplayerProtocol.ParseFrame(frame).Message);
        Assert.Equal(name, decoded.DisplayName);
        Assert.Equal(24, Encoding.UTF8.GetByteCount(decoded.DisplayName));
        Assert.Equal(PetNameLimits.MaxUtf8Bytes, HelloMessage.PetNameMaxBytes);
    }

    [Fact]
    public void Hello_旧客户端48字节显示名_新解码仍接受不崩()
    {
        // 旧客户端仍可能发 48B 显示名：协议层必须继续收（否则旧→新断连），
        // 展示层由 Core.PetNameRules 截断到 12 字素 / 24B。
        string oldName = new('猫', 16); // 16 CJK = 48B
        var hello = Assert.IsType<HelloMessage>(Roundtrip(new HelloMessage(oldName, string.Empty)));

        Assert.Equal(oldName, hello.DisplayName);
        Assert.Equal(HelloMessage.DisplayNameMaxBytes, Encoding.UTF8.GetByteCount(hello.DisplayName));
    }

    [Fact]
    public void Welcome_旧服务器无名字新字段_新解码按既有名字字段回退不崩()
    {
        // 旧服务器 Welcome 里名字就是普通显示名；新客户端把它当宠物名使用（无额外字段可缺）。
        var writer = new LegacyWelcomeWriter(4, "A7K2MQ", [("旧客户端猫猫", 1)]);
        FrameParseResult result = MultiplayerProtocol.ParseFrame(writer.Encode());

        Assert.Equal(FrameParseStatus.Ok, result.Status);
        var welcome = Assert.IsType<WelcomeMessage>(result.Message);
        Assert.Equal("旧客户端猫猫", welcome.Members[0].PetName);
        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[0].Species);
    }

    /// <summary>测试侧旧格式 Welcome 编码器（GDD-09 之前：成员条目无 species 字节）。</summary>
    private sealed class LegacyWelcomeWriter(ushort yourId, string roomCode, IReadOnlyList<(string Name, ushort Id)> members)
    {
        public byte[] Encode()
        {
            var payload = new List<byte> { (byte)(yourId & 0xFF), (byte)(yourId >> 8) };
            byte[] room = Encoding.UTF8.GetBytes(roomCode);
            payload.Add((byte)room.Length);
            payload.AddRange(room);
            payload.Add((byte)members.Count);
            foreach ((string name, ushort id) in members)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                payload.Add((byte)(id & 0xFF));
                payload.Add((byte)(id >> 8));
                payload.Add((byte)nameBytes.Length);
                payload.AddRange(nameBytes);
            }

            var frame = new byte[MultiplayerProtocol.HeaderSize + payload.Count];
            frame[0] = (byte)(MultiplayerProtocol.Magic & 0xFF);
            frame[1] = (byte)(MultiplayerProtocol.Magic >> 8);
            frame[2] = MultiplayerProtocol.ProtocolVersion;
            frame[3] = (byte)MsgType.Welcome;
            uint len = (uint)payload.Count;
            frame[4] = (byte)len;
            frame[5] = (byte)(len >> 8);
            frame[6] = (byte)(len >> 16);
            frame[7] = (byte)(len >> 24);
            payload.CopyTo(frame, MultiplayerProtocol.HeaderSize);
            return frame;
        }
    }

    private static ProtocolMessage Roundtrip(ProtocolMessage message)
    {
        byte[] frame = MultiplayerProtocol.Encode(message);
        FrameParseResult result = MultiplayerProtocol.ParseFrame(frame);
        Assert.Equal(FrameParseStatus.Ok, result.Status);
        Assert.Equal(frame.Length, result.FrameLength);
        Assert.NotNull(result.Message);
        return result.Message;
    }

    /// <summary>测试侧写 u32（SpanCodec 的 SpanWriter 是 internal，这里同构重写避免为测试放宽可见性）。</summary>
    private sealed class SpanWriterShim
    {
        public void WriteU32(byte[] buffer, uint value)
        {
            buffer[4] = (byte)value;
            buffer[5] = (byte)(value >> 8);
            buffer[6] = (byte)(value >> 16);
            buffer[7] = (byte)(value >> 24);
        }
    }
}
