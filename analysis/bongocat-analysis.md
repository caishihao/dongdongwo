# Bongo Cat（Steam 版）静态分析报告

> 分析引擎：Craft Anything（REA 静态分析，未加载/未执行任何目标代码）
> 分析对象：`D:/BongoCat/BongoCat_Data/Managed/Assembly-CSharp.dll`
> 程序集 SHA-256：`d6209ecce915e15aadf4de11191eef5c6d3c6aaafbd82a67c773b72070ae54cb`（MVID `f801116a-2743-4bcd-9903-fc08b7aa70a8`）
> 证据基线：ev_f1847bac…（程序集概要）、ev_7eb441d2…（成员清单）、ev_103c7c3e…/ev_b9530b51…（用户字符串）
> 说明：本报告只记录**玩法机制**（机制本身不受版权保护）。原作美术、音频、代码均不复制，复刻须使用原创素材与原创实现。

## 1. 技术形态（观察所得）

- Unity 引擎 + Mono IL2C#（il-only PE/CLI，v4.0.30319），694 个类型 / 3,743 个方法 / 4,723 个字段 / 472 条用户字符串。
- 第三方库：Heathen.Steamworks（Steam 集成）、DOTween（动画）、SDL2-CS（手柄输入）、Discord.Sdk（Rich Presence）、Unity Localization（35+ 语言）、Newtonsoft.Json。
- Windows 专属能力通过 P/Invoke 实现：DWM 透明窗口、Win32 键盘钩子、任务栏隐藏、注册表自启动。

## 2. 核心循环：桌面打击猫

**这是整个游戏的"玩法"本体，其余全是围绕它的元游戏。**

流程：全局键盘钩子捕获任意按键 → 猫的左/右爪交替敲击（`Cat.Tap(int)` → `CatHitLeft`/`CatHitRight` 协程，`_hitDuration` 控制回弹时长）→ 播放鼓点音效（`RandomSound`，素材名 BongoBeat / BongoMinus / BongoTap）。

关键实现点（来自 `BongoCat.Cat`、`BongoCat.OSSpecific.GlobalKeyHook`、`GlobalKeyHook.WinKeyHook` 成员签名，观察所得）：

| 机制 | 证据（类型/成员） |
|---|---|
| 透明无边框窗口（DWM 优先，色键兜底） | `TransparentWindow`、`WinTransparentWindow`、字符串 "DWM transparency failed or disabled, using color key method" |
| 窗口置顶、可拖动、锁定位置 | `AlwaysOnTop`、`Draggable.KeepInBounds`、`MainCat._lockPosition` |
| 全局按键钩子（键盘） | `WinKeyHook.GetAsyncKeyState(i4)->i2`、`BUTTONS:i4[]`、`WasPressed:bool[]`、事件 `OnKeyPressed<int>` |
| 手柄支持（SDL） | `GlobalKeyHook.InitSDL`、`ProcessControllerInput`、扳机键敲击（TRIGGER_TIME_THRESHOLD 冷却） |
| 自适应轮询（省 CPU） | `_activePollIntervalMs` / `_idlePollIntervalMs` / `_idleAfterSeconds`，空闲时降频 |
| 猫的部件化渲染 | `Cat` 字段：body/rightPaw/hat/bongos×3 各自独立 Sprite + punch 替换帧 |
| 附加动画 | `CatBobbing`（呼吸浮动）、`CatRotator`（旋转）、`FlipVisuals`（镜像） |
| 位置记忆 | `MainCat.SavePosition`、Prefs 键 `BongoMenuPosXRel/YRel`（相对屏幕比例存储，跨分辨率还原） |
| 多显示器 | `MainScreenSetting`（F8 切换主屏）、监视器边界枚举与越界回中 |

## 3. 元游戏系统（按证据归类）

