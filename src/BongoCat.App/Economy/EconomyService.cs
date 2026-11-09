using System.Globalization;
using BongoCat.App.Inventory;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

namespace BongoCat.App.Economy;

/// <summary>
/// <see cref="IEconomyService"/> 默认实现：规则在 App 层（纯逻辑可测），
/// 状态全部落在 <see cref="ISaveService"/> 内存模型（存档即单一数据源，跨会话经存档字段续接）。
/// </summary>
/// <remarks>
/// 防作弊边界（GDD-02 §7 + 任务 §6）：
/// - 日上限：敲击产币按本地日期封顶，跨天以"日期字符串序比较"重置——回拨时钟只会让 today &lt; saved，
///   走"不重置不奖励"分支，无法刷每日奖励/上限；
/// - 心跳补算上限：待机恢复间隔 &gt; 10 分钟不补算（GDD-02 §3）；
/// - 掉落桶：24h 滚动窗口（墙钟 UTC ticks，跨会话续接），超上限暂停物品掉落，最老条目过期后自动恢复。
/// </remarks>
public sealed class EconomyService : IEconomyService
{
    /// <summary>心跳补算上限（秒）：超过视为待机/挂起，本间隔不补收益（GDD-02 §3）。</summary>
    public const double MaxHeartbeatSeconds = 600;

    /// <summary>脏存档节流落盘间隔（秒，epics E4-S2）。</summary>
    public const double SaveThrottleSeconds = 30;

    private readonly ISaveService _save;
    private readonly IInventoryService _inventory;
    private readonly EconomyConfig _config;
    private readonly Random _random;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    private long _lastTickTimestamp = -1;   // 上次 Tick（单调时钟；-1 = 尚未 Tick）
    private long _lastDropTimestamp = -1;   // 上次掉落 roll（单调时钟；会话内起算，离线不累计）
    private long _lastFlushTimestamp = -1;  // 上次节流落盘
    private bool _dirty;

    public event Action<CoinSource, long>? CoinsChanged;

    public event Action<CoinSource, long>? CoinsSpent;

    public event Action<DropEvent>? DropOccurred;

    /// <summary>一次宝箱成功开启（免费或购买；E6 通行证每日任务事件源）。</summary>
    public event Action? ChestOpened;

    public EconomyService(
        ISaveService save,
        IInventoryService inventory,
        EconomyConfig config,
        Random random,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    private SaveModel Save => _save.Current;

    public long Coins => Save.Coins;

    public bool CanOpenChest
        => Save.ChestChargeSeconds + 1e-6 >= _config.ChestCharge.SecondsPerChest;

    public TimeSpan TimeToNextChest
        => TimeSpan.FromSeconds(Math.Max(0, _config.ChestCharge.SecondsPerChest - Save.ChestChargeSeconds));

    public TimeSpan TimeToNextDrop
    {
        get
        {
            double interval = _config.IdleDrop.IntervalMinutes * 60;
            if (_lastDropTimestamp < 0)
            {
                return TimeSpan.FromSeconds(interval);
            }

            double elapsed = _time.GetElapsedTime(_lastDropTimestamp, _time.GetTimestamp()).TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(0, interval - elapsed));
        }
    }

    public int DropBucketCount => Save.DropBucket.Count;

    /// <inheritdoc />
    public void Initialize()
    {
        CleanupExpiredBucket();
        EnsureDailyReset();
        long now = _time.GetTimestamp();
        _lastTickTimestamp = now;    // 心跳基准从启动起算（首个 Tick 不重复累计启动前时间）
        _lastDropTimestamp = now;    // 掉落间隔从启动起算（离线不累计）
        FlushNow();
        _log?.Invoke($"[Economy] 初始化完成：金币 {Save.Coins}，掉落桶 {Save.DropBucket.Count}/{_config.DropBucket.MaxItems}");
    }

    /// <inheritdoc />
    public void OnValidTap()
    {
        EnsureDailyReset();

        // 敲击产币：每 CoinsPer 次 1 币，当日累计产币封顶 DailyCap（防按键宏刷币，达上限敲击仍正常）
        int per = _config.Keystroke.CoinsPer;
        int cap = _config.Keystroke.DailyCap;
        int before = Math.Min(Save.DailyTapCount / per, cap);
        Save.DailyTapCount++;
        int after = Math.Min(Save.DailyTapCount / per, cap);

        if (after > before)
        {
            AddCoins(after - before, CoinSource.Tap);
        }

        MarkDirty();
    }

