using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Input;
using BongoCat.Core.Net;
using BongoCat.Core.Save;
using BongoCat.Protocol;

namespace BongoCat.App;

/// <summary>
/// 多人联机编排（E8 用例层）：本机敲击/拖动/换装 → 上行广播（节流），远端事件 → 下行分发（UI 播放）。
/// 纯逻辑零 WPF 依赖：UI 层订阅本类事件后自行跳 Dispatcher（分层纪律同 TrayCommandController）。
/// </summary>
/// <remarks>
/// 节流规则：本机敲击上行最多 30 事件/秒（滑动 1s 窗口，超出静默丢弃——敲击是瞬时语义，
/// 缓存重放没有意义；服务器侧另有 50 帧/s 硬限，UI 手速极限远低于两者，此节流是双保险）。
/// 位置广播：拖动结束时一次（10Hz 连续节流留给未来拖动中实时同步，V1 只在 DragCompleted 上报）。
/// 装扮广播（E8 追加）：加入成功立即一次；换装事件（InventoryService.Changed）经 500ms 防抖
/// 合并后再发——背包连点换装只广播最终态；timer 由注入的 TimeProvider 创建，可测。
/// </remarks>
public sealed class MultiplayerCoordinator : IDisposable
{
    /// <summary>本机敲击上行节流上限（事件/秒）。</summary>
    public const int MaxOutgoingTapsPerSecond = 30;

    /// <summary>换装广播防抖窗口（用户拍板：换装后 500ms 合并一次）。</summary>
    public static readonly TimeSpan OutfitDebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly IMultiplayerClient _client;
    private readonly ISaveService _save;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly CosmeticCatalog? _catalog;
    private readonly StyleCatalog? _styles;
    private readonly SpeciesCatalog? _species;

    private readonly Dictionary<ushort, string> _members = new();
    private readonly Dictionary<ushort, (double X, double Y)> _remotePositions = new();
    private readonly Dictionary<ushort, (string Hat, string Skin, string Bongos)> _remoteCosmetics = new();
    private readonly Dictionary<ushort, byte> _remoteStyles = new();
    private readonly Dictionary<ushort, byte> _remoteSpecies = new();

    // 远端装扮原始三元组（speciesOnly 重清洗依据：物种同步可能晚于装扮到达，物种到位后重洗一次）
    private readonly Dictionary<ushort, (string Hat, string Skin, string Bongos)> _remoteRawCosmetics = new();

    // 敲击节流（滑动 1s 窗口）
    private long _throttleWindowStartTicks;
    private int _throttleCount;

    // 换装广播防抖（500ms 合并；timer 由 TimeProvider 创建可注入）
    private ITimer? _outfitDebounceTimer;
    private bool _outfitDirty;

    private string _roomCode = string.Empty;

    public MultiplayerCoordinator(
        IMultiplayerClient client,
        ISaveService save,
        TimeProvider? time = null,
        Action<string>? log = null,
        CosmeticCatalog? catalog = null,
        StyleCatalog? styles = null,
        SpeciesCatalog? species = null)
    {
        _client = client;
        _save = save;
        _time = time ?? TimeProvider.System;
        _log = log;
        _catalog = catalog;
        _styles = styles;
        _species = species;

        _client.StateChanged += OnStateChanged;
        _client.Joined += OnJoined;
        _client.MemberJoined += OnMemberJoined;
        _client.MemberLeft += OnMemberLeft;
        _client.RemoteCatState += OnRemoteCatState;
        _client.RemoteTap += OnRemoteTap;
        _client.CosmeticSyncReceived += OnCosmeticSync;
        _client.JoinRejected += OnJoinRejected;
        _client.Disconnected += OnDisconnected;
    }

    /// <summary>当前连接状态（UI 状态栏）。</summary>
    public MpConnectionState State => _client.State;

    /// <summary>本机成员 id（服务器分配；未加入为 0）。</summary>
    public ushort MyMemberId => _client.MyMemberId;

    /// <summary>存档模型（多人窗口回填 Mp* 配置初值）。</summary>
    public SaveModel SaveCurrent => _save.Current;

    /// <summary>当前房间码（未加入为空）。</summary>
    public string RoomCode => _roomCode;

    /// <summary>成员表快照（含自己；id → 显示名）。</summary>
    public IReadOnlyDictionary<ushort, string> Members
    {
        get
        {
            lock (_members)
            {
                return new Dictionary<ushort, string>(_members);
            }
        }
    }

