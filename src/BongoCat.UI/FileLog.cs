using System.IO;

namespace BongoCat.UI;

/// <summary>
/// 极简文件日志（E1 临时设施：无日志库依赖，Platform 各服务经回调接收）。
/// 输出：%TEMP%\bongocat-remix.log，烟测 / 排障用，E2 换 ILogger。
/// </summary>
public static class FileLog
{
    private static readonly object Lock = new();
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bongocat-remix.log");

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
            catch
            {
                // 日志失败不影响运行
            }
        }
    }
}