    /// <inheritdoc />
    public void Tick()
    {
        long now = _time.GetTimestamp();
        double elapsed = _lastTickTimestamp < 0
            ? 0
            : _time.GetElapsedTime(_lastTickTimestamp, now).TotalSeconds;
        _lastTickTimestamp = now;

        if (elapsed <= 0)
        {
            return; // 时钟回拨/重复 Tick：负间隔零收益（防刷）
        }

        if (elapsed > MaxHeartbeatSeconds)
        {
            // 待机恢复巨间隔：不补算（GDD-02 §3），只重新起算
            _log?.Invoke($"[Economy] 心跳间隔 {elapsed:0}s > {MaxHeartbeatSeconds:0}s，本段不补算");
            elapsed = 0;
        }

        // ① 在线滴灌：每 SecondsPerCoin 秒 1 币（余数进存档累积器，跨 Tick/跨会话续接）
        double perCoin = _config.Drip.SecondsPerCoin;
        Save.IdleCoinAccumulator += elapsed;
        if (Save.IdleCoinAccumulator >= perCoin)
        {
            long coins = (long)(Save.IdleCoinAccumulator / perCoin);
            Save.IdleCoinAccumulator -= coins * perCoin;
            AddCoins(coins, CoinSource.Drip);
        }

        // ② 免费宝箱充能（封顶 1 箱：开箱消耗后重新积累）
        Save.ChestChargeSeconds = Math.Min(
            Save.ChestChargeSeconds + elapsed, _config.ChestCharge.SecondsPerChest);

        // ③ 挂机掉落 roll（到点即 roll，间隔重置）
        double dropInterval = _config.IdleDrop.IntervalMinutes * 60;
        if (_lastDropTimestamp >= 0 &&
            _time.GetElapsedTime(_lastDropTimestamp, now).TotalSeconds >= dropInterval)
        {
            _lastDropTimestamp = now;
            RollDrop();
        }

        MarkDirty();

        // ④ 脏存档节流落盘（敲击/滴灌/充能高频小改动不逐次写盘）
        if (_dirty &&
            (_lastFlushTimestamp < 0 ||
             _time.GetElapsedTime(_lastFlushTimestamp, now).TotalSeconds >= SaveThrottleSeconds))
        {
            FlushNow();
        }
    }

    /// <inheritdoc />
    public bool TryOpenChest(out IReadOnlyList<ChestOpenResult> results)
    {
        results = [];

        if (!CanOpenChest)
        {
            return false;
        }

        if (!_config.TryGetChest(_config.ChestCharge.ChestId, out ChestRule? chest))
        {
            // 加载期已校验 chestId 引用；此处为程序 bug 防御，fail-fast
            throw new InvalidOperationException($"充能宝箱 id 不在 chests 列表中：{_config.ChestCharge.ChestId}");
        }

        results = OpenChestItems(chest);
        Save.ChestChargeSeconds = Math.Max(0, Save.ChestChargeSeconds - _config.ChestCharge.SecondsPerChest);
        Save.LastChestTicks = _time.GetTimestamp();
        FlushNow();
        ChestOpened?.Invoke();
        _log?.Invoke($"[Economy] 开{chest.Id}宝箱：{string.Join("、", results.Select(DescribeGrant))}");
        return true;
    }

    // ── E5：金币支出 / 买箱 ──────────────────────────────────────────────

    /// <inheritdoc />
    public void GrantCoins(long amount, CoinSource source) => AddCoins(amount, source);

    /// <inheritdoc />
    public bool TrySpendCoins(long amount, CoinSource source)
    {
        if (amount <= 0)
        {
            return false; // 支出必须为正（记账不可为负，防御性拒绝）
        }

        if (Save.Coins < amount)
        {
            _log?.Invoke($"[Economy] 支出拒绝：余额 {Save.Coins} < 需要 {amount}（{source}）");
            return false;
        }

        Save.Coins -= amount;
        FlushNow();
        CoinsSpent?.Invoke(source, amount);
        _log?.Invoke($"[Economy] 支出 {amount} 金币（{source}），余额 {Save.Coins}");
        return true;
    }

    /// <inheritdoc />
    public bool TryBuyChest(string chestId, out IReadOnlyList<ChestOpenResult> results)
    {
        results = [];

        if (!_config.TryGetChest(chestId, out ChestRule? chest))
        {
            _log?.Invoke($"[Economy] 买箱拒绝：未知宝箱 id {chestId}");
            return false;
        }

        if (!TrySpendCoins(chest.Price, CoinSource.Purchase))
        {
            return false; // 金币不足：零状态变化
        }

        results = OpenChestItems(chest); // 与免费开箱同一 roll/保底/入库/重复折算链路
        Save.LastChestTicks = _time.GetTimestamp();
        FlushNow();
        ChestOpened?.Invoke();
        _log?.Invoke($"[Economy] 购买并开启{chest.Id}宝箱（{chest.Price} 金币）：{string.Join("、", results.Select(DescribeGrant))}");
        return true;
    }

