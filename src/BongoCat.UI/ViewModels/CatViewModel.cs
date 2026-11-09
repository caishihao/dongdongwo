using System.Windows;
using System.Windows.Threading;
using BongoCat.App;
using BongoCat.App.Inventory;
using BongoCat.Core.Input;
using BongoCat.Core.Save;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BongoCat.UI.ViewModels;

/// <summary>
/// 猫窗口 ViewModel（UI 层规则：不引用 Visual/BitmapImage 等视图类型）。
/// 职责：① 启动按键轮询并把 Channel 事件投递到 UI 线程（ADR-002）；
///       ② 把 CatDirector 的 TapStarted 转发给 View 驱动动画；
///       ③ 拖动结束后把窗口位置按比例写入存档（ADR-004 位置记忆）；
///       ④ E3：暴露当前装扮（hat/skin/bongos 的 artKey），换装事件驱动 View 换绑渲染层。
/// </summary>
public partial class CatViewModel : ObservableObject
{
    private readonly CatDirector _director;
    private readonly IKeyHookService _keys;
    private readonly ISaveService _saveService;
    private readonly IInventoryService _inventory;

    private Task? _consumeTask;

    /// <summary>一次敲击开始（View 订阅后驱动爪子动画）。</summary>
    public event Action<PawSide>? TapStarted;

    /// <summary>整体水平镜像（GDD-01 §5，纯视觉）。</summary>
    [ObservableProperty]
    private bool _isMirrored;

    /// <summary>存档内存模型（View 读取做位置还原；View 不直接改，经 OnDragEnded）。</summary>
    public SaveModel Save => _saveService.Current;

    // ── E3：当前装扮（空串 = 默认外观；单一数据源 = 库存服务/存档）──────

    /// <summary>装备中的帽子 artKey（空 = 无帽）。</summary>
    public string EquippedHat => _inventory.EquippedHat;

    /// <summary>装备中的皮肤 artKey（空 = 原创基础皮肤）。</summary>
    public string EquippedSkin => _inventory.EquippedSkin;

    /// <summary>装备中的鼓 artKey（空 = 原创基础鼓色）。</summary>
    public string EquippedBongos => _inventory.EquippedBongos;

    public CatViewModel(
        CatDirector director,
        IKeyHookService keys,
        ISaveService saveService,
        IInventoryService inventory)
    {
        _director = director;
        _keys = keys;
        _saveService = saveService;
        _inventory = inventory;

        // 转发导演事件给 View（此时仍在调用线程 = UI 线程，见 ConsumeLoop）
        _director.TapStarted += side => TapStarted?.Invoke(side);

        // E3：库存/装备变化 → 通知 View 换绑（装备操作全部发生在 UI 线程，无需调度）
        _inventory.Changed += OnInventoryChanged;
    }

    private void OnInventoryChanged()
    {
        OnPropertyChanged(nameof(EquippedHat));
        OnPropertyChanged(nameof(EquippedSkin));
        OnPropertyChanged(nameof(EquippedBongos));
    }

    /// <summary>启动：启动轮询 → 启动 UI 线程消费循环。（存档加载已上移到 App.OnStartup，E2：托盘/尺寸档位依赖先于窗口）</summary>
    public void Start()
    {
        IsMirrored = Save.CatMirrored;

        _keys.Start();

        // 消费循环：Channel（轮询线程写入）→ UI 线程执行导演编排（ADR-002 投递规则）
        var dispatcher = Application.Current.Dispatcher;
        _consumeTask = Task.Run(async () =>
        {
            await foreach (KeyPressEvent e in _keys.Reader.ReadAllAsync())
            {
                await dispatcher.InvokeAsync(() => _director.OnKeyPressed(e));
            }
        });
    }

    /// <summary>拖动结束：按工作区比例记录位置并立即落盘（ADR-004 位置记忆）。</summary>
    public void OnDragEnded(double relX, double relY)
    {
        Save.CatPositionRelX = Math.Clamp(relX, 0.0, 1.0);
        Save.CatPositionRelY = Math.Clamp(relY, 0.0, 1.0);
        Save.CatMirrored = IsMirrored;
        _saveService.Save();
    }
}
