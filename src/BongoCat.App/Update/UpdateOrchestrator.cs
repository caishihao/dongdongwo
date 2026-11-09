using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using System.Diagnostics;
using BongoCat.Core.Update;

namespace BongoCat.App.Update;

/// <summary>
/// 自更新编排（状态机：无更新 → 已暂存 → 待重启生效）。
/// 启动后 10s 后台调用 <see cref="CheckNowAsync"/>（不阻塞启动）；任何失败只记日志 +
/// <see cref="UpdateFailed"/> 事件，旧版继续运行，绝不把客户端置于不可用态。
/// </summary>
/// <remarks>
/// 暂存目录约定（%APPDATA%/BongoCatRemix/update/）：更新包 zip + 解压出的
/// BongoCatRemix.new.exe。换位（File.Replace 语义）由 Platform 层 SelfUpdateApplier 执行。
/// </remarks>
public sealed class UpdateOrchestrator
{
    /// <summary>暂存的新版 exe 固定文件名（zip 内唯一条目解压为此名）。</summary>
    public const string StagedExeName = "BongoCatRemix.new.exe";

    private readonly IUpdateTransferClient _transfer;
    private readonly string _updateDir;
    private readonly Version _currentVersion;
    private readonly Func<UpdateManifest, bool> _signatureVerifier;
    private readonly Action<string>? _log;

    /// <summary>检测到新版本（已通过签名验证，尚未开始/正在下载）。</summary>
    public event Action<UpdateManifest>? UpdateAvailable;

    /// <summary>新版本已暂存就绪（SHA256 已验证，重启后生效）。</summary>
    public event Action<UpdateManifest>? UpdateStaged;

    /// <summary>更新链路任一环节失败（参数 = 原因描述；UI 可弹气泡，旧版继续可用）。</summary>
    public event Action<string>? UpdateFailed;

    public UpdateOrchestrator(
        IUpdateTransferClient transfer,
        string updateDir,
        Version? currentVersion = null,
        Action<string>? log = null,
        Func<UpdateManifest, bool>? signatureVerifier = null)
    {
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _updateDir = updateDir ?? throw new ArgumentNullException(nameof(updateDir));
        _currentVersion = currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        _log = log;
        _signatureVerifier = signatureVerifier ?? UpdateSignatureVerifier.Verify;
    }

    public UpdateState State { get; private set; } = UpdateState.NoUpdate;

    /// <summary>最近一次通过验证的 manifest（Available/Staged 时非 null）。</summary>
    public UpdateManifest? StagedManifest { get; private set; }

    /// <summary>暂存新版 exe 的完整路径（State == Staged 时非 null 且文件存在）。</summary>
    public string? StagedExePath { get; private set; }

    /// <summary>暂存目录（托盘/诊断展示用）。</summary>
    public string UpdateDirectory => _updateDir;

    /// <summary>
    /// 完整检查链：取 manifest → 解析 → 签名验证 → 版本比较 → 下载 → SHA256 → 解压暂存。
    /// 任一环节失败即置 Failed 并返回 false；已处于 Staged 则跳过（幂等）。
    /// </summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct = default)
    {
        if (State == UpdateState.Staged)
        {
            return true; // 已就绪待重启，无需重复检查
        }

        try
        {
            string json = await _transfer.GetManifestJsonAsync(ct).ConfigureAwait(false);
            UpdateManifest? manifest = UpdateManifest.TryParse(json);
            if (manifest is null)
            {
                return Fail("manifest 解析失败（缺字段/非法值），已拒绝");
            }

            _log?.Invoke($"[Update] 拉到 manifest：v{manifest.Version} 包={manifest.PackageFile}");

            if (UpdateChecker.Evaluate(manifest, _currentVersion) != UpdateVerdict.Available)
            {
                _log?.Invoke($"[Update] 无更新（当前 v{_currentVersion}）");
                State = UpdateState.NoUpdate;
                StagedManifest = null;
                return true;
            }

            if (!_signatureVerifier(manifest))
            {
                return Fail("manifest 签名验证失败，已拒绝更新");
            }

            State = UpdateState.Available;
            StagedManifest = manifest;
            UpdateAvailable?.Invoke(manifest);

            string packagePath = await DownloadAndVerifyAsync(manifest, ct).ConfigureAwait(false);
            string stagedExe = ExtractStagedExe(packagePath);

            StagedExePath = stagedExe;
            State = UpdateState.Staged;
            _log?.Invoke($"[Update] 新版本 v{manifest.Version} 已暂存：{stagedExe}（重启后生效）");
            UpdateStaged?.Invoke(manifest);
            return true;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Failed;
            return false; // 取消不算错误事件（多为退出/手动重查）
        }
        catch (Exception ex)
        {
            return Fail($"更新检查失败：{ex.Message}（旧版继续运行）");
        }
    }