    /// <summary>
    /// 按宝箱权重 roll 出 Items 件物品入库（免费开箱与买箱共用链路）。
    /// E5 保底（GDD-02 §5）：连续 Threshold 箱未出 epic+ → 本箱首件必出 epic+
    /// （强制品质满足 minQuality 语义；box 内其余件照常 roll）；出 epic+ 清零计数，否则 +1。
    /// </summary>
    private List<ChestOpenResult> OpenChestItems(ChestRule chest)
    {
        // 保底只对参战箱生效（GDD-02 §5"银/金箱"：ExcludedChestIds 内的宝箱既不触发也不推进计数）
        bool pityEligible = !_config.Pity.ExcludedChestIds.Contains(chest.Id);
        bool pityDue = pityEligible && Save.ChestPityCount >= _config.Pity.Threshold - 1;
        bool gotMinQuality = false;
        var opened = new List<ChestOpenResult>(chest.Items);
        for (int i = 0; i < chest.Items; i++)
        {
            ItemQuality quality = pityDue && i == 0
                ? _config.Pity.MinQuality
                : RollQuality(chest.Weights);
            if (quality >= _config.Pity.MinQuality)
            {
                gotMinQuality = true;
            }

            CosmeticItem item = PickItemOfQuality(quality);
            ItemGrantResult grant = _inventory.GrantItem(item.Id); // 内部落盘 + Changed 广播
            long dupCoins = AwardDuplicateConversion(item.Quality, grant.WasDuplicate);
            opened.Add(new ChestOpenResult(item, grant.WasDuplicate, dupCoins));
        }

        if (pityEligible)
        {
            Save.ChestPityCount = gotMinQuality ? 0 : Math.Min(Save.ChestPityCount + 1, _config.Pity.Threshold - 1);
        }

        if (pityDue)
        {
            _log?.Invoke($"[Economy] 宝箱保底触发（连续 {_config.Pity.Threshold} 箱无 {_config.Pity.MinQuality}+），本箱必出");
        }

        return opened;
    }

    // ── 每日重置 / 防时钟回拨 ────────────────────────────────────────────

    /// <summary>
    /// 每日重置判定（本地日期字符串比较）：today &gt; saved → 重置敲击计数 + 每日登录奖励；
    /// today &lt; saved（时钟回拨）→ 不重置不奖励（防刷）；相等 → 无事。
    /// 日期为空（首次启动 / v3 升级用户）→ 视为当日首次启动。
    /// </summary>
    private void EnsureDailyReset()
    {
        string today = NowDateString();
        string saved = Save.DailyCoinDate;

        if (string.IsNullOrEmpty(saved))
        {
            Save.DailyCoinDate = today;
            GrantDailyLoginBonus();
            return;
        }

        int cmp = string.CompareOrdinal(today, saved);
        if (cmp > 0)
        {
            Save.DailyCoinDate = today;
            Save.DailyTapCount = 0; // 跨天：敲击产币计数重置（日上限重新开始累计）
            GrantDailyLoginBonus();
            _log?.Invoke($"[Economy] 跨天重置（{saved} → {today}）：敲击产币计数已清零");
        }
        else if (cmp < 0)
        {
            _log?.Invoke($"[Economy] 检测到系统时钟回拨（存档日期 {saved} 晚于今天 {today}），不重置不奖励");
        }
    }

    private void GrantDailyLoginBonus()
    {
        if (_config.DailyLoginBonus > 0)
        {
            AddCoins(_config.DailyLoginBonus, CoinSource.DailyLogin);
            _log?.Invoke($"[Economy] 每日首次启动：+{_config.DailyLoginBonus} 金币（{NowDateString()}）");
        }
    }

    private string NowDateString()
        => _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── 挂机掉落 ─────────────────────────────────────────────────────────

