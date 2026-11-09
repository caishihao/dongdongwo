namespace BongoCat.Core.Save;

/// <summary>
/// 存档内存模型（v11 宠物名字；v10 物种字段；v9 风格包字段；E7 v7：+ 教程状态/主题 id；
/// E6 v6 通行证赛季字段；v5 图鉴永久点亮/装扮预设/宝箱保底计数；v4 经济字段；
/// v3 库存/装备槽；v2 位置 + 设置 + 窗口尺寸）。
/// 位置按主屏工作区"比例"存储（0~1 浮点），跨分辨率还原（ADR-004 位置记忆节）。
/// </summary>
public sealed class SaveModel
{
    /// <summary>存档 schema 版本（ADR-004 规则 2：迁移器链式升级。E9 起 v11，新增 PetName）。</summary>
    public const int CurrentSchemaVersion = 11;

    /// <summary>写入存档文件的 schema 版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>猫窗口左上角 X（工作区宽度比例 0~1）。默认近中偏左，首次启动即可见。</summary>
    public double CatPositionRelX { get; set; } = 0.45;

    /// <summary>猫窗口左上角 Y（工作区高度比例 0~1）。</summary>
    public double CatPositionRelY { get; set; } = 0.32;

    /// <summary>整体水平镜像（纯视觉，GDD-01 §5）。</summary>
    public bool CatMirrored { get; set; }

    /// <summary>主音量 0~1。</summary>
    public float MasterVolume { get; set; } = 1.0f;

    /// <summary>静音。</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// 猫窗口整体缩放（相对基准 130×110 DIP，v2 新增）。
    /// 合法档位 0.75 / 1.0 / 1.5（托盘菜单三档）；读取侧用 <see cref="IsValidWindowScale"/> 防御非法值。
    /// </summary>
    public double WindowScale { get; set; } = 1.0;

    /// <summary>合法尺寸档位（E2 用户拍板：75% / 100% / 150% 三档）。</summary>
    public static readonly double[] ValidWindowScales = [0.75, 1.0, 1.5];

    // ── E3 v3：库存与装备（装扮元游戏，GDD-03）────────────────────────

    /// <summary>
    /// 库存（itemId → 持有数量；E3 全部为 1，字典结构为 E4 宝箱重复掉落预留）。
    /// 空目录 = 未初始化或无物品；由 <c>InventoryService.Initialize</c> 授予新手装。
    /// </summary>
    public Dictionary<string, int> OwnedItems { get; set; } = new(StringComparer.Ordinal);

    /// <summary>装备中的帽子 id（空串 = 默认无帽）。</summary>
    public string EquippedHat { get; set; } = string.Empty;

    /// <summary>装备中的皮肤 id（空串 = 原创基础皮肤）。</summary>
    public string EquippedSkin { get; set; } = string.Empty;

    /// <summary>装备中的鼓 id（空串 = 原创基础鼓色）。</summary>
    public string EquippedBongos { get; set; } = string.Empty;

    /// <summary>
    /// 库存是否已初始化（新手装授予幂等标记，v3 新增）。
    /// 不能用 OwnedItems 是否为空判断——玩家卸下/未来清空库存会误触发重复授予。
    /// </summary>
    public bool InventoryInitialized { get; set; }

    // ── E4 v4：经济系统（GDD-02；金币/产币计数/掉落桶/宝箱充能）────────────

    /// <summary>金币余额（记账不可为负；单一数据源，经 EconomyService 变更）。</summary>
    public long Coins { get; set; }

    /// <summary>当日累计有效敲击次数（每日按本地日期重置；敲击产币封顶依据）。</summary>
    public int DailyTapCount { get; set; }

    /// <summary>每日重置标记日（本地日期 yyyy-MM-dd；字符串序比较防时钟回拨刷奖励）。</summary>
    public string DailyCoinDate { get; set; } = string.Empty;

