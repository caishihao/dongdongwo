using BongoCat.App.Inventory;
using BongoCat.App.Styles;
using BongoCat.Core.Input;
using BongoCat.Core.Net;
using BongoCat.Core.Save;
using BongoCat.Protocol;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// MultiplayerCoordinator 单测（E8）：敲击上行节流 30/s、加入持久化 v8 字段、
/// 成员表维护、远端事件转发。用 Fake 客户端（不建真实 TCP）。
/// GDD-08 追加：风格字节随 CAT_STATE 上行/下行、远端风格缓存与非法值回退。
/// </summary>
public class MultiplayerCoordinatorTests
{
    private readonly FakeMultiplayerClient _client = new();
    private readonly FakeSaveService _save = new();
    private readonly MutableTimeProvider _time = new();
    private readonly MultiplayerCoordinator _mp;

    public MultiplayerCoordinatorTests()
    {
        // 注入真实 styles.json 目录（与生产同一份数据，风格字节映射按真实配置验证）
        _mp = new MultiplayerCoordinator(
            _client, _save, _time, null, null, StyleCatalogLoader.LoadFromAssembly(_ => { }));
    }

    [Fact]
    public void Join_把服务器地址与显示名持久化_并启动客户端()
    {
        _mp.Join("1.2.3.4:9437", "布丁", "A2C4E6");

        Assert.Equal("1.2.3.4:9437", _save.Current.MpServerAddress);
        Assert.Equal("布丁", _save.Current.MpDisplayName);
        Assert.True(_client.Started);
        Assert.Equal("A2C4E6", _client.LastRoomCode);
    }

    [Fact]
    public void Joined事件_写入房间码与成员表_含自己_并落MpLastRoomCode()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", [new RosterEntry(2, "糯米"), new RosterEntry(5, "年糕")]);

