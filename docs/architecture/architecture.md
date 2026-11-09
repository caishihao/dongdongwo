# BongoCat 复刻 — 总体架构

> 作者：程基岩（engineering-lead）｜状态：Draft v1（待主理人评审）
> 依据：`analysis/bongocat-analysis.md`（静态分析，仅机制观察）；平台决策：C# WPF / .NET 8 / Windows 10-11
> 铁律：全部原创实现与原创素材；不反编译抄代码、不提取原作资产。

## 1. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│  UI 层 (BongoCat.UI)        WPF 视图 + MVVM，不持有游戏状态  │
│  Views / ViewModels / 资源字典 / 设置面板 / 图鉴 / 联机大厅    │
├─────────────────────────────────────────────────────────────┤
│  App 层 (BongoCat.App)      用例编排、状态机、动画控制器      │
│  CatDirector / EconomyService / InventoryService / Exchange  │
│  PawPassService / DropService / LobbySession                │
├─────────────────────────────────────────────────────────────┤
│  Core 层 (BongoCat.Core)    纯逻辑，零 WPF/Win32 依赖，可测   │
│  领域模型（Cat/Item/Quality/Economy）· 规则 · 存档模型 · 协议 │
├─────────────────────────────────────────────────────────────┤
│  基础设施 (BongoCat.Platform)                              │
│  KeyHook(轮询) · TransparentWindow · Audio · Save(IO) · Net │
└─────────────────────────────────────────────────────────────┘
依赖方向：UI → App → Core ← Platform（Platform 实现 Core/App 定义的接口）
```

### 分层规则

| 层 | 允许引用 | 禁止 |
|---|---|---|
| Core | BCL only | WPF、Win32 P/Invoke、文件 IO（IO 走接口） |
| App | Core | 直接 P/Invoke（一律经 Platform 接口） |
| Platform | Core、App（实现接口） | 反向依赖 UI |
| UI | App、Core | 绕过 App 直接调 Platform 服务 |

### 项目结构（单 sln 多项目）

```
BongoCat.sln
├── src/BongoCat.Core/          net8.0           领域模型与规则
├── src/BongoCat.App/           net8.0           用例服务、状态机
├── src/BongoCat.Platform/      net8.0-windows   Win32/音频/存档/网络实现
├── src/BongoCat.UI/            net8.0-windows   WPF 应用入口（exe）
├── src/BongoCat.Protocol/      net8.0           联机消息协议（Core 亦可，独立便于 Net 测试）
├── tests/BongoCat.Core.Tests/  xunit            领域规则测试（兑换、经济、掉落）
├── tests/BongoCat.App.Tests/   xunit            用例编排测试（伪造 Platform 接口）
└── tests/BongoCat.Protocol.Tests/ xunit         协议编解码往返测试
```

**理由**：
1. **多项目而非单项目多目录**：Core 不引用 `WindowsDesktop.Sdk`，强制"逻辑无 UI 依赖"的编译期约束，比目录约定可靠；测试工程无需加载 WPF 程序集。
2. UI 是唯一 exe；Platform 以 DI 装配注入，未来如需换引擎/出 Mac 版，只换 Platform + UI。
3. Protocol 独立是为了联机两端（V3）用同一套编解码，避免序列化漂移。

### MVVM 选型：CommunityToolkit.Mvvm

- `ObservableObject` / `[ObservableProperty]` / `[RelayCommand]` 源生成器，零反射、AOT 友好。
- `IMessenger` 用于跨 ViewModel 弱引用事件（如"物品掉落 → UI 提示"）。
- 不引入 Prism/ReactiveUI：依赖少、团队心智负担小，此体量足够。

### 依赖注入：Microsoft.Extensions.DependencyInjection

- `Generic Host`（`Host.CreateApplicationBuilder`）承载：DI 容器 + 日志（`ILogger`）+ 配置 + 生命周期管理（轮询定时器、存档定时落盘）。
- 装配在 `UI/App.xaml.cs` 的 `ConfigureServices`：Platform 实现 → App 服务 → ViewModel 全部 transient/singleton 显式注册。
- 所有跨层服务只依赖接口（见 `module-map.md`）。

## 2. 关键数据流

### 2.1 按键 → 猫敲鼓（主循环，60fps 目标）

```
KeyPollTimer(Platform, 8ms 活动 / 50ms 空闲)
  → IKeyHookService.KeyPresssed 事件
  → CatDirector.OnKeyPress(key)
      · 决定左/右爪（交替队列）
      · 触发 ICatAnimationController.PlayTap(side)
      · 触发 IAudioService.PlayDrum(随机池)
  → UI 层由动画控制器驱动 Storyboard/WriteableBitmap 帧替换
