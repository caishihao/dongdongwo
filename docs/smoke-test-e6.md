# E6 烟雾测试说明（docs/smoke-test-e6.md）

> 适用范围：E6 —— 赛季通行证 PawPass：赛季配置数据驱动（seasons/*.json 嵌入资源）、XP 获取
> （敲击 10 XP/1000 键日上限 300 + 每日任务 3 条自动入账）、等级曲线推进、双轨里程碑领取（幂等）、
> 高级轨 2500 金币解锁（TrySpendCoins 审计）、赛季窗口判定与惰性切换（仅重置赛季字段）、存档 v6。
> 前置：E1–E5 烟测（docs/smoke-test-e1..e5.md）各检查点保持有效；本文只写 E6 增量。

## 1. 构建

```powershell
cd D:\BongoCat
dotnet build BongoCatReplica.sln          # 预期：0 警告 0 错误
dotnet test                               # 预期：164/164 通过（原 132 + E6 新增 32）
```

E6 新增测试分布：赛季配置 15（解析全字段/曲线阈值表/窗口两端含当日/奖励查询/seasonId 空白/
非法日期/区间倒挂/maxLevel<1/曲线 base<1 step<0/解锁价为负/任务未知类型·目标 0·xp 0/奖励越界·
未知轨别·金币额 0/缺 itemId·未知类型/同节点双轨重复/非法 JSON）、通行证服务 16（初始化装载/
敲击 XP 每 1000 键 10 点跨阈值升级广播/日上限 300 且任务 XP 不占额度/满级封顶/阈值差 1 XP 边界/
双轨幂等拒绝含轨别记录/高级轨未解锁拒绝+2500 解扣币审计+解锁后可领+重复解锁拒绝/未达阈值不冒泡/
赛季切换只重置赛季字段资产全保留/无激活赛季进度保留且领取解锁拒绝/宝箱任务自动入账同日不重复/
合成任务自动入账/经济开箱事件转发任务计数/跨天重置任务与 XP 计数赛季 XP 保留/时钟回拨冻结/
真实嵌入配置对账 2 份相邻窗口+16660 满级阈值）、迁移 v6 2（v4/v5 链式补齐赛季零值、v6 幂等不重置）。

## 2. 启动 / 退出

```powershell
.\src\BongoCat.UI\bin\Debug\net8.0-windows\BongoCatRemix.exe --smoke-exit-ms=6000
```

首次以 E6 版本启动（旧 v5 存档自动迁移），日志 `%TEMP%\bongocat-remix.log` 应依次出现：

```
[Pass] season_001.json 已载入：第一赛季·初来乍到（2026-10-26 ~ 2026-12-06，100 项奖励，任务池 3 条）
[Pass] season_002.json 已载入：第二赛季·冬日鼓点（2026-12-07 ~ 2027-01-17，100 项奖励，任务池 3 条）
[Pass] 共 2 个赛季配置（当前激活：第一赛季·初来乍到）
[Save] 迁移 v5 → v6：新增 SeasonId / SeasonXP / ClaimedMilestones / PremiumUnlocked / ...
[Pass] 首次进入赛季：第一赛季·初来乍到（2026/10/26 ~ 2026/12/6）
[Pass] 初始化完成：第一赛季·初来乍到 Lv1，XP 0，高级轨 未解锁，已领 0 项
[Tray] 托盘图标已创建（菜单：显示隐藏/背包/通行证/音量/静音/镜像/尺寸/退出）
```

**第二次启动幂等**：不再出现迁移日志与"首次进入赛季"；金币/赛季 XP/领取记录/高级轨状态全部延续。
（若第二次启动又见"首次进入赛季"= JsonSaveService.ApplyLoaded 漏拷 v6 字段，属 bug。）

## 3. 人工检查点（主理人验收）

| 检查点 | 预期 |
|---|---|
| 托盘菜单 | 新增**通行证**项；点击打开通行证窗口（普通窗口，非置顶，奶油底 + 深棕描边） |
| 背包头部 | 新增**通行证**快捷按钮（开宝箱按钮右侧），点击同样打开通行证窗口 |
| 通行证顶栏 | 赛季名"第一赛季·初来乍到 · 剩余 N 天"+ "Lv 1 · 0 / 100 XP" + 进度条 + **解锁高级轨（2500 金币）**按钮（金币不足灰置） |
| 今日任务行 | "今日任务：开启 2 个宝箱 0/2 · 累计敲击 3000 次 0/3000 · 合成 1 次 0/1"（进度随行为实时推进） |
| 里程碑主体 | Lv1~Lv50 横向列表（左→右），每节点上格免费轨/下格高级轨，奖励文案 + 品质色点；Lv1 双格琥珀高亮"点击领取"（1 级阈值 0 开局可领），其余灰 |
| 领取免费轨 | 点 Lv1 免费格 → "金币 ×100 / 已入账" chip + 金币余额 +100；节点变"已领取 ✓"灰绿；重复点击拒绝（LastEvent 提示已领取） |
| 领取高级轨 | 未解锁时高级格灰置不可点；花 2500 解锁后（托盘气泡"通行证 Lv N 里程碑奖励可领取！"）可领取节点高亮；金币扣 2500（审计 CoinSource.PremiumUnlock） |
| 敲击升 XP | 敲 1000 键 +10 XP（进度条/等级文本推进）；跨级时托盘气泡"通行证 Lv N 里程碑奖励可领取！" |
| 升级阈值 | 每级需求 = 100 + (level-1)×10；Lv30 累计 7350 XP、Lv50 满级累计 16660 XP（GDD-05 §6 验算一致） |
| 跨 0 点 | 每日任务进度/敲击 XP 计数清零，赛季 XP 与领取记录保留 |
| 赛季切换（可临时改 season_001.json endDate 加速） | endDate 次日首次启动 → 自动装载 season_002 + 气泡"新赛季开启：第二赛季·冬日鼓点"；**仅清** SeasonXP/领取记录/高级轨解锁，金币/物品/图鉴/预设/保底计数全保留 |
| 视觉 | Path 填色正常（无 Fill="None" 回退）；窗口缩放/位置记忆不受影响（物理像素纪律未触碰） |

## 4. 存档 schema v6

存档：`%APPDATA%\BongoCatRemix\save.json`（信封 = SchemaVersion + CRC32 + PayloadJson，不变）。

**v6 新增字段**（v5 全部字段保留；金币/物品/图鉴/预设为永久资产永不清空——用户拍板）：

| 字段 | 类型 | 含义 | v6 默认 |
|---|---|---|---|
| SeasonId | string | 当前赛季 id（空 = 未进入；与激活配置不符 → 惰性重置赛季字段） | "" |
| SeasonXP | long | 本赛季累计 XP（等级由曲线阈值表推导，满级封顶停止累计） | 0 |
| ClaimedMilestones | list&lt;string&gt; | 已领奖励键 "seasonId:level:track"（领取幂等依据） | [] |
| PremiumUnlocked | bool | 高级轨已解锁（赛季内一次性 2500 金币） | false |
| DailyXpDate | string | 敲击 XP 每日重置标记日（字符串序比较防时钟回拨） | "" |
| DailyXpEarned | int | 当日敲击 XP 累计（上限 300；任务 XP 不占额度） | 0 |
| DailyChestCount | int | 当日开箱数（任务 openChest 进度） | 0 |
| DailySynthCount | int | 当日合成数（任务 exchange 进度） | 0 |
| DailyCompletedQuests | list&lt;string&gt; | 今日已自动入账任务 id（防重复入账） | [] |

**迁移（ADR-004 规则 2 链式）**：v1 → … → v5 → v6 一链到底；v5 玩家赛季字段零值起步，
首次进入按当前赛季惰性装载。⚠️ **JsonSaveService.ApplyLoaded 为逐字段手拷（保持 Current 引用稳定），
新增存档字段必须同步补拷**——E6 首测即因漏拷导致 SeasonId 不续档，已修复（见 §6）。

## 5. 赛季配置摘要（App EmbeddedResource，随版本热更）

两个赛季窗口无缝衔接（endDate + 1 天 = 下赛季 startDate），奖励引用 items.json 既有 12 件物品与金币：

| | season_001 第一赛季·初来乍到 | season_002 第二赛季·冬日鼓点（占位） |
|---|---|---|
| 窗口 | 2026-10-26 ~ 2026-12-06 | 2026-12-07 ~ 2027-01-17 |
| 曲线/满级/解锁价 | base 100 + step 10 / 50 级 / 2500 金币 | 同左 |
| 每日任务池 | 开 2 箱 +30XP · 敲 3000 键 +20XP · 合成 1 次 +25XP | 同左 |
| 免费轨 | 奇数级金币 100→580 递增（L50 收官 500）；偶数级轮发 12 件物品 | 同构（物品起点旋转、金额微调） |
| 高级轨 | 奇数级金币 200→1400 递增；偶数级轮发 uncommon~legendary 物品；L50 镇季 = 春日花环（legendary） | 同构；L50 = 奶牛纹布丁 |

满级时间验算（GDD-05 §6）：普通玩家 ~220 XP/日 → 33 天达 30 级 ✓；重度玩家（敲击 XP 封顶 300 +
任务 75 = 375/日）→ 42 天 15750 XP ≈ Lv48（GDD-05 §2"满级"按其自身两处口径折算后取敲击行上限口径，
见 §6 决策 3）。

## 6. 关键实现决策与已知边界

- **赛季配置走 EmbeddedResource**：GDD-05 §1"可随版本热更"= 随版本发布更新 → 与 items/economy
  同模式嵌入（SeasonConfigLoader，`BongoCat.App.assets.seasons.*`），测试读同一份真实数据；
  免发版调参需求出现时在加载器加"%APPDATA% 覆盖目录优先"分支即可。奖励 itemId 在加载期对
  items.json 目录校验，引用失效 fail-fast（GDD-05 §7 边界：不留到领取时才暴露）。
- **无 Token**：GDD-05 全文未定义赛季代币/兑换商店（奖励类型仅 coins/item）→ 按任务书条件分支
  "Token 仅作为奖励物品持有，说明即可"的最简解读：**不引入 PassTokens 字段**，奖励=金币+物品。
- **XP 日上限口径**：GDD-05 §6 数值表把"每日上限 300 XP"挂在**敲击 XP 行**（§5 防刷句为泛述）；
  取行级口径——敲击 XP 封顶 300/日，任务 XP（固定 ~75）不占额度。普通 219/日→Lv31 ✓ 与 §2 自洽；
  重度 375/日→Lv48（其"42 天满级"口径按此解读）。若主理人拍板"全 XP 共享 300 上限"，改
  PassService.AddXp 调用点一行即可（数值已参数化为常量）。
- **赛季切换简化**：GDD-05 §5"7 天宽限期补领 + 往期归档"未实现——用户拍板"切换仅重置
  SeasonXP/ClaimedMilestones/PremiumUnlocked"，即切换时未领奖励作废（诚实设计不追补的更紧口径）。
  往期归档/宽限补领留 V2。
- **每日任务池轮换**：池恰 3 条 = 每日 3 条全激活（GDD-05 §5"从池轮换取 3 条"在 |pool|=3 时退化为
  恒等）；池扩充后按 DayOfYear 相位轮转取 3 条。任务 XP 完成即自动入账（§7：无需手动领）。
- **溢出 XP 不转金币**：GDD-05 §2"满级溢出 XP 转金币"汇率未定义 → 满级后停止累计（记开放问题）。
- **敲击 XP 复用经济计数**：PassService.OnValidTap 必须晚于 EconomyService.OnValidTap 订阅
  （App.OnStartup 已按此顺序）——有效敲击防刷判定（GDD-05 §5"与经济共用判定"）天然复用，零重复计数。
- **服务拆分**：IPassService 独立（App/Pass）——赛季窗口/XP/领取为独立领域；金币仍走
  IEconomyService 单一出口（GrantCoins(PassReward) / TrySpendCoins(PremiumUnlock)），
  物品走 IInventoryService.GrantItem（上限折算复用 sink 链路）。
- **事件源**：IEconomyService 新增 ChestOpened（免费+买箱都触发）；合成复用既有
  IExchangeService.ExchangeOccurred——PassService 只被事件驱动，不侵入经济/兑换内部。
- **修复防回退清单**：MainWindow 透明/Hook/物理像素/Path Fill/嵌入资源五项均未触碰；
  新增 seasons/*.json 为 App EmbeddedResource（清单第 5 条同模式扩展）。
- **已知限制**：① 烟测未覆盖 PassWindow 视觉（视觉验收由主理人做）；② 赛季切换瞬间在线的
  "一次性公告"依赖气泡，错过不可回放；③ 每日任务无 UI 单独面板，仅通行证顶栏一行进度文本。
