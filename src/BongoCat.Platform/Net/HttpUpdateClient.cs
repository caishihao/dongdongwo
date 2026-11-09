using System.Net;
using System.Net.Http;
using BongoCat.Core.Update;

namespace BongoCat.Platform.Net;

/// <summary>
/// HTTP 更新传输实现（RelayServer 9438 端点的 manifest/包下载）。
/// 超时 + 取消令牌双保险；SHA256/签名校验不在本层（App 层职责）。
/// </summary>
public sealed class HttpUpdateClient : IUpdateTransferClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="baseUrl">如 http://your.server.ip:9438（无尾斜杠）。</param>
    /// <param name="timeoutSeconds">请求总超时（包下载以 ContentLength 级取消令牌控制，此处为兜底闸）。</param>
    public HttpUpdateClient(string baseUrl, int timeoutSeconds = 30, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
        });
        _ownsHttpClient = httpClient is null;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BongoCatRemix-Updater");
    }

    public async Task<string> GetManifestJsonAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync("manifest.json", HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadPackageAsync(string packageFile, string targetPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        using HttpResponseMessage response = await _http.GetAsync(
            $"packages/{Uri.EscapeDataString(packageFile)}", HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using FileStream target = File.Create(targetPath);
        await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
        await target.FlushAsync(ct).ConfigureAwait(false);
    }
}
