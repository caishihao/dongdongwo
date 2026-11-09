# E1 烟雾测试说明（docs/smoke-test-e1.md）

> 适用范围：E1 桌宠核心 MVP（透明猫 + 全局按键敲鼓 + 合成音效 + 拖动位置记忆）。
> 环境：Windows 10 1607+ / .NET 8 Desktop Runtime（构建需 .NET SDK ≥ 8）/ DWM 开启（Win10/11 默认开启）。

## 1. 构建

```powershell
cd D:\BongoCat
dotnet build BongoCatReplica.sln          # 预期：0 警告 0 错误
dotnet test                               # 预期：4/4 通过（CatDirector 交替/去抖/恢复/每键一音效）
```

## 2. 启动

```powershell
# 方式 A：直接运行
.\src\BongoCat.UI\bin\Debug\net8.0-windows\BongoCatRemix.exe
# 方式 B：源码运行
dotnet run --project src\BongoCat.UI
# 方式 C（CI/自动化烟测）：N 毫秒后自动优雅退出
.\BongoCatRemix.exe --smoke-exit-ms=8000
```

运行日志写入 `%TEMP%\bongocat-remix.log`（E1 临时设施，E2 换 ILogger）。

## 3. 检查点

### 3.1 透明置顶窗口（ADR-001）
- [ ] 猫浮在桌面上，猫形状**之外的区域完全透明**（能透出桌面壁纸/其他窗口）。
- [ ] 覆盖普通窗口时猫仍在最上层（Topmost）。
- [ ] 任务栏无图标（WS_EX_TOOLWINDOW）。
- 日志应出现：`[Window] DWM 透明已启用（hwnd=0x…）`。

### 3.2 全局键盘 → 敲鼓（ADR-002 / ADR-006）
- [ ] 在**任意**其他应用（记事本/浏览器）里打字，猫的左右爪**交替**下压回弹。
- [ ] 左爪 = 低音 bongo，右爪 = 高音 bongo；各 3 个音高变体随机轮换，微变音高（不呆板）。
- [ ] 按住一个键不放：只敲一次（30ms 去抖，GDD-01 §7）。
- [ ] 高频连打（如 10+ 次/秒）不卡顿、不破音、不丢音（混音叠加 + tanh 软限幅）。
- [ ] 鼠标点击不触发敲击（轮询排除 VK 0x01~0x06）。
- 已知行为：管理员权限窗口聚焦时按键**仍可**捕获（GetAsyncKeyState 不受 UIPI 限制）。

### 3.3 拖动 + 位置记忆（ADR-004）
- [ ] 按住猫拖动：窗口跟随鼠标，**不抢当前应用焦点**（WM_MOUSEACTIVATE → MA_NOACTIVATE）。
- [ ] 松开后拖到屏幕边缘会停在界内（还原/保存均做 clamp）。
- [ ] 按 **Ctrl+Alt+Q** 退出 → 重启后猫回到上次位置（按主屏工作区比例还原）。

### 3.4 退出与存档（ADR-004）
- [ ] 退出后 `%APPDATA%\BongoCatRemix\save.json` 存在，含 `SchemaVersion/CatPositionRelX/…/Checksum`。
- [ ] 手工把 save.json 改坏（删几个字符）再启动：日志出现"尝试备份 .bak"，程序仍可正常进入安全模式，**不清档不崩溃**。
- [ ] 强杀进程（`taskkill /F /IM BongoCatRemix.exe`）不损坏文件（原子写入 + .tmp 中转）。

## 4. 自动化窗口自检（可选）

窗口为 NOACTIVATE/TOOLWINDOW，`Process.MainWindowHandle` 会误报为 0。
如需脚本断言窗口存在，可用 EnumWindows 探针核对：`IsWindowVisible=true`、`size=260x220`、
`topmost=True`、`noactivate=True`、`toolwindow=True`。

## 5. E1 已知边界（验收时不算缺陷）

| 项 | 说明 | 后续 |
|---|---|---|
| 色键兜底 | DWM 不可用时留接口未实现（用户拍板） | E2 设置面板显示当前透明模式 |
| 退出方式 | 无托盘菜单，仅 Ctrl+Alt+Q；`taskkill`（不带 /F）的 WM_CLOSE 可能不被 NOACTIVATE 窗口消费 | E2 托盘菜单 |
| 空闲 CPU | 启动初期 JIT 导致 CPU 偏高属正常；稳态待 PerfView 证据（epics E1-S3 验收项） | E2 性能剖析 |
| 混合 DPI 双屏 | 位置按主屏工作区比例存取，拖动用屏幕像素 delta 绕开换算 | E1-S6 真机混合 DPI 验收 |
| 音频池上限 | >24 重叠音丢弃本次触发（防内存增长） | E2 音频体系化 |
| 音效变体池复用说明 | 音色实例播放完毕归还对象池（ADR-006 规则 2 已落地） | — |
