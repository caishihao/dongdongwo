using System.Security.Cryptography;

namespace BongoCat.App.Update;

/// <summary>
/// manifest 签名验证（ECDsa P-256）。公钥硬编码进客户端，私钥由发布脚本本地保管
/// （deploy/keys/update-signing.pkcs8，不入库）——防传输链路篡改，签名无效即拒绝更新。
/// </summary>
public static class UpdateSignatureVerifier
{
    /// <summary>发布脚本生成的签名公钥（SPKI，SubjectPublicKeyInfo，base64）。</summary>
    public const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBDvZ1AxcUtbvasUa9MrCwsbkn//taN2rIFcZEj9NZgAH35MR7gffyw0XktUT7WQs1iL5zF8587dj9wV4Gf/zZA==";

    /// <summary>验证 manifest 签名是否有效（默认内置公钥；测试/私有部署可用 <see cref="VerifyWith"/> 注入公钥）。</summary>
    public static bool Verify(UpdateManifest manifest)
        => VerifyWith(manifest, PublicKeySpkiBase64);

    /// <summary>用指定 SPKI 公钥验签（发布脚本同口径：对 CanonicalPayload 验签）。</summary>
    public static bool VerifyWith(UpdateManifest manifest, string publicKeySpkiBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(manifest.CanonicalPayload());
            return ecdsa.VerifyData(payload, Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false; // base64 非法
        }
        catch (CryptographicException)
        {
            return false; // 公钥/签名格式非法
        }
    }
}
