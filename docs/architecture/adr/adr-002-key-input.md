# ADR-002 全局键盘捕获方案

状态：Proposed｜日期：2025｜决策人：程基岩

## 上下文
猫要在玩家打字/玩其他游戏时敲鼓，必须捕获**全局**键盘输入。原作观察到的机制：`WinKeyHook.GetAsyncKeyState(i4)->i2` + `BUTTONS[]`/`WasPressed[]` 状态数组 + `OnKeyPressed<int>` 事件，以及活动/空闲双频轮询（`_activePollIntervalMs`/`_idlePollIntervalMs`/`_idleAfterSeconds`）。注意：原作名为 Hook 实为轮询。

## 备选方案

### A. 低级键盘钩子 SetWindowsHookEx(WH_KEYBOARD_LL)
- 优点：事件驱动，零轮询 CPU；按键时序精确；还能区分按下/抬起。
- 缺点：
  1. **管理员权限窗口的按键收不到**（UIPI 隔离）——玩家用管理员身份玩游戏是常见场景，钩子静默失效，桌宠"死了"，且无报错，客服灾难。
  2. 回调有系统超时（默认 ~300ms 内必须返回，可被 `LowLevelHooksTimeout` 缩短甚至移除钩子）——回调里做任何重活都会掉钩子。
  3. 全局 DLL 注入观感差，**杀软误报率显著更高**（低级钩子是键盘记录器的标志行为，直接命中静态启发式规则）。

### B. GetAsyncKeyState 轮询 ✅
- 优点：
  1. **不受 UIPI 限制**——能读到管理员权限进程的按键状态（异步键状态表是系统级的）；
  2. 无回调超时问题，轮询线程崩了自己重启即可；
  3. 行为模式与"读状态"而非"拦截输入"，杀软启发式友好得多；
  4. 实现极简：一个 `DispatcherTimer`/专用线程循环 0x01~0xFE。
- 缺点：
  1. 轮询间隔 < 按键持续时间才不漏检（8ms 活动、50ms 空闲足够，最短按键 ~40ms）；极端快速敲击可能漏一两次——对"猫敲鼓"场景无感知损失（原作同样如此，且有 WasPressed 边沿检测缓解）；
  2. 常驻一个高频循环——用双频 + 空闲降频 + 停止条件（无按键且窗口隐藏时挂起）把空闲 CPU 压到 ~0。

## 决定
**方案 B**：`KeyPollingService`（后台线程 + `WaitHandle` 可暂停）轮询 `GetAsyncKeyState`。

具体参数（写入 `GameConfig`，可调）：
- 活动轮询间隔：**8ms**；空闲间隔：**50ms**；进入空闲条件：连续 **10s** 无键按下。
- 边沿检测：`WasPressed[i] = (state & 0x8000) != 0` 与上一帧比较，仅上升沿触发事件。
- 事件以 `struct KeyPressEvent` 经 `System.Threading.Channels.Channel<KeyPressEvent>` 投递到 UI 线程（热路径零分配，满足 core/ 热路径无 LINQ/无装箱规则）。
- 自适应休眠：轮询线程用 `SpinWait` 短档 + `Thread.Sleep(interval)`；空闲态改为 `Sleep(50)` 并允许被事件源唤醒。

## 后果
- 拿到的是"按下状态流"而非"键值流"：无法可靠知道**哪个键**被按（快速轮询下多数键可读，但不能保证）。当前玩法只需要"有按键 → 敲鼓"，**不依赖键值**；若未来要做"特定键触发特效"，需在 V3 评估补充 `RegisterHotKey`（不会误报、不越权）。
- 鼠标侧键/手柄是独立输入源：手柄走 SDL（V3 可选，接口已预留 `IInputSource`），鼠标点击猫本体走 WPF 事件。
- 轮询线程异常要有看门狗：连续 3 次异常后指数退避重启并上报日志。
- 杀软误报仍可能存在（全局输入监听是敏感行为），见 risks.md R4 缓解清单（代码签名、提交 Microsoft 误报申诉）。
