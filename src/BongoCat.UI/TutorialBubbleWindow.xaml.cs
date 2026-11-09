using System.Windows;
using System.Windows.Threading;

namespace BongoCat.UI;

/// <summary>气泡定位锚点：目标参考点 + 该点坐标系标记（设备像素 / DIP）。</summary>
/// <param name="Point">目标参考点（猫顶部中心或托盘角）。</param>
/// <param name="IsDevicePixels">true = 物理像素（需按本窗口 DPI 变换转 DIP）；false = 已是 DIP。</param>
public readonly record struct TutorialAnchor(Point Point, bool IsDevicePixels);

/// <summary>
/// 教程气泡窗口（E7-S1 UI 侧）：每步刷新文案/按钮，250ms 心跳跟随目标位置。
/// code-behind 只做定位与按钮转发——步进逻辑全部在 App 层 <see cref="App.Tutorial"/>（UI 不持有状态）。
/// </summary>
public partial class TutorialBubbleWindow : Window
{
    /// <summary>步文案（与 App 层 TutorialService 步号一一对应）。</summary>
    private static readonly Dictionary<int, (string Title, string Body)> StepTexts = new()
    {
        [0] = ("① 敲敲看", "用键盘随便按几个键，猫就会跟着打鼓！敲 3 次试试。"),
        [1] = ("② 拖拖看", "按住猫拖动一下，把它放到你喜欢的位置。"),
        [2] = ("③ 找背包", "点屏幕右下角托盘的猫图标打开背包换装扮（也可以直接点下面的按钮）。"),
    };

    private readonly DispatcherTimer _followTimer;
    private readonly Func<TutorialAnchor>? _anchorProvider;

    public TutorialBubbleWindow(Func<TutorialAnchor>? anchorProvider, Action openBag, Action next, Action skip)
    {
        InitializeComponent();
        _anchorProvider = anchorProvider;

        OpenBagButton.Click += (_, _) => openBag();
        NextButton.Click += (_, _) => next();
        SkipButton.Click += (_, _) => skip();

        // 跟随心跳：猫被拖动/屏幕变化时气泡不脱靶（廉价轮询，250ms 一次纯定位计算）
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _followTimer.Tick += (_, _) => Reposition();
    }

    /// <summary>进入新步骤：刷新文案与按钮可见性（步 2 出现"打开背包"直达按钮）。</summary>
    public void ShowStep(int step)
    {
        if (!StepTexts.TryGetValue(step, out (string Title, string Body) text))
        {
            return;
        }

        StepTitle.Text = text.Title;
        StepBody.Text = text.Body;
        OpenBagButton.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible)
        {
            Show();
            _followTimer.Start();
        }

        Reposition();
    }

    /// <summary>定位：目标点右侧偏移，越出工作区则向左/向上收拢（全程 DIP）。</summary>
    private void Reposition()
    {
        if (_anchorProvider is null)
        {
            return;
        }

        TutorialAnchor anchor = _anchorProvider();
        Point dip = anchor.IsDevicePixels ? ToDip(anchor.Point) : anchor.Point;

        double left = dip.X + 26;
        double top = dip.Y - ActualHeight - 10;

        // clamp 进工作区（右/下越出 → 收回；顶越出 → 放到目标点下方）
        var work = SystemParameters.WorkArea;
        if (left + ActualWidth > work.Right)
        {
            left = dip.X - ActualWidth - 26;
        }

        if (top < work.Top)
        {
            top = dip.Y + 16;
        }

        Left = Math.Max(work.Left, left);
        Top = Math.Max(work.Top, Math.Min(top, work.Bottom - ActualHeight));
    }

    /// <summary>物理像素 → 本窗口 DPI 下的 DIP（气泡与猫可能同屏不同 DPI，以气泡自身变换为准）。</summary>
    private Point ToDip(Point devicePoint)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(devicePoint) ?? devicePoint;
    }

    protected override void OnClosed(EventArgs e)
    {
        _followTimer.Stop();
        base.OnClosed(e);
    }
}
