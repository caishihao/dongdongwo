using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BongoCat.RelayServer;

/// <summary>
/// 更新分发 HTTP 端点（与 9437 自定义 TCP 协议完全隔离，独立监听）：
///   GET /manifest.json      → 更新清单（updateDir/manifest.json，不存在则 404——无发布即无更新）
///   GET /packages/&lt;file&gt;   → 更新包（updateDir/&lt;file&gt;）
/// 防御：路径穿越拒绝（包名白名单正则）、非 GET 405、单连接限速防滥用。
/// </summary>
public sealed partial class UpdateHttpEndpoint : IAsyncDisposable
{
    /// <summary>包文件名白名单：仅字母/数字/点/横线/下划线，不以点开头（防 ../、隐藏文件、绝对路径）。</summary>
    private static readonly Regex SafePackageFilePattern = SafeFileRegex();

    /// <summary>单连接带宽上限（字节/秒）——防滥用；100MB 包 ~100s 内可下完。</summary>
    private const int MaxBytesPerSecond = 4 * 1024 * 1024;

    private readonly string _updateDir;
    private readonly RelayLog _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public UpdateHttpEndpoint(int port, string updateDir, RelayLog log)
    {
        _updateDir = Path.GetFullPath(updateDir);
        _log = log;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeFileRegex();

    public bool IsRunning => _acceptTask is not null && !_acceptTask.IsCompleted;

    /// <summary>启动监听（失败抛出给调用方——端口占用/权限不足需显式暴露）。</summary>
    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _log.Info($"更新端点监听 {_listener.Prefixes.First()}（目录 {_updateDir}）");
        _acceptTask = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan drainTimeout)
    {
        _cts?.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_acceptTask is not null)
        {
            await Task.WhenAny(_acceptTask, Task.Delay(drainTimeout)).ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                return; // listener 已停止
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = HandleAsync(context, ct); // 每请求独立任务；异常内部消化
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string remote = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal))
            {
                await WriteTextAsync(context, 405, "method not allowed\n", ct).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/manifest.json", StringComparison.Ordinal))
            {
                await ServeFileAsync(context, Path.Combine(_updateDir, "manifest.json"), throttle: false, ct)
                    .ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/packages/", StringComparison.Ordinal))
            {
                string fileName = Uri.UnescapeDataString(path["/packages/".Length..]);
                if (!SafePackageFilePattern.IsMatch(fileName) || fileName.Length == 0 || fileName[0] == '.')
                {
                    _log.Warn($"更新端点拒绝非法包名：{remote} {fileName}");
                    await WriteTextAsync(context, 400, "invalid package name\n", ct).ConfigureAwait(false);
                    return;
                }

                await ServeFileAsync(context, Path.Combine(_updateDir, fileName), throttle: true, ct)
                    .ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(context, 404, "not found\n", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"更新端点请求异常 {remote} {path}: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // 连接已断：忽略
            }
        }
    }

    private async Task ServeFileAsync(HttpListenerContext context, string filePath, bool throttle, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            await WriteTextAsync(context, 404, "not found\n", ct).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = new FileInfo(filePath).Length;
        await using (FileStream source = File.OpenRead(filePath))
        {
            byte[] buffer = new byte[256 * 1024];
            int chunkPerSecond = Math.Max(1, MaxBytesPerSecond / buffer.Length);
            int chunksThisSecond = 0;
            var secondStart = Stopwatch.StartNew();

            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (!throttle)
                {
                    continue;
                }

                // 粗粒度限速：每秒配额用尽即休眠到下一秒（实现简单，精度足够）
                if (++chunksThisSecond >= chunkPerSecond)
                {
                    int elapsed = (int)secondStart.ElapsedMilliseconds;
                    if (elapsed < 1000)
                    {
                        await Task.Delay(1000 - elapsed, ct).ConfigureAwait(false);
                    }

                    chunksThisSecond = 0;
                    secondStart.Restart();
                }
            }
        }

        context.Response.Close();
        _log.Info($"更新端点 200 {context.Request.RemoteEndPoint} {context.Request.Url?.AbsolutePath}");
    }

    private static async Task WriteTextAsync(HttpListenerContext context, int status, string text, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        byte[] body = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _listener.Close();
    }
}
