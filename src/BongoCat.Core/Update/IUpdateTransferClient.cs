namespace BongoCat.Core.Update;

/// <summary>
/// 更新传输抽象（HTTP 细节留在 Platform 实现；App 层只关心"取 manifest / 下包"两个能力）。
/// 接口放 Core：Platform 实现、App 消费，两端共用（分层规则同 IMultiplayerClient）。
/// </summary>
public interface IUpdateTransferClient
{
    /// <summary>GET manifest.json，返回原始 JSON 文本。</summary>
    Task<string> GetManifestJsonAsync(CancellationToken ct);

    /// <summary>下载更新包到 <paramref name="targetPath"/>（覆盖写）。</summary>
    Task DownloadPackageAsync(string packageFile, string targetPath, CancellationToken ct);
}

/// <summary>更新编排状态机状态。</summary>
public enum UpdateState
{
    /// <summary>无更新/未检查。</summary>
    NoUpdate,

    /// <summary>检测到新版本（下载中或尚未开始）。</summary>
    Available,

    /// <summary>新版本已暂存就绪，等待用户重启生效。</summary>
    Staged,

    /// <summary>最近一次检查/下载/校验失败（旧版继续可用，可再次检查）。</summary>
    Failed,
}
