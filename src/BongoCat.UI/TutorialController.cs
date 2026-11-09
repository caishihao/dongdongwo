using System.Windows;
using BongoCat.App.Tutorial;

namespace BongoCat.UI;

/// <summary>
/// 教程会话编排（E7-S1 UI 侧 glue）：订阅 App 层 <see cref="ITutorialService"/> 步进事件，
/// 创建/刷新/关闭气泡窗口；目标锚点按步切换（步 0/1 = 猫顶部中心，步 2 = 托盘角方向）。
/// 教程非模态：气泡只是普通置顶小窗，不阻塞经济/掉落/其他窗口。
/// </summary>
public sealed class TutorialController
{
    private readonly ITutorialService _tutorial;
    private readonly Func<Point> _catAnchorProvider;
    private readonly Action _openBag;
    private readonly Action<string>? _log;
    private TutorialBubbleWindow? _bubble;

    public TutorialController(
        ITutorialService tutorial,
        Func<Point> catAnchorProvider,
        Action openBag,
        Action<string>? log = null)
    {
        _tutorial = tutorial ?? throw new ArgumentNullException(nameof(tutorial));
        _catAnchorProvider = catAnchorProvider ?? throw new ArgumentNullException(nameof(catAnchorProvider));
        _openBag = openBag ?? throw new ArgumentNullException(nameof(openBag));
        _log = log;

        _tutorial.StepEntered += OnStepEntered;
        _tutorial.Completed += OnCompleted;
    }

    /// <summary>首次启动开始（存档未完成时由 App 调用；步进度续接由服务负责）。</summary>
    public void StartFromSave() => _tutorial.Start();

    /// <summary>托盘"新手教程"重播（完成态也能重看）。</summary>
    public void Replay() => _tutorial.StartReplay();

    private void OnStepEntered(int step)
    {
        if (_bubble is null)
        {
            _bubble = new TutorialBubbleWindow(
                GetAnchor,
                _openBag,
                _tutorial.Advance,
                _tutorial.Skip);
            _bubble.Closed += (_, _) => _bubble = null;
        }

        _bubble.ShowStep(step);
        _log?.Invoke($"[Tutorial] 气泡进入步 {step}");
    }

    private void OnCompleted()
    {
        _bubble?.Close();
        _bubble = null;
        _log?.Invoke("[Tutorial] 气泡已关闭（教程完成/跳过）");
    }

    /// <summary>锚点按步切换：步 0/1 指向猫顶部中心（物理像素），步 2 指向托盘方向（工作区右下角，DIP）。</summary>
    private TutorialAnchor GetAnchor()
        => _tutorial.CurrentStep == 2
            ? new TutorialAnchor(
                new Point(SystemParameters.WorkArea.Right - 60, SystemParameters.WorkArea.Bottom - 20),
                IsDevicePixels: false)
            : new TutorialAnchor(_catAnchorProvider(), IsDevicePixels: true);
}