    private void RollDrop()
    {
        CleanupExpiredBucket();

        double roll = _random.NextDouble();
        IdleDropRule drop = _config.IdleDrop;

        if (roll < drop.Nothing)
        {
            DropOccurred?.Invoke(DropEvent.Nothing);
            return;
        }

        if (roll < drop.Nothing + drop.CoinPack)
        {
            long coins = _random.Next(drop.CoinPackMin, drop.CoinPackMax + 1); // uniform 含上界
            AddCoins(coins, CoinSource.DropCoinPack);
            FlushNow();
            DropOccurred?.Invoke(new DropEvent(DropKind.CoinPack, coins, null, false, 0));
            return;
        }

        // 物品掉落：桶满 → 暂停（24h 滚动窗口上限，任务 §2；最老条目过期后下次 roll 自动恢复）
        if (Save.DropBucket.Count >= _config.DropBucket.MaxItems)
        {
            _log?.Invoke($"[Economy] 掉落桶已满（{_config.DropBucket.MaxItems} 件/{_config.DropBucket.WindowHours:0}h），本次物品掉落暂停");
            DropOccurred?.Invoke(new DropEvent(DropKind.Suppressed, 0, null, false, 0));
            return;
        }

        ItemQuality quality = RollQuality(drop.Weights);
        CosmeticItem item = PickItemOfQuality(quality);

        ItemGrantResult grant = _inventory.GrantItem(item.Id); // 内部落盘 + Changed 广播
        long dupCoins = AwardDuplicateConversion(item.Quality, grant.WasDuplicate);

        Save.DropBucket.Add(_time.GetUtcNow().UtcTicks); // 墙钟 UTC ticks：跨会话续接 24h 窗口
        FlushNow();

        DropOccurred?.Invoke(new DropEvent(DropKind.Item, dupCoins, item, grant.WasDuplicate, dupCoins));
    }

    /// <summary>清理掉落桶中超出 24h 窗口的过期条目（加载/每次 roll 前调用）。</summary>
    private void CleanupExpiredBucket()
    {
        long cutoff = _time.GetUtcNow().UtcTicks
            - (long)TimeSpan.FromHours(_config.DropBucket.WindowHours).Ticks;
        int removed = Save.DropBucket.RemoveAll(t => t <= cutoff);
        if (removed > 0)
        {
            _dirty = true;
            _log?.Invoke($"[Economy] 掉落桶过期清理 {removed} 件（剩余 {Save.DropBucket.Count}）");
        }
    }

    /// <summary>按品质权重 roll 品质（权重和已由加载期校验 ≈ 1）。</summary>
    private ItemQuality RollQuality(IReadOnlyDictionary<ItemQuality, double> weights)
    {
        double roll = _random.NextDouble() * weights.Values.Sum();
        double acc = 0;
        foreach ((ItemQuality quality, double weight) in weights)
        {
            acc += weight;
            if (roll < acc)
            {
                return quality;
            }
        }

        return weights.Keys.Last(); // 浮点兜底（理论上不可达）
    }

    private CosmeticItem PickItemOfQuality(ItemQuality quality)
    {
        List<CosmeticItem> pool = _inventory.Catalog.Items.Where(i => i.Quality == quality).ToList();
        if (pool.Count == 0)
        {
            // 目录与经济配置不一致属于程序 bug：fail-fast 暴露，不静默空掉
            throw new InvalidOperationException($"items.json 中品质 {quality} 没有任何物品，无法掉落/开箱");
        }

        return pool[_random.Next(pool.Count)];
    }

    /// <summary>重复品自动折算：拆解价 × 折算率（向下取整，GDD-02 §5），非重复零收益。</summary>
    private long AwardDuplicateConversion(ItemQuality quality, bool wasDuplicate)
    {
        if (!wasDuplicate)
        {
            return 0;
        }

        long coins = (long)Math.Floor(_config.Salvage[quality] * _config.DuplicateConversionRate);
        if (coins > 0)
        {
            AddCoins(coins, CoinSource.DuplicateConversion);
        }

        return coins;
    }

    // ── 记账 / 落盘 ──────────────────────────────────────────────────────

    /// <summary>金币入账（只增不减、来源审计；amount ≤ 0 直接忽略——记账不可为负）。</summary>
    private void AddCoins(long amount, CoinSource source)
    {
        if (amount <= 0)
        {
            return;
        }

        Save.Coins += amount;
        _dirty = true;
        CoinsChanged?.Invoke(source, amount);
    }

    private void MarkDirty() => _dirty = true;

    private void FlushNow()
    {
        _save.Save();
        _dirty = false;
        _lastFlushTimestamp = _time.GetTimestamp();
    }

    private static string DescribeGrant(ChestOpenResult r)
        => r.WasDuplicate ? $"{r.Item.DisplayName}（重复 → +{r.DuplicateCoins}）" : $"{r.Item.DisplayName}（新）";
}
