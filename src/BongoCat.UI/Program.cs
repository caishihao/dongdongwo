using BongoCat.Platform.Update;

namespace BongoCat.UI;

/// <summary>
/// 进程入口（自换位分流）：--apply-update=&lt;旧exe路径&gt; → 换位流程不创建 WPF App；
/// 正常启动 → 清理上次换位遗留 .old → 交给 WPF App。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? applyTarget = SelfUpdateApplier.TryGetApplyTarget(args);
        if (applyTarget is not null)
        {
            SelfUpdateApplier.Apply(applyTarget, msg => System.Diagnostics.Trace.WriteLine(msg));
            return 0; // 成败均退出：失败时旧 exe 未受损（换位含回滚），用户手动重启即可
        }

        SelfUpdateApplier.CleanupStaleOldFile(); // 上次换位成功的首个新实例清 .old（no-op 居多）

        var app = new App(); // App.xaml 仅空资源字典、无 StartupUri，无需 InitializeComponent
        app.Run();
        return 0;
    }
}
