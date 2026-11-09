using System.Globalization;
using System.Text.Json;
using BongoCat.Core.Cosmetics;

namespace BongoCat.Core.Config;

/// <summary>economy.json 结构非法（权重和不等于 1 / 未知品质 / 非法区间 / 未知宝箱引用等）。</summary>
public sealed class EconomyConfigException : Exception
{
    public EconomyConfigException(string message) : base(message) { }
}

// ── 数值子规则（GDD-02 §6.5 economy.json 结构；全部只读，运行期不可变）──────────

/// <summary>敲击产币规则：每 CoinsPer 次有效敲击 1 金币，每日上限 DailyCap（用户拍板 500）。</summary>
public sealed record KeystrokeRule(int CoinsPer, int DailyCap);

/// <summary>在线滴灌规则：每 SecondsPerCoin 秒在线 1 金币（无上限，量级温和）。</summary>
public sealed record DripRule(double SecondsPerCoin);

/// <summary>
/// 挂机掉落规则：应用每 IntervalMinutes 分钟 roll 一次——
/// Nothing 概率无掉落 / CoinPack 概率金币包 uniform[CoinPackMin, CoinPackMax] / 其余概率物品（Weights 品质权重）。
/// </summary>
public sealed record IdleDropRule(
    double IntervalMinutes,
    double Nothing,
    double CoinPack,
    double Item,
    int CoinPackMin,
    int CoinPackMax,
    IReadOnlyDictionary<ItemQuality, double> Weights);

/// <summary>宝箱定义：Id / 售价 Price（金币购买为后续 Epic 预留）/ 每箱物品数 Items / 品质权重 Weights。</summary>
public sealed record ChestRule(string Id, int Price, int Items, IReadOnlyDictionary<ItemQuality, double> Weights);

/// <summary>免费宝箱充能规则：在线累计 SecondsPerChest 秒可开一个 ChestId 类型的宝箱。</summary>
public sealed record ChestChargeRule(double SecondsPerChest, string ChestId);

/// <summary>掉落物品桶：WindowHours 小时滚动窗口内最多 MaxItems 件，超上限暂停物品掉落。</summary>
public sealed record DropBucketRule(double WindowHours, int MaxItems);

/// <summary>
/// 宝箱保底规则（GDD-02 §5）：连续 Threshold 箱未出 MinQuality 及以上 → 该箱必出 MinQuality+。
/// ExcludedChestIds = 不参与保底的宝箱 id（GDD-02 §5 明确保底只对"银/金箱"——
/// wooden 售价低，保底会击穿"期望回收率 &lt; 100%"红线形成买箱套利）。
/// 被排除的宝箱既不触发也不推进保底计数（计数只属于参战箱）。
/// </summary>
public sealed record PityRule(int Threshold, ItemQuality MinQuality, IReadOnlyList<string> ExcludedChestIds)
{
    public PityRule(int Threshold, ItemQuality MinQuality)
        : this(Threshold, MinQuality, ["wooden"]) { } // GDD-02 §5 默认只对银/金箱
}

/// <summary>库存上限规则（E5）：单件物品最多 MaxPerItem 件，超出自动折算金币（按拆解价）。</summary>
public sealed record InventoryLimitRule(int MaxPerItem);

/// <summary>
/// 经济数值总配置（GDD-02 §6：全部数值集中于 economy.json，数据驱动可热调）。
/// 纯内存数据结构，零 IO；解析失败 fail-fast（<see cref="EconomyConfigException"/>），
/// 保证带病数值不会流进经济逻辑（回收率红线、防宏上限都依赖数值合法）。
/// </summary>
public sealed class EconomyConfig
{
    /// <summary>权重和校验容差（浮点 JSON 精度）。</summary>
    private const double WeightSumTolerance = 1e-6;

    public KeystrokeRule Keystroke { get; init; } = new(250, 500);

    public DripRule Drip { get; init; } = new(120);

    /// <summary>每日首次启动登录奖励（金币）。</summary>
    public int DailyLoginBonus { get; init; } = 50;

