# E8 烟雾测试说明（docs/smoke-test-e8.md）

> 适用范围：E8 —— 云中继互联网多人。三块：RelayServer（独立 exe）+ 客户端多人
> （MultiplayerClientService / MultiplayerCoordinator / 远程猫窗口 / 多人窗口 / 托盘"多人"）
> + 存档 v7→v8（MpServerAddress / MpDisplayName / MpLastRoomCode）。
> 前置：E1–E7 烟测各检查点保持有效；本文只写 E8 增量。

## 1. 构建

```powershell
cd D:\BongoCat
dotnet build BongoCatReplica.sln          # 预期：0 警告 0 错误（含 RelayServer）
dotnet test                               # 预期：266/266 通过
```

E8 新增测试分布：

- **BongoCat.Protocol.Tests 28**（新项目）：全部 9 种消息 Encode→ParseFrame 往返保真（含
  归一化坐标双精度逐位保真、空名册、边界 0/1、装扮三元组含空串/超长 artKey 拒绝）、帧头
  布局对账 ADR-005（magic "BC"/version/little-endian payloadLen）、分片到达、NeedMoreData/
  Invalid 全分支（错魔数/错版本/payloadLen 超限/未知消息类型/超长显示名/非法 UTF8/心跳带
  payload）、常量守门。
- **BongoCat.RelayServer.Tests 19**（新项目，其中 4 个真实 TCP 回路）：房间创建（6 位无歧义码、
  字符集守门）、凭码加入、不存在/格式非法→RoomNotFound（不泄露存在性）、满员→RoomFull、
  并发上限→ServerBusy、空名→InvalidField、空闲回收（9 分钟不回收 / 11 分钟回收并释放码、
  有人永不回收）、成员 id 复用、会话限速（窗口内超限断开、窗口滚动恢复）、保活刷新、缓冲扩容
  至上限后拒绝；**回路测试**（loopback 真连接）：创建拿码→凭码加入→Tap/CosmeticSync 转发
  （装扮归属 id 服务器重写）→越界坐标服务器钳制→Bye 下线广播、加入不存在房间收 RoomNotFound
  且连接被关、满员第 3 人收 RoomFull、限速超限连接被断。
- **BongoCat.App.Tests 207**（原 191 + E8 新增 16）：协调器 14（Join 持久化 v8 字段、
  Welcome 建表含自己、敲击节流 30/s 窗口滚动、未加入丢弃、拖动广播存档位置、成员上下线、
  断线清表重建、被拒文案、Leave 清场、**装扮 5**：加入即广播目录清洗后三元组、换装 500ms
  防抖合并到点发最终态、未在房间零流量、远端装扮合法保留/未知回退/槽位错配回退、离场断线
  清缓存）+ 迁移 2（v7→v8 默认值、v8 幂等）。
- **BongoCat.Platform.Tests 12**（原 3 + E8 新增 9）：重连指数退避参数化 8 档（1s→…→30s 封顶）、
  心跳 20s 常量守门、真 JsonSaveService 回环扩展 2（v8 字段写读逐字段保真、v6 文件自动迁移 v8）。

## 2. 单机双开自测（无需云服务器，本机回路全流程）

### 2.1 启动中继（窗口 1）

```powershell
.\src\BongoCat.RelayServer\bin\Debug\net8.0\BongoCat.RelayServer.exe --port 9527
```

预期 stdout：

```
HH:mm:ss.fff [INFO] 中继服务器监听 0.0.0.0:9527（房间上限 64，单房 8 人，限速 50 帧/s，心跳超时 60s，空闲回收 10min）
HH:mm:ss.fff [INFO] 已注册 SIGTERM 优雅关闭
HH:mm:ss.fff [INFO] 启动完成，进程 <pid>，等待 Ctrl+C / SIGTERM 退出
```

### 2.2 双开客户端（窗口 2、3）

客户端支持环境变量隔离存档吗？——**不支持，同机双开共用同一存档**（%APPDATA%\BongoCatRemix\save.json）。
双开会互相覆盖存档（最后退出者落盘胜出），**本步骤会覆盖你的真实存档设置**，请先备份：
`copy %APPDATA%\BongoCatRemix\save.json %APPDATA%\BongoCatRemix\save.json.e8bak`

```powershell
start .\src\BongoCat.UI\bin\Debug\net8.0-windows\BongoCatRemix.exe
start .\src\BongoCat.UI\bin\Debug\net8.0-windows\BongoCatRemix.exe
```

两个实例各自：托盘右键 → **多人** →

- 实例 A：服务器地址 `127.0.0.1:9527`，显示名 `甲` → **创建房间** → 记下窗口里的 6 位房间码；
- 实例 B：服务器地址 `127.0.0.1:9527`，显示名 `乙`，房间码填 A 的码 → **加入房间**。

### 2.3 检查点（单机双开）

