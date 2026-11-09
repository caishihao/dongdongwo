using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BongoCat.RelayServer;
using Xunit;

namespace BongoCat.RelayServer.Tests;

/// <summary>
/// 更新分发 HTTP 端点集成测试（真 HTTP 回路）：manifest/包下载/路径穿越拒绝/未知路径 404/方法 405。
/// </summary>
public class UpdateHttpEndpointTests : IAsyncDisposable
{
    private readonly string _updateDir;
    private readonly RelayLog _log;
    private readonly UpdateHttpEndpoint _endpoint;
    private readonly int _port;
    private readonly HttpClient _http = new();

    public UpdateHttpEndpointTests()
    {
        _updateDir = Path.Combine(Path.GetTempPath(), "bongocat-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_updateDir);
        _log = RelayLog.CreateDefault(Path.Combine(Path.GetTempPath(), "relay-ep-test.log"));
        _port = Random.Shared.Next(40000, 60000);
        _endpoint = new UpdateHttpEndpoint(_port, _updateDir, _log);
        _endpoint.StartAsync(new CancellationTokenSource().Token).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _endpoint.DisposeAsync();
        _log.Dispose();
        try
        {
            Directory.Delete(_updateDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Base => $"http://127.0.0.1:{_port}";

    [Fact]
    public async Task manifest_文件存在_返回内容()
    {
        await File.WriteAllTextAsync(Path.Combine(_updateDir, "manifest.json"),
            "{\"version\":\"1.2.3\",\"packageFile\":\"pkg.zip\"}");

        Assert.Equal("{\"version\":\"1.2.3\",\"packageFile\":\"pkg.zip\"}",
            await _http.GetStringAsync($"{Base}/manifest.json"));
    }

    [Fact]
    public async Task manifest_未发布_404()
    {
        HttpResponseMessage response = await _http.GetAsync($"{Base}/manifest.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 包下载_字节一致()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(Path.Combine(_updateDir, "pkg.zip"), payload);

        Assert.Equal(payload, await _http.GetByteArrayAsync($"{Base}/packages/pkg.zip"));
    }

    [Fact]
    public async Task 包下载_文件不存在_404()
    {
        HttpResponseMessage response = await _http.GetAsync($"{Base}/packages/ghost.zip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/packages/..%2f..%2fevil.txt")]   // 编码穿越（HttpListener 403 / 我方 400 双重防线）
    [InlineData("/packages/%2e%2e%2fescape.zip")]  // 全编码穿越
    public async Task 包下载_编码穿越_被拒绝(string path)
    {
        HttpResponseMessage response = await _http.GetAsync(Base + path);
        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            $"实际状态码 {response.StatusCode}");
    }

    [Theory]
    [InlineData("/packages/sub%2fdir.zip")]        // 编码子目录（HttpListener 不拦，我方 400）
    [InlineData("/packages/bad%20name.zip")]       // 空格（非法字符）
    [InlineData("/packages/.hidden.zip")]          // 隐藏文件
    public async Task 包下载_非法包名_400拒绝(string path)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync(Base + path)).StatusCode);
    }

    [Fact]
    public async Task 包下载_白名单字符_通过到达404()
    {
        // 合法包名（不存在文件）应走到"文件不存在"分支而非包名校验拒绝
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync($"{Base}/packages/valid-package_name.1.2.3.zip")).StatusCode);
    }

    [Fact]
    public async Task 未知路径_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync($"{Base}/other")).StatusCode);
    }

    [Fact]
    public async Task 非GET方法_405()
    {
        HttpResponseMessage response = await _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{Base}/manifest.json"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
