using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BongoCat.App.Update;
using BongoCat.Core.Update;
using Xunit;

namespace BongoCat.App.Tests;

/// <summary>E9 自更新：编排器状态机（下载/SHA256/暂存/失败恢复），fake HTTP + 真实 ECDsa 往返。</summary>
public class UpdateOrchestratorTests : IDisposable
{
    private readonly string _dir;
    private readonly ECDsa _signer;
    private readonly string _signerSpki;
    private readonly FakeTransferClient _transfer;
    private readonly List<string> _failures = [];
    private readonly List<UpdateManifest> _staged = [];

    public UpdateOrchestratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bongocat-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _signerSpki = Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());
        _transfer = new FakeTransferClient();
    }

    public void Dispose()
    {
        _signer.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateOrchestrator CreateOrchestrator(Version current)
    {
        var orchestrator = new UpdateOrchestrator(
            _transfer, _dir, current,
            signatureVerifier: m => UpdateSignatureVerifier.VerifyWith(m, _signerSpki));
        orchestrator.UpdateStaged += m => _staged.Add(m);
        orchestrator.UpdateFailed += _failures.Add;
        return orchestrator;
    }

    private (UpdateManifest Manifest, byte[] PackageBytes) PreparePackage(string version, byte[] exeBytes)
    {
        string zipPath = Path.Combine(_dir, "pkg-" + version + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("BongoCatRemix.exe");
            using Stream s = entry.Open();
            s.Write(exeBytes);
        }

        byte[] package = File.ReadAllBytes(zipPath);
        string sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var manifest = new UpdateManifest(version, "pkg.zip", sha, "", "测试版本", "");
        string payload = manifest.CanonicalPayload();
        manifest = manifest with { Signature = Convert.ToBase64String(_signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256)) };
        _transfer.ManifestJson = JsonSerializer.Serialize(manifest); // 属性名大小写不敏感，默认命名即可
        _transfer.PackageBytes = package;
        File.Delete(zipPath);
        return (manifest, package);
    }

    [Fact]
    public async Task 检查_有新版本_下载校验暂存成功()
    {
        PreparePackage("2.0.0", [1, 2, 3, 4]);
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.True(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Staged, orchestrator.State);
        Assert.NotNull(orchestrator.StagedExePath);
        Assert.True(File.Exists(orchestrator.StagedExePath));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(orchestrator.StagedExePath!));
        Assert.False(File.Exists(Path.Combine(_dir, "pkg.zip"))); // 包已消费删除
    }

    [Fact]
    public async Task 检查_无更新_状态不变()
    {
        PreparePackage("1.0.0", [1]); // 与当前版本相同
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.True(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.NoUpdate, orchestrator.State);
        Assert.Empty(_staged);
    }

    [Fact]
    public async Task 检查_签名被篡改_拒绝且不下载()
    {
        PreparePackage("2.0.0", [1]);
        // 破坏签名：重签一个不同 payload 的签名
        var tampered = JsonSerializer.Deserialize<UpdateManifest>(_transfer.ManifestJson)!;
        byte[] fakeSig = _signer.SignData(Encoding.UTF8.GetBytes("tampered|payload|xx"), HashAlgorithmName.SHA256);
        _transfer.ManifestJson = JsonSerializer.Serialize(tampered with { Signature = Convert.ToBase64String(fakeSig) });
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.False(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Failed, orchestrator.State);
        Assert.Empty(_transfer.DownloadCalls);
    }

    [Fact]
    public async Task 检查_包hash不匹配_失败且删除残包()
    {
        PreparePackage("2.0.0", [1, 2, 3]);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(_transfer.ManifestJson)!;
        // 篡改 Sha256 后必须重签（否则先撞签名校验）；服务端实际场景 = 包下载损坏但清单合法
        string tamperedSha = new string('f', 64);
        string tamperedPayload = $"{manifest.Version}|{manifest.PackageFile}|{tamperedSha}";
        _transfer.ManifestJson = JsonSerializer.Serialize(manifest with
        {
            Sha256 = tamperedSha,
            Signature = Convert.ToBase64String(_signer.SignData(Encoding.UTF8.GetBytes(tamperedPayload), HashAlgorithmName.SHA256)),
        });
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.False(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Failed, orchestrator.State);
        Assert.False(File.Exists(Path.Combine(_dir, "pkg.zip"))); // 残包已删
        Assert.Contains(_failures, f => f.Contains("SHA256"));
    }

    [Fact]
    public async Task 检查_网络异常_失败可恢复()
    {
        PreparePackage("2.0.0", [1]);
        _transfer.ThrowOnGetManifest = new IOException("连接被重置");
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.False(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Failed, orchestrator.State);

        _transfer.ThrowOnGetManifest = null; // 故障恢复：下次检查成功
        Assert.True(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Staged, orchestrator.State);
    }

    [Fact]
    public async Task 已暂存_重复检查_幂等跳过()
    {
        PreparePackage("2.0.0", [1]);
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));
        Assert.True(await orchestrator.CheckNowAsync());

        _transfer.ThrowOnGetManifest = new IOException("断网"); // 幂等：不再访问网络
        Assert.True(await orchestrator.CheckNowAsync());
        Assert.Equal(UpdateState.Staged, orchestrator.State);
    }

    [Fact]
    public void 遗留暂存_检测恢复_Staged态()
    {
        string staged = Path.Combine(_dir, UpdateOrchestrator.StagedExeName);
        File.WriteAllBytes(staged, [9]);
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));

        Assert.True(orchestrator.DetectExistingStaged());
        Assert.Equal(UpdateState.Staged, orchestrator.State);
        Assert.Equal(staged, orchestrator.StagedExePath);

        File.Delete(staged);
        var fresh = CreateOrchestrator(new Version(1, 0, 0));
        Assert.False(fresh.DetectExistingStaged());
    }

    [Fact]
    public void 遗留暂存_版本不高于当前_清理不恢复Staged()
    {
        // 用带真实版本信息的程序集（App.dll 默认 1.0.0.0）当暂存 exe
        string appAssembly = typeof(UpdateOrchestrator).Assembly.Location;
        Assert.True(File.Exists(appAssembly));
        string staged = Path.Combine(_dir, UpdateOrchestrator.StagedExeName);
        File.Copy(appAssembly, staged, overwrite: true);

        // 当前 1.0.0 ≥ 暂存 1.0.0 → 清理：返回 false、删除文件、不进 Staged 态
        var orchestrator = CreateOrchestrator(new Version(1, 0, 0));
        Assert.False(orchestrator.DetectExistingStaged());
        Assert.NotEqual(UpdateState.Staged, orchestrator.State);
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void 遗留暂存_版本高于当前_保留Staged待换位()
    {
        string appAssembly = typeof(UpdateOrchestrator).Assembly.Location;
        string staged = Path.Combine(_dir, UpdateOrchestrator.StagedExeName);
        File.Copy(appAssembly, staged, overwrite: true);

        // 当前 0.9.0 < 暂存 1.0.0 → 正常恢复 Staged 态（真实"重启换位"场景）
        var orchestrator = CreateOrchestrator(new Version(0, 9, 0));
        Assert.True(orchestrator.DetectExistingStaged());
        Assert.Equal(UpdateState.Staged, orchestrator.State);
        Assert.True(File.Exists(staged));
    }

    private sealed class FakeTransferClient : IUpdateTransferClient
    {
        public string ManifestJson = "";
        public byte[] PackageBytes = [];
        public Exception? ThrowOnGetManifest;
        public List<string> DownloadCalls = [];

        public Task<string> GetManifestJsonAsync(CancellationToken ct)
        {
            if (ThrowOnGetManifest is not null)
            {
                throw ThrowOnGetManifest;
            }

            return Task.FromResult(ManifestJson);
        }

        public Task DownloadPackageAsync(string packageFile, string targetPath, CancellationToken ct)
        {
            DownloadCalls.Add(packageFile);
            File.WriteAllBytes(targetPath, PackageBytes);
            return Task.CompletedTask;
        }
    }
}
