using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;

namespace PwManager.App.Services;

/// <summary>PasswordBox の内容を string を経由せずに UTF-8 バイト列で取り出す。</summary>
public static class SecureInput
{
    public static byte[] ToUtf8(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        if (secure.Length == 0) return [];
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        var chars = new char[secure.Length];
        try
        {
            Marshal.Copy(ptr, chars, 0, chars.Length);
            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            Array.Clear(chars);
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    public static bool SameContent(PasswordBox a, PasswordBox b)
    {
        var x = ToUtf8(a);
        var y = ToUtf8(b);
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y); }
        finally { Array.Clear(x); Array.Clear(y); }
    }

    public static int Length(PasswordBox box) => box.SecurePassword.Length;
}
