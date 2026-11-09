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
    public void CatState_新payload字节数_20字节_payloadVersion3()
    {
        Assert.Equal(20, CatStateMessage.PayloadBytes);
        Assert.Equal(19, CatStateMessage.V2PayloadBytes);
        Assert.Equal(18, CatStateMessage.LegacyPayloadBytes);
        Assert.Equal(3, CatStateMessage.PayloadVersion);
        Assert.Equal(20, new CatStateMessage(1, 0.5, 0.5, CatStyleByte.Chibi, CatSpeciesByte.Dino).MeasurePayload());
    }

    [Fact]
    public void CatSpeciesByte_枚举白名单_全枚举合法超界非法()
    {
        Assert.Equal(0, CatSpeciesByte.Cat);
        Assert.Equal(1, CatSpeciesByte.Dino);
        for (byte b = 0; b <= CatSpeciesByte.Max; b++)
        {
            Assert.True(CatSpeciesByte.IsValid(b));
            Assert.Equal(b, CatSpeciesByte.Normalize(b));
        }

        Assert.False(CatSpeciesByte.IsValid(2));
        Assert.False(CatSpeciesByte.IsValid(255));
        Assert.Equal(CatSpeciesByte.Cat, CatSpeciesByte.Normalize(2));
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

