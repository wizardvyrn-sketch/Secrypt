using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PwManager.Core.Crypto;

/// <summary>
/// 秘密データ用のバッファ。GC に移動されないようピン留めし、Windows ではページアウトを抑止（VirtualLock）、
/// Dispose 時にゼロ化する。秘密は string に載せず、このバッファで受け渡す。
/// </summary>
public sealed class SecretBuffer : IDisposable
{
    private byte[]? _buffer;
    private bool _locked;

    public SecretBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _buffer = GC.AllocateArray<byte>(length, pinned: true);
        TryLock();
    }

    public static SecretBuffer FromSpan(ReadOnlySpan<byte> data)
    {
        var sb = new SecretBuffer(data.Length);
        data.CopyTo(sb.Span);
        return sb;
    }

    public static SecretBuffer Random(int length)
    {
        var sb = new SecretBuffer(length);
        RandomNumberGenerator.Fill(sb.Span);
        return sb;
    }

    public int Length => (_buffer ?? throw new ObjectDisposedException(nameof(SecretBuffer))).Length;

    public Span<byte> Span => _buffer ?? throw new ObjectDisposedException(nameof(SecretBuffer));

    public ReadOnlySpan<byte> ReadOnlySpan => Span;

    public bool IsDisposed => _buffer is null;

    /// <summary>ログや例外メッセージに中身が出ないよう、固定の文字列を返す（docs/10 §3.1）。</summary>
    public override string ToString() => "***";

    public void Dispose()
    {
        var buf = _buffer;
        if (buf is null) return;
        CryptographicOperations.ZeroMemory(buf);
        TryUnlock(buf);
        _buffer = null;
    }

    private void TryLock()
    {
        if (!OperatingSystem.IsWindows() || _buffer is null || _buffer.Length == 0) return;
        try
        {
            _locked = NativeMethods.VirtualLock(Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0), (nuint)_buffer.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _locked = false; // ベストエフォート
        }
    }

    private void TryUnlock(byte[] buf)
    {
        if (!_locked || buf.Length == 0) return;
        try
        {
            NativeMethods.VirtualUnlock(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0), (nuint)buf.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        _locked = false;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualLock(IntPtr lpAddress, nuint dwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualUnlock(IntPtr lpAddress, nuint dwSize);
    }
}
