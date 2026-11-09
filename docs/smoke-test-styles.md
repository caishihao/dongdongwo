# 烟测手册 · 风格包系统（GDD-08）

> 前置：`dotnet build BongoCatReplica.sln` 0 错误 0 警告；`dotnet test` 全过（292 个）。
> 存档隔离：设环境变量 `BONGOCAT_SAVE_DIR=D:\tmp\bongocat-smoke` 再启动 exe（烟测专用覆盖口，
> 绝不触碰真实 %APPDATA%\BongoCatRemix 存档）。日志：%TEMP%\bongocat-remix.log。

## 1. 首启"第 0 步：选形象"（GDD-08 §3）

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1.1 | 删除隔离存档目录 → 启动 | 猫窗口出现（黏土默认渲染）+ 弹出选择器（6 张竖卡：黏土/贴纸/扁平/线稿/Q版/剪纸） |
| 1.2 | 观察卡片 | 每卡内嵌该风格猫预览；未选卡纯静态（低配降级方案：**只动画选中卡**） |
| 1.3 | 点击任一卡 | 卡片放大 1.15× + 金色（琥珀）描边，"就决定它了"点亮；选中卡每 4s 自动轻敲一次 |
| 1.4 | 确认 | 猫立即变成所选风格 → E7 教程第 1 步气泡出现（教程全程看到"自己的猫"） |
| 1.5 | 重启应用 | 不再弹选择器（StyleChosen=true 独立标记防重复弹）；教程从上次步续接 |
| 1.6 | 删存档重跑，点右下角"跳过" | 黏土猫 + 直接进教程第 1 步；存档 `CatStyleId='clay'`、`StyleChosen=True` |

日志锚点：`[Styles] styles.json 已载入：6 风格`、`[Style] 已装载风格 '<id>'`、`[Styles] 首启风格选择完成：<id>`。

## 2. 托盘"形象"子菜单即时切换（GDD-08 §3.3）

| 步骤 | 操作 | 预期 |
|---|---|---|
| 2.1 | 右键托盘 → 形象 | 6 项单选，当前风格打勾（空存档无勾选） |
| 2.2 | 逐一切换 5 种新风格 | 猫即时换风格（模板重载），装扮（帽/皮肤/鼓）保留不丢 |
| 2.3 | 换风格后敲键 | 敲击手感随风格变化（见 §3 参数表） |
| 2.4 | 重启 | 风格保持（存档续档 v9） |

## 3. 五风格视觉与手感检查点（对照 GDD-08 §2）

| 风格 | 视觉锚点 | 敲击手感（压深/回弹/挤压/旋转/错位） |
|---|---|---|
| 黏土 | 白描边底版平涂、珊瑚暖色、头顶高光、豆豆眼、**头身比放大一档**（头顶 37 / 横径 156） | 8px / 120ms Back0.5 / 挤压 0.94 / 无旋转 |
| 贴纸 | 全部件 5px 白贴纸描边 + **1.8px 内描边 `#6B4A32`（双层描边）**、底部投影、圆胖剪影 | 9px / 130ms Back0.7 / 挤压 0.93 |
| 极简扁平 | 无描边无投影、梯形身体、点线脸、**蓝鼓**、胶囊爪 | 6px / 110ms Quadratic / 无挤压 |
| 手绘线稿 | 墨色 `#4A403A` 细轮廓、纸底 `#FDF8EF`、空心圆眼、斜线腮红、爪 ±2° 颤感 | 7px / 130ms Back0.4 / 爪旋转 ±2° |
| Q版厚描边 | 头身比 1.4:1 大头、9px 黑描边、**加大腮红（26×15 @ 85%）**、两点高光 | 11px / 150ms Elastic(1,3) / 挤压 0.90 / 爪 4° / **鼓沉 3px** |
| 剪纸分层 | 撕纸边、色纸层错位阴影、深色纸片眼、无描边 | 8px / 140ms Back0.5 / **hat/face 反向弹起 2px 后 80ms 归位** |

通用：所有动画只动 RenderTransform（零 Layout 重算）；用户回弹设置（GDD-01）按 `ReboundMs/120` 全局缩放各风格时长。

预览参考图：`analysis/style-previews/preview_<id>.png`（圣诞帽 + 奶油皮肤渲染，烟测辅助工具产物）。