        Assert.Equal("X9T4CB", _mp.RoomCode);
        Assert.Equal("X9T4CB", _save.Current.MpLastRoomCode);
        Assert.Equal(3, _mp.Members.Count);
        Assert.Equal("布丁", _mp.Members[_client.MyMemberId]);   // 自己在名册
        Assert.Equal("糯米", _mp.Members[2]);
        Assert.Equal("年糕", _mp.Members[5]);
    }

    [Fact]
    public void 敲击上行_节流30事件每秒_超出静默丢弃()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        int sent = 0;
        for (int i = 0; i < 40; i++)
        {
            _mp.NotifyLocalTap(PawSide.Left);
        }

        sent = _client.TapsSent;
        Assert.Equal(MultiplayerCoordinator.MaxOutgoingTapsPerSecond, sent);

        // 窗口滚动（>1s）后恢复发送
        _time.Advance(TimeSpan.FromSeconds(2));
        _mp.NotifyLocalTap(PawSide.Right);
        Assert.Equal(sent + 1, _client.TapsSent);
    }

    [Fact]
    public void 未加入房间_敲击与位置广播直接丢弃()
    {
        _mp.NotifyLocalTap(PawSide.Left);
        _mp.NotifyLocalDragCompleted();

        Assert.Equal(0, _client.TapsSent);
        Assert.Equal(0, _client.CatStatesSent);
    }

    [Fact]
    public void 拖动完成_广播存档中的归一化位置()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);
        _save.Current.CatPositionRelX = 0.42;
        _save.Current.CatPositionRelY = 0.17;

        _mp.NotifyLocalDragCompleted();

        // 第 1 次 = 加入成功时的初始 CAT_STATE（GDD-08：位置 + 风格字节一起广播），
        // 第 2 次 = 本次拖动完成的位置广播
        Assert.Equal(2, _client.CatStatesSent);
        Assert.Equal(0.42, _client.LastCatStateX);
        Assert.Equal(0.17, _client.LastCatStateY);
    }

    [Fact]
    public void 风格变更_立即重发CatState_携带存档风格字节()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);
        int baseline = _client.CatStatesSent;

        _save.Current.CatStyleId = "chibi";
        _mp.NotifyStyleChanged();

        Assert.Equal(baseline + 1, _client.CatStatesSent);
        Assert.Equal(CatStyleByte.Chibi, _client.LastStyleByte);
    }

    [Fact]
    public void 风格变更_未加入房间_不产生网络流量()
    {
        _mp.NotifyStyleChanged();

        Assert.Equal(0, _client.CatStatesSent);
    }

    [Fact]
    public void 远端风格字节_缓存并转发事件_非法字节回退clay()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        var received = new List<(ushort MemberId, byte Style)>();
        _mp.RemoteStyleReceived += (id, style) => received.Add((id, style));

        // 合法字节（Q版）
        _client.FireCatState(7, 0.5, 0.4, CatStyleByte.Chibi);
        Assert.Equal(CatStyleByte.Chibi, _mp.TryGetRemoteStyle(7));
        Assert.Equal(CatStyleByte.Chibi, received[^1].Style);

        // 非法字节（>5）→ 回退 clay，不断连（GDD-08 §4）
        _client.FireCatState(7, 0.5, 0.4, 99);
        Assert.Equal(CatStyleByte.Clay, _mp.TryGetRemoteStyle(7));
        Assert.Equal(CatStyleByte.Clay, received[^1].Style);
    }

    [Fact]
    public void 加入成功_立即广播CatState_风格来自存档()
    {
        _save.Current.CatStyleId = "papercut";
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        Assert.Equal(1, _client.CatStatesSent);
        Assert.Equal(CatStyleByte.Papercut, _client.LastStyleByte);
    }

    [Fact]
    public void 存档风格id未知_ToByte回退clay字节()
    {
        _save.Current.CatStyleId = "not_a_style";
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        Assert.Equal(CatStyleByte.Clay, _client.LastStyleByte);
    }

    [Fact]
    public void 远端成员上下线_成员表与事件同步维护()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        var joined = new List<(ushort, string)>();
        var left = new List<ushort>();
        _mp.RemoteMemberJoined += (id, name) => joined.Add((id, name));
        _mp.RemoteMemberLeft += id => left.Add(id);

        _client.FireMemberJoined(7, "花卷");
        Assert.Equal("花卷", _mp.Members[7]);
        Assert.Single(joined);

        _client.FireMemberLeft(7);
        Assert.False(_mp.Members.ContainsKey(7));
        Assert.Single(left);
    }

    [Fact]
    public void 断线重连中_成员表清空_重新加入后重建()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", [new RosterEntry(2, "糯米")]);

        _client.FireStateChanged(MpConnectionState.Reconnecting);
        Assert.Empty(_mp.Members);

        // 重连成功 → Welcome 重建名册
        _client.FireJoined("X9T4CB", [new RosterEntry(2, "糯米")]);
        Assert.Equal(2, _mp.Members.Count);
    }

    [Fact]
    public void 加入被拒_状态清空_通知文案与原因码对应()
    {
        var notifications = new List<string>();
        _mp.NotificationRaised += text => notifications.Add(text);

        _mp.Join("1.2.3.4", "布丁", "WRONG");
        _client.FireJoined("WRONG", []); // 先进房再被踢的场景防御：直接验证被拒路径
        _client.FireJoinRejected(ByeReason.RoomNotFound);

        Assert.Contains(notifications, t => t.Contains("房间不存在", StringComparison.Ordinal));
        Assert.Empty(_mp.Members);
        Assert.Equal(string.Empty, _mp.RoomCode);
    }

    [Fact]
    public void Leave_主动离开_清空成员表并停客户端()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", [new RosterEntry(2, "糯米")]);

        _mp.Leave();

        Assert.False(_client.Started);
        Assert.Empty(_mp.Members);
        Assert.Equal(string.Empty, _mp.RoomCode);
    }

    // ── E8 追加：装扮三元组同步 ────────────────────────────────────────

    [Fact]
    public void 加入成功_立即广播存档装扮三元组_目录清洗后发送()
    {
        var catalog = ItemCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        save.Current.EquippedHat = "hat_beret";
        save.Current.EquippedSkin = "skin_orange";
        save.Current.EquippedBongos = "bongos_gold";
        var mp = new MultiplayerCoordinator(client, save, _time, null, catalog);
        mp.Join("1.2.3.4", "布丁", string.Empty);

        client.FireJoined("X9T4CB", []);
        Assert.Equal(1, client.CosmeticsSent);
        Assert.Equal("hat_beret", client.LastHat);
        Assert.Equal("skin_orange", client.LastSkin);
        Assert.Equal("bongos_gold", client.LastBongos);
    }

    [Fact]
    public void 换装广播_500ms防抖合并_到点发送最终态一次()
    {
        var catalog = ItemCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        var mp = new MultiplayerCoordinator(client, save, _time, null, catalog);
        save.Current.EquippedHat = "hat_beret";
        mp.Join("1.2.3.4", "布丁", string.Empty);

        client.FireJoined("X9T4CB", []);
        int initial = client.CosmeticsSent;

        // 连续两次换装（背包连点）：都在防抖窗口内 → 到点合并为一次最终态
        save.Current.EquippedHat = "hat_santa";
        mp.NotifyOutfitChanged();
        save.Current.EquippedHat = "hat_graduation";
        mp.NotifyOutfitChanged();
        Assert.Equal(initial, client.CosmeticsSent); // 未到点：零流量

        _time.FireDueTimers();
        Assert.Equal(initial + 1, client.CosmeticsSent);
        Assert.Equal("hat_graduation", client.LastHat); // 最终态
    }

    [Fact]
    public void 未在房间_换装不产生任何网络流量()
    {
        _save.Current.EquippedHat = "hat_beret";
        _mp.NotifyOutfitChanged();
        _time.FireDueTimers();

        Assert.Equal(0, _client.CosmeticsSent);
    }

    [Fact]
    public void 远端装扮_合法id保留_未知id与槽位错配回退默认_空串保留()
    {
        var catalog = ItemCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        var mp = new MultiplayerCoordinator(client, save, _time, null, catalog);
        mp.Join("1.2.3.4", "布丁", string.Empty);
        client.FireJoined("X9T4CB", []);

        var received = new List<(ushort Id, string Hat, string Skin, string Bongos)>();
        mp.CosmeticSyncReceived += (id, hat, skin, bongos) => received.Add((id, hat, skin, bongos));

        client.FireCosmeticSync(2, "hat_beret", "skin_cow", "bongos_gold");
        client.FireCosmeticSync(3, "hat_不存在", "skin_orange", "bongos_wood");
        client.FireCosmeticSync(4, "skin_orange", string.Empty, string.Empty); // hat 槽塞了 skin id → 错配

        Assert.Equal((2, "hat_beret", "skin_cow", "bongos_gold"), (received[0].Id, received[0].Hat, received[0].Skin, received[0].Bongos));
        // 未知 id → 空串（默认黏土色渲染），合法部分保留
        Assert.Equal((3, string.Empty, "skin_orange", "bongos_wood"), (received[1].Id, received[1].Hat, received[1].Skin, received[1].Bongos));
        // 槽位错配 → 置空；空串（默认）原样保留
        Assert.Equal((4, string.Empty, string.Empty, string.Empty), (received[2].Id, received[2].Hat, received[2].Skin, received[2].Bongos));

        // 缓存供建窗初装查询
        Assert.Equal(("hat_beret", "skin_cow", "bongos_gold"), mp.TryGetRemoteCosmetics(2));
        Assert.Null(mp.TryGetRemoteCosmetics(99));
    }

    [Fact]
    public void 成员离开或断线_装扮缓存同步清除()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);
        _client.FireCosmeticSync(2, "hat_beret", "skin_cow", "bongos_gold");
        Assert.NotNull(_mp.TryGetRemoteCosmetics(2));

        _client.FireMemberLeft(2);
        Assert.Null(_mp.TryGetRemoteCosmetics(2));

        _client.FireCosmeticSync(3, "hat_beret", "skin_cow", "bongos_gold");
        _client.FireStateChanged(MpConnectionState.Reconnecting);
        Assert.Null(_mp.TryGetRemoteCosmetics(3));
    }

    // ── GDD-09 §4：物种同步 ─────────────────────────────────────────────

    [Fact]
    public void 物种变更_立即重发CatState_携带存档物种字节()
    {
        var mp = CreateSpeciesAwareCoordinator();
        mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);
        int baseline = _client.CatStatesSent;

        _save.Current.SpeciesId = "dino";
        mp.NotifyStyleChanged();

        Assert.Equal(baseline + 1, _client.CatStatesSent);
        Assert.Equal(CatSpeciesByte.Dino, _client.LastSpeciesByte);
    }

    /// <summary>注入真实物种目录的协调器（物种字节映射按生产同一份 species.json 验证）。</summary>
    private MultiplayerCoordinator CreateSpeciesAwareCoordinator()
        => new(_client, _save, _time, null, null, null, SpeciesCatalogLoader.LoadFromAssembly(_ => { }));

    [Fact]
    public void 加入成功_立即广播CatState_物种来自存档()
    {
        // 独立 FakeClient：避免 fixture 共享协调器对同一 FireJoined 也反应、双写发送计数
        var localClient = new FakeMultiplayerClient();
        var mp = new MultiplayerCoordinator(localClient, _save, _time, null, null, null,
            SpeciesCatalogLoader.LoadFromAssembly(_ => { }));
        _save.Current.SpeciesId = "dino";
        mp.Join("1.2.3.4", "布丁", string.Empty);
        localClient.FireJoined("X9T4CB", []);

        Assert.Equal(1, localClient.CatStatesSent);
        Assert.Equal(CatSpeciesByte.Dino, localClient.LastSpeciesByte);
    }

    [Fact]
    public void 远端物种字节_缓存并转发事件_非法字节回退cat()
    {
        var mp = CreateSpeciesAwareCoordinator();
        mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        var received = new List<(ushort MemberId, byte Species)>();
        mp.RemoteSpeciesReceived += (id, species) => received.Add((id, species));

        // 合法字节（恐龙）
        _client.FireCatState(7, 0.5, 0.4, CatStyleByte.Chibi, CatSpeciesByte.Dino);
        Assert.Equal(CatSpeciesByte.Dino, mp.TryGetRemoteSpecies(7));
        Assert.Equal(CatSpeciesByte.Dino, received[^1].Species);

        // 非法字节（>1）→ 回退 cat，不断连（GDD-09 §4）
        _client.FireCatState(7, 0.5, 0.4, CatStyleByte.Chibi, 99);
        Assert.Equal(CatSpeciesByte.Cat, mp.TryGetRemoteSpecies(7));
        Assert.Equal(CatSpeciesByte.Cat, received[^1].Species);
    }

    [Fact]
    public void 远端物种未知_按cat防御_TryGetRemoteSpecies返回null()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);

        Assert.Null(_mp.TryGetRemoteSpecies(42)); // 未同步 = 旧客户端/未收到 CatState
    }

    [Fact]
    public void 远端装扮_dino_default物种未到先被清洗_物种到达后重洗收敛()
    {
        // 真实目录 + 物种目录（生产装配顺序）：加入房间后 OnJoined 先发装扮后发 CatState，
        // 接收端很可能先收装扮后收物种——speciesOnly 物品先被按 cat 清洗置空，
        // 物种到达时经缓存重洗一次收敛正确渲染（GDD-09 装扮同步兼容）
        var catalog = ItemCatalogLoader.LoadFromAssembly(_ => { });
        var speciesCatalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        save.Current.OwnedItems["dino_default"] = 1; // 远端玩家持有恐龙专属皮肤（接收端不校验持有，仅目录清洗）
        var mp = new MultiplayerCoordinator(client, save, _time, null, catalog, null, speciesCatalog);
        mp.Join("1.2.3.4", "布丁", string.Empty);
        client.FireJoined("X9T4CB", []);

        var received = new List<(ushort Id, string Skin)>();
        mp.CosmeticSyncReceived += (id, _, skin, _) => received.Add((id, skin));

        // 远端恐龙先发装扮（dino_default）——此刻物种未知，按 cat 防御置空
        client.FireCosmeticSync(2, string.Empty, "dino_default", string.Empty);
        Assert.Equal(string.Empty, received[^1].Skin);

        // 物种随后到达（CatState species=1）→ 缓存重洗，dino_default 收敛放行
        client.FireCatState(2, 0.5, 0.5, CatStyleByte.Clay, CatSpeciesByte.Dino);
        Assert.Equal("dino_default", received[^1].Skin);
        Assert.Equal((string.Empty, "dino_default", string.Empty), mp.TryGetRemoteCosmetics(2));
    }

    [Fact]
    public void 远端装扮_物种为cat时dino_default保持置空()
    {
        var catalog = ItemCatalogLoader.LoadFromAssembly(_ => { });
        var speciesCatalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        var mp = new MultiplayerCoordinator(client, save, _time, null, catalog, null, speciesCatalog);
        mp.Join("1.2.3.4", "布丁", string.Empty);
        client.FireJoined("X9T4CB", []);

        var received = new List<(ushort Id, string Skin)>();
        mp.CosmeticSyncReceived += (id, _, skin, _) => received.Add((id, skin));

        client.FireCatState(3, 0.5, 0.5, CatStyleByte.Clay, CatSpeciesByte.Cat);
        client.FireCosmeticSync(3, string.Empty, "dino_default", string.Empty);

        Assert.Equal(string.Empty, received[^1].Skin); // 猫收 dino_default = 物种不符 → 置空
    }

    // ── E9：宠物名（名册名字字段）与成员列表物种 ───────────────────────

    [Fact]
    public void 名册名字_超长控制符_接收侧清洗并按物种回退()
    {
        _mp.Join("1.2.3.4", "布丁", string.Empty);

        var joinedNames = new List<(ushort Id, string Name)>();
        _mp.RemoteMemberJoined += (id, name) => joinedNames.Add((id, name));

        // 旧客户端 48 字节显示名 + 控制符 + 表情；新接收侧截断到 24B / 12 字素
        _client.FireJoined("X9T4CB", [
            new RosterEntry(2, "  糯米\u0000团  ", CatSpeciesByte.Dino),
            new RosterEntry(5, "🐱🐱🐱🐱🐱🐱🐱", CatSpeciesByte.Agua),
            new RosterEntry(7, "   ", CatSpeciesByte.Dino),
        ]);

        Assert.Equal("糯米团", _mp.Members[2]);
        Assert.Equal("🐱🐱🐱🐱🐱🐱", _mp.Members[5]);
        Assert.Equal("小恐龙", _mp.Members[7]); // 空白名字 → 物种默认名
        Assert.Equal("糯米团", joinedNames.Single(n => n.Id == 2).Name);
        Assert.Equal("小恐龙", joinedNames.Single(n => n.Id == 7).Name);
    }

    [Fact]
    public void 成员物种_来自Welcome名册与CatState_缓存可查()
    {
        var speciesCatalog = SpeciesCatalogLoader.LoadFromAssembly(_ => { });
        var client = new FakeMultiplayerClient();
        var save = new FakeSaveService();
        var mp = new MultiplayerCoordinator(client, save, _time, null, null, null, speciesCatalog);
        mp.Join("1.2.3.4", "布丁", string.Empty);
        client.FireJoined("X9T4CB", [new RosterEntry(2, "糯米", CatSpeciesByte.Dino)]);

        Assert.Equal(CatSpeciesByte.Dino, mp.TryGetRemoteSpecies(2));

        client.FireCatState(2, 0.5, 0.5, CatStyleByte.Clay, CatSpeciesByte.Agua);
        Assert.Equal(CatSpeciesByte.Agua, mp.TryGetRemoteSpecies(2));
    }

    [Fact]
    public void 改名_房间内刷新本机成员表并触发MembersChanged_未连接无动作()
    {
        int changed = 0;
        _mp.MembersChanged += () => changed++;

        _save.Current.PetName = "小布丁";
        _mp.NotifyPetNameChanged();
        Assert.Equal(0, changed); // 未加入：无成员表可刷

        _mp.Join("1.2.3.4", "布丁", string.Empty);
        _client.FireJoined("X9T4CB", []);
        changed = 0;

        _save.Current.PetName = "小布丁";
        _mp.NotifyPetNameChanged();

        Assert.Equal(1, changed);
        Assert.Equal("小布丁", _mp.Members[_client.MyMemberId]);
    }

    /// <summary>可控 TimeProvider（节流窗口推进用；Core 规则：时间可注入）。
    /// GetTimestamp 同步重写为 UtcTicks（默认实现用真实 Stopwatch，注入即失效）；
    /// CreateTimer 返回手动触发的 ManualTimer（装扮防抖 500ms 由测试显式推进，不依赖真实时钟）。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>触发所有已到期的手动 timer（Advance 语义：时间推进后回调到点）。</summary>
        public void FireDueTimers()
        {
            ManualTimer[] snapshot;
            lock (_timers)
            {
                snapshot = [.. _timers];
            }

            foreach (ManualTimer timer in snapshot)
            {
                timer.Trigger();
            }
        }

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>手动触发的 ITimer（只支持单次回调语义，测试专用）。</summary>
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _fired;

        public void Trigger()
        {
            if (!_fired)
            {
                _fired = true;
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _fired = true; // Dispose 后 Trigger 不再触发

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>IMultiplayerClient 伪造：记录动作 + 手动触发事件（无真实网络）。</summary>
    private sealed class FakeMultiplayerClient : IMultiplayerClient
    {
        public bool Started { get; private set; }

        public string LastRoomCode { get; private set; } = string.Empty;

        public int TapsSent { get; private set; }

        public int CatStatesSent { get; private set; }

        public double LastCatStateX { get; private set; }

        public double LastCatStateY { get; private set; }

        public byte LastStyleByte { get; private set; }

        public byte LastSpeciesByte { get; private set; }

        public MpConnectionState State { get; private set; } = MpConnectionState.Disconnected;

        public ushort MyMemberId { get; private set; } = 1;

        public event Action<MpConnectionState>? StateChanged;
        public event Action<string, IReadOnlyList<RosterEntry>>? Joined;
        public event Action<ushort, string>? MemberJoined;
        public event Action<ushort>? MemberLeft;

#pragma warning disable CS0067 // 伪造实现：下行事件由测试触发器逐个 Fire，这三个用例不触发
        public event Action<ushort, double, double, byte, byte>? RemoteCatState;
        public event Action<ushort, TapSide, long>? RemoteTap;
        public event Action<ByeReason>? JoinRejected;
#pragma warning restore CS0067

        public event Action<ushort, string, string, string>? CosmeticSyncReceived;

#pragma warning disable CS0067
        public event Action<string>? Disconnected;
#pragma warning restore CS0067

        public int CosmeticsSent { get; private set; }

        public string LastHat { get; private set; } = string.Empty;

        public string LastSkin { get; private set; } = string.Empty;

        public string LastBongos { get; private set; } = string.Empty;

        public bool SendCosmetics(string hatId, string skinId, string bongosId)
        {
            CosmeticsSent++;
            LastHat = hatId;
            LastSkin = skinId;
            LastBongos = bongosId;
            return true;
        }

        public void Start(string serverAddress, string displayName, string roomCode)
        {
            Started = true;
            LastRoomCode = roomCode;
        }

        public void Stop() => Started = false;

        public bool SendTap(TapSide side)
        {
            TapsSent++;
            return true;
        }

        public bool SendCatState(double normX, double normY, byte styleByte, byte speciesByte)
        {
            CatStatesSent++;
            LastCatStateX = normX;
            LastCatStateY = normY;
            LastStyleByte = styleByte;
            LastSpeciesByte = speciesByte;
            return true;
        }

        // ── 测试触发器 ──

        public void FireJoined(string roomCode, IReadOnlyList<RosterEntry> roster)
        {
            MyMemberId = 1;
            State = MpConnectionState.Joined; // 与真实现一致：Welcome 即 Joined
            Joined?.Invoke(roomCode, roster);
        }

        public void FireMemberJoined(ushort id, string name) => MemberJoined?.Invoke(id, name);

        public void FireMemberLeft(ushort id) => MemberLeft?.Invoke(id);

        public void FireStateChanged(MpConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public void FireJoinRejected(ByeReason reason)
        {
            // 与真实现一致：SessionLoop 先 SetState(Failed) 再触发 JoinRejected
            State = MpConnectionState.Failed;
            StateChanged?.Invoke(MpConnectionState.Failed);
            JoinRejected?.Invoke(reason);
        }

        public void FireCosmeticSync(ushort id, string hat, string skin, string bongos)
            => CosmeticSyncReceived?.Invoke(id, hat, skin, bongos);

        public void FireCatState(ushort id, double x, double y, byte styleByte, byte speciesByte = CatSpeciesByte.Cat)
            => RemoteCatState?.Invoke(id, x, y, styleByte, speciesByte);

        public void Dispose()
        {
        }
    }
}
