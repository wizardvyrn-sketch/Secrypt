using System.Globalization;
using PwManager.Application.Diagnostics;
using PwManager.Application.Infrastructure;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;

namespace PwManager.Cli;

/// <summary>
/// 診断用 CLI。
/// <code>
/// pwm diag vault [path]     ヘッダ・スロット一覧（秘密なし）
/// pwm diag tpm              TPM の利用可否（Windows のみ）
/// pwm backups [path]        バックアップ世代の一覧
/// pwm bench argon2          KDF の所要時間
/// pwm logs [--days N]       直近ログの要約
/// </code>
/// path を省略すると既定の Vault（PWM_DATA で切り替え可）。
/// </summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter output, TextWriter error, ITpmSealer? tpm = null, AppDataPaths? paths = null)
    {
        paths ??= AppDataPaths.FromEnvironment();
        tpm ??= CreateTpm();
        string Vault(int i) => args.Length > i ? args[i] : paths.DefaultVaultPath;
        try
        {
            switch (args)
            {
                case ["diag", "vault", ..]:
                    output.Write(VaultDiagnostics.DescribeVault(Vault(2), tpm));
                    return 0;
                case ["diag", "tpm"]:
                    output.WriteLine(VaultDiagnostics.DescribeTpm(tpm));
                    return 0;
                case ["backups", ..]:
                    output.Write(VaultDiagnostics.DescribeBackups(Vault(1)));
                    return 0;
                case ["bench", "argon2"]:
                    output.Write(VaultDiagnostics.BenchArgon2([("Password", KdfParameters.Password), ("PIN", KdfParameters.Pin), ("Recovery", KdfParameters.Recovery)]));
                    return 0;
                case ["logs"]:
                    output.Write(VaultDiagnostics.SummarizeLogs(paths.LogDirectory, 7));
                    return 0;
                case ["logs", "--days", var d] when int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0:
                    output.Write(VaultDiagnostics.SummarizeLogs(paths.LogDirectory, days));
                    return 0;
                default:
                    error.WriteLine("""
                        使い方:
                          pwm diag vault [path]   Vault のヘッダ・解錠方法の一覧（秘密は表示しません）
                          pwm diag tpm            TPM の利用可否
                          pwm backups [path]      バックアップ世代の一覧
                          pwm bench argon2        鍵の導出（Argon2id）の所要時間
                          pwm logs [--days N]     直近のログの要約（既定 7 日）
                        """);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"読み込めませんでした: {PathRedactor.RedactText(ex.Message)}");
            return 1;
        }
    }

    private static ITpmSealer? CreateTpm()
    {
#if WINDOWS
        return new PwManager.Platform.Windows.WindowsTpmSealer();
#else
        return null;
#endif
    }
}