| # | 检查项 | 预期 |
|---|---|---|
| 1 | A 创建房间 | 多人窗口显示 6 位房间码（不含 0/O/1/I/L）；状态"已连接"；成员列表 `#1 甲（我）` |
| 2 | B 凭码加入 | A、B 窗口成员列表都变成两条；A 桌面渐显乙的远程猫（简笔猫头+名字牌），B 桌面同理 |
| 3 | 远程猫点击穿透 | 鼠标点远程猫：无任何反应，点击落到下层窗口（WS_EX_TRANSPARENT） |
| 4 | 敲击同步 | 任一实例打字 → 双方桌面**远程猫**爪子下压回弹 + 本机播敲击音效；主猫动画不受影响 |
| 5 | 位置同步 | 拖动本机主猫 → 对方桌面该远程猫跟随移动（归一化坐标，跨分辨率语义一致） |
| 5b | 装扮同步（E8 追加） | A 托盘背包换帽子/皮肤/鼓 → 关背包后 ≤500ms，B 桌面 A 的远程猫渲染同步变化（本地 items.json 映射，无素材传输）；未知装扮 id 的猫显示默认黏土色不崩溃 |
| 6 | 成员离开 | B 托盘多人窗口点"离开房间" → A 桌面乙的远程猫 300ms 渐隐消失；A 成员列表回到单人 |
| 7 | 断线重连 | 关掉中继（Ctrl+C）→ 两端多人窗口变"连接断开，正在重连…"、远程猫全部渐隐；重启中继 → 自动重连成功、（仍持有房间码的一方重新创建/加入后）恢复 |
| 8 | 加入被拒 | B 填错误房间码 → 提示"房间不存在：检查房间码是否正确"，不重连 |
| 9 | 服务器日志 | 中继窗口依次出现：连接建立 → 加入成功：房间 XXXX 成员 #N（…/8）→ 离开房间 → 空闲回收（若 10 分钟） |
| 10 | 存档 v8 | 退出客户端后 `%APPDATA%\BongoCatRemix\save.json` 出现 `"MpServerAddress"`, `"MpDisplayName"`, `"MpLastRoomCode"`；重启后多人窗口自动回填 |

### 2.4 日志抽查（%TEMP%\bongocat-remix.log）

```
[Mp] 请求加入：127.0.0.1:9527 房间=(新建)
[Mp] TCP 已连接 127.0.0.1:9527
[Mp] 加入成功：房间 A2C4E6，成员 #1（1 位其他成员）
[RemoteCat] 远程猫 #2「乙」上桌（当前 1 只）
[RemoteCat] 远程猫 #2 渐隐离桌（剩 0 只）
[Save] 迁移 v7 → v8：新增 MpServerAddress（空）/ MpDisplayName=猫猫 / MpLastRoomCode（空）   ← 旧存档首启
```

## 3. 云服务器真实验收（真服务器，主理人操作）

按 `deploy\DEPLOY.md`：发布（`scripts\publish-relay.ps1`）→ 上传 → systemd 启用 →
**安全组放行 TCP 9527** → 客户端填公网 IP 重复 §2.3 检查点 1–6、8、10。

服务器端额外检查：

```bash
journalctl -u bongocat-relay -f      # 连接/加入/离开日志
ss -tlnp | grep 9527                 # 监听确认
```

### 3.1 云端部署验证（部署后必做）

服务器上（SSH）：

| # | 检查项 | 命令 / 做法 | 预期 |
|---|---|---|---|
| 1 | 服务状态 | `systemctl is-active bongocat-relay` | 输出 `active`；`journalctl` 无 UnauthorizedAccess / status=6 崩溃循环（若有先查 `chown nobody:nogroup /opt/bongocat-relay`，见 DEPLOY.md §3 前置步骤） |
| 2 | 监听确认 | `ss -tln \| grep 9527` | 9527 处于 LISTEN |
| 3 | 协议握手 | 双客户端（两端各一台机器）填公网 IP 重复 §2.3 检查点 1–6、8、10 | 见下方要点 |

双客户端协议握手测试要点（对应 §2.3，跨互联网语义一致）：

- **Welcome 含房间码**：创建方收到服务器 Welcome，6 位码与多人窗口显示一致（不含 0/O/1/I/L）；
- **MemberJoin 双向广播**：加入方加入后，创建方与加入方成员列表都变为两条，双方桌面均出现对方远程猫；
- **CosmeticSync 转发**：任一端换装 ≤500ms 后对端远程猫渲染同步变化（装扮归属 id 服务器重写，只转发不理解语义）；
- **离场清房**：一方"离开房间"或直接关闭客户端 → 对端远程猫 300ms 渐隐、成员列表回落，房间空置超 10 分钟被回收并释放码。

## 4. 修复防回退清单（E8 相关改动后必须仍然成立）

1. MainWindow.OnSourceInitialized 中 `_hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;`（E1 黑底 bug）——远程猫窗口同款处理；
2. `_hwndSource.AddHook(WndProc);`（E1 热键/点击 bug）；
3. 位置/尺寸逻辑全程物理像素——远程猫定位走 TryGetWorkAreaPixels/SetWindowScreenPos 同坐标系；
4. XAML Path 禁用 Fill="None"；
5. items.json / economy.json / seasons 均为 App EmbeddedResource。

## 5. 设计与实现的边界（验收时知晓）

- 远程猫渲染与主猫共用同一套 CosmeticArt artKey 映射（毛色/帽子几何/鼓配色，E8 追加装扮同步）；
  装扮只传 3 个字符串 id，美术资源零传输；未知/伪造 id 双重防御（协调器目录清洗 + CosmeticArt
  未知 key 回退）→ 默认黏土色，不崩溃。
- 远端敲击音效跟随主音量（06-multiplayer.md 的"固定 30% 低音量"留 V3+ 独立音轨）。
- 位置广播在拖动结束时上报一次（拖动中 10Hz 连续同步留后续；服务器侧已做 [0,1] 钳制）。
- 装扮广播时机：加入房间成功立即一次 + 换装后 500ms 防抖合并（背包连点只发最终态）。
- 房间为内存态：服务器重启房间即消失（会话纯临时，不持久化联机世界——06-multiplayer.md 目标节）。
- 烟测钩子保留：`--smoke-exit-ms=N` 启动 N ms 后自动退出（回归 E1-E7 全部烟测步骤仍可用）。
- 客户端不发合法 Bye 帧直接断开（如进程被杀/网络闪断）会被服务器按"帧=Invalid"记日志断开——预期防御行为，非 bug。
