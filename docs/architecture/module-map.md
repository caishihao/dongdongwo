# 模块图与关键接口

> 配套 architecture.md；接口为 C# 草案（final 以代码评审为准）。所有服务注册于 DI，UI 只依赖 App 层接口。

## 1. 模块依赖图

```
BongoCat.UI (Views/ViewModels)
   │  依赖
   ▼
BongoCat.App ──实现──▶ ◆ 接口（定义于 App/Core，实现在 Platform）
   │                      ◆ IKeyHookService      (Platform: KeyPollingService)
   ▼                      ◆ ITransparentWindow   (Platform: DwmTransparentWindow)
BongoCat.Core              ◆ IAudioService        (Platform: NAudioService)
   ▲                      ◆ ISaveService         (Platform: JsonSaveService)
   │                      ◆ INetLobby            (Platform: TcpLobbyService)
BongoCat.Platform ────────┘
BongoCat.Protocol (INetLobby 消息类型)
```

## 2. 接口草案

### 输入

```csharp
/// 全局键盘捕获（ADR-002：GetAsyncKeyState 轮询实现）
public interface IKeyHookService : IDisposable
{
    /// 按键按下（上升沿）事件。参数为扫描码/VK，玩法不依赖具体键值。
    event Action<KeyPressEvent>? KeyPressed;
    bool IsRunning { get; }
    void Start();
    void Stop();          // 空闲挂起 / 设置中关闭监听
}

public readonly record struct KeyPressEvent(int VirtualKey, long TimestampTicks);
```

### 猫编排

```csharp
/// 猫行为导演：输入事件 → 动画 + 音效 + 经济钩子
public interface ICatAnimationController
{
    /// side: 0=左爪, 1=右爪（交替策略由实现内部维护）
    void PlayTap(int side);
    /// punch 帧替换与回弹时长来自 GameConfig.HitDurationMs
    void PlayPunch(int side);
    void SetOutfit(OutfitSnapshot outfit);  // 装扮变更整体刷新
    void SetMirrored(bool mirrored);        // FlipVisuals
    event Action<int>? TapStarted;          // UI Storyboard 订阅
}
```

```csharp
/// 交替策略 + 敲击编排（App 层，纯逻辑可测）
public sealed class CatDirector
{
    public CatDirector(IKeyHookService keys, ICatAnimationController anim,
                       IAudioService audio, TimeProvider time, GameConfig config);
    /// 每次按键：交替左右爪 → anim.PlayTap → audio.PlayDrum
    public void OnKeyPressed(KeyPressEvent e);
}
```

### 窗口

```csharp
/// 透明置顶窗口能力（ADR-001）
public interface ITransparentWindowService
{
    TransparencyMode ActiveMode { get; }   // Dwm | ColorKey
    bool TryEnableDwmTransparency(nint hwnd);
    void SetTopmost(nint hwnd, bool topmost);
    void HideFromTaskbar(nint hwnd);
    void KeepInBounds(Rect workArea, ref Point pos); // 越界回中
}

public enum TransparencyMode { Dwm, ColorKey }
```

### 音频（ADR-006）

```csharp
public interface IAudioService
{
    /// 从指定随机池播放（洗牌袋），线程安全非阻塞
    void Play(string soundGroup);
    float MasterVolume { get; set; }   // 0~1
    bool Muted { get; set; }
}

public interface ISoundLibrary
{
    void LoadAll(string assetDir);                 // 启动预解码
    IReadOnlyList<SoundId> GetPool(string group);  // drum_beat / drum_minus / emote_xxx
}
```

### 存档（ADR-004）

```csharp
public interface ISaveService
{
    SaveModel Current { get; }             // 内存模型，变更即打脏
    void MarkDirty();
    Task SaveAsync(CancellationToken ct);  // 原子写入 + .bak 轮换
    Task<LoadResult> LoadAsync(CancellationToken ct); // 损坏→备份→安全模式
    event Action? Saved;
}

public sealed class SaveModel
{
    public int SchemaVersion { get; set; } = SaveMigrator.LatestVersion;
    public uint Checksum { get; set; }
    public EconomyState Economy { get; set; } = new();
    public List<ItemStack> Inventory { get; set; } = [];
    public DexState Dex { get; set; } = new();
    public DropTimerState DropTimers { get; set; } = new();
    /// 猫位置（主屏工作区比例 0~1，跨分辨率还原）
    public PointF CatPositionRel { get; set; }
    public bool CatMirrored { get; set; }
    public List<PresetModel> Presets { get; set; } = [];
}
```

