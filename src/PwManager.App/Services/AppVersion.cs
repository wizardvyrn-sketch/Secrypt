using System.Reflection;

namespace PwManager.App.Services;

/// <summary>
/// 表示用の版（1.00A 形式）。Directory.Build.props の DisplayVersion がアセンブリのメタデータとして埋め込まれる。
/// </summary>
public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DisplayVersion")?.Value ?? "?";

    /// <summary>解錠画面に出す表記（例: Ver. 1.01A）。</summary>
    public static string Label => $"Ver. {Display}";
}
