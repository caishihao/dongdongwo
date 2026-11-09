using System.Text;

namespace BongoCat.RelayServer;

/// <summary>
/// 极简日志（与客户端 UI.FileLog 同款风格，不引日志库）：stdout + 追加写文件双路。
/// 文件路径可用 --log-file 指定，默认与可执行文件同目录 relay.log（systemd 下建议指向可写目录）。
/// </summary>
public sealed class RelayLog(string filePath) : IDisposable
{
    private readonly object _lock = new();

    public static RelayLog CreateDefault(string? overridePath)
    {
        string path = overridePath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "relay.log");
        return new RelayLog(path);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 文件写入失败不影响运行（磁盘满/权限），stdout 仍有输出
            }
        }
    }

    public void Dispose()
    {
    }
}