    /// <summary>取远端猫位置（无记录返回 null；UI 创建远程猫窗口时查询初始位）。</summary>
    public (double X, double Y)? TryGetRemotePosition(ushort memberId)
        => _remotePositions.TryGetValue(memberId, out var pos) ? pos : null;

    /// <summary>取远端猫装扮三元组（清洗后的 artKey；无记录返回 null = 全默认渲染）。</summary>
    public (string Hat, string Skin, string Bongos)? TryGetRemoteCosmetics(ushort memberId)
        => _remoteCosmetics.TryGetValue(memberId, out var cosmetics) ? cosmetics : null;

    /// <summary>
    /// 取远端猫风格字节（GDD-08；无记录返回 null = 旧客户端/未同步，按 clay 渲染）。
    /// 字节已由协议解码与目录双重防御收敛（非法值 → clay）。
    /// </summary>
    public byte? TryGetRemoteStyle(ushort memberId)
        => _remoteStyles.TryGetValue(memberId, out byte style) ? style : null;

    /// <summary>
    /// 取远端猫物种字节（GDD-09 §4；无记录返回 null = 旧客户端/未同步，按 cat 渲染）。
    /// 字节已由协议解码与目录双重防御收敛（非法值 → cat）。
    /// </summary>
    public byte? TryGetRemoteSpecies(ushort memberId)
        => _remoteSpecies.TryGetValue(memberId, out byte species) ? species : null;

    // ── 下行事件（UI 订阅；触发线程 = 客户端后台线程）────────────────────

    /// <summary>连接状态变化。</summary>
    public event Action<MpConnectionState>? StateChanged;

    /// <summary>成员表变化（加入成功/上下线）。</summary>
    public event Action? MembersChanged;

    /// <summary>远端成员上线（UI 建远程猫窗口；参数 = 成员 id + 显示名）。</summary>
    public event Action<ushort, string>? RemoteMemberJoined;

    /// <summary>远端成员下线（UI 渐隐关窗）。</summary>
    public event Action<ushort>? RemoteMemberLeft;

    /// <summary>远端敲击（UI 播对应远程猫的敲击动画 + 音效）。</summary>
    public event Action<ushort, TapSide>? RemoteTapReceived;

    /// <summary>远端猫位置更新（UI 移动其远程猫窗口）。</summary>
    public event Action<ushort, double, double>? RemoteCatStateReceived;

    /// <summary>远端装扮三元组到达（已过目录清洗；UI 换绑远程猫渲染，未知 id 已回退默认）。</summary>
    public event Action<ushort, string, string, string>? CosmeticSyncReceived;

    /// <summary>远端风格字节到达（GDD-08；已收敛合法值；UI 按字节重载远程猫渲染模板）。</summary>
    public event Action<ushort, byte>? RemoteStyleReceived;

    /// <summary>远端物种字节到达（GDD-09 §4；已收敛合法值；UI 按字节重载远程猫形体模板）。</summary>
    public event Action<ushort, byte>? RemoteSpeciesReceived;

    /// <summary>状态栏/气泡文案（重连中、成员变更、被拒原因等人话）。</summary>
    public event Action<string>? NotificationRaised;

    /// <summary>加入被拒（UI 关房间面板并展示原因）。</summary>
    public event Action<ByeReason>? JoinRejected;

    // ── 上行动作 ────────────────────────────────────────────────────────

    /// <summary>
    /// 加入或创建房间（roomCode 空 = 创建）。成功后把服务器地址/显示名/房间码持久化，
    /// 下次打开多人窗口自动回填（MpLastRoomCode 仅便利字段）。
    /// </summary>
    public void Join(string serverAddress, string displayName, string roomCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        SaveModel save = _save.Current;
        // E9：名册名字（宠物名）统一清洗到 UTF-8 ≤24 字节；旧调用方传显示名也兼容
        displayName = PetNameRules.Sanitize(displayName, PetNameRules.DefaultNameForSpecies(save.SpeciesId));
        roomCode = roomCode.Trim().ToUpperInvariant();

        save.MpServerAddress = serverAddress.Trim();
        save.MpDisplayName = displayName;
        _save.Save();

        lock (_members)
        {
            _members.Clear();
        }

        _remotePositions.Clear();
        _remoteCosmetics.Clear();
        _remoteRawCosmetics.Clear();
        _remoteStyles.Clear();
        _remoteSpecies.Clear();
        _client.Start(serverAddress, displayName, roomCode);
        _log?.Invoke($"[Mp] 请求加入：{save.MpServerAddress} 房间={ifEmpty(roomCode, "(新建)")}");

        static string ifEmpty(string value, string fallback) => value.Length == 0 ? fallback : value;
    }

