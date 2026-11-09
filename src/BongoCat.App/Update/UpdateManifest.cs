using System.Text.Json;

namespace BongoCat.App.Update;

/// <summary>
/// 更新清单（GET /manifest.json 反序列化目标）。
/// </summary>
/// <param name="Version">新版本号（与客户端程序集版本比较，System.Version 语义）。</param>
/// <param name="PackageFile">更新包文件名（仅允许安全字符，防路径穿越）。</param>
/// <param name="Sha256">更新包 zip 的 SHA256（64 位十六进制）。</param>
/// <param name="Signature">ECDsa P-256 签名（base64），对 "version|packageFile|sha256" 规范串签名。</param>
/// <param name="ReleaseNotes">发布说明（可选）。</param>
/// <param name="PublishedAt">发布时间（可选，ISO 8601）。</param>
public sealed record UpdateManifest(
    string Version,
    string PackageFile,
    string Sha256,
    string Signature,
    string ReleaseNotes,
    string PublishedAt)
{
    /// <summary>
    /// 解析并校验 manifest JSON。任何缺字段/非法值（版本不可解析、包名带路径分隔符、
    /// sha256 非 64 位十六进制、签名为空）一律返回 null——绝不安装半包。
    /// </summary>
    public static UpdateManifest? TryParse(string json)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        // 版本必须可解析（客户端比较依赖 System.Version 语义；注意 Version 被同名属性遮蔽，需全限定）
        if (!System.Version.TryParse(manifest.Version, out _))
        {
            return null;
        }

        // 包名白名单：仅字母/数字/点/横线/下划线，禁止任何路径分隔符（防穿越/绝对路径/隐藏文件）
        if (manifest.PackageFile.Length == 0 ||
            manifest.PackageFile[0] == '.' ||
            manifest.PackageFile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
        {
            return null;
        }

        // SHA256 必须 64 位十六进制
        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(c => !Uri.IsHexDigit(c)))
        {
            return null;
        }

        if (manifest.Signature.Length == 0)
        {
            return null;
        }

        return manifest;
    }

    /// <summary>签名覆盖的规范串："version|packageFile|sha256"（发布脚本同口径）。</summary>
    public string CanonicalPayload() => $"{Version}|{PackageFile}|{Sha256}";
}