    /// <summary>启动时检测上次会话遗留的暂存 exe（崩溃/退出后恢复 Staged 态）。
    /// 暂存版本不高于当前版本 → 直接清理（换位成功后运行中 exe 无法自删，靠下次启动收尾，
    /// 否则托盘会永远显示"重启并更新"且反复用同版本覆盖）。</summary>
    public bool DetectExistingStaged()
    {
        if (State == UpdateState.Staged)
        {
            return true;
        }

        string staged = Path.Combine(_updateDir, StagedExeName);
        if (!File.Exists(staged))
        {
            return false;
        }

        if (TryGetFileVersion(staged, out Version? stagedVersion)
            && Norm(stagedVersion) <= Norm(_currentVersion))
        {
            _log?.Invoke($"[Update] 遗留暂存 v{stagedVersion} 不高于当前 v{_currentVersion}，清理暂存");
            TryDelete(staged);
            return false;
        }

        StagedExePath = staged;
        State = UpdateState.Staged;
        _log?.Invoke("[Update] 检测到上次会话遗留的暂存更新，重启后生效");
        return true;
    }

    /// <summary>归一化为 3 段（Major.Minor.Build）。.NET Version 比较对缺失段按"更小"处理
    /// （1.0.0 &lt; 1.0.0.0），同版本 3/4 段混比会误判，先归一再比。</summary>
    private static Version Norm(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool TryGetFileVersion(string path, out Version version)
    {
        version = new Version(0, 0, 0);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(info.FileVersion, out version!);
        }
        catch
        {
            return false; // 读不出版本（文件损坏/被锁）→ 保守保留暂存，走原有 Staged 恢复
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Update] 暂存清理失败（下次启动重试）：{ex.Message}");
            StagedExePath = path; // 删不掉则维持 Staged 态，下次启动再清
            State = UpdateState.Staged;
        }
    }

    private async Task<string> DownloadAndVerifyAsync(UpdateManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(_updateDir);
        string packagePath = Path.Combine(_updateDir, manifest.PackageFile);
        await _transfer.DownloadPackageAsync(manifest.PackageFile, packagePath, ct).ConfigureAwait(false);

        string actual;
        await using (FileStream stream = File.OpenRead(packagePath))
        {
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        // 流关闭后再删除（打开期间删除会撞文件锁）
        if (!string.Equals(actual, manifest.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            File.Delete(packagePath);
            throw new InvalidOperationException($"更新包 SHA256 不匹配（期望 {manifest.Sha256[..8]}…，实际 {actual[..8]}…），已删除残包");
        }

        _log?.Invoke($"[Update] 包下载并校验通过：{packagePath}");
        return packagePath;
    }

    /// <summary>从 zip 解出唯一条目为暂存 exe（BongoCatRemix.new.exe），旧暂存先清除。</summary>
    private string ExtractStagedExe(string packagePath)
    {
        string stagedPath = Path.Combine(_updateDir, StagedExeName);
        File.Delete(stagedPath); // 不存在则 no-op

        using (FileStream zipStream = File.OpenRead(packagePath))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault()
                ?? throw new InvalidOperationException("更新包 zip 为空，已拒绝");
            entry.ExtractToFile(stagedPath, overwrite: true);
        }

        // zip 流已关闭才能删包（此前在 using 作用域内删除会撞文件锁）
        File.Delete(packagePath);
        return stagedPath;
    }

    private bool Fail(string message)
    {
        State = UpdateState.Failed;
        _log?.Invoke($"[Update] {message}");
        UpdateFailed?.Invoke(message);
        return false;
    }
}