    /// <summary>主动离开房间（断开连接；远程猫由 UI 层按状态事件清理）。</summary>
    public void Leave()
    {
        _roomCode = string.Empty;
        _outfitDirty = false;
        _outfitDebounceTimer?.Dispose();
        _outfitDebounceTimer = null;
        lock (_members)
        {
            _members.Clear();
        }

        _remotePositions.Clear();
        _remoteCosmetics.Clear();
        _remoteRawCosmetics.Clear();
        _remoteStyles.Clear();
        _remoteSpecies.Clear();
        _client.Stop();
        MembersChanged?.Invoke();
    }

    /// <summary>
    /// 换装事件入口（App.xaml.cs 订阅 InventoryService.Changed 接线）：
    /// 置脏标记 + 启动 500ms 防抖 timer；窗口已在跑则不重启——到点统一读存档当前值发送，
    /// 连续换装天然合并为最终态一次广播（用户拍板：换装后 500ms 合并一次）。
    /// </summary>
    public void NotifyOutfitChanged()
    {
        if (State != MpConnectionState.Joined)
        {
            return; // 未在房间：换装不产生任何网络流量
        }

        _outfitDirty = true;
        if (_outfitDebounceTimer is not null)
        {
            return; // 防抖窗口已在倒计时：本次变更并入（无需重启）
        }

        _outfitDebounceTimer = _time.CreateTimer(
            _ => FlushOutfitBroadcast(),
            null,
            OutfitDebounceInterval,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>防抖到点：把存档当前装备三元组清洗后广播（幂等可重入）。</summary>
    private void FlushOutfitBroadcast()
    {
        _outfitDebounceTimer?.Dispose();
        _outfitDebounceTimer = null;
        if (!_outfitDirty)
        {
            return;
        }

        _outfitDirty = false;
        SendCurrentOutfit();
    }

    /// <summary>读取存档当前装备三元组，清洗后广播（加入成功与换装防抖共用同一发送路径）。</summary>
    private void SendCurrentOutfit()
    {
        if (State != MpConnectionState.Joined)
        {
            return;
        }

        SaveModel save = _save.Current;
        // 自己的存档同样过目录清洗（防御历史残留 id），发送值与渲染值同源
        byte mySpecies = CurrentSpeciesByte();
        string hat = NormalizeArtKey(save.EquippedHat, ItemSlot.Hat, mySpecies);
        string skin = NormalizeArtKey(save.EquippedSkin, ItemSlot.Skin, mySpecies);
        string bongos = NormalizeArtKey(save.EquippedBongos, ItemSlot.Bongos, mySpecies);
        _client.SendCosmetics(hat, skin, bongos);
        _log?.Invoke($"[Mp] 装扮广播：hat='{hat}' skin='{skin}' bongos='{bongos}'");
    }

    /// <summary>本机一次有效敲击（CatDirector.TapStarted 转发；App.xaml.cs 接线）。</summary>
    public void NotifyLocalTap(PawSide side)
    {
        if (State != MpConnectionState.Joined)
        {
            return;
        }

        // 节流：滑动 1s 窗口内最多 30 事件，超出静默丢弃（敲击是瞬时语义，缓存重放无意义）
        long now = _time.GetTimestamp();
        if (now - _throttleWindowStartTicks >= TimeSpan.FromSeconds(1).Ticks)
        {
            _throttleWindowStartTicks = now;
            _throttleCount = 0;
        }

        if (_throttleCount >= MaxOutgoingTapsPerSecond)
        {
            return;
        }

        if (_client.SendTap(side == PawSide.Left ? TapSide.Left : TapSide.Right))
        {
            _throttleCount++;
        }
    }

    /// <summary>本机拖动结束（MainWindow.DragCompleted 转发）：广播自己当前归一化位置 + 风格字节。</summary>
    public void NotifyLocalDragCompleted()
    {
        if (State != MpConnectionState.Joined)
        {
            return;
        }

        SendCurrentCatState();
    }

    /// <summary>
    /// 本机风格变更（托盘"形象"/首启选择器确认后由 App 接线转发）：立即重发 CAT_STATE
    /// （GDD-08 §4：style 变更触发 CAT_STATE 重发，与 outfit 变更同通道；位置取存档当前值）。
    /// </summary>
    public void NotifyStyleChanged()
    {
        if (State != MpConnectionState.Joined)
        {
            return; // 未在房间：风格变更不产生任何网络流量（加入时 OnJoined 会全量重发）
        }

        SendCurrentCatState();
    }

    /// <summary>读存档当前位置 + 风格/物种字节发送 CAT_STATE（拖动与风格/物种变更共用同一上行路径）。</summary>
    private void SendCurrentCatState()
    {
        SaveModel save = _save.Current;
        byte styleByte = CurrentStyleByte();
        byte speciesByte = CurrentSpeciesByte();
        _client.SendCatState(save.CatPositionRelX, save.CatPositionRelY, styleByte, speciesByte);
        _log?.Invoke($"[Mp] 猫状态广播：pos=({save.CatPositionRelX:F2},{save.CatPositionRelY:F2}) style={styleByte} species={speciesByte}");
    }

    /// <summary>本机风格字节（存档 CatStyleId → styles.json ByteId；未知 id 回退 clay=0）。</summary>
    private byte CurrentStyleByte() => _styles?.ToByte(_save.Current.CatStyleId) ?? CatStyleByte.Clay;

    /// <summary>本机物种字节（存档 SpeciesId → species.json ByteId；未知 id 回退 cat=0，GDD-09 §4）。</summary>
    private byte CurrentSpeciesByte() => _species?.ToByte(_save.Current.SpeciesId) ?? CatSpeciesByte.Cat;

    /// <summary>名册名字清洗：空/超长/控制符 → 物种默认名（旧客户端 48 字节显示名也能安全展示）。</summary>
    private static string SanitizeRosterName(string? displayName, byte speciesByte)
        => PetNameRules.Sanitize(displayName, PetNameRules.DefaultNameForSpecies(CatSpeciesByte.Normalize(speciesByte)));

    /// <summary>
    /// E9 本地改名通知：联机中更新自己的成员表条目并刷新 UI。协议没有改名消息——
    /// 对端在下次进入房间收到新名册快照时生效（避免新消息类型踢掉旧客户端，见文档）。
    /// </summary>
    public void NotifyPetNameChanged()
    {
        if (State != MpConnectionState.Joined)
        {
            return;
        }

        string name = PetNameRules.Sanitize(
            _save.Current.PetName, PetNameRules.DefaultNameForSpecies(_save.Current.SpeciesId));
        lock (_members)
        {
            _members[_client.MyMemberId] = name;
        }

        MembersChanged?.Invoke();
        _log?.Invoke($"[Mp] 本地改名「{name}」；对端将在下次进入房间时看到新名字");
    }

    // ── 客户端事件 → 状态维护 + 下行转发 ────────────────────────────────

    private void OnStateChanged(MpConnectionState state)
    {
        if (state == MpConnectionState.Joined)
        {
            return; // Joined 时机由 OnJoined（拿到房间码）统一通知
        }

        StateChanged?.Invoke(state);
        if (state == MpConnectionState.Reconnecting)
        {
            NotificationRaised?.Invoke("连接断开，正在重连…");
            lock (_members)
            {
                _members.Clear();
            }

            _remotePositions.Clear();
            _remoteCosmetics.Clear();
            _remoteRawCosmetics.Clear();
            _remoteStyles.Clear();
            _remoteSpecies.Clear();
            MembersChanged?.Invoke();
        }
        else if (state == MpConnectionState.Failed)
        {
            lock (_members)
            {
                _members.Clear();
            }

            _remoteCosmetics.Clear();
            _remoteRawCosmetics.Clear();
            _remoteStyles.Clear();
            _remoteSpecies.Clear();
            MembersChanged?.Invoke();
        }
        else if (state == MpConnectionState.Disconnected)
        {
            _roomCode = string.Empty;
            StateChanged?.Invoke(state);
        }
    }

    private void OnJoined(string roomCode, IReadOnlyList<RosterEntry> roster)
    {
        _roomCode = roomCode;
        _save.Current.MpLastRoomCode = roomCode;
        _save.Save();

        lock (_members)
        {
            _members.Clear();
            string selfName = string.IsNullOrWhiteSpace(_save.Current.PetName)
                ? _save.Current.MpDisplayName
                : _save.Current.PetName;
            _members[_client.MyMemberId] = PetNameRules.Sanitize(
                selfName, PetNameRules.DefaultNameForSpecies(_save.Current.SpeciesId));
            foreach (RosterEntry entry in roster)
            {
                _members[entry.MemberId] = SanitizeRosterName(entry.DisplayName, entry.Species);
                // 名册已带物种快照（服务器从 CatState 观测缓存）；先填，之后 CatState 再覆盖
                _remoteSpecies[entry.MemberId] = entry.Species;
            }
        }

        StateChanged?.Invoke(MpConnectionState.Joined);
        MembersChanged?.Invoke();
        NotificationRaised?.Invoke($"已加入房间 {roomCode}");

        // 已在房间内的成员逐个上桌（远程猫窗口由 UI 建窗；远端首个 CatState 会归位）
        foreach (RosterEntry entry in roster)
        {
            RemoteMemberJoined?.Invoke(entry.MemberId, PetNameRules.Sanitize(
                entry.DisplayName, PetNameRules.DefaultNameForSpecies(entry.Species)));
        }

        // E8 追加：加入成功立即广播自己的装扮三元组（等价于"背包关闭后的初始同步"，
        // 让新加入者尽快看到彼此的猫；此后换装走 500ms 防抖增量）
        SendCurrentOutfit();

        // GDD-08：加入成功立即广播自己的位置 + 风格字节（远端建窗即按正确风格渲染）
        SendCurrentCatState();
    }

    private void OnMemberJoined(ushort memberId, string displayName)
    {
        // 新成员 species 尚未随 MemberJoin 下发（服务器在首个 CatState 观测），先按猫回退
        string name = SanitizeRosterName(displayName, CatSpeciesByte.Cat);
        lock (_members)
        {
            _members[memberId] = name;
        }

        MembersChanged?.Invoke();
        RemoteMemberJoined?.Invoke(memberId, name);
        NotificationRaised?.Invoke($"「{name}」加入了房间");
    }

    private void OnMemberLeft(ushort memberId)
    {
        string? name = null;
        lock (_members)
        {
            if (_members.TryGetValue(memberId, out string? found))
            {
                name = found;
                _members.Remove(memberId);
            }
        }

        _remotePositions.Remove(memberId);
        _remoteCosmetics.Remove(memberId);
        _remoteRawCosmetics.Remove(memberId);
        _remoteStyles.Remove(memberId);
        _remoteSpecies.Remove(memberId);
        MembersChanged?.Invoke();
        RemoteMemberLeft?.Invoke(memberId);
        if (!string.IsNullOrEmpty(name))
        {
            NotificationRaised?.Invoke($"「{name}」离开了");
        }
    }

    private void OnRemoteCatState(ushort memberId, double x, double y, byte styleByte, byte speciesByte)
    {
        _remotePositions[memberId] = (x, y);

        // GDD-08 §4 / GDD-09 §4 防御：style/species 字节已在协议解码收敛，这里再做目录级兜底
        //（目录缺该字节 → clay/cat），随后缓存 + 转发 UI（远程猫按字节重载模板）
        byte style = _styles is { } styles && styles.TryGetByByte(styleByte, out _)
            ? styleByte
            : CatStyleByte.Clay;
        if (style != styleByte)
        {
            _log?.Invoke($"[Mp] 远端 style 字节 {styleByte} 无法解析，回退 clay");
        }

        byte species = _species is { } sp && sp.TryGetByByte(speciesByte, out _)
            ? speciesByte
            : CatSpeciesByte.Cat;
        if (species != speciesByte)
        {
            _log?.Invoke($"[Mp] 远端 species 字节 {speciesByte} 无法解析，回退 cat");
        }

        bool speciesChanged = !_remoteSpecies.TryGetValue(memberId, out byte previousSpecies) ||
                              previousSpecies != species;
        _remoteStyles[memberId] = style;
        _remoteSpecies[memberId] = species;
        RemoteCatStateReceived?.Invoke(memberId, x, y);
        RemoteStyleReceived?.Invoke(memberId, style);
        RemoteSpeciesReceived?.Invoke(memberId, species);
        if (speciesChanged)
        {
            // E9：成员列表「名字 + 物种」随物种首次到达/变化刷新
            MembersChanged?.Invoke();
        }

        // GDD-09：物种到位后按新物种重洗装扮缓存（dino_default 等 speciesOnly 物品，
        // 装扮同步早于物种同步到达时曾被按默认物种清洗——现在重洗一次收敛正确渲染）
        if (_remoteRawCosmetics.TryGetValue(memberId, out var raw))
        {
            RenormalizeRemoteCosmetics(memberId, raw);
        }
    }

    private void OnRemoteTap(ushort memberId, TapSide side, long timestampMs)
        => RemoteTapReceived?.Invoke(memberId, side);

    /// <summary>
    /// 远端装扮到达：先过本地目录清洗（槽位校验 + 未知 id 置空 = 回退默认渲染 +
    /// speciesOnly 物种校验，GDD-09），再缓存（新成员建窗后可查）并转发 UI。
    /// 空串 = 默认黏土色，原样保留。
    /// </summary>
    private void OnCosmeticSync(ushort memberId, string hatId, string skinId, string bongosId)
    {
        var raw = (hatId, skinId, bongosId);
        _remoteRawCosmetics[memberId] = raw; // 原始值缓存：物种晚到时重清洗依据
        RenormalizeRemoteCosmetics(memberId, raw);
    }

    /// <summary>
    /// 按"当前已知远端物种"清洗远端装扮三元组并转发（物种未知按 cat 防御——
    /// dino_default 早到会被暂时置空，物种到达时经 OnRemoteCatState 重洗收敛）。
    /// </summary>
    private void RenormalizeRemoteCosmetics(ushort memberId, (string Hat, string Skin, string Bongos) raw)
    {
        byte species = _remoteSpecies.TryGetValue(memberId, out byte s) ? s : CatSpeciesByte.Cat;
        string hat = NormalizeArtKey(raw.Hat, ItemSlot.Hat, species);
        string skin = NormalizeArtKey(raw.Skin, ItemSlot.Skin, species);
        string bongos = NormalizeArtKey(raw.Bongos, ItemSlot.Bongos, species);

        _remoteCosmetics[memberId] = (hat, skin, bongos);
        CosmeticSyncReceived?.Invoke(memberId, hat, skin, bongos);
    }

    /// <summary>
    /// artKey 防御清洗：空串（默认/未装备）原样通过；目录存在且槽位匹配且物种限定
    /// 匹配（speciesOnly 空 = 全物种；非空须与 species 一致，GDD-09 §1）→ 原值；
    /// 未知 id / 槽位错配 / 物种不符 → 空串（渲染层回退默认黏土色——伪造 id 无害化，不崩溃）。
    /// 目录未注入（单测简化场景）时按"信任输入"处理。
    /// </summary>
    private string NormalizeArtKey(string artKey, ItemSlot slot, byte species)
    {
        if (artKey.Length == 0)
        {
            return artKey;
        }

        return _catalog is { } catalog && catalog.TryGet(artKey, out CosmeticItem item) && item.Slot == slot &&
               (item.SpeciesOnly.Length == 0 ||
                (_species is { } sp && sp.TryGetByByte(species, out SpeciesPack? pack) &&
                 string.Equals(pack.Id, item.SpeciesOnly, StringComparison.Ordinal)))
            ? artKey
            : string.Empty;
    }

    private void OnJoinRejected(ByeReason reason)
    {
        _roomCode = string.Empty;
        string text = reason switch
        {
            ByeReason.RoomNotFound => "房间不存在：检查房间码是否正确",
            ByeReason.RoomFull => "房间已满（上限 8 人）",
            ByeReason.ServerBusy => "服务器繁忙：房间数已达上限，稍后再试",
            ByeReason.ServerShutdown => "服务器正在维护，稍后再试",
            ByeReason.InvalidField => "显示名或房间码非法",
            _ => $"加入被拒（{reason}）",
        };

        _log?.Invoke($"[Mp] 加入被拒：{reason}");
        JoinRejected?.Invoke(reason);
        NotificationRaised?.Invoke(text);
    }

    private void OnDisconnected(string reason)
        => NotificationRaised?.Invoke($"连接断开（{reason}），正在重连…");

    public void Dispose()
    {
        _outfitDebounceTimer?.Dispose();
        _client.StateChanged -= OnStateChanged;
        _client.Joined -= OnJoined;
        _client.MemberJoined -= OnMemberJoined;
        _client.MemberLeft -= OnMemberLeft;
        _client.RemoteCatState -= OnRemoteCatState;
        _client.RemoteTap -= OnRemoteTap;
        _client.CosmeticSyncReceived -= OnCosmeticSync;
        _client.JoinRejected -= OnJoinRejected;
        _client.Disconnected -= OnDisconnected;
        _client.Dispose();
    }
}
