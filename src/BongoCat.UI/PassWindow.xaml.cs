using System.Windows;
using BongoCat.UI.ViewModels;

namespace BongoCat.UI;

/// <summary>
/// 通行证窗口（E6：普通窗口，非置顶可关闭；与桌宠/背包窗口互不干扰）。
/// code-behind 只做 DataContext 装配（状态与命令全部在 <see cref="PassViewModel"/>；
/// 剩余天数跨 0 点由 App 的 1Hz pass Tick → PassChanged 驱动刷新，本窗口不再开计时器）。
/// </summary>
public partial class PassWindow : Window
{
    public PassWindow(PassViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Refresh(); // 每次打开都按最新赛季状态重建（兜底，正常路径由 PassChanged 事件驱动）
    }
}
