using System.Globalization;
using System.Text.Json;

namespace BongoCat.Core.Config;

/// <summary>seasons/*.json 结构非法（日期区间倒挂 / 奖励越界 / 双轨重复 / 未知任务类型等）。</summary>
public sealed class SeasonConfigException : Exception
{
    public SeasonConfigException(string message) : base(message) { }
}

/// <summary>通行证奖励轨别（GDD-05 §5：双轨——免费轨直接领，高级轨需金币一次性解锁）。</summary>
public enum PassTrack
{
    /// <summary>免费轨（所有玩家可领）。</summary>
    Free,

    /// <summary>高级轨（需 PremiumUnlockCoins 金币一次性解锁）。</summary>
    Premium,
}

/// <summary>通行证奖励类型（GDD-05 §4：金币/物品；Token 未在 GDD-05 定义 → 不设奖励类型）。</summary>
public enum PassRewardKind
{
    /// <summary>金币（经经济层 GrantCoins 入账，来源审计）。</summary>
    Coins,

    /// <summary>物品（经库存层 GrantItem 入库，超出单件上限自动折算金币）。</summary>
    Item,
}

/// <summary>每日任务类型（GDD-05 §5 每日任务池）。</summary>
public enum PassQuestType
{
    /// <summary>开启宝箱（免费开箱与金币买箱均计数）。</summary>
    OpenChest,

    /// <summary>累计有效敲击。</summary>
    Keystrokes,

    /// <summary>合成（3 合 1）次数。</summary>
    Exchange,
}

/// <summary>每日任务规则：Id / 类型 / 目标数量 / 完成奖励 XP（GDD-05 §5：任务 XP 完成时自动入账，无需手动领）。</summary>
public sealed record PassQuestRule(string Id, PassQuestType Type, int Target, int Xp);

/// <summary>通行证里程碑奖励：Level / Track / 类型（金币 Amount 或物品 ItemId）。</summary>
public sealed record PassReward(
    int Level,
    PassTrack Track,
    PassRewardKind Kind,
    long Amount = 0,
    string ItemId = "");

/// <summary>
/// 赛季配置（GDD-05 §6 seasons/&lt;id&gt;.json 结构）：赛季窗口、等级曲线、双轨奖励表、每日任务池。
/// 纯内存数据结构，零 IO；解析失败 fail-fast（<see cref="SeasonConfigException"/>），
/// 保证带病赛季配置不会流进通行证逻辑（奖励越界/双轨重复都会破坏领取幂等语义）。
/// </summary>
public sealed class SeasonConfig
{
    public string SeasonId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>赛季开始日（本地时区日期，含当日）。</summary>
    public DateOnly StartDate { get; init; }

    /// <summary>赛季结束日（本地时区日期，含当日；GDD-05 §5 窗口含两端）。</summary>
    public DateOnly EndDate { get; init; }

    /// <summary>满级等级（GDD-05 §6 默认 50）。</summary>
    public int MaxLevel { get; init; } = 50;

    /// <summary>等级曲线：每级需求 = Base + (level-1) × Step（GDD-05 §6 线性微增）。</summary>
    public int XpCurveBase { get; init; } = 100;

    public int XpCurveStep { get; init; } = 10;

    /// <summary>高级轨一次性解锁价（用户拍板 2500 金币，无内购铁律）。</summary>
    public long PremiumUnlockCoins { get; init; } = 2500;

    /// <summary>每日任务池（每日取 QuestsPerDay 条轮换；任务 XP 完成时自动入账）。</summary>
    public IReadOnlyList<PassQuestRule> DailyQuestPool { get; init; } = [];

    /// <summary>双轨奖励表（按 Level + Track 唯一）。</summary>
    public IReadOnlyList<PassReward> Rewards { get; init; } = [];

    /// <summary>每日激活任务条数（GDD-05 §5：3 条/日）。</summary>
    public const int QuestsPerDay = 3;

    /// <summary>
    /// 达到 Level 级所需的累计 XP（GDD-05 §6：每级需求 = Base + (level-1)×Step）。
    /// 1 级阈值为 0（赛季开局即可领 1 级奖励）；纯计算、幂等。
    /// </summary>
    public long ThresholdForLevel(int level)
    {
        long total = 0;
        for (int i = 1; i < level; i++)
        {
            total += XpCurveBase + (i - 1) * XpCurveStep;
        }

        return total;
    }

