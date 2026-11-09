# E5 烟雾测试说明（docs/smoke-test-e5.md）

> 适用范围：E5 —— 金币支出与合成系统：3 合 1 合成（同品质同槽位）、手动拆解（图鉴不回灰）、
> 金币买箱（三档商店）、宝箱保底（银/金箱 15 箱 epic+）、库存单件上限自动折算、装扮预设、存档 v5。
> 前置：E1–E4 烟测（docs/smoke-test-e1/2/3/4.md）各检查点保持有效；本文只写 E5 增量。

## 1. 构建

```powershell
cd D:\BongoCat
dotnet build BongoCatReplica.sln          # 预期：0 警告 0 错误
dotnet test                               # 预期：132/132 通过（原 66 + E5 新增 66）
```

E5 新增测试分布：兑换合成 13（成功守恒/同名 3 件/自动选取优先重复件/重复折算 60%/零金币成本/审计事件/
legendary 三件不同→unique/异质异槽不足装备中 unique 空池全拒绝矩阵）、经济扩展 11（支出不足拒绝/CoinsSpent
审计/非正数拒绝/买箱不足拒绝/未知箱拒绝/买箱成功入库/买箱不影响免费充能/保底计数递增/第 15 箱必出 epic+/
出 epic+ 清零/minQuality 品阶）、库存扩展 7（图鉴点亮不回灰/幂等点亮/新手装点亮/上限折算/无配置不截断/
RemoveOne/ApplyOutfit）、预设 8（CRUD/同名覆盖/空名拒绝/原子切换/已拆解物品跳过）、配置 9（E5 段对账 +
threshold/minQuality/maxPerItem 非法值防御）、迁移 v5 2（v4→v5 图鉴由库存点亮/v5 幂等不重置）。

## 2. 启动 / 退出

```powershell
.\src\BongoCat.UI\bin\Debug\net8.0-windows\BongoCatRemix.exe --smoke-exit-ms=6000
```

首次以 E5 版本启动（旧 v4 存档自动迁移），日志 `%TEMP%\bongocat-remix.log` 应依次出现：

```
[Economy] economy.json 已载入：敲击 250/币(日上限 500)、滴灌 120s/币、掉落 15min/次、宝箱充能 2h/wooden、掉落桶 40 件/24h
[Save] 迁移 v4 → v5：新增 DiscoveredItems（由库存 keys 点亮）/ Presets / ChestPityCount
[Economy] 初始化完成：金币 <延续余额>，掉落桶 <延续>/40
```

**第二次启动幂等**：不再出现迁移日志；金币/图鉴/预设/保底计数全部延续存档值。

## 3. 人工检查点（主理人验收）