### 经济 / 库存 / 兑换

```csharp
public interface IEconomyService
{
    long Coins { get; }
    bool TrySpend(long amount);
    void Grant(long amount, CoinSource source);
    event Action<long>? CoinsChanged;
}

public interface IInventoryService
{
    IReadOnlyCollection<ItemStack> Items { get; }
    /// 掉落/宝箱/兑换产物入库。返回是否成功（上限/校验）。
    bool Add(ItemStack stack, ItemSource source);
    bool Remove(ItemId id, int count);
    bool Equip(ItemId id, SlotKind slot);  // hat/skin/bongos
    event Action? InventoryChanged;
}

public interface IExchangeService
{
    /// 同品质 N 件低阶物品 → 1 件高阶（N 由 ItemBalance 配置）
    ExchangeResult TryExchange(IReadOnlyList<ItemId> inputs);
    AutoExchangePolicy AutoPolicy { get; set; }  // AutoExchanger
}

public interface IDropService   // 挂机掉落心跳
{
    void Tick(TimeSpan elapsed);            // Host 定时器每 60s
    ChestRoll RollChest(ChestType type);
    event Action<ItemStack>? ItemDropped;   // IMessenger 广播给 UI
}

public interface IDexService    // BongoDex 式图鉴
{
    void MarkSeen(ItemId id);
    DexProgress Progress { get; }
    bool IsCollected(ItemId id);
}
```

### 通行证（V3 骨架先行）

```csharp
public interface IPawPassService
{
    SeasonInfo CurrentSeason { get; }
    PassXpState Xp { get; }                 // 免费轨 F / 高级轨 P
    RewardGrant? Claim(int track, int tier); // 里程碑领取，幂等
}
```

### 联机（V3，ADR-005）

```csharp
public interface INetLobby : IAsyncDisposable
{
    Task<HostResult> HostAsync(int port, CancellationToken ct);
    Task<JoinResult> JoinAsync(string roomCode, CancellationToken ct);
    event Action<RemoteCat>? CatJoined;
    event Action<TapEvent>? RemoteTap;
    event Action<GiftEvent>? GiftReceived;
    Task SendTapAsync(int side);            // 客户端仅发意图
    Task SendGiftAsync(Guid toCat, ItemId item);
    bool IsHosting { get; }
}

public sealed record RemoteCat(Guid CatId, string Name, PointF PosRel, OutfitSnapshot Outfit);
```

## 3. 关键流程落位

| 流程 | 编排者 | 涉及接口 |
|---|---|---|
| 按键敲鼓 | `CatDirector` | IKeyHookService → ICatAnimationController + IAudioService |
| 拖动 + 位置记忆 | `CatWindowVM` | ITransparentWindowService → ISaveService |
| 挂机掉落 | `DropService` | TimeProvider + ISaveService → IInventoryService |
| 宝箱开启 | `ChestViewModel` | IDropService.RollChest → IInventoryService.Add |
| 品质合成 | `ExchangeViewModel` | IExchangeService → IInventoryService |
| 装扮预设 | `PresetService` | IInventoryService → ICatAnimationController.SetOutfit → ISaveService |

## 4. 测试映射

| 接口 | 假实现 | 验证点 |
|---|---|---|
| IKeyHookService | `FakeKeySource`（测试直接灌事件） | 交替爪序列、去抖 |
| IAudioService | 计数器假实现 | 每键恰一音效、静音不触发 |
| ISaveService | 内存假实现 + 真实现的文件级测试 | 原子性、损坏回退、迁移链 |
| IExchangeService | 真 Core 实现 | 同品质校验、品质阶跃、数量守恒 |
| IDropService | 注入 FakeTimeProvider | 心跳累积、离线补偿上限 |
| INetLobby | 本地双端 loopback 集成测试 | 帧编解码往返、断线踢出 |
