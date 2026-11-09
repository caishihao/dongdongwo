using BongoCat.App.Inventory;
using BongoCat.Core.Save;

namespace BongoCat.App.Presets;

/// <summary>
/// 装扮预设服务（E5；epics E3-S6 的存档内实现——V1 预设随主存档持久化，
/// 不再走 E3 草案的多文件 presets/*.json：预设是玩家资产，跟随存档备份/迁移链更安全）。
/// 规则：名称 trim 后非空、按名称 upsert（覆盖同名）；应用 = 三槽一次切换（单次落盘 + 单次广播）；
/// 应用时非法/已拆解的物品 id 自动跳过该槽（保留当前装备，不崩溃——GDD-03 §7 失效引用策略）。
/// 线程纪律：UI 线程调用（与 InventoryService 一致）。
/// </summary>
public interface IPresetService
{
    /// <summary>当前预设列表（存档单一数据源的只读快照）。</summary>
    IReadOnlyList<CosmeticPreset> Presets { get; }

    /// <summary>保存当前三槽装备为命名预设（同名覆盖）。名称为空/纯空白 → false。</summary>
    bool SavePreset(string name, out string? failureReason);

    /// <summary>应用预设（三槽原子切换）。名称不存在 → false，零状态变化。</summary>
    bool ApplyPreset(string name, out string? failureReason);

    /// <summary>删除预设。名称不存在 → false。</summary>
    bool DeletePreset(string name);
}

public sealed class PresetService : IPresetService
{
    private readonly ISaveService _save;
    private readonly IInventoryService _inventory;
    private readonly Action<string>? _log;

    public PresetService(ISaveService save, IInventoryService inventory, Action<string>? log = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _log = log;
    }

    private SaveModel Save => _save.Current;

    public IReadOnlyList<CosmeticPreset> Presets => Save.Presets;

    public bool SavePreset(string name, out string? failureReason)
    {
        failureReason = null;
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            failureReason = "预设名称不能为空";
            return false;
        }

        CosmeticPreset preset = new(
            trimmed,
            _inventory.EquippedHat,
            _inventory.EquippedSkin,
            _inventory.EquippedBongos);

        int index = Save.Presets.FindIndex(p => string.Equals(p.Name, trimmed, StringComparison.Ordinal));
        bool replaced = index >= 0;
        if (replaced)
        {
            Save.Presets[index] = preset;
        }
        else
        {
            Save.Presets.Add(preset);
        }

        _save.Save();
        _log?.Invoke($"[Preset] {(replaced ? "覆盖" : "保存")}预设「{trimmed}」" +
                     $"（帽 {preset.HatId} / 皮 {preset.SkinId} / 鼓 {preset.BongosId}）");
        return true;
    }

    public bool ApplyPreset(string name, out string? failureReason)
    {
        failureReason = null;
        CosmeticPreset? preset = Save.Presets.Find(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (preset is null)
        {
            failureReason = "预设不存在";
            return false;
        }

        // 三槽一次切换（ApplyOutfit 单次落盘 + 单次广播；非法 id 槽位保留原装备）
        _inventory.ApplyOutfit(preset.HatId, preset.SkinId, preset.BongosId);
        _log?.Invoke($"[Preset] 应用预设「{preset.Name}」");
        return true;
    }

    public bool DeletePreset(string name)
    {
        int removed = Save.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (removed == 0)
        {
            return false;
        }

        _save.Save();
        _log?.Invoke($"[Preset] 删除预设「{name}」");
        return true;
    }
}