| 检查点 | 预期 |
|---|---|
| 背包底栏 | 选中物品后新增**合成**/**拆解 +N** 按钮；条件不满足灰置，悬停 tooltip 说明原因（"同品质同槽位物品不足 3 件"/"装备中的物品不可拆解（请先卸下）"/"unique 为顶阶，不可再合成"等） |
| 合成 | 同品质同槽 3 件（同名可）→ 1 件高一品质随机物品（**排除输入 id**）；结果 chip 品质色 + "新物品！"/"重复 +N"；金币零成本（重复品除外 +60% 折算） |
| 拆解 | 全额拆解价 10/40/150/600/2500；装备中/unique 灰置；拆后**图鉴保持点亮**（图鉴页显示"曾获得"，不打码） |
| 图鉴页 | "已获得"→ 拆解后变"曾获得"，收集进度按"曾拥有"计（不回灰） |
| 商店 · 预设 Tab | 三档宝箱卡（木箱 150 / 银箱 600 / 金箱 2400，economy.json chests 数据驱动）；**金币不足购买按钮灰置**；购买成功立即开出（同免费开箱 chip 反馈） |
| 金币闪动 | 头部金币数字在任何增减后闪一次琥珀金（#E8A020）再回深棕，约 0.7s |
| 预设区 | 下拉选预设 + 应用/删除；"另存为"输入名称 + 保存当前装扮；应用 = 三槽原子切换；预设里的物品已被拆解 → 该槽保留原装备不崩溃 |
| 保底（观测性验证，可临时改 economy.json `pity.threshold` 加速） | 连续 15 次**银/金箱**未出 epic+ → 第 15 箱必出 epic+（wooden 不参与保底也不推进计数——保底若作用于木箱会击穿回收率 <100% 红线，GDD-02 §5 只定义"银/金箱"） |
| 库存上限 | 单件攒到 9 件后再次获得 → 数量封顶 9，超出部分按拆解价全额折算金币 |

## 4. 存档 schema v5

存档：`%APPDATA%\BongoCatRemix\save.json`（信封 = SchemaVersion + CRC32 + PayloadJson，不变）。

**v5 新增字段**（v4 全部字段保留）：

| 字段 | 类型 | 含义 | v5 默认 |
|---|---|---|---|
| DiscoveredItems | list&lt;string&gt; | 图鉴永久点亮（曾经拥有过；迁移时由 OwnedItems keys 并集初始化） | [] |
| Presets | list&lt;{Name,HatId,SkinId,BongosId}&gt; | 装扮预设（按名称 upsert） | [] |
| ChestPityCount | int | 宝箱保底计数（只由银/金箱推进；出 epic+ 清零，封顶 14） | 0 |

**迁移（ADR-004 规则 2 链式）**：v1 → … → v4 → v5 一链到底；v4 玩家图鉴由当前库存点亮（不回灰）；
已点亮过（DiscoveredItems 非空）绝不覆盖——幂等可重放。未来更高版本仍"只读不写"拒绝。

## 5. economy.json E5 新增参数摘要（嵌入 App 程序集）

```
"pity": { "threshold": 15, "minQuality": "epic", "excludeChestIds": ["wooden"] }
"inventory": { "maxPerItem": 9 }
```

买箱定价 = chests[].price（木箱 150 / 银箱 600 / 金箱 2400，GDD-02 §6.2 原表，非任务书兜底值）。
合成**金币零成本**（GDD-04 §2/§8-3：消耗物品即成本，unique 合成不加费）；输出排除输入 id（GDD-04 §8-1 采纳）。

## 6. 关键实现决策与已知边界

- **服务拆分**：新建 `IExchangeService`（App/Exchange）承载合成+拆解，金币仍由 `IEconomyService` 单一出口——
  经济服务新增 `GrantCoins`（公开入账：ManualSalvage/OverflowConversion/DuplicateConversion 来源审计）、
  `TrySpendCoins`（唯一支出路径，不可为负）、`TryBuyChest`（扣币 + 复用免费开箱同一 roll/保底/入库链路）、
  `CoinsSpent` 事件。理由：避免 EconomyService 膨胀成物+币双域接口，物品转化规则独立全矩阵单测。
- **预设**：V1 随主存档持久化（SaveModel.Presets），不走 E3 草案的多文件 presets/*.json——
  预设跟随存档备份/迁移链更安全（epics E3-S6 验收口径沿用）。
- **合成事务原子性**：先扣 3 件后加 1 件、立即落盘；崩溃窗口（毫秒级）可能丢 3 件输入，V1 不做流水回滚（已知限制）。
- **保底排除木箱**：模拟验证 wooden+保底 → 期望回收率 104.6% > 100%（买箱套利），按 GDD-02 §5 原文
  "银/金箱"收窄；`pity.excludeChestIds` 参数化可调。
- **未纳入本批**（主理人任务清单未列）：epics E5-S3 AutoExchanger 自动兑换策略、GDD-04 §5 跨槽位混合合成（V2 开关）；
  UI 采"选中一件 → 自动选取 3 件输入"的简洁交互（epics E5-S2 多选/预览留 V2）。
- **金币数字审计**：背包头部/托盘金币均为存档单一数据源；支出只经 TrySpendCoins，拒绝路径零状态变化。
- 视觉验收依赖 §3 人工检查点；自动化覆盖构建/132 单测/烟测日志。