    /// <summary>滴灌产币秒数累积器（不足 1 币的余数，跨 Tick/跨会话续接）。</summary>
    public double IdleCoinAccumulator { get; set; }

    /// <summary>
    /// 挂机掉落物品桶：24h 滚动窗口内掉落物品的墙钟 UTC ticks 列表。
    /// 超 MaxItems 暂停物品掉落，最老条目过期（加载/roll 前清理）后自动恢复。
    /// </summary>
    public List<long> DropBucket { get; set; } = new();

    /// <summary>上次开宝箱的时间戳（TimeProvider 单调时钟，诊断用）。</summary>
    public long LastChestTicks { get; set; }

    /// <summary>免费宝箱充能秒数（在线累计，封顶 = 单箱充能值，开箱消耗）。</summary>
    public double ChestChargeSeconds { get; set; }

    // ── E5 v5：兑换合成（图鉴永久点亮 / 装扮预设 / 宝箱保底）────────────────

    /// <summary>
    /// 图鉴永久点亮记录（曾经拥有过的物品 id，GDD-03 §5 + E5 拆解规则）。
    /// 拆解/合成消耗后库存减少但图鉴<b>不回灰</b>——点亮是永久资产。
    /// 由 GrantItem 写入；v4 迁移时用 OwnedItems 当前 keys 并集初始化。
    /// </summary>
    public List<string> DiscoveredItems { get; set; } = new();

    /// <summary>装扮预设列表（E5：整套装扮存/取/切换，背包窗口预设区）。</summary>
    public List<CosmeticPreset> Presets { get; set; } = new();

    /// <summary>
    /// 宝箱保底计数（GDD-02 §5：连续 N 箱未出 epic 及以上 → 下一箱必出 epic+）。
    /// 开箱后出 epic+ 即清零，否则 +1（封顶 threshold-1，下一箱触发保底）。
    /// </summary>
    public int ChestPityCount { get; set; }

    // ── E6 v6：通行证赛季（GDD-05；赛季窗口/XP/双轨领取/高级轨解锁）────────

    /// <summary>当前赛季 id（空串 = 尚未进入任何赛季；与赛季配置不符时由 PassService 惰性重置）。</summary>
    public string SeasonId { get; set; } = string.Empty;

    /// <summary>本赛季累计通行证 XP（等级 = 曲线阈值表推导，满级后不再累计）。</summary>
    public long SeasonXP { get; set; }

    /// <summary>已领取的里程碑奖励（键 = "seasonId:level:track"，如 "season_001:5:free"；领取幂等依据）。</summary>
    public List<string> ClaimedMilestones { get; set; } = new();

    /// <summary>高级轨是否已解锁（赛季内一次性，2500 金币；赛季切换时重置）。</summary>
    public bool PremiumUnlocked { get; set; }

    /// <summary>敲击 XP 每日重置标记日（本地日期 yyyy-MM-dd；字符串序比较防时钟回拨刷 XP）。</summary>
    public string DailyXpDate { get; set; } = string.Empty;

    /// <summary>当日敲击 XP 累计（GDD-05 §6：敲击 XP 每日上限 300，任务 XP 不占此额度）。</summary>
    public int DailyXpEarned { get; set; }

    /// <summary>当日已开宝箱数（通行证每日任务 openChest 进度；跨天由 PassService 重置）。</summary>
    public int DailyChestCount { get; set; }

    /// <summary>当日已合成次数（通行证每日任务 exchange 进度；跨天由 PassService 重置）。</summary>
    public int DailySynthCount { get; set; }

    /// <summary>今日已完成并自动入账 XP 的任务 id 列表（防同日重复入账）。</summary>
    public List<string> DailyCompletedQuests { get; set; } = new();

    // ── E7 v7：教程与主题（GDD-07；首次引导状态 + UI 主题持久化）────────────

