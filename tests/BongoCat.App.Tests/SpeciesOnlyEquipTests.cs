using BongoCat.App.Inventory;
using BongoCat.Core.Cosmetics;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 物种限定换装防御测试（GDD-09 §1 speciesOnly）：
/// 猫不可穿 dino_default（拒绝）、龙可穿四色板与 dino_default、
/// 换物种后 ValidateSpeciesCompatibility 卸下跨物种装备。
/// 目录用嵌入 items.json 真实数据（dino_default 带 speciesOnly=dino）。
/// </summary>
public class SpeciesOnlyEquipTests
{
    private readonly FakeSaveService _save = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private InventoryService CreateService()
    {
        var svc = new InventoryService(_save, _catalog);
        svc.Initialize();
        svc.GrantItem("dino_default"); // 测试用例手动持有恐龙专属皮肤
        return svc;
    }

    [Fact]
    public void dino_default_目录标记speciesOnly为dino()
    {
        Assert.True(_catalog.TryGet("dino_default", out CosmeticItem item));
        Assert.Equal("dino", item.SpeciesOnly);
        Assert.True(_catalog.TryGet("skin_cream", out CosmeticItem cream));
        Assert.Equal(string.Empty, cream.SpeciesOnly); // 四色板全物种可穿
    }

    [Fact]
    public void 猫装备dino_default_拒绝()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "cat";
        int saves = _save.SaveCount;

        Assert.False(svc.Equip("dino_default"));
        Assert.Equal("skin_cream", svc.EquippedSkin); // 原装备不动（默认装）
        Assert.Equal(saves, _save.SaveCount);         // 拒绝路径不落盘
    }

    [Fact]
    public void 龙装备dino_default_成功()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "dino";

        Assert.True(svc.Equip("dino_default"));
        Assert.Equal("dino_default", svc.EquippedSkin);
    }

    [Fact]
    public void 龙装备四色板_成功_物种通用物品不受限()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "dino";

        Assert.True(svc.Equip("skin_orange"));
        Assert.Equal("skin_orange", svc.EquippedSkin);
    }

    [Fact]
    public void 换物种到猫_ValidateSpeciesCompatibility_卸下dino_default()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "dino";
        Assert.True(svc.Equip("dino_default"));

        _save.Current.SpeciesId = "cat"; // 模拟托盘切换物种（存档已写）
        svc.ValidateSpeciesCompatibility();

        Assert.Equal(string.Empty, svc.EquippedSkin); // 跨物种装备卸下恢复默认
        Assert.Equal("hat_dev_cap", svc.EquippedHat); // 物种通用装备不动
    }

    // ── GDD-09 §7：阿瓜专属皮肤 agua_default（speciesOnly=agua）────────────

    [Fact]
    public void agua_default_目录标记speciesOnly为agua()
    {
        Assert.True(_catalog.TryGet("agua_default", out CosmeticItem item));
        Assert.Equal("agua", item.SpeciesOnly);
        Assert.Equal(ItemSlot.Skin, item.Slot);
    }

    [Fact]
    public void 阿瓜装备agua_default_成功()
    {
        InventoryService svc = CreateService();
        svc.GrantItem("agua_default"); // 测试用例手动持有阿瓜专属皮肤
        _save.Current.SpeciesId = "agua";

        Assert.True(svc.Equip("agua_default"));
        Assert.Equal("agua_default", svc.EquippedSkin);
    }

    [Fact]
    public void 猫装备agua_default_拒绝()
    {
        InventoryService svc = CreateService();
        svc.GrantItem("agua_default");
        _save.Current.SpeciesId = "cat";

        Assert.False(svc.Equip("agua_default"));
        Assert.Equal("skin_cream", svc.EquippedSkin); // 原装备不动（默认装）
    }

    [Fact]
    public void 龙装备agua_default_拒绝()
    {
        InventoryService svc = CreateService();
        svc.GrantItem("agua_default");
        _save.Current.SpeciesId = "dino";

        Assert.False(svc.Equip("agua_default"));
    }

    [Fact]
    public void 换物种到阿瓜_ValidateSpeciesCompatibility_卸下dino_default()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "dino";
        Assert.True(svc.Equip("dino_default"));

        _save.Current.SpeciesId = "agua"; // 托盘切到阿瓜
        svc.ValidateSpeciesCompatibility();

        Assert.Equal(string.Empty, svc.EquippedSkin); // 恐龙专属皮肤卸下（agua_default 免费默认，无需装备校验残留）
    }

    [Fact]
    public void 换物种到龙_ValidateSpeciesCompatibility_四色板保留()
    {
        InventoryService svc = CreateService();
        Assert.Equal("skin_cream", svc.EquippedSkin);

        _save.Current.SpeciesId = "dino";
        svc.ValidateSpeciesCompatibility();

        Assert.Equal("skin_cream", svc.EquippedSkin); // 全物种物品保留
    }

    [Fact]
    public void ValidateSpeciesCompatibility_无跨物种装备_零落盘()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "cat";
        int saves = _save.SaveCount;

        svc.ValidateSpeciesCompatibility();
        svc.ValidateSpeciesCompatibility();

        Assert.Equal(saves, _save.SaveCount); // 无变化不落盘（幂等）
    }

    [Fact]
    public void ApplyOutfit_物种不符id_该槽保留原装备()
    {
        InventoryService svc = CreateService();
        _save.Current.SpeciesId = "cat";
        Assert.Equal("skin_cream", svc.EquippedSkin);

        svc.ApplyOutfit(string.Empty, "dino_default", string.Empty);

        Assert.Equal("skin_cream", svc.EquippedSkin); // speciesOnly 不符 → 跳过保留
    }
}
