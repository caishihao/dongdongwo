using System.Windows;
using System.Windows.Threading;
using BongoCat.UI.ViewModels;

namespace BongoCat.UI;

/// <summary>
/// 背包/图鉴窗口（E3：普通窗口，非置顶可关闭；与桌宠置顶窗口互不干扰）。
/// code-behind 只做 DataContext 装配 + E4 经济倒计时心跳（1Hz 调 vm.TickCountdown，
/// 停止于窗口关闭）——状态与命令全部在 <see cref="InventoryViewModel"/>。
/// E6：头部新增"通行证"快捷按钮（经回调路由 TrayCommandController.OpenPass，UI 不直接开窗）。
/// </summary>
public partial class InventoryWindow : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private readonly Action? _openPassRequested;

    public InventoryWindow(InventoryViewModel vm, Action? openPassRequested = null)
    {
        InitializeComponent();
        DataContext = vm;
        _openPassRequested = openPassRequested;
        vm.Refresh(); // 每次打开都按最新库存重建（兜底，正常路径由 Changed 事件驱动）
        vm.TickCountdown(); // 打开即刷新一次倒计时显示（不等首个 1s tick）

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => vm.TickCountdown();
        _countdownTimer.Start();
        Closed += (_, _) => _countdownTimer.Stop();
    }

    private void OnOpenPassClick(object sender, RoutedEventArgs e) => _openPassRequested?.Invoke();
}
