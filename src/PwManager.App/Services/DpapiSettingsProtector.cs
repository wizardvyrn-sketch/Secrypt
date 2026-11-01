using System.Security.Cryptography;
using System.Text;
using PwManager.Application.Ports;

namespace PwManager.App.Services;

/// <summary>
/// 設定ファイルの一部（最後に使った鍵ファイルの場所）を Windows の DPAPI で暗号化する。現在の Windows ユーザーだけが復号できる。
/// 同じユーザーで動くプログラムからは復号できるため、守れるのは「設定ファイルが単体でコピー・流出した場合」まで。
/// </summary>
public sealed class DpapiSettingsProtector : ISettingsProtector
{
    private static readonly byte[] Entropy = "Secrypt/settings/v1"u8.ToArray();

    public string Protect(string plainText)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
