using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BongoCat.Platform.Net;
using BongoCat.Platform.Update;
using Xunit;

namespace BongoCat.Platform.Tests;

/// <summary>E9 自更新：SelfUpdateApplier 换位逻辑（临时目录模拟：正常换位 / 文件锁失败 / .old 清理）。</summary>
public class SelfUpdateApplierTests : IDisposable
{
    private readonly string _dir;

    public SelfUpdateApplierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bongocat-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void 解析_换位参数()
    {
        Assert.Null(SelfUpdateApplier.TryGetApplyTarget([]));
        Assert.Null(SelfUpdateApplier.TryGetApplyTarget(["--smoke-exit-ms=100"]));
        Assert.Null(SelfUpdateApplier.TryGetApplyTarget(["--apply-update="])); // 空路径非法
        string target = SelfUpdateApplier.TryGetApplyTarget(["--apply-update=C:\\old\\BongoCatRemix.exe"])!;
        Assert.Equal("C:\\old\\BongoCatRemix.exe", target);
    }

    [Fact]
    public void 换位_目标不存在_失败()
    {
        Assert.False(SelfUpdateApplier.Apply(
            Path.Combine(_dir, "missing.exe"), launch: _ => { }));
    }

    [Fact]
    public void 换位_新exe不存在_失败()
    {
        string target = WriteFile("target.exe", [1, 2, 3]);
        Assert.False(SelfUpdateApplier.Apply(
            target, launch: _ => { },
            sourceOverride: Path.Combine(_dir, "no-source.exe")));
        // 旧 exe 未被动过
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
    }

    [Fact]
    public void 换位_无进程占用_正常换位()
    {
        string target = WriteFile("target.exe", [1, 2, 3]);
        string source = WriteFile("source.new.exe", [9, 9, 9, 9]);
        string? launched = null;

        Assert.True(SelfUpdateApplier.Apply(
            target,
            launch: p => launched = p,
            sourceOverride: source,
            exitWaitTimeout: TimeSpan.FromSeconds(1)));

        // 新 exe 就位、旧 exe 成 .old、新源文件消失、启动回调收到目标路径
        Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(target));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target + ".old"));
        Assert.False(File.Exists(source));
        Assert.Equal(target, launched);
    }

    [Fact]
    public void 换位_目标被文件锁_失败保旧版()
    {
        string target = WriteFile("target.exe", [1, 2, 3]);
        string source = WriteFile("source.new.exe", [9]);

        // 独占锁定 target（模拟文件锁）：换位必须失败且旧版完好、无半换位残留
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(SelfUpdateApplier.Apply(
                target, launch: _ => { },
                sourceOverride: source,
                exitWaitTimeout: TimeSpan.FromSeconds(1)));
        }

        Assert.Equal([1, 2, 3], File.ReadAllBytes(target)); // 旧版完好
        Assert.False(File.Exists(target + ".old"));          // 无半换位残留
        Assert.Equal([9], File.ReadAllBytes(source));        // 暂存未被动过
    }

    [Fact]
    public void 清理_残留old_删除()
    {
        string selfOld = Environment.ProcessPath + ".old";
        try
        {
            File.WriteAllBytes(selfOld, [1]);
            SelfUpdateApplier.CleanupStaleOldFile();
            Assert.False(File.Exists(selfOld));
        }
        finally
        {
            File.Delete(selfOld);
        }
    }

    [Fact]
    public void 清理_无残留_静默no_op()
    {
        SelfUpdateApplier.CleanupStaleOldFile(); // 不存在不应抛异常
    }
}

/// <summary>E9：HttpUpdateClient 回路（本地 HttpListener 假服务器：manifest/包下载/404 失败）。</summary>
public class HttpUpdateClientLoopbackTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly List<Task> _handlers = [];

    public HttpUpdateClientLoopbackTests()
    {
        _prefix = $"http://127.0.0.1:{Random.Shared.Next(40000, 60000)}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Respond(Action<HttpListenerContext> write)
    {
        _handlers.Add(Task.Run(async () =>
        {
            HttpListenerContext ctx = await _listener.GetContextAsync();
            write(ctx);
        }));
    }

    [Fact]
    public async Task manifest_回路_成功()
    {
        Respond(ctx =>
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"version\":\"1.2.3\"}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.OutputStream.Close();
        });

        var client = new HttpUpdateClient(_prefix.TrimEnd('/'));
        string json = await client.GetManifestJsonAsync(CancellationToken.None);
        Assert.Contains("1.2.3", json);
    }

    [Fact]
    public async Task 包下载_回路_字节一致()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        Respond(ctx =>
        {
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload);
            ctx.Response.OutputStream.Close();
        });

        string target = Path.Combine(Path.GetTempPath(), "bongocat-http-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var client = new HttpUpdateClient(_prefix.TrimEnd('/'));
            await client.DownloadPackageAsync("pkg.zip", target, CancellationToken.None);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task manifest_服务器404_抛错()
    {
        Respond(ctx =>
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        });

        var client = new HttpUpdateClient(_prefix.TrimEnd('/'));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetManifestJsonAsync(CancellationToken.None));
    }
}
