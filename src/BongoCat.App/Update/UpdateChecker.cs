namespace BongoCat.App.Update;

/// <summary>版本比较判定结果。</summary>
public enum UpdateVerdict
{
    /// <summary>manifest 非法（解析失败/签名无效/包 hash 不匹配预期形态）→ 拒绝。</summary>
    Invalid,

    /// <summary>无更新（manifest 版本不高于当前版本）。</summary>
    UpToDate,

    /// <summary>有新版本可用。</summary>
    Available,
}

/// <summary>
/// 更新判定（纯逻辑）：程序集版本 vs manifest 版本（Version.TryParse，解析失败按不更新）。
/// </summary>
public static class UpdateChecker
{
    /// <summary>判定 manifest 相对当前版本是否需要更新。签名验证由调用方先行执行。</summary>
    public static UpdateVerdict Evaluate(UpdateManifest manifest, Version current)
    {
        if (!Version.TryParse(manifest.Version, out Version? remote) || remote is null)
        {
            return UpdateVerdict.UpToDate; // 解析失败按不更新（manifest.TryParse 已挡一道，双保险）
        }

        return remote > current ? UpdateVerdict.Available : UpdateVerdict.UpToDate;
    }
}
