namespace PwManager.Application.Infrastructure;

/// <summary>
/// ログ・診断に書くパスからユーザー名を伏せる（D-4）。例: C:\Users\taro\Documents\a.pwmv → %USERPROFILE%\Documents\a.pwmv
/// </summary>
public static class PathRedactor
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>パス 1 つを伏せる。</summary>
    public static string Redact(string? path) => Redact(path,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    /// <summary>文章（例外メッセージ・スタック）の中に含まれるパスからユーザー名を伏せる。</summary>
    public static string RedactText(string? text) => RedactText(text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    internal static string Redact(string? path, string? userProfile, string? userName)
    {
        var result = RedactText(path, userProfile, userName);
        // パスの末尾がユーザー名（C:\Users\taro など）
        if (!string.IsNullOrEmpty(userName))
            foreach (var sep in Separators)
                if (result.EndsWith(sep + userName, Comparison)) result = result[..^userName.Length] + "%USERNAME%";
        return result;
    }

    internal static string RedactText(string? text, string? userProfile, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        if (!string.IsNullOrEmpty(userProfile))
        {
            var profile = userProfile.TrimEnd(Separators);
            if (profile.Length > 1) result = result.Replace(profile, "%USERPROFILE%", Comparison);
        }
        // 別ドライブの D:\Users\taro\... なども、パスの区切りに挟まれたユーザー名は伏せる
        if (!string.IsNullOrEmpty(userName))
            foreach (var sep in Separators)
                result = result.Replace($"{sep}{userName}{sep}", $"{sep}%USERNAME%{sep}", Comparison);
        return result;
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