### 3.1 经济与收集
- 货币：BongoCoins（`CoinStash`）。
- 物品来源：`ItemGenerator`（开局发放 `_itemGenAmount`）、`PlaytimeItemDrop`（挂机时长掉落，含心跳 `ItemDropHeartbeat`）、宝箱（`ChestType`：Generator/Chest/Bundle/playtimegenerator/manual）、Steam 库存同步。
- 品质分级：common / uncommon / rare / epic / legendary / unique，颜色 #B0B0B0 / #5ACC3D / #258ED4 / #B939DB / #FFB000 / #FF3D87（字符串证据）。
- 物品槽位：hat / skin / bongos / emote / consumable / uitheme（`Cat.SetHat/SetSkin/SetBongos/SetItem/ClearItemSlot`）。
- 兑换合成：`ItemExchange`——同品质低阶物品合成高阶（字符串 "Not all items had same quality. Aborting"），支持自动兑换 `AutoExchanger`。

### 3.2 Steam 集成（复刻时不可用的部分）
库存物品实际存放在 Steam Inventory Service（`SteamJsonParser` 解析 SteamItemJson/Bundle/Generator/Exchange），云存档、排行榜、促销物品、市场链接、投票、垃圾箱删除、宝箱跨端交换（带重试）。

### 3.3 多人模式
- Steam 大厅：建房/邀请/大厅码加入/`+connect_lobby` 命令行加入、大厅成员上限、AFK 检测。
- 猫的摆放：`CatSpawnManager` + 6 种排布算法（Grid / BorderLine / AroundBorder / Free / Random / AlignedWithMainCat），可存槽位。
- 互动：互赠礼物（"sent a gift!"）、名字牌、猫爪敲击同步（`CatPatter`）、排行榜、**Battle Royale 模式**（倒计时、淘汰人数、CountdownRoundEnd/LobbyClosed 事件）。

### 3.4 PawPass（赛季通行证）
赛季制（证据：2609/2610/2611），进度存 Steam 统计，免费/高级双轨奖励（rewardType F/P），里程碑领取气泡、往期通行证查看、PawPassToken 兑换物品。

### 3.5 自定义与周边
- 预设系统：presets.json（本地 JSON 持久化整套装扮）。
- BongoDex：图鉴式收集索引（按 CollectionNames 分类）。
- 表情系统：EmoteSpawner 粒子喷发 + 消耗品行为覆盖。
- UI 主题：UiThemeSet（颜色/字体/精灵角色化）。
- 支持者 DLC 商店、合作活动（BongoCatxTheFarmerWasReplaced、TapTapLootxBongoCat——后者走 IPC 管道）、圣诞日历、投票（Poll）、GIF 生成。
- 彩蛋：在大厅码输入 `ACCOUNTABILITY` 可领免费物品（方法名 `Thank_you_for_keeping_us_accountable_…`，观察所得）。

## 4. 复刻可行性结论

| 层 | 浏览器 HTML | C# 桌面（WPF/WinForms） | Unity |
|---|---|---|---|
| 猫 + 全局按键打击 | ❌ 需页面聚焦 | ✅ 全局钩子 | ✅ |
| 透明置顶桌宠 | ❌ | ✅ WPF AllowsTransparency | ✅ + Win32 |
| 鼓点音效 | ✅ WebAudio | ✅ | ✅ |
| 装扮/品质/兑换/图鉴 | ✅ | ✅ | ✅ |
| 挂机掉落/通行证 | ✅ | ✅ | ✅ |
| Steam 多人 | ❌ | ❌（可换 WebSocket 局域网） | ✅ |

**核心玩法（按键→猫敲鼓）+ 元游戏（收集/兑换/图鉴）完全可以在单机环境复刻。**
多人与 Steam 库存属于平台能力，复刻时应替换为本地存档 + 可选局域网联机。

## 5. 建议的复刻范围分层

1. **MVP**：透明置顶窗口 + 全局键盘钩子 + 左右爪交替敲击动画 + 鼓点音效 + 拖动/位置记忆。
2. **V1**：装扮槽位（帽/皮肤/鼓）+ 品质与图鉴（BongoDex 式）+ 金币 + 挂机掉落 + 宝箱。
3. **V2**：兑换合成、预设、表情、UI 主题、教程引导。
4. **V3（可选）**：通行证赛季、局域网多人摆放。

## 6. 未决事项（unknowns）

- 各品质掉率、金币数值、兑换配方等平衡数值在序列化资产（resources.assets / StreamingAssets）中，本次未解析（静态元数据不含数值平衡）。
- 动画帧图与音效在 .assets/.resS 内，未提取（复刻不应提取，需原创素材）。
- Steam Inventory 服务端配置不可见。
