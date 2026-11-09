# ADR-001 透明窗口方案

状态：Proposed｜日期：2025｜决策人：程基岩

## 上下文
桌宠本体是一张透明、置顶、无边框、不进任务栏、可拖动的猫。WPF 提供两条透明路线；原作（Unity）采用 DWM 透明 + 色键兜底（分析报告 §2 证据字符串 "DWM transparency failed or disabled, using color key method"）。

## 备选方案

### A. AllowsTransparency=true（WPF 全软件合成）
- 优点：纯 WPF API，最简单；任意形状抗锯齿。
- 缺点：整窗口走软件渲染路径，**禁用硬件加速与 DWM 合成的直接路径**；位图层多、窗口大时 CPU 占用高；与某些全局钩子/分层窗口场景有历史兼容性问题；无法承载 Airspace 冲突外的 WebView 等。

### B. HwndSource + DWM ExtendFrameIntoClientArea（margins = -1）✅
- 优点：窗口仍是普通 DWM 合成窗口，WPF 渲染走正常硬件管线，性能最好；即"黑魔法"最少的主流做法。
- 缺点：需要 P/Invoke（DwmExtendFrameIntoClientArea、DwmIsCompositionEnabled）；DWM 关闭/失败需要兜底。
- 兜底：DWM 不可用时回退 8-bit 色键（如 #FF00FF）+ `SetLayeredWindowAttributes(LWA_COLORKEY)`——与原作兜底机制同构，但为原创实现。
- 缺点：色键模式下同色像素全部透穿（素材不得含该颜色），无抗锯齿半透明。

### C. SetLayeredWindowAttributes(LWA_ALPHA) 常规分层窗口
- 只能整体半透明，不能抠形状。排除。

### D. Win32 DirectComposition / DirectX 自绘
- 性能上限最高但工程量与维护成本不成比例，WPF 生态下不划算。排除（记录备用）。

## 决定
**方案 B**：`HwndSource` 上调用 `DwmExtendFrameIntoClientArea(-1)` 实现逐像素透明；初始化失败（`DwmIsCompositionEnabled=false` 或返回非 S_OK）时回退色键分层窗口。窗口样式：`WS_POPUP` + `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`；背景画刷 `Brushes.Transparent`；`ResizeMode=NoResize`，`ShowInTaskbar=false`。

## 后果
- P/Invoke 集中在 `Platform/Native/DwmNative.cs`，失败路径必须有日志与色键回退的 UI 自检（设置面板显示"当前透明模式：DWM/色键"）。
- 色键回退时需在素材管线检查无 #FF00FF 像素（构建脚本加像素扫描告警）。
- `WS_EX_NOACTIVATE` 需配合窗口消息 `WM_MOUSEACTIVATE = MA_NOACTIVATE`，否则点击猫会抢玩家游戏焦点——这是桌宠体验的关键细节。
- 兼容性：Windows 10 1607+ 的 DWM 无法用户态禁用，色键兜底实际仅覆盖极端环境（远程会话/安全模式）。