    public IdleDropRule IdleDrop { get; init; } = new(
        15, 0.50, 0.30, 0.20, 15, 35,
        new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.70,
            [ItemQuality.Uncommon] = 0.22,
            [ItemQuality.Rare] = 0.07,
            [ItemQuality.Epic] = 0.009,
            [ItemQuality.Legendary] = 0.001,
        });

    public IReadOnlyList<ChestRule> Chests { get; init; } =
    [
        new("wooden", 150, 2, new Dictionary<ItemQuality, double>
        {
            [ItemQuality.Common] = 0.55,
            [ItemQuality.Uncommon] = 0.28,
            [ItemQuality.Rare] = 0.13,
            [ItemQuality.Epic] = 0.035,
            [ItemQuality.Legendary] = 0.005,
        }),
    ];

    /// <summary>品质 → 拆解价（unique 不可拆解，不在表内；GDD-02 §6.1）。</summary>
    public IReadOnlyDictionary<ItemQuality, long> Salvage { get; init; } = new Dictionary<ItemQuality, long>
    {
        [ItemQuality.Common] = 10,
        [ItemQuality.Uncommon] = 40,
        [ItemQuality.Rare] = 150,
        [ItemQuality.Epic] = 600,
        [ItemQuality.Legendary] = 2500,
    };

    /// <summary>重复物品自动折算比例（拆解价 × 该比例 = 返还金币，GDD-02 §5）。</summary>
    public double DuplicateConversionRate { get; init; } = 0.60;

    public ChestChargeRule ChestCharge { get; init; } = new(7200, "wooden");

    public DropBucketRule DropBucket { get; init; } = new(24, 40);

    /// <summary>宝箱保底（GDD-02 §5：连续 15 箱未出 epic+ → 下一箱必出 epic+）。</summary>
    public PityRule Pity { get; init; } = new(15, ItemQuality.Epic);

    /// <summary>单件物品库存上限（超出自动按拆解价折算金币，E5）。</summary>
    public InventoryLimitRule InventoryLimit { get; init; } = new(9);

    /// <summary>按 id 取宝箱定义；不存在返回 false（加载期已校验 ChestCharge 引用，运行期防御兜底）。</summary>
    public bool TryGetChest(string id, out ChestRule chest)
    {
        foreach (ChestRule c in Chests)
        {
            if (string.Equals(c.Id, id, StringComparison.Ordinal))
            {
                chest = c;
                return true;
            }
        }

        chest = null!;
        return false;
    }

    /// <summary>
    /// 从 JSON 文本解析经济配置（纯逻辑、可单测；嵌入资源读取由上层负责）。
    /// 校验规则：权重表非空、权重和 ≈ 1、品质名合法且不含 unique（不掉落不出箱，GDD-02 §6.1）、
    /// 区间/数量/价格为非负、拆解价五品质齐全为正、折算率 ∈ (0,1]、ChestCharge 引用存在的宝箱。
    /// </summary>
    public static EconomyConfig ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        EconomyFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<EconomyFileDto>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new EconomyConfigException($"economy.json 不是合法 JSON：{ex.Message}");
        }

        if (dto is null)
        {
            throw new EconomyConfigException("economy.json 内容为空");
        }

        var keystroke = ValidateKeystroke(dto.Keystroke);
        var drip = ValidateDrip(dto.Drip);
        int dailyBonus = dto.DailyLoginBonus ?? 50;
        if (dailyBonus < 0)
        {
            throw new EconomyConfigException("dailyLoginBonus 不能为负");
        }

        var idleDrop = ValidateIdleDrop(dto.IdleDrop);
        var chests = ValidateChests(dto.Chests);
        var salvage = ValidateSalvage(dto.Salvage);
        double rate = dto.DuplicateConversionRate ?? 0.60;
        if (rate is <= 0 or > 1)
        {
            throw new EconomyConfigException($"duplicateConversionRate 必须在 (0,1] 区间：{rate}");
        }

        var charge = ValidateChestCharge(dto.ChestCharge, chests);
        var bucket = ValidateDropBucket(dto.DropBucket);
        var pity = ValidatePity(dto.Pity);
        var inventoryLimit = ValidateInventoryLimit(dto.Inventory);

        return new EconomyConfig
        {
            Keystroke = keystroke,
            Drip = drip,
            DailyLoginBonus = dailyBonus,
            IdleDrop = idleDrop,
            Chests = chests,
            Salvage = salvage,
            DuplicateConversionRate = rate,
            ChestCharge = charge,
            DropBucket = bucket,
            Pity = pity,
            InventoryLimit = inventoryLimit,
        };
    }

    // ── 校验辅助（非法即抛 EconomyConfigException，绝不带病运行）──────────────

    private static KeystrokeRule ValidateKeystroke(KeystrokeDto? dto)
    {
        if (dto is null || dto.CoinsPer <= 0)
        {
            throw new EconomyConfigException("keystroke.coinsPer 必须为正数");
        }

        if (dto.DailyCap < 0)
        {
            throw new EconomyConfigException("keystroke.dailyCap 不能为负");
        }

        return new KeystrokeRule(dto.CoinsPer, dto.DailyCap);
    }

    private static DripRule ValidateDrip(DripDto? dto)
    {
        if (dto is null || dto.SecondsPerCoin <= 0)
        {
            throw new EconomyConfigException("drip.secondsPerCoin 必须为正数");
        }

        return new DripRule(dto.SecondsPerCoin);
    }

    private static IdleDropRule ValidateIdleDrop(IdleDropDto? dto)
    {
        if (dto is null)
        {
            throw new EconomyConfigException("缺少 idleDrop 配置");
        }

        if (dto.IntervalMinutes <= 0)
        {
            throw new EconomyConfigException("idleDrop.intervalMinutes 必须为正数");
        }

        if (dto.Nothing < 0 || dto.CoinPack < 0 || dto.Item < 0)
        {
            throw new EconomyConfigException("idleDrop 概率不能为负");
        }

        if (Math.Abs(dto.Nothing + dto.CoinPack + dto.Item - 1.0) > WeightSumTolerance)
        {
            throw new EconomyConfigException(
                $"idleDrop 三项概率之和必须为 1：{dto.Nothing}+{dto.CoinPack}+{dto.Item}");
        }

        if (dto.CoinPackMin < 0 || dto.CoinPackMin > dto.CoinPackMax)
        {
            throw new EconomyConfigException("idleDrop.coinPackMin/Max 非法（min ≥ 0 且 min ≤ max）");
        }

        return new IdleDropRule(
            dto.IntervalMinutes, dto.Nothing, dto.CoinPack, dto.Item,
            dto.CoinPackMin, dto.CoinPackMax, ParseWeights(dto.Weights, "idleDrop.weights"));
    }

    private static IReadOnlyList<ChestRule> ValidateChests(List<ChestDto>? dtos)
    {
        if (dtos is null || dtos.Count == 0)
        {
            throw new EconomyConfigException("chests 数组缺失或为空");
        }

        var chests = new List<ChestRule>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChestDto? e in dtos)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Id))
            {
                throw new EconomyConfigException("chests 含 null 或空 id 元素");
            }

            if (!seen.Add(e.Id))
            {
                throw new EconomyConfigException($"宝箱 id 重复：{e.Id}");
            }

            if (e.Price < 0)
            {
                throw new EconomyConfigException($"宝箱 {e.Id} 的 price 不能为负");
            }

            if (e.Items < 1)
            {
                throw new EconomyConfigException($"宝箱 {e.Id} 的 items 必须至少为 1");
            }

            chests.Add(new ChestRule(e.Id, e.Price, e.Items, ParseWeights(e.Weights, $"chests[{e.Id}].weights")));
        }

        return chests;
    }

    private static IReadOnlyDictionary<ItemQuality, long> ValidateSalvage(SalvageDto? dto)
    {
        if (dto is null)
        {
            throw new EconomyConfigException("缺少 salvage 配置");
        }

        var table = new Dictionary<ItemQuality, long>
        {
            [ItemQuality.Common] = RequirePositive(dto.Common, "salvage.common"),
            [ItemQuality.Uncommon] = RequirePositive(dto.Uncommon, "salvage.uncommon"),
            [ItemQuality.Rare] = RequirePositive(dto.Rare, "salvage.rare"),
            [ItemQuality.Epic] = RequirePositive(dto.Epic, "salvage.epic"),
            [ItemQuality.Legendary] = RequirePositive(dto.Legendary, "salvage.legendary"),
        };
        return table;

        static long RequirePositive(long value, string name)
            => value > 0 ? value : throw new EconomyConfigException($"{name} 必须为正数");
    }

    private static ChestChargeRule ValidateChestCharge(ChestChargeDto? dto, IReadOnlyList<ChestRule> chests)
    {
        if (dto is null || dto.SecondsPerChest <= 0)
        {
            throw new EconomyConfigException("chestCharge.secondsPerChest 必须为正数");
        }

        if (string.IsNullOrWhiteSpace(dto.ChestId) || !chests.Any(c => c.Id == dto.ChestId))
        {
            throw new EconomyConfigException($"chestCharge.chestId='{dto.ChestId}' 不在 chests 列表中");
        }

        return new ChestChargeRule(dto.SecondsPerChest, dto.ChestId);
    }

    private static DropBucketRule ValidateDropBucket(DropBucketDto? dto)
    {
        if (dto is null || dto.WindowHours <= 0)
        {
            throw new EconomyConfigException("dropBucket.windowHours 必须为正数");
        }

        if (dto.MaxItems < 0)
        {
            throw new EconomyConfigException("dropBucket.maxItems 不能为负");
        }

        return new DropBucketRule(dto.WindowHours, dto.MaxItems);
    }

    private static PityRule ValidatePity(PityDto? dto)
    {
        if (dto is null)
        {
            return new PityRule(15, ItemQuality.Epic); // 缺省回落默认（GDD-02 §5）
        }

        if (dto.Threshold < 1)
        {
            throw new EconomyConfigException("pity.threshold 必须至少为 1");
        }

        if (!Enum.TryParse<ItemQuality>(dto.MinQuality, ignoreCase: true, out ItemQuality minQuality)
            || minQuality is not (ItemQuality.Epic or ItemQuality.Legendary or ItemQuality.Unique))
        {
            throw new EconomyConfigException($"pity.minQuality 必须为 epic 及以上品质：{dto.MinQuality}");
        }

        var excluded = dto.ExcludeChestIds ?? ["wooden"]; // GDD-02 §5 默认只对银/金箱
        if (excluded.Any(string.IsNullOrWhiteSpace))
        {
            throw new EconomyConfigException("pity.excludeChestIds 不能含空白 id");
        }

        return new PityRule(dto.Threshold, minQuality, excluded);
    }

    private static InventoryLimitRule ValidateInventoryLimit(InventoryDto? dto)
    {
        if (dto is null)
        {
            return new InventoryLimitRule(9); // 缺省回落默认（E5 任务书：单件 9 件）
        }

        if (dto.MaxPerItem < 1)
        {
            throw new EconomyConfigException("inventory.maxPerItem 必须至少为 1");
        }

        return new InventoryLimitRule(dto.MaxPerItem);
    }

    /// <summary>解析品质权重表：品质名合法、不含 unique、权重非负、和 ≈ 1。</summary>
    private static IReadOnlyDictionary<ItemQuality, double> ParseWeights(
        Dictionary<string, double>? weights, string fieldName)
    {
        if (weights is null || weights.Count == 0)
        {
            throw new EconomyConfigException($"{fieldName} 缺失或为空");
        }

        var result = new Dictionary<ItemQuality, double>(weights.Count);
        foreach ((string key, double weight) in weights)
        {
            if (!Enum.TryParse<ItemQuality>(key, ignoreCase: true, out ItemQuality quality))
            {
                throw new EconomyConfigException($"{fieldName} 未知品质：{key}");
            }

            if (quality == ItemQuality.Unique)
            {
                // GDD-02 §6.1：unique 不掉落、不出箱——权重表出现即视为配置错误
                throw new EconomyConfigException($"{fieldName} 不允许出现 unique 权重");
            }

            if (weight < 0)
            {
                throw new EconomyConfigException($"{fieldName} 权重不能为负：{key}={weight}");
            }

            if (!result.TryAdd(quality, weight))
            {
                throw new EconomyConfigException($"{fieldName} 品质重复：{key}");
            }
        }

        double sum = result.Values.Sum();
        if (Math.Abs(sum - 1.0) > WeightSumTolerance)
        {
            throw new EconomyConfigException($"{fieldName} 权重之和必须为 1：实际 {sum.ToString("R", CultureInfo.InvariantCulture)}");
        }

        return result;
    }

    /// <summary>economy.json 属性名用小驼峰，解析忽略大小写并容忍人工注释与尾逗号（与 items.json 同策略）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class EconomyFileDto
    {
        public KeystrokeDto? Keystroke { get; set; }
        public DripDto? Drip { get; set; }
        public int? DailyLoginBonus { get; set; }
        public IdleDropDto? IdleDrop { get; set; }
        public List<ChestDto>? Chests { get; set; }
        public SalvageDto? Salvage { get; set; }
        public double? DuplicateConversionRate { get; set; }
        public ChestChargeDto? ChestCharge { get; set; }
        public DropBucketDto? DropBucket { get; set; }
        public PityDto? Pity { get; set; }
        public InventoryDto? Inventory { get; set; }
    }

    private sealed class KeystrokeDto
    {
        public int CoinsPer { get; set; }
        public int DailyCap { get; set; }
    }

    private sealed class DripDto
    {
        public double SecondsPerCoin { get; set; }
    }

    private sealed class IdleDropDto
    {
        public double IntervalMinutes { get; set; }
        public double Nothing { get; set; }
        public double CoinPack { get; set; }
        public double Item { get; set; }
        public int CoinPackMin { get; set; }
        public int CoinPackMax { get; set; }
        public Dictionary<string, double>? Weights { get; set; }
    }

    private sealed class ChestDto
    {
        public string? Id { get; set; }
        public int Price { get; set; }
        public int Items { get; set; }
        public Dictionary<string, double>? Weights { get; set; }
    }

    private sealed class ChestChargeDto
    {
        public double SecondsPerChest { get; set; }
        public string? ChestId { get; set; }
    }

    private sealed class DropBucketDto
    {
        public double WindowHours { get; set; }
        public int MaxItems { get; set; }
    }

    private sealed class PityDto
    {
        public int Threshold { get; set; }
        public string? MinQuality { get; set; }
        public List<string>? ExcludeChestIds { get; set; }
    }

    private sealed class InventoryDto
    {
        public int MaxPerItem { get; set; }
    }

    private sealed class SalvageDto
    {
        public long Common { get; set; }
        public long Uncommon { get; set; }
        public long Rare { get; set; }
        public long Epic { get; set; }
        public long Legendary { get; set; }
    }
}
