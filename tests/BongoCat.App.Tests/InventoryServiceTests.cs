using BongoCat.App.Inventory;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// 库存/装备服务测试（epics E3-S2：Equip/Unequip 全路径 + 新手装授予幂等 + 图鉴计算）。
/// 目录用嵌入的 items.json 真实数据（与生产同源，防测试/生产目录漂移）。
/// </summary>
public class InventoryServiceTests
{
    private readonly FakeSaveService _save = new();
    private readonly CosmeticCatalog _catalog = ItemCatalogLoader.LoadFromAssembly();

    private InventoryService CreateService() => new(_save, _catalog);

    [Fact]
    public void 首次初始化_授予5件新手装_默认装备三件common_并落盘()
    {
        InventoryService svc = CreateService();

        svc.Initialize();

        Assert.Equal(5, svc.Owned.Count);
        Assert.All(InventoryService.StarterKitIds, id => Assert.Equal(1, svc.Owned[id]));
        Assert.Equal("hat_dev_cap", svc.EquippedHat);
        Assert.Equal("skin_cream", svc.EquippedSkin);
        Assert.Equal("bongos_wood", svc.EquippedBongos);
        Assert.True(_save.Current.InventoryInitialized);
        Assert.Equal(1, _save.SaveCount); // 首启动写入存档
    }

    [Fact]
    public void 再次初始化_幂等_不重复发放不重置装备()
    {
        InventoryService svc = CreateService();
        svc.Initialize();
        svc.Equip("hat_beret");
        int savesAfterSetup = _save.SaveCount;

        svc.Initialize();
        svc.Initialize();

        Assert.Equal(5, svc.Owned.Count);                       // 不重复发放
        Assert.Equal(1, svc.Owned["hat_dev_cap"]);
        Assert.Equal("hat_beret", svc.EquippedHat);             // 不重置用户装备
        Assert.Equal(savesAfterSetup, _save.SaveCount);         // 幂等路径零落盘
    }

    [Fact]
    public void 装备未拥有物品_拒绝且零状态变化()
    {
        InventoryService svc = CreateService();
        svc.Initialize();
        int saves = _save.SaveCount;
        bool fired = false;
        svc.Changed += () => fired = true;

        Assert.False(svc.Equip("hat_santa"));    // 未拥有（rare）
        Assert.False(svc.Equip("nonexistent"));  // 未知 id
        Assert.Equal("hat_dev_cap", svc.EquippedHat); // 原装备不动
        Assert.Equal(saves, _save.SaveCount);    // 拒绝路径不落盘
        Assert.False(fired);                     // 拒绝路径不广播
    }

    [Fact]
    public void 同槽换装_槽位互斥_旧装备被替换()
    {
        InventoryService svc = CreateService();
        svc.Initialize();

        Assert.True(svc.Equip("hat_beret"));

        Assert.Equal("hat_beret", svc.EquippedHat); // 同槽唯一
        Assert.True(svc.IsOwned("hat_dev_cap"));    // 替换 ≠ 移除，旧帽仍在库存
        Assert.Equal("skin_cream", svc.EquippedSkin); // 跨槽不受影响
    }

    [Fact]
    public void 卸下_恢复默认空串并落盘()
    {
        InventoryService svc = CreateService();
        svc.Initialize();

        Assert.True(svc.Unequip(ItemSlot.Hat));

        Assert.Equal(string.Empty, svc.EquippedHat);
        Assert.True(svc.IsOwned("hat_dev_cap")); // 卸下不销毁
        Assert.Equal(2, _save.SaveCount);        // 初始化 + 卸下（立即落盘纪律）
    }

