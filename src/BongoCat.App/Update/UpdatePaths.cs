using System.IO;

namespace BongoCat.App.Update;

/// <summary>更新相关路径与配置解析（烟测可用 BONGOCAT_SAVE_DIR / BONGOCAT_UPDATE_URL 隔离）。</summary>
public static class UpdatePaths
{
    /// <summary>更新分发基址（RelayServer 9438 HTTP 端点；烟测可覆盖）。</summary>
    public const string DefaultUpdateBaseUrl = "http://124.222.175.227:9438";

    /// <summary>默认更新分发基址（可被 BONGOCAT_UPDATE_URL 环境变量覆盖——烟测/私有部署）。</summary>
    public static string ResolveUpdateBaseUrl()
    {
        string? custom = Environment.GetEnvironmentVariable("BONGOCAT_UPDATE_URL");
        return string.IsNullOrWhiteSpace(custom) ? DefaultUpdateBaseUrl : custom.TrimEnd('/');
    }

    /// <summary>更新暂存目录：%APPDATA%/BongoCatRemix/update（BONGOCAT_SAVE_DIR 烟测时随之隔离）。</summary>
    public static string ResolveUpdateDir()
    {
        string? smokeDir = Environment.GetEnvironmentVariable("BONGOCAT_SAVE_DIR");
        return string.IsNullOrWhiteSpace(smokeDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BongoCatRemix",
                "update")
            : Path.Combine(smokeDir, "update");
    }
}