    /// <summary>
    /// 新手教程是否已完成/跳过（完成后永不打扰；重看走托盘"新手教程"重播，不影响完成态）。
    /// v6 → v7 迁移时置 false：老玩家也会看到一次教程，作为新功能引导（E7 交接拍板）。
    /// </summary>
    public bool TutorialCompleted { get; set; }

    /// <summary>
    /// 教程步进度（0=敲敲看，1=拖拖看，2=找背包；GDD-07 §7 边界：按步骤粒度存进度，
    /// 退出/崩溃后重启从当前步续接；TutorialCompleted=true 时本字段无意义）。
    /// </summary>
    public int TutorialStep { get; set; }

    /// <summary>
    /// UI 主题 id（GDD-07 uitheme 槽位；只作用于背包/通行证/教程等普通窗口，
    /// 不影响猫本体渲染——那是 skin 槽位职责）。非法值由 ThemeService 回退默认奶油主题。
    /// </summary>
    public string UiThemeId { get; set; } = "cream";

    // ── E8 v8：云中继多人（房间码加入；服务器地址/显示名/上次房间码持久化）────

    /// <summary>
    /// 中继服务器地址（host:port 或裸 IP；E8 端口默认 9437 可省略）。
    /// 空串 = 多人功能未配置（多人窗口首次打开显示占位提示）。
    /// </summary>
    public string MpServerAddress { get; set; } = string.Empty;

    /// <summary>联机显示名（远程猫头顶名字牌；空由 UI 回退"猫猫"）。</summary>
    public string MpDisplayName { get; set; } = "猫猫";

    /// <summary>上次成功加入的房间码（下次打开多人窗口自动填入，方便直接重连；仅便利字段）。</summary>
    public string MpLastRoomCode { get; set; } = string.Empty;

    // ── v9：风格包（GDD-08 §3；风格是表达轴，全免费自选、不进经济）────────

    /// <summary>
    /// 猫风格 id（styles.json 中的 id，如 "clay"/"sticker"；空串 = 待选择）。
    /// 合法性由 StyleCatalog 防御校验：指向被移除/损坏的风格 → 回退 clay 并记日志
    /// （GDD-08 §6 边界 1），存档原值保留不改写。
    /// </summary>
    public string CatStyleId { get; set; } = string.Empty;

    /// <summary>
    /// 风格是否已选择（独立于 TutorialCompleted 的防重复弹标记，GDD-08 §3.1）：
    /// 选择器触发条件 = TutorialCompleted==false 且 StyleChosen==false——
    /// 选过风格但没做完教程的玩家重进不重复弹选择器；跳过 = 置 clay + true。
    /// </summary>
    public bool StyleChosen { get; set; }

    // ── v10：物种（GDD-09 §5；物种是表达轴，与风格同轴全免费自选、不进经济）──

    /// <summary>
    /// 物种 id（species.json 中的 id："cat"/"dino"；空串按 "cat" 解析——兼容防御，GDD-09 §5）。
    /// 合法性由 SpeciesCatalog 防御校验：指向被移除/损坏的物种 → 回退 cat 并记日志 +
    /// 托盘气泡一次，存档原值保留不改写。物种与风格在选择器中一次同时确定，
    /// 不新增 SpeciesChosen 标记（StyleChosen=true 即代表"物种+风格均已选定"，GDD-09 §3.2）。
    /// </summary>
    public string SpeciesId { get; set; } = "cat";

    // ── v11：宠物名字（E9 取名；存档/首登输入/托盘重命名/联机名册共用清洗规则）──

    /// <summary>
    /// 宠物名字（≤12 个字素、UTF-8 ≤24 字节；首登按物种预填默认名，用户清空回退默认名）。
    /// 空串 = 尚未取名（加载侧按当前物种补默认名，见 <see cref="PetNameRules"/>）。
    /// 联机时该值作为名册条目的名字字段发送（复用 DisplayName 线字段，旧端兼容）。
    /// </summary>
    public string PetName { get; set; } = string.Empty;