```

空闲降频：`_idleAfterSeconds`（默认 10s）无按键 → 轮询间隔升至空闲档；任一按键恢复活动档。（对应原作机制观察，见分析报告 §2。）

### 2.2 存档流

内存模型（Core）→ `ISaveService.SaveAsync()` → JSON 序列化 → 临时文件写入 → `File.Replace` 原子替换（ADR-004）。挂机掉落心跳由 `DropService` 每 60s 累计，落盘节流为最多 30s 一次 + 退出时强制保存。

### 2.3 联机流（V3）

`INetLobby`（TCP，ADR-005）→ `LobbySession` 编排 → 对端猫状态（位置/装扮/敲击事件）→ UI 渲染为"影子猫"。权威方为房主，客户端仅上报意图。

## 3. 渲染架构（详见 ADR-003）

- 主窗口：`HwndSource` + DWM `ExtendFrameIntoClientArea` 透明（色键兜底），`WS_EX_TOPMOST | WS_EX_TOOLWINDOW`（不进任务栏）、`WS_EX_NOACTIVATE`（抢焦点会导致钩子轮询玩家键盘时窗口闪动）。
- 猫 = 部件图层（body / pawL / pawR / hat / bongosL / bongosR），每部件一张预解码 `BitmapImage`，tap 时爪部件用 `TranslateTransform/RotateTransform` 动画 + punch 帧替换，合成走 WPF 视觉树（Dev未卡顿时无需 WriteableBitmap；如剖析发现瓶颈再切换，ADR-003 给出升级路径）。
- 呼吸浮动（CatBobbing）、镜像（Flip）用 RenderTransform，不重绘位图。

## 4. 目录与命名约定

- 路径作用域编码标准（本文即控制清单，逐条可执行）：
  - `Core/`：不允许 `async void`；所有随机必须可注入 `Random`（种子可复现，便于测试与将来回放）。
  - `Platform/`：P/Invoke 全部集中 `Native/` 子目录，每个 API 声明 `SetLastError = true` 并记录返回码；热路径（轮询回调内）零分配——不用 LINQ、不装箱事件参数（用 struct + 手写事件或通道）。
  - `App/`：服务只通过构造函数注入；禁止 `DateTime.Now` 直接取时——用 `TimeProvider`（.NET 8 内置），挂机掉落计时可测。
  - `UI/`：ViewModel 不引用 `Visual`/`BitmapImage` 类型；图像路径以 string/ItemId 传递，View 侧用转换器解析。
  - TODO 格式：`// TODO(user): 描述`，禁止裸 TODO。
- 配置常量（轮询间隔、掉落心跳、音量等）集中在 `Core/Config/GameConfig.cs`，数据驱动。

## 5. 与原作机制的对应表（可追溯）

| 原作机制（分析报告 §2） | 本方案落点 | ADR |
|---|---|---|
| DWM 透明窗口 + 色键兜底 | `TransparentWindowService` | ADR-001 |
| GetAsyncKeyState 轮询 + 双频省电 | `KeyPollingService` | ADR-002 |
| 部件化渲染 + punch 帧 | `CatRenderView` 图层合成 | ADR-003 |
| 位置按屏幕比例持久化 | `SaveModel.CatPositionRel`（0~1 浮点） | ADR-004 |
| 左右爪交替（协程） | Storyboard 动画 + `_hitDuration` 配置 | — |
| 音效随机池 | `IAudioService.PlayDrum(pool)` | ADR-006 |
| Steam 库存 | 本地 JSON 存档 | ADR-004 |
| Steam 大厅 | 局域网 TCP 大厅 | ADR-005 |

## 6. 里程碑对齐

- MVP（E1）：透明窗口 + 轮询钩子 + 敲击动画 + 音效 + 位置记忆。
- V1（E2-E4）：装扮/图鉴/经济/挂机掉落。
- V2（E5-E7）：兑换、预设、主题、教程。
- V3（E8）：局域网多人（+ 通行证框架 PawPassService 骨架先行）。
