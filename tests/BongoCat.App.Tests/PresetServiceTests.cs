using BongoCat.App.Inventory;
using BongoCat.App.Presets;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 装扮预设服务测试（E5；epics E3-S6 验收口径）：保存/应用/删除 CRUD、同名覆盖、
/// 空名拒绝、应用时物品已拆解 → 该槽保留原装备（GDD-03 §7 失效引用策略）、预设随存档持久化。
/// </summary>
public class PresetServiceTests
{
    private readonly FakeSaveService _save = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private (InventoryService Inventory, PresetService Presets) Create()
    {
        var inventory = new InventoryService(_save, _catalog);
        inventory.Initialize(); // 授予新手装（未预置标记），装备/应用测试有真实物品可用
        return (inventory, new PresetService(_save, inventory));
    }

    [Fact]
    public void 保存预设_捕获当前三槽装备_随存档落盘()
    {
        var (inventory, presets) = Create();
        inventory.Equip("hat_beret");
        inventory.Equip("skin_orange");
        int saves = _save.SaveCount;

        Assert.True(presets.SavePreset("日常", out _));

        CosmeticPreset preset = Assert.Single(_save.Current.Presets);
        Assert.Equal("日常", preset.Name);
        Assert.Equal("hat_beret", preset.HatId);
        Assert.Equal("skin_orange", preset.SkinId);
        Assert.Equal("bongos_wood", preset.BongosId); // 新手装默认鼓
        Assert.Equal(saves + 1, _save.SaveCount);
    }

    [Fact]
    public void 保存预设_空名与纯空白_拒绝()
    {
        var (_, presets) = Create();

        Assert.False(presets.SavePreset("", out string? reason1));
        Assert.Contains("不能为空", reason1);
        Assert.False(presets.SavePreset("   ", out string? reason2));
        Assert.Contains("不能为空", reason2);
        Assert.Empty(_save.Current.Presets);
    }

    [Fact]
    public void 保存预设_同名覆盖_upsert()
    {
        var (inventory, presets) = Create();
        inventory.Equip("hat_beret");
        presets.SavePreset("日常", out _);
        inventory.GrantItem("hat_santa"); // rare 帽非新手装，先入库
        inventory.Equip("hat_santa");

        Assert.True(presets.SavePreset("日常", out _)); // 同名再存

        CosmeticPreset preset = Assert.Single(_save.Current.Presets); // 不新增条目
        Assert.Equal("hat_santa", preset.HatId);                      // 内容已更新
    }

    [Fact]
    public void 应用预设_三槽原子切换_单次广播()
    {
        var (inventory, presets) = Create();
        inventory.GrantItem("bongos_neon"); // rare 鼓非新手装，先入库
        inventory.Equip("hat_beret");
        inventory.Equip("skin_orange");
        inventory.Equip("bongos_neon");
        presets.SavePreset("节日", out _);

        inventory.Unequip(ItemSlot.Hat);
        inventory.Unequip(ItemSlot.Skin);
        inventory.Unequip(ItemSlot.Bongos);
        int fired = 0;
        inventory.Changed += () => fired++;

        Assert.True(presets.ApplyPreset("节日", out _));

        Assert.Equal("hat_beret", inventory.EquippedHat);
        Assert.Equal("skin_orange", inventory.EquippedSkin);
        Assert.Equal("bongos_neon", inventory.EquippedBongos);
        Assert.Equal(1, fired); // 三槽一次切换
    }

    [Fact]
    public void 应用预设_物品已拆解_该槽保留原装备_不崩溃()
    {
        var (inventory, presets) = Create();
        inventory.Equip("hat_beret");   // uncommon 帽
        presets.SavePreset("日常", out _);

        inventory.Unequip(ItemSlot.Hat);
        inventory.RemoveOne("hat_beret"); // 模拟拆解：预设里的帽子已不在库存

        Assert.True(presets.ApplyPreset("日常", out _));

        Assert.Equal(string.Empty, inventory.EquippedHat);  // 非法引用跳过（保持卸下态）
        Assert.Equal("skin_cream", inventory.EquippedSkin); // 其余槽正常应用（新手装默认）
    }

    [Fact]
    public void 应用预设_名称不存在_拒绝()
    {
        var (_, presets) = Create();

        Assert.False(presets.ApplyPreset("不存在", out string? reason));
        Assert.Contains("不存在", reason);
    }

    [Fact]
    public void 删除预设_移除并落盘_不存在返回false()
    {
        var (_, presets) = Create();
        presets.SavePreset("日常", out _);

        Assert.True(presets.DeletePreset("日常"));
        Assert.Empty(_save.Current.Presets);
        Assert.False(presets.DeletePreset("日常")); // 已删
        Assert.False(presets.DeletePreset("从未存在"));
    }

    [Fact]
    public void 预设列表_暴露存档单一数据源()
    {
        var (_, presets) = Create();
        presets.SavePreset("日常", out _);
        presets.SavePreset("节日", out _);

        Assert.Equal(2, presets.Presets.Count);
        Assert.Equal(["日常", "节日"], presets.Presets.Select(p => p.Name).ToList());
    }
}
