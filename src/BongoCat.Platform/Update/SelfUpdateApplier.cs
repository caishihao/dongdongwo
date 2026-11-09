using System.Diagnostics;
using System.IO;

namespace BongoCat.Platform.Update;

/// <summary>
/// 单文件自换位（Windows 允许改名运行中的 exe）：
/// 旧 exe 改名 .old → 新 exe 移动到位 → 新进程正常启动 → 新实例启动时清 .old。
/// 无需外部更新器；换位失败（文件锁/权限）保留暂存并返回 false，旧版继续运行。
/// </summary>
public static class SelfUpdateApplier
{
    /// <summary>等待旧进程退出的上限（超时视为文件锁不可解，放弃换位保留暂存）。</summary>
    private static readonly TimeSpan DefaultExitWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>解析 --apply-update=&lt;旧exe路径&gt; 参数。返回 null = 无此参数或格式非法。</summary>
    public static string? TryGetApplyTarget(string[] args)
    {
        string? arg = Array.Find(args, a => a.StartsWith("--apply-update=", StringComparison.Ordinal));
        if (arg is null)
        {
            return null;
        }

        string path = arg["--apply-update=".Length..];
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// 启动时清理上次换位遗留的 .old 文件（换位成功后新实例的第一次启动执行；不存在则 no-op）。
    /// </summary>
    public static void CleanupStaleOldFile(Action<string>? log = null)
    {
        try
        {
            string oldPath = Environment.ProcessPath + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
                log?.Invoke($"[Update] 已清理旧版残留：{oldPath}");
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响运行（下次启动再试）
            log?.Invoke($"[Update] 清理 .old 失败（下次启动重试）：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行换位（由暂存 exe 以 --apply-update=&lt;旧exe路径&gt; 启动后调用）：
    /// 等待旧进程退出 → 旧 exe 改名 .old → 新 exe 移动到位 → 以无参正常模式启动新 exe。
    /// 返回 false = 换位失败（保留暂存，旧 exe 未被破坏）。
    /// sourceOverride/launch/exitWaitTimeout：仅供单测注入（生产传 null 走默认）。
    /// </summary>
    public static bool Apply(
        string targetExePath,
        Action<string>? log = null,
        Action<string>? launch = null,
        string? sourceOverride = null,
        TimeSpan? exitWaitTimeout = null)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);
            string? selfPath = sourceOverride ?? Environment.ProcessPath;
            if (selfPath is null || !File.Exists(selfPath) || !File.Exists(targetExePath))
            {
                log?.Invoke("[Update] 换位失败：路径不存在");
                return false;
            }

            WaitForTargetExit(targetExePath, log, exitWaitTimeout ?? DefaultExitWaitTimeout);

            string oldPath = targetExePath + ".old";
            File.Delete(oldPath);                    // 上次残留 .old（防御）
            File.Move(targetExePath, oldPath);       // 旧 exe 让位（运行中 exe 允许改名）
            try
            {
                File.Move(selfPath, targetExePath);  // 新 exe 就位
            }
            catch
            {
                File.Move(oldPath, targetExePath);   // 回滚：旧 exe 复位，保住可用态
                throw;
            }

            if (launch is not null)
            {
                launch(targetExePath);
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetExePath) ?? ".",
                });
            }

            log?.Invoke($"[Update] 换位完成，新版已启动：{targetExePath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Update] 换位失败（暂存保留，旧版未受损）：{ex.Message}");
            return false;
        }
    }

    /// <summary>按进程路径匹配等待旧进程退出（轮询 250ms；超时抛 TimeoutException 由 Apply 收尾）。</summary>
    private static void WaitForTargetExit(string targetExePath, Action<string>? log, TimeSpan timeout)
    {
        string normalized = Path.GetFullPath(targetExePath).ToLowerInvariant();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            List<Process> running = [];
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path is not null &&
                        Path.GetFullPath(path).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        running.Add(p);
                    }
                }
                catch
                {
                    // 系统进程/权限不足：MainModule 抛异常 → 不匹配
                }
            }

            if (running.Count == 0)
            {
                return;
            }

            log?.Invoke("[Update] 等待旧进程退出…");
            foreach (Process p in running)
            {
                p.Dispose();
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"旧进程 {timeout.TotalSeconds:0}s 内未退出：{targetExePath}");
    }
}
