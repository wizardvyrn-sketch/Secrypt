using System.Security.Cryptography;
using System.Text;
using PwManager.Application.Results;

namespace PwManager.Application.Policies;

/// <summary>PIN・パスワードの入力規則（R-05: 初期設定と「鍵と設定」で共有する）。</summary>
public static class CredentialPolicy
{
    /// <summary>TPM の PIN の最小文字数（TPM 側で総当たりが止まるため短めでよい）。</summary>
    public const int MinPinLength = 6;

    /// <summary>鍵ファイルと組み合わせるパスワードの最小文字数。</summary>
    public const int MinPasswordLength = 10;

    /// <summary>入力欄の UTF-8 バイト列の文字数（UTF-16 の文字単位。PasswordBox の Length と同じ数え方）。</summary>
    public static int CharCount(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetCharCount(utf8);

    public static OpResult CheckNewPin(ReadOnlySpan<byte> pin, ReadOnlySpan<byte> confirm)
        => Check(pin, confirm, MinPinLength, ErrorCode.PinTooShort);

    public static OpResult CheckNewPassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> confirm)
        => Check(password, confirm, MinPasswordLength, ErrorCode.PasswordTooShort);

    private static OpResult Check(ReadOnlySpan<byte> value, ReadOnlySpan<byte> confirm, int min, ErrorCode tooShort)
    {
        if (CharCount(value) < min) return OpResult.Fail(tooShort);
        if (!CryptographicOperations.FixedTimeEquals(value, confirm)) return OpResult.Fail(ErrorCode.ConfirmationMismatch);
        return OpResult.Success;
    }
}
