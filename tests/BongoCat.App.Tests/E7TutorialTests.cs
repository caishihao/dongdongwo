using BongoCat.App.Tutorial;
using BongoCat.Core.Save;

using Xunit;

namespace BongoCat.App.Tests;

/// <summary>
/// E7 新手教程服务测试（GDD-07 §5 三步口径 + §7 边界：跳过/续接/幂等快进/重播不动经济）。
/// </summary>
public class E7TutorialTests
{
    private static (TutorialService Sut, FakeSaveService Save) Make()
    {
        var save = new FakeSaveService();
        return (new TutorialService(save), save);
    }

    [Fact]
    public void 步0_敲击不足3次_不推进并保持激活()
    {
        var (sut, _) = Make();
        sut.Start();

        sut.NotifyTap();
        sut.NotifyTap();

        Assert.Equal(0, sut.CurrentStep);
        Assert.True(sut.IsActive);
        Assert.False(sut.IsCompleted);
    }

    [Fact]
    public void 步0_敲击3次_进入步1并按步骤粒度落盘()
    {
        var (sut, save) = Make();
        int? entered = null;
        sut.StepEntered += s => entered = s;
        sut.Start();

        sut.NotifyTap();
        sut.NotifyTap();
        sut.NotifyTap();

        Assert.Equal(1, entered);
        Assert.Equal(1, sut.CurrentStep);
        Assert.Equal(1, save.Current.TutorialStep); // GDD-07 §7 崩溃续接依据
        Assert.True(save.SaveCount > 0);
    }

    [Fact]
    public void 步1_位移小于阈值_不推进()
    {
        var (sut, _) = Make();
        sut.Start();
        for (int i = 0; i < TutorialService.TapGoalCount; i++)
        {
            sut.NotifyTap();
        }

        // 16px（< 20px 阈值）：原地点击/微抖不算拖动
        sut.NotifyDragEnded(12, 10);

        Assert.Equal(1, sut.CurrentStep);
    }

    [Fact]
    public void 步1_位移达到阈值_推进到步2()
    {
        var (sut, _) = Make();
        sut.Start();
        for (int i = 0; i < TutorialService.TapGoalCount; i++)
        {
            sut.NotifyTap();
        }

        sut.NotifyDragEnded(30, 40); // 欧氏距离 50px ≥ 阈值

        Assert.Equal(2, sut.CurrentStep);
    }

    [Fact]
    public void 步2_打开背包_完成并置存档完成标志()
    {
        var (sut, save) = Make();
        bool completed = false;
        sut.Completed += () => completed = true;
        sut.Start();
        for (int i = 0; i < TutorialService.TapGoalCount; i++)
        {
            sut.NotifyTap();
        }

        sut.NotifyDragEnded(50, 0);
        sut.NotifyInventoryOpened();

        Assert.True(completed);
        Assert.False(sut.IsActive);
        Assert.True(save.Current.TutorialCompleted);
        Assert.True(save.SaveCount > 0);
    }

    [Fact]
    public void 跳过_任意步骤直接置完成_跳过标志优先于进度()
    {
        var (sut, save) = Make();
        sut.Start();
        sut.NotifyTap(); // 步 0 进行到一半

        sut.Skip();

        Assert.False(sut.IsActive);
        Assert.True(save.Current.TutorialCompleted);
    }

    [Fact]
    public void 已完成_Start为no_op_完成态优先于进度_永不打扰()
    {
        var (sut, _) = Make();
        sut.Start();
        sut.Skip();
        Assert.True(sut.IsCompleted);

        int events = 0;
        sut.StepEntered += _ => events++;
        sut.Start();

        Assert.False(sut.IsActive);
        Assert.Equal(0, events); // 重看必须走显式重播入口
    }

    [Fact]
    public void 完成后_操作通知无效_不触发任何步进事件()
    {
        var (sut, _) = Make();
        sut.Start();
        sut.Skip();
        int events = 0;
        sut.StepEntered += _ => events++;

        sut.NotifyTap();
        sut.NotifyDragEnded(999, 999);
        sut.NotifyInventoryOpened();

        Assert.Equal(0, events);
    }

    [Fact]
    public void 崩溃续接_存档步进度1_重启从步1继续()
    {
        var save = new FakeSaveService();
        save.Current.TutorialStep = 1; // 上次会话死在步 1
        var sut = new TutorialService(save);

        sut.Start();

        Assert.Equal(1, sut.CurrentStep);
        Assert.True(sut.IsActive);
    }

    [Fact]
    public void 越界存档步号_收敛到步0_防御手改存档()
    {
        var save = new FakeSaveService();
        save.Current.TutorialStep = 99;
        var sut = new TutorialService(save);

        sut.Start();

        Assert.Equal(0, sut.CurrentStep);
    }

    [Fact]
    public void 重播_从步0开始_经济与既有资产绝不重置()
    {
        var (sut, save) = Make();
        sut.Start();
        sut.Skip();

        // 模拟玩家已有资产（重播不重置经济 = E7 验收硬指标）
        save.Current.Coins = 4321;
        save.Current.OwnedItems["hat_beret"] = 2;
        save.Current.SeasonXP = 999;
        save.Current.ChestPityCount = 7;

        int saveCountBefore = save.SaveCount;
        sut.StartReplay();
        Assert.Equal(0, sut.CurrentStep);
        Assert.Equal(saveCountBefore, save.SaveCount); // 重播开始不落盘（完成态保持）

        for (int i = 0; i < TutorialService.TapGoalCount; i++)
        {
            sut.NotifyTap();
        }

        sut.NotifyDragEnded(50, 0);
        sut.NotifyInventoryOpened();

        Assert.True(sut.IsCompleted);
        Assert.Equal(4321, save.Current.Coins);                            // 金币不动
        Assert.Equal(2, save.Current.OwnedItems["hat_beret"]);             // 库存不动
        Assert.Equal(999, save.Current.SeasonXP);                          // 通行证不动
        Assert.Equal(7, save.Current.ChestPityCount);                      // 保底进度不动
    }

    [Fact]
    public void 提前开背包_任意步骤快进完成_GDD幂等口径()
    {
        var (sut, save) = Make();
        sut.Start();

        sut.NotifyInventoryOpened(); // 玩家没跟着教程走，自己开了背包

        Assert.False(sut.IsActive);
        Assert.True(save.Current.TutorialCompleted);
    }

    [Fact]
    public void 下一步_逐级推进_最后一步确认即完成()
    {
        var (sut, save) = Make();
        sut.Start();

        sut.Advance();
        sut.Advance();
        Assert.Equal(2, sut.CurrentStep);

        sut.Advance(); // 最后一步"下一步" = 确认完成
        Assert.True(save.Current.TutorialCompleted);
        Assert.False(sut.IsActive);
    }

    [Fact]
    public void 常量_三步口径_敲击3次_拖动阈值20px()
    {
        Assert.Equal(3, TutorialService.StepCount);
        Assert.Equal(3, TutorialService.TapGoalCount);
        Assert.Equal(20.0, TutorialService.DragMinDistancePx);
    }
}
