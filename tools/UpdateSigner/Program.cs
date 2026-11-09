using System.Security.Cryptography;

namespace BongoCat.UpdateSigner;

/// <summary>
/// 更新签名工具（供 scripts/publish-release.ps1 调用）：
///   ensure-key &lt;pkcs8私钥路径&gt; &lt;spki公钥输出路径&gt;  — 无密钥自动生成（P-256），有则导出公钥
///   sign &lt;pkcs8私钥路径&gt; &lt;payload文件路径&gt;          — 对 payload 做 ECDsa P-256/SHA256 签名，输出 base64
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("""
                用法：
                  UpdateSigner ensure-key <pkcs8私钥路径> <spki公钥输出路径>
                  UpdateSigner sign <pkcs8私钥路径> <payload文件路径>
                """);
            return 1;
        }

        return args[0] switch
        {
            "ensure-key" => EnsureKey(args[1], args[2]),
            "sign" => Sign(args[1], args[2]),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}");
        return 1;
    }

    private static int EnsureKey(string keyPath, string spkiPath)
    {
        ECDsa ecc;
        if (File.Exists(keyPath))
        {
            ecc = ECDsa.Create();
            ecc.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
            Console.WriteLine("密钥已存在：复用 " + keyPath);
        }
        else
        {
            ecc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
            File.WriteAllBytes(keyPath, ecc.ExportPkcs8PrivateKey());
            Console.WriteLine("已生成新签名密钥：" + keyPath);
            Console.WriteLine("注意：新公钥必须同步硬编码进客户端 UpdateSignatureVerifier.PublicKeySpkiBase64！");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(spkiPath))!);
        File.WriteAllText(spkiPath, Convert.ToBase64String(ecc.ExportSubjectPublicKeyInfo()));
        Console.WriteLine("公钥 SPKI(base64) 已写：" + spkiPath);
        return 0;
    }

    private static int Sign(string keyPath, string payloadPath)
    {
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine("签名私钥不存在：" + keyPath);
            return 1;
        }

        var ecc = ECDsa.Create();
        ecc.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
        byte[] signature = ecc.SignData(File.ReadAllBytes(payloadPath), HashAlgorithmName.SHA256);
        Console.WriteLine(Convert.ToBase64String(signature));
        return 0;
    }
}
