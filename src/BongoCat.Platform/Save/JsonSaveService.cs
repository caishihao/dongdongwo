using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BongoCat.Core.Save;

namespace BongoCat.Platform.Save;

/// <summary>
/// JSON 存档服务（ADR-004 决定 C）：%APPDATA%\BongoCatRemix\save.json。
/// 可靠性规则落地（逐条对照 ADR-004）：
/// 1. 原子写入：save.json.tmp → File.Replace（有旧文件）/ File.Move（首次）；
/// 2. schemaVersion 顶层字段（E2 起 v2：新增 WindowScale；SaveMigrator 链式迁移，ADR-004 规则 2）；
/// 3. .bak 轮换：每次成功保存前把旧存档滚为 .bak；
/// 4. 校验：文件头带 payload 的 CRC32，校验失败视为损坏 → .bak → 安全模式（绝不静默清档）。
/// </summary>
public sealed class JsonSaveService : ISaveService
{
    private const string FileName = "save.json";
    private const string TmpSuffix = ".tmp";
    private const string BakSuffix = ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, // 人工可读可调试（ADR-004 选 JSON 的加分项）
    };

    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly string _bakPath;
    private readonly Action<string>? _log;

    public SaveModel Current { get; } = new();

    public JsonSaveService(Action<string>? log = null, string? directoryOverride = null)
    {
        _log = log;
        // directoryOverride：仅供单测做写读回环（临时目录），绝不触碰真实用户存档；
        // 生产路径传 null → %APPDATA%\BongoCatRemix\save.json（ADR-004 决定 C）。
        // BONGOCAT_SAVE_DIR：烟测专用目录覆盖（CI/人工烟测隔离真实存档；
        // GetFolderPath 不读 APPDATA 环境变量——Shell API 直取用户配置，故需显式覆盖口）。
        string dir = directoryOverride ??
            (Environment.GetEnvironmentVariable("BONGOCAT_SAVE_DIR") is { Length: > 0 } smokeDir
                ? smokeDir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BongoCatRemix"));
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, FileName);
        _tmpPath = _filePath + TmpSuffix;
        _bakPath = _filePath + BakSuffix;
    }

    /// <inheritdoc />
    public void Load()
    {
        if (TryLoadFrom(_filePath))
        {
            return;
        }

        // 主文件损坏/不存在 → 尝试 .bak（ADR-004 规则 4）
        _log?.Invoke("[Save] 主存档不可用，尝试备份 .bak");
        if (TryLoadFrom(_bakPath))
        {
            return;
        }

        // 安全模式：保留内存默认值（绝不静默清档，首次运行也走此处）
        _log?.Invoke("[Save] 备份也不可用，进入安全模式（默认存档，本次退出时覆写）");
    }

    private bool TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var envelope = JsonSerializer.Deserialize<SaveEnvelope>(File.ReadAllText(path));
            if (envelope is null || envelope.PayloadJson is null)
            {
                return false;
            }

            // CRC32 校验不匹配 → 损坏（ADR-004 规则 5）
            if (Crc32.Compute(envelope.PayloadJson) != envelope.Checksum)
            {
                _log?.Invoke($"[Save] {path} 校验失败（checksum 不匹配）");
                return false;
            }

            var model = JsonSerializer.Deserialize<SaveModel>(envelope.PayloadJson);
            if (model is null || model.SchemaVersion > SaveModel.CurrentSchemaVersion)
            {
                // 未知未来版本：只读不写（ADR-004 规则 2）
                _log?.Invoke("[Save] 存档版本高于当前程序，已忽略");
                return false;
            }

            // 版本迁移（ADR-004 规则 2）：v1 → v2（新增 WindowScale）等链式升级，先迁移再拷贝
            SaveMigrator.MigrateToCurrent(model, _log);

            ApplyLoaded(model);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Save] 加载 {path} 异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>把磁盘模型逐字段拷入内存单例（保持 Current 引用稳定）。</summary>
    private void ApplyLoaded(SaveModel model)
    {
        Current.SchemaVersion = model.SchemaVersion;
        Current.CatPositionRelX = Math.Clamp(model.CatPositionRelX, 0.0, 1.0);
        Current.CatPositionRelY = Math.Clamp(model.CatPositionRelY, 0.0, 1.0);
        Current.CatMirrored = model.CatMirrored;
        Current.MasterVolume = model.MasterVolume;
        Current.Muted = model.Muted;
        Current.WindowScale = model.WindowScale;

        // E3 v3：库存与装备槽
        Current.OwnedItems = new Dictionary<string, int>(model.OwnedItems ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        Current.EquippedHat = model.EquippedHat ?? string.Empty;
        Current.EquippedSkin = model.EquippedSkin ?? string.Empty;
        Current.EquippedBongos = model.EquippedBongos ?? string.Empty;
        Current.InventoryInitialized = model.InventoryInitialized;

        // E4 v4：经济字段（负值防御已由 SaveMigrator 收敛，这里再做一次钳制兜底）
        Current.Coins = Math.Max(0, model.Coins);
        Current.DailyTapCount = Math.Max(0, model.DailyTapCount);
        Current.DailyCoinDate = model.DailyCoinDate ?? string.Empty;
        Current.IdleCoinAccumulator = Math.Max(0, model.IdleCoinAccumulator);
        Current.DropBucket = new List<long>(model.DropBucket ?? new List<long>());
        Current.LastChestTicks = model.LastChestTicks;
        Current.ChestChargeSeconds = Math.Max(0, model.ChestChargeSeconds);

        // E5 v5：图鉴点亮 / 装扮预设 / 宝箱保底（负值防御兜底）
        Current.DiscoveredItems = new List<string>(model.DiscoveredItems ?? new List<string>());
        Current.Presets = new List<CosmeticPreset>(model.Presets ?? new List<CosmeticPreset>());
        Current.ChestPityCount = Math.Max(0, model.ChestPityCount);

        // E6 v6：通行证赛季字段（负值防御兜底）
        Current.SeasonId = model.SeasonId ?? string.Empty;
        Current.SeasonXP = Math.Max(0, model.SeasonXP);
        Current.ClaimedMilestones = new List<string>(model.ClaimedMilestones ?? new List<string>());
        Current.PremiumUnlocked = model.PremiumUnlocked;
        Current.DailyXpDate = model.DailyXpDate ?? string.Empty;
        Current.DailyXpEarned = Math.Max(0, model.DailyXpEarned);
        Current.DailyChestCount = Math.Max(0, model.DailyChestCount);
        Current.DailySynthCount = Math.Max(0, model.DailySynthCount);
        Current.DailyCompletedQuests = new List<string>(model.DailyCompletedQuests ?? new List<string>());

        // E7 v7：教程状态与 UI 主题（bool/string 原样拷贝；主题 id 合法性由 ThemeService 防御回退，
        // 步进度负值已由 SaveMigrator 收敛——此处防回退纪律：v7 字段必须逐字段拷贝，否则不续档）
        Current.TutorialCompleted = model.TutorialCompleted;
        Current.TutorialStep = Math.Max(0, model.TutorialStep);
        Current.UiThemeId = model.UiThemeId ?? string.Empty;

        // E8 v8：云中继多人字段（string 原样拷贝；地址/房间码格式由多人窗口在连接时校验，
        // 显示名空值由 UI 回退默认——此处防回退纪律：v8 字段必须逐字段拷贝，否则不续档）
        Current.MpServerAddress = model.MpServerAddress ?? string.Empty;
        Current.MpDisplayName = model.MpDisplayName ?? string.Empty;
        Current.MpLastRoomCode = model.MpLastRoomCode ?? string.Empty;

        // v9：风格包字段（GDD-08）。string 原样拷贝；风格 id 合法性由 StyleCatalog 在
        // 渲染侧防御回退 clay（存档原值保留）——此处防回退纪律：v9 字段必须逐字段拷贝，
        // 否则风格选择不续档（E6 教训，ApplyLoaded 漏拷 = 新字段静默丢失）
        Current.CatStyleId = model.CatStyleId ?? string.Empty;
        Current.StyleChosen = model.StyleChosen;

        // v10：物种字段（GDD-09）。string 原样拷贝；空串/非法 id 由 SpeciesCatalog 在
        // App 层防御回退 cat（存档原值保留）——防回退纪律：v10 字段必须逐字段拷贝，
        // 否则物种选择不续档（E6 教训，ApplyLoaded 漏拷 = 新字段静默丢失）
        Current.SpeciesId = model.SpeciesId ?? string.Empty;
    }

    /// <inheritdoc />
    public void Save()
    {
        try
        {
            string payload = JsonSerializer.Serialize(Current, JsonOptions);
            var envelope = new SaveEnvelope
            {
                SchemaVersion = SaveModel.CurrentSchemaVersion,
                Checksum = Crc32.Compute(payload),
                PayloadJson = payload,
            };

            File.WriteAllText(_tmpPath, JsonSerializer.Serialize(envelope, JsonOptions));

            // .bak 轮换（ADR-004 规则 4）：写成功后旧存档滚为 .bak
            if (File.Exists(_filePath))
            {
                File.Replace(_tmpPath, _filePath, _bakPath); // 原子替换（ADR-004 规则 1）
            }
            else
            {
                File.Move(_tmpPath, _filePath); // 首次保存
            }
        }
        catch (Exception ex)
        {
            // 保存失败不抛出（桌宠不能因磁盘问题崩溃），交由下次保存重试
            _log?.Invoke($"[Save] 保存失败：{ex.Message}");
        }
    }
}

/// <summary>存档信封：payload JSON + CRC32 校验（ADR-004 规则 5）。</summary>
internal sealed class SaveEnvelope
{
    public int SchemaVersion { get; set; }
    public uint Checksum { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}

/// <summary>最小 CRC32 实现（IEEE 802.3 多项式，无第三方依赖）。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(string text)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