    [Fact]
    public void 存档装备引用失效_初始化时静默卸下不崩溃()
    {
        _save.Current.EquippedHat = "hat_deleted";       // 目录中不存在
        _save.Current.EquippedSkin = "hat_beret";        // 存在但槽位不对（皮肤槽戴帽）
        _save.Current.OwnedItems["skin_cream"] = 1;      // 合法引用
        _save.Current.OwnedItems["bongos_wood"] = 1;     // 合法引用
        _save.Current.EquippedBongos = "bongos_wood";
        _save.Current.InventoryInitialized = true;
        InventoryService svc = CreateService();

        svc.Initialize();

        Assert.Equal(string.Empty, _save.Current.EquippedHat);   // 失效引用 → 卸下
        Assert.Equal(string.Empty, _save.Current.EquippedSkin);  // 槽位不符 → 卸下
        Assert.Equal("bongos_wood", _save.Current.EquippedBongos); // 合法引用不动
        Assert.Equal(1, _save.SaveCount); // 仅校验修改过才落盘
    }

    [Fact]
    public void 装备成功_广播Changed事件()
    {
        InventoryService svc = CreateService();
        svc.Initialize();
        int fired = 0;
        svc.Changed += () => fired++;

        svc.Equip("hat_beret");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void 图鉴计算_5件已拥有9件未拥有_未拥有打码()
    {
        InventoryService svc = CreateService();
        svc.Initialize();

        var dex = svc.GetCollectopedia();

        Assert.Equal(16, dex.Count); // GDD-09 §8：目录 16 件（新增小黄/小白专属皮肤）
        Assert.Equal(5, dex.Count(e => e.Owned));
        Assert.Equal(11, dex.Count(e => !e.Owned));

        CollectopediaEntry owned = dex.Single(e => e.Item.Id == "skin_cream");
        Assert.Equal("奶油小猫", owned.DisplayName);
        Assert.Equal("已获得", owned.OwnedText);

        CollectopediaEntry locked = dex.Single(e => e.Item.Id == "hat_flower_crown");
        Assert.Equal("？？？", locked.DisplayName); // 未获得名称打码（GDD-03 §7 收集惊喜）
        Assert.Equal("未获得", locked.OwnedText);
    }

    [Fact]
    public void 库存数量字典_重复授予不叠加_E4预留语义验证()
    {
        // E3 全部 1 件；此处固化"字典按 id 累加"的实现语义，供 E4 宝箱路径复用
        _save.Current.OwnedItems["hat_beret"] = 2;
        _save.Current.InventoryInitialized = true;
        InventoryService svc = CreateService();

        svc.Initialize();

        Assert.Equal(2, svc.Owned["hat_beret"]); // 已初始化 → 不再授予
        Assert.True(svc.IsOwned("hat_beret"));
    }

    // ── E4：GrantItem（掉落/宝箱入库路径）────────────────────────────────

    [Fact]
    public void GrantItem_新物品_数量1并落盘广播()
    {
        InventoryService svc = CreateService();
        svc.Initialize();
        int saves = _save.SaveCount;
        int fired = 0;
        svc.Changed += () => fired++;

        ItemGrantResult result = svc.GrantItem("hat_santa");

        Assert.Equal("hat_santa", result.ItemId);
        Assert.False(result.WasDuplicate);
        Assert.Equal(1, result.NewCount);
        Assert.True(svc.IsOwned("hat_santa"));
        Assert.Equal(saves + 1, _save.SaveCount);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void GrantItem_重复获得_数量累加并标记WasDuplicate()
    {
        InventoryService svc = CreateService();
        svc.Initialize();

        ItemGrantResult first = svc.GrantItem("hat_dev_cap");  // 新手装已有 → 即为重复
        ItemGrantResult second = svc.GrantItem("hat_dev_cap");

        Assert.True(first.WasDuplicate);
        Assert.Equal(2, first.NewCount);
        Assert.True(second.WasDuplicate);
        Assert.Equal(3, second.NewCount);
        Assert.Equal(3, svc.Owned["hat_dev_cap"]);
    }

    [Fact]
    public void GrantItem_未知id_视为程序bug抛异常()
    {
        InventoryService svc = CreateService();
        svc.Initialize();

        Assert.Throws<InvalidOperationException>(() => svc.GrantItem("nonexistent"));
    }
}