## 4. 装扮 × 风格兼容（GDD-08 §1）

| 步骤 | 操作 | 预期 |
|---|---|---|
| 4.1 | 托盘换风格 → 背包装帽子 | 帽子几何跨风格同一套（剪影一致）；贴纸风白框帽、线稿风照常显示 |
| 4.2 | 装 skin_cream/skin_orange/skin_gray | 色板颜色跨风格一致；线稿风下 12% 淡填充 + 纸底 |
| 4.3 | 装 skin_cow | 贴纸/扁平/Q版=色块斑；线稿=轮廓线斑（不填充）；剪纸=独立色纸片斑 |
| 4.4 | 扁平风 + 不装鼓 | 鼓为 `#5B8DEF` 风格默认单色；装任意鼓后走内容层色板 |
| 4.5 | 换风格不清空装扮 | 装备槽 id 原样保留（风格与装扮完全解耦，GDD-08 §5） |

## 5. 多人远端风格（GDD-08 §4）

| 步骤 | 操作 | 预期 |
|---|---|---|
| 5.1 | 双客户端（同新版本）进同一房间 | 双方按各自存档风格渲染远端猫（加入即广播 CAT_STATE 携带 style 字节） |
| 5.2 | 一方托盘换风格 | 对方远程猫**即时重载对应风格模板**（已收到的装扮保留） |
| 5.3 | 旧客户端互通 | 旧客户端 18 字节 payload → 新客户端按黏土渲染远端猫，会话正常（不断连） |
| 5.4 | 非法字节防御 | 远端 style 字节 >5 → 双层收敛（协议 Decode + 协调器目录）回退 clay，日志可见，不断连 |

⚠️ 服务器注意：线上中继（your.server.ip:9527）为旧版本时，CatState 由服务器解码重编码，
**style 字节会被旧服务器丢弃**（旧代码 `new CatStateMessage(id, x, y)` 不透传）——远端风格同步需重部署服务器后才生效；房间功能不受影响。

## 6. 开放问题②结论（Q版 hat 锚点 × 远端 240×200 画布）

**结论：远端窗体保持固定 110×100（240×200 逻辑画布等比缩放），无需自适应高度。**
验证方式：离屏 RenderTargetBitmap 按**生产同路径**（StyleRenderer.ApplyStyle + hat_santa）渲染
6 风格并逐像素检查（产物 `analysis/style-previews/preview_chibi.png`）：
- Q版 hat 锚点上移量为 styles.json `hatAnchorOffsetY = -10px`；
- 最高的圣诞帽（含白底版描边）顶点落位 y≈4 ≥ 0，全部帽子剪影完整在画布内，**无裁切**；
- 约束已固化：StylePack.HatAnchorOffsetY 的 JSON 校验范围 [-64,64]，且模板设计保证
  头顶上移量 ≤ 12px（头身比 1.4:1 只放大头部、不抬高头顶超出黏土锚点带），后续调参越界会在烟测图上立即暴露。

## 7. 防回退清单核对（本系统改动后逐项保持）

1. ✅ MainWindow.OnSourceInitialized `CompositionTarget.BackgroundColor = Colors.Transparent` —— 未触碰
2. ✅ `_hwndSource.AddHook(WndProc)` —— 未触碰
3. ✅ 位置/尺寸全程物理像素（TryGetWorkAreaPixels/TryGetWindowRectPixels/SetWindowScreenPos）—— 未触碰
4. ✅ XAML Path 无 `Fill="None"`
5. ✅ styles.json 为 App EmbeddedResource（与 items/economy/seasons 同模式），测试读同一份真实数据
6. ✅ RemoteCatWindow 与主猫共用 StyleRenderer/CatTapAnimator/CosmeticArt（单一数据源扩大到风格层）
7. ✅ JsonSaveService.ApplyLoaded 已同步补拷 v9 字段（CatStyleId/StyleChosen）+ Platform.Tests 真回环守门

## 8. 存量回归

- 8ms 键盘轮询与 60s CPU 日志机制未触碰（KeyPollingService/文件日志原样）。
- E7 主题键作用于选择器窗口（Bg.Window/Bg.Card/Border.Card/Text.*/Button.Accent*/Accent.Amber DynamicResource）。
- 敲击/拖动/教程/经济/通行证/多人既有 266 测试全部保持通过（新增 26 个共 292）。
