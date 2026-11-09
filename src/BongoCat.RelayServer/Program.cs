using System.Runtime.InteropServices;
using System.Text;

namespace BongoCat.RelayServer;

/// <summary>命令行配置解析结果（未识别参数直接退出并打印用法，绝不带病启动）。</summary>
public sealed class RelayOptions
{
    public int Port { get; init; } = 9437;

    public RelayLimits Limits { get; init; } = new();

    public string? LogFile { get; init; }

    /// <summary>更新分发 HTTP 端口（9438；0 = 关闭更新端点）。</summary>
    public int UpdatePort { get; init; } = 9438;

    /// <summary>更新包/清单目录（默认 exe 同级 updates/）。</summary>
    public string? UpdateDir { get; init; }

    /// <summary>解析命令行。返回 null = 参数非法（已向 stderr 输出用法）。</summary>
    public static RelayOptions? TryParse(string[] args)
    {
        int port = 9437;
        int maxMembers = 8;
        int maxRooms = 64;
        int maxMsgPerSec = 50;
        int heartbeatTimeoutSec = 60;
        int idleMinutes = 10;
        int updatePort = 9438;
        string? updateDir = null;
        string? logFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            bool hasValue = i + 1 < args.Length && !value.StartsWith("--", StringComparison.Ordinal);

            switch (key)
            {
                case "--port" when hasValue && int.TryParse(value, out int p) && p is > 0 and < 65536:
                    port = p;
                    break;
                case "--max-members" when hasValue && int.TryParse(value, out int mm) && mm is >= 2 and <= 64:
                    maxMembers = mm;
                    break;
                case "--max-rooms" when hasValue && int.TryParse(value, out int mr) && mr is >= 1 and <= 4096:
                    maxRooms = mr;
                    break;
                case "--max-msg-per-sec" when hasValue && int.TryParse(value, out int ms) && ms is >= 1 and <= 10000:
                    maxMsgPerSec = ms;
                    break;
                case "--heartbeat-timeout-sec" when hasValue && int.TryParse(value, out int ht) && ht is >= 5 and <= 3600:
                    heartbeatTimeoutSec = ht;
                    break;
                case "--idle-minutes" when hasValue && int.TryParse(value, out int im) && im is >= 1 and <= 1440:
                    idleMinutes = im;
                    break;
                case "--update-port" when hasValue && int.TryParse(value, out int up) && up is >= 0 and < 65536:
                    updatePort = up;
                    break;
                case "--update-dir" when hasValue:
                    updateDir = value;
                    break;
                case "--log-file" when hasValue:
                    logFile = value;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"无法识别的参数：{key}（值：{value}）");
                    PrintUsage();
                    return null;
            }

            if (hasValue)
            {
                i++; // 消费参数值
            }
        }

        return new RelayOptions
        {
            Port = port,
            Limits = new RelayLimits
            {
                MaxMembersPerRoom = maxMembers,
                MaxRooms = maxRooms,
                MaxMessagesPerSecond = maxMsgPerSec,
                HeartbeatTimeoutSeconds = heartbeatTimeoutSec,
                RoomIdleMinutes = idleMinutes,
            },
            LogFile = logFile,
            UpdatePort = updatePort,
            UpdateDir = updateDir,
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BongoCat 云中继服务器（E8 互联网多人 + 更新分发）

            用法：bongocat-relay [--port N] [--max-members N] [--max-rooms N]
                                 [--max-msg-per-sec N] [--heartbeat-timeout-sec N]
                                 [--idle-minutes N] [--update-port N] [--update-dir path]
                                 [--log-file path]

            默认值：port=9437  max-members=8  max-rooms=64  max-msg-per-sec=50
                    heartbeat-timeout-sec=60  idle-minutes=10
                    update-port=9438（0 = 关闭更新端点）  update-dir=<exe目录>/updates
                    log-file=<exe目录>/relay.log
            """);
    }
}

/// <summary>
/// 入口：解析参数 → 启动宿主 → 等待 SIGTERM / Ctrl+C 优雅关闭。
/// 跨平台（net8.0 控制台，无 WPF 依赖），可 dotnet publish linux-x64 单文件部署到腾讯云。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Windows 控制台默认 GBK 会把中文日志打成乱码；统一 UTF-8（Linux 无影响）
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 无控制台（systemd 重定向）时不可设置：忽略
        }

        RelayOptions? options = RelayOptions.TryParse(args);
        if (options is null)
        {
            return 1;
        }

        using var log = RelayLog.CreateDefault(options.LogFile);
        var registry = new RoomRegistry(options.Limits);
        var host = new RelayServerHost(options.Limits, registry, log);

        using var shutdown = new CancellationTokenSource();
        // SIGTERM（systemd stop 发送；仅 Unix 平台可注册，Windows 降级为 Ctrl+C）
        try
        {
            _ = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shutdown.Cancel());
            log.Info("已注册 SIGTERM 优雅关闭");
        }
        catch (PlatformNotSupportedException)
        {
            log.Info("当前平台不支持 SIGTERM 注册，使用 Ctrl+C 优雅关闭");
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 走优雅关闭而非强杀
            shutdown.Cancel();
        };

        await host.StartAsync(options.Port);
        log.Info($"启动完成，进程 {Environment.ProcessId}，等待 Ctrl+C / SIGTERM 退出");

        // 更新分发端点（9438）：独立 HttpListener，失败不拖垮中继本体（仅记日志）
        UpdateHttpEndpoint? updateEndpoint = null;
        if (options.UpdatePort > 0)
        {
            string updateDir = options.UpdateDir
                ?? Path.Combine(AppContext.BaseDirectory, "updates");
            try
            {
                Directory.CreateDirectory(updateDir);
                updateEndpoint = new UpdateHttpEndpoint(options.UpdatePort, updateDir, log);
                await updateEndpoint.StartAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                log.Warn($"更新端点启动失败（中继本体不受影响）：{ex.Message}");
                await (updateEndpoint?.DisposeAsync() ?? ValueTask.CompletedTask);
                updateEndpoint = null;
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (updateEndpoint is not null)
        {
            await updateEndpoint.StopAsync(TimeSpan.FromSeconds(2));
            await updateEndpoint.DisposeAsync();
        }

        await host.StopAsync(TimeSpan.FromSeconds(5));
        log.Info("已优雅关闭");
        return 0;
    }
}