    /// <summary>该日期是否处于赛季窗口（本地时区；窗口含两端，GDD-05 §5）。</summary>
    public bool IsActiveOn(DateOnly date) => StartDate <= date && date <= EndDate;

    /// <summary>查询某级某轨奖励；无该节点奖励返回 false（配置允许稀疏奖励表）。</summary>
    public bool TryGetReward(int level, PassTrack track, out PassReward reward)
    {
        foreach (PassReward r in Rewards)
        {
            if (r.Level == level && r.Track == track)
            {
                reward = r;
                return true;
            }
        }

        reward = null!;
        return false;
    }

    /// <summary>
    /// 从 JSON 文本解析赛季配置（纯逻辑、可单测；嵌入资源读取由上层负责）。
    /// 校验规则：id/名称非空；日期合法且 endDate ≥ startDate；maxLevel ≥ 1；曲线 base ≥ 1 / step ≥ 0；
    /// 解锁价 ≥ 0；任务池条目类型合法 / 目标 ≥ 1 / XP ≥ 1 / id 唯一；
    /// 奖励 level ∈ [1, maxLevel]、轨别合法、金币额 ≥ 1 或物品 id 非空、(level, track) 不重复。
    /// </summary>
    public static SeasonConfig ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SeasonFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SeasonFileDto>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new SeasonConfigException($"赛季配置不是合法 JSON：{ex.Message}");
        }

        if (dto is null)
        {
            throw new SeasonConfigException("赛季配置内容为空");
        }

        if (string.IsNullOrWhiteSpace(dto.SeasonId))
        {
            throw new SeasonConfigException("seasonId 不能为空");
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new SeasonConfigException($"赛季 {dto.SeasonId} 缺少 name");
        }

        DateOnly start = ParseDate(dto.StartDate, dto.SeasonId, "startDate");
        DateOnly end = ParseDate(dto.EndDate, dto.SeasonId, "endDate");
        if (end < start)
        {
            throw new SeasonConfigException(
                $"赛季 {dto.SeasonId} 日期区间倒挂：{start} > {end}");
        }

        int maxLevel = dto.MaxLevel ?? 50;
        if (maxLevel < 1)
        {
            throw new SeasonConfigException($"赛季 {dto.SeasonId} maxLevel 必须至少为 1");
        }

        int baseXp = dto.XpCurve?.Base ?? 100;
        int step = dto.XpCurve?.Step ?? 10;
        if (baseXp < 1)
        {
            throw new SeasonConfigException($"赛季 {dto.SeasonId} xpCurve.base 必须至少为 1");
        }

        if (step < 0)
        {
            throw new SeasonConfigException($"赛季 {dto.SeasonId} xpCurve.step 不能为负");
        }

        long premiumCoins = dto.PremiumUnlockCoins ?? 2500;
        if (premiumCoins < 0)
        {
            throw new SeasonConfigException($"赛季 {dto.SeasonId} premiumUnlockCoins 不能为负");
        }

        var pool = ValidateQuestPool(dto.DailyQuestPool, dto.SeasonId);
        var rewards = ValidateRewards(dto.Rewards, dto.SeasonId, maxLevel);

        return new SeasonConfig
        {
            SeasonId = dto.SeasonId,
            Name = dto.Name,
            StartDate = start,
            EndDate = end,
            MaxLevel = maxLevel,
            XpCurveBase = baseXp,
            XpCurveStep = step,
            PremiumUnlockCoins = premiumCoins,
            DailyQuestPool = pool,
            Rewards = rewards,
        };
    }

    // ── 校验辅助（非法即抛 SeasonConfigException，绝不带病运行）──────────────

    private static DateOnly ParseDate(string? text, string seasonId, string field)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date))
        {
            throw new SeasonConfigException(
                $"赛季 {seasonId} 的 {field} 非法（须为 yyyy-MM-dd）：{text ?? "<空>"}");
        }

        return date;
    }

    private static IReadOnlyList<PassQuestRule> ValidateQuestPool(List<QuestDto>? dtos, string seasonId)
    {
        if (dtos is null)
        {
            return []; // 无任务池 = 赛季只有敲击 XP（合法降级，GDD-05 §5 任务为 XP 加项）
        }

        var rules = new List<PassQuestRule>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (QuestDto? e in dtos)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Id))
            {
                throw new SeasonConfigException($"赛季 {seasonId} dailyQuestPool 含 null 或空 id 元素");
            }

            if (!seen.Add(e.Id))
            {
                throw new SeasonConfigException($"赛季 {seasonId} 任务 id 重复：{e.Id}");
            }

            if (!Enum.TryParse<PassQuestType>(e.Type, ignoreCase: true, out PassQuestType type))
            {
                throw new SeasonConfigException($"赛季 {seasonId} 任务 {e.Id} 未知类型：{e.Type}");
            }

            if (e.Target < 1)
            {
                throw new SeasonConfigException($"赛季 {seasonId} 任务 {e.Id} target 必须至少为 1");
            }

            if (e.Xp < 1)
            {
                throw new SeasonConfigException($"赛季 {seasonId} 任务 {e.Id} xp 必须至少为 1");
            }

            rules.Add(new PassQuestRule(e.Id, type, e.Target, e.Xp));
        }

        return rules;
    }

    private static IReadOnlyList<PassReward> ValidateRewards(List<RewardDto>? dtos, string seasonId, int maxLevel)
    {
        if (dtos is null)
        {
            return []; // 稀疏奖励表合法（无奖励节点仍显示，只是不可领取）
        }

        var rewards = new List<PassReward>(dtos.Count);
        var seen = new HashSet<(int, PassTrack)>();
        foreach (RewardDto? e in dtos)
        {
            if (e is null)
            {
                throw new SeasonConfigException($"赛季 {seasonId} rewards 含 null 元素");
            }

            if (e.Level < 1 || e.Level > maxLevel)
            {
                throw new SeasonConfigException(
                    $"赛季 {seasonId} 奖励 level={e.Level} 越界（须在 1~{maxLevel}）");
            }

            if (!Enum.TryParse<PassTrack>(e.Track, ignoreCase: true, out PassTrack track))
            {
                throw new SeasonConfigException($"赛季 {seasonId} 奖励 L{e.Level} 未知轨别：{e.Track}");
            }

            if (!seen.Add((e.Level, track)))
            {
                throw new SeasonConfigException(
                    $"赛季 {seasonId} 奖励节点重复：L{e.Level} {track} 轨（每级每轨最多 1 个奖励）");
            }

            PassReward reward;
            switch ((e.Type ?? string.Empty).ToLowerInvariant())
            {
                case "coins":
                    if (e.Amount < 1)
                    {
                        throw new SeasonConfigException(
                            $"赛季 {seasonId} 奖励 L{e.Level} {track} 金币额必须至少为 1");
                    }

                    reward = new PassReward(e.Level, track, PassRewardKind.Coins, e.Amount);
                    break;
                case "item":
                    if (string.IsNullOrWhiteSpace(e.ItemId))
                    {
                        throw new SeasonConfigException(
                            $"赛季 {seasonId} 奖励 L{e.Level} {track} 缺少 itemId");
                    }

                    reward = new PassReward(e.Level, track, PassRewardKind.Item, ItemId: e.ItemId);
                    break;
                default:
                    throw new SeasonConfigException(
                        $"赛季 {seasonId} 奖励 L{e.Level} {track} 未知类型：{e.Type}（GDD-05 仅定义 coins/item）");
            }

            rewards.Add(reward);
        }

        return rewards;
    }

    /// <summary>seasons/*.json 属性名用小驼峰，解析忽略大小写并容忍人工注释与尾逗号（与 items/economy 同策略）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class SeasonFileDto
    {
        public string? SeasonId { get; set; }
        public string? Name { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public int? MaxLevel { get; set; }
        public XpCurveDto? XpCurve { get; set; }
        public long? PremiumUnlockCoins { get; set; }
        public List<QuestDto>? DailyQuestPool { get; set; }
        public List<RewardDto>? Rewards { get; set; }
    }

    private sealed class XpCurveDto
    {
        public int Base { get; set; }
        public int Step { get; set; }
    }

    private sealed class QuestDto
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public int Target { get; set; }
        public int Xp { get; set; }
    }

    private sealed class RewardDto
    {
        public int Level { get; set; }
        public string? Track { get; set; }
        public string? Type { get; set; }
        public long Amount { get; set; }
        public string? ItemId { get; set; }
    }
}