    /// <summary>当前物种对应的默认宠物名（空/未知物种回退猫）。</summary>
    public string DefaultPetName => PetNameRules.DefaultNameForSpecies(SpeciesId);

    /// <summary>尺寸是否为合法档位（容忍 1e-6 浮点误差）。</summary>
    public static bool IsValidWindowScale(double scale)
        => ValidWindowScales.Any(s => Math.Abs(s - scale) < 1e-6);
}

/// <summary>存档持久化服务（ADR-004：JSON + 原子写入 + .bak 轮换，Platform 层实现）。</summary>
public interface ISaveService
{
    /// <summary>内存模型，读取后直接改字段即可。</summary>
    SaveModel Current { get; }

    /// <summary>启动时加载：损坏 → .bak → 安全模式（保留默认值，绝不静默清档）。</summary>
    void Load();

    /// <summary>保存：临时文件写入 + File.Replace 原子替换 + .bak 轮换。</summary>
    void Save();
}

/// <summary>装扮预设快照（E5：三槽装备 id，空串 = 默认无装扮；按名称 upsert）。</summary>
public sealed record CosmeticPreset(string Name, string HatId, string SkinId, string BongosId);

/// <summary>
/// 存档版本迁移器（ADR-004 规则 2：链式升级 v(n-1) → v(n) → … → 当前版本）。
/// 纯逻辑、可单测；由 Platform 的 JsonSaveService 在反序列化后、字段拷贝前调用。
/// </summary>
public static class SaveMigrator
{
    /// <summary>
    /// 把磁盘读出的模型就地迁移到 <see cref="SaveModel.CurrentSchemaVersion"/>。
    /// 已是当前版本则原样返回（绝不覆盖用户已有设置）。
    /// </summary>
    /// <param name="model">反序列化出的存档模型（会被就地修改）。</param>
    /// <param name="log">可选迁移日志回调（记录升级轨迹）。</param>
    public static void MigrateToCurrent(SaveModel model, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        // v1 → v2（E2）：新增 WindowScale。v1 存档没有该字段，JSON 反序列化时保持默认 1.0；
        // 这里显式赋值并落版本号，保证迁移语义明确、未来 v3 可继续链式追加。
        if (model.SchemaVersion < 2)
        {
            model.WindowScale = 1.0;
            model.SchemaVersion = 2;
            log?.Invoke("[Save] 迁移 v1 → v2：新增 WindowScale=1.0");
        }

        // v2 → v3（E3）：新增库存/装备槽字段。旧存档反序列化后这些字段为类型默认值，
        // 这里显式赋值保证迁移语义明确（空库存、全部卸下、未初始化），并落版本号。
        if (model.SchemaVersion < 3)
        {
            model.OwnedItems ??= new Dictionary<string, int>(StringComparer.Ordinal);
            model.EquippedHat = model.EquippedHat ?? string.Empty;
            model.EquippedSkin = model.EquippedSkin ?? string.Empty;
            model.EquippedBongos = model.EquippedBongos ?? string.Empty;
            model.SchemaVersion = 3;
            log?.Invoke("[Save] 迁移 v2 → v3：新增 OwnedItems / Equipped* / InventoryInitialized（默认空库存）");
        }

        // v3 → v4（E4）：新增经济字段。旧存档没有金币与经济计数，默认零值起步；
        // DailyCoinDate 置空 → 首次进入 E4 时按"当日首次启动"发每日登录奖励。
        if (model.SchemaVersion < 4)
        {
            model.Coins = 0;
            model.DailyTapCount = 0;
            model.DailyCoinDate = string.Empty;
            model.IdleCoinAccumulator = 0;
            model.DropBucket ??= new List<long>();
            model.LastChestTicks = 0;
            model.ChestChargeSeconds = 0;
            model.SchemaVersion = 4;
            log?.Invoke("[Save] 迁移 v3 → v4：新增 Coins / DailyTapCount / DailyCoinDate / " +
                        "IdleCoinAccumulator / DropBucket / LastChestTicks / ChestChargeSeconds（默认零值）");
        }

        // v4 → v5（E5）：图鉴永久点亮 / 装扮预设 / 宝箱保底计数。
        // DiscoveredItems 用 OwnedItems 当前 keys 并集初始化（老玩家图鉴进度不回灰）；
        // 已点亮过（DiscoveredItems 非空）则绝不覆盖——迁移只增不改、幂等可重放。
        if (model.SchemaVersion < 5)
        {
            model.DiscoveredItems ??= new List<string>();
            if (model.DiscoveredItems.Count == 0 && model.OwnedItems is { Count: > 0 })
            {
                model.DiscoveredItems.AddRange(model.OwnedItems.Keys);
            }

            model.Presets ??= new List<CosmeticPreset>();
            model.ChestPityCount = 0;
            model.SchemaVersion = 5;
            log?.Invoke("[Save] 迁移 v4 → v5：新增 DiscoveredItems（由库存 keys 点亮）/ Presets / ChestPityCount");
        }

        // v5 → v6（E6）：通行证赛季字段。旧存档无赛季进度，全部默认零值起步
        // （SeasonId 空 → 首次进入 E6 时按当前赛季惰性装载；金币/物品/图鉴/预设永不动）。
        if (model.SchemaVersion < 6)
        {
            model.SeasonId = string.Empty;
            model.SeasonXP = 0;
            model.ClaimedMilestones ??= new List<string>();
            model.PremiumUnlocked = false;
            model.DailyXpDate = string.Empty;
            model.DailyXpEarned = 0;
            model.DailyChestCount = 0;
            model.DailySynthCount = 0;
            model.DailyCompletedQuests ??= new List<string>();
            model.SchemaVersion = 6;
            log?.Invoke("[Save] 迁移 v5 → v6：新增 SeasonId / SeasonXP / ClaimedMilestones / PremiumUnlocked / " +
                        "DailyXpDate / DailyXpEarned / DailyChestCount / DailySynthCount / DailyCompletedQuests（默认零值）");
        }

        // v6 → v7（E7）：新手教程状态 + UI 主题。旧存档无教程字段 → TutorialCompleted=false
        //（老玩家也会看到一次教程作为新功能引导，E7 交接拍板）；主题回默认奶油。
        // 金币/物品/图鉴/预设/赛季进度永不动（迁移只增不改、幂等可重放）。
        if (model.SchemaVersion < 7)
        {
            model.TutorialCompleted = false;
            model.TutorialStep = 0;
            model.UiThemeId = "cream";
            model.SchemaVersion = 7;
            log?.Invoke("[Save] 迁移 v6 → v7：新增 TutorialCompleted=false / TutorialStep=0 / UiThemeId=cream");
        }

        // v7 → v8（E8）：云中继多人字段。旧存档无联机配置 → 服务器地址空（功能未配置）、
        // 显示名默认"猫猫"、上次房间码空。金币/物品/教程/主题等一切既有状态永不动。
        if (model.SchemaVersion < 8)
        {
            model.MpServerAddress = string.Empty;
            model.MpDisplayName = "猫猫";
            model.MpLastRoomCode = string.Empty;
            model.SchemaVersion = 8;
            log?.Invoke("[Save] 迁移 v7 → v8：新增 MpServerAddress（空）/ MpDisplayName=猫猫 / MpLastRoomCode（空）");
        }

        // v8 → v9（GDD-08）：风格包字段。旧存档无风格选择 → CatStyleId 空（待选择）+
        // StyleChosen=false（首次启动弹"第 0 步选形象"选择器；E7 v6→v7 迁移已把老玩家
        // TutorialCompleted 置 false，故老玩家同样看一次选择器——与教程引导同一口径）。
        // 金币/物品/装扮/教程/主题/联机字段永不动（迁移只增不改、幂等可重放）。
        if (model.SchemaVersion < 9)
        {
            model.CatStyleId = string.Empty;
            model.StyleChosen = false;
            model.SchemaVersion = 9;
            log?.Invoke("[Save] 迁移 v8 → v9：新增 CatStyleId（空=待选择）/ StyleChosen=false");
        }

        // v9 → v10（GDD-09）：物种字段。旧存档无物种 → SpeciesId="cat"（默认）；
        // 同时刻意清 CatStyleId="" + StyleChosen=false（设计决策：物种与风格在选择器
        // 升级版第 0 步中一次同时确定，全体老玩家重看一次选择器，顺便获得选龙机会，
        // 与 v8→v9 迁移策略同构；GDD-09 §3.2/§5）。金币/物品/装扮/教程/主题/联机字段
        // 永不动（迁移只增不改、幂等可重放）。
        if (model.SchemaVersion < 10)
        {
            model.SpeciesId = "cat";
            model.CatStyleId = string.Empty;
            model.StyleChosen = false;
            model.SchemaVersion = 10;
            log?.Invoke("[Save] 迁移 v9 → v10：新增 SpeciesId=cat；清 CatStyleId/StyleChosen（重看升级版选择器）");
        }

        // v10 → v11（E9 取名）：宠物名字字段。旧存档无该字段 → 按当前物种补默认名
        // （cat=猫猫 / dino=小恐龙 / agua=阿瓜）；已有名字按清洗规则收敛（超长/控制符/空）。
        // 金币/物品/装扮/教程/主题/联机/风格/物种字段永不动（迁移只增不改、幂等可重放）。
        if (model.SchemaVersion < 11)
        {
            string defaultName = PetNameRules.DefaultNameForSpecies(model.SpeciesId);
            model.PetName = PetNameRules.Sanitize(model.PetName, defaultName);
            model.SchemaVersion = 11;
            log?.Invoke($"[Save] 迁移 v10 → v11：新增 PetName=\"{model.PetName}\"（按物种 {model.SpeciesId} 补默认名）");
        }

        // 防御：无论来自哪个版本，宠物名都按统一规则收敛（手改存档/旧档残留控制符兜底）
        string expectedName = PetNameRules.Sanitize(model.PetName, PetNameRules.DefaultNameForSpecies(model.SpeciesId));
        if (!string.Equals(model.PetName, expectedName, StringComparison.Ordinal))
        {
            log?.Invoke($"[Save] PetName 脏值收敛为 \"{expectedName}\"");
            model.PetName = expectedName;
        }

        // 防御：无论来自哪个版本，收敛非法尺寸到 100%（防止手改存档/旧 bug 造成窗口异常）
        if (!SaveModel.IsValidWindowScale(model.WindowScale))
        {
            log?.Invoke($"[Save] WindowScale={model.WindowScale:0.###} 非法档位，收敛为 1.0");
            model.WindowScale = 1.0;
        }

        // 防御：经济字段不可为负（手改存档兜底）
        if (model.Coins < 0)
        {
            log?.Invoke("[Save] Coins 为负，收敛为 0");
            model.Coins = 0;
        }

        // 防御：赛季 XP / 每日 XP 不可为负（手改存档兜底；负值会破坏等级阈值判定）
        if (model.SeasonXP < 0)
        {
            log?.Invoke("[Save] SeasonXP 为负，收敛为 0");
            model.SeasonXP = 0;
        }

        if (model.DailyXpEarned < 0)
        {
            log?.Invoke("[Save] DailyXpEarned 为负，收敛为 0");
            model.DailyXpEarned = 0;
        }

        // 防御：教程步进度不可为负（手改存档兜底；越界大步号由 TutorialService.Start 收敛）
        if (model.TutorialStep < 0)
        {
            log?.Invoke("[Save] TutorialStep 为负，收敛为 0");
            model.TutorialStep = 0;
        }
    }
}
