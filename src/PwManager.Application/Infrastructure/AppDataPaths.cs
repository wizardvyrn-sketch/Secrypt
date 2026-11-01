namespace PwManager.Application.Infrastructure;

/// <summary>
/// データの置き場所。環境変数 PWM_DATA で切り替えられる（試行時に本番の設定・ログを汚さない: docs/10 §7）。
/// </summary>
public sealed class AppDataPaths
{
    public const string DataEnvironmentVariable = "PWM_DATA";

    public AppDataPaths(string dataDirectory) => DataDirectory = Path.GetFullPath(dataDirectory);

    public string DataDirectory { get; }
    public string DefaultVaultPath => Path.Combine(DataDirectory, "vault.pwmv");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// 既定（%LOCALAPPDATA%\NewPWManageTool）。PWM_DATA が設定されていればそちら。
    /// アプリ名は Secrypt に変えたが、既存の Vault を見失わないようフォルダ名は旧名のまま。
    /// </summary>
    public static AppDataPaths FromEnvironment()
    {
        var overridden = Environment.GetEnvironmentVariable(DataEnvironmentVariable);
        return new AppDataPaths(!string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewPWManageTool"));
    }
}
