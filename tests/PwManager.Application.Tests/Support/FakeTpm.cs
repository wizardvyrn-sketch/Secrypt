using System.Security.Cryptography;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;

namespace PwManager.Application.Tests.Support;

/// <summary>
/// PC ごとの TPM を模擬する。blob は「この TPM の ID + 暗号文」で、別の TPM（別 PC・クリア後）の blob は読み込めない
/// （実機の TPM_RC_INTEGRITY 相当で TpmUnavailableException）。PIN 違いは失敗回数（DA）を数え、上限でロックする。
/// </summary>
internal sealed class FakeTpm(int maxFailures = 31) : ITpmSealer
{
    private byte[] _deviceId = RandomNumberGenerator.GetBytes(16);
    private byte[] _deviceKey = RandomNumberGenerator.GetBytes(32);

    public int Failures { get; private set; }
    public bool Available { get; set; } = true;
    public int UnsealCalls { get; private set; }

    /// <summary>TPM のクリア（SRK が作り直され、以前の blob は使えなくなる）。</summary>
    public void Clear()
    {
        _deviceId = RandomNumberGenerator.GetBytes(16);
        _deviceKey = RandomNumberGenerator.GetBytes(32);
        Failures = 0;
    }

    /// <summary>DA ロックの解除（時間経過）。</summary>
    public void ResetLockout() => Failures = 0;

    public bool IsAvailable(out string reason)
    {
        reason = Available ? string.Empty : "TPM がありません（テスト）";
        return Available;
    }

    public byte[] Seal(ReadOnlySpan<byte> authValue, ReadOnlySpan<byte> secret)
    {
        EnsureAvailable();
        var nonce = CryptoPrimitives.RandomBytes(24);
        var ct = CryptoPrimitives.AeadEncrypt(_deviceKey, nonce, SHA256.HashData(authValue), secret);
        return [.. _deviceId, .. nonce, .. ct];
    }

    public bool IsSealedOnThisDevice(ReadOnlySpan<byte> blob)
    {
        EnsureAvailable();
        return blob.Length > 16 + 24 && blob[..16].SequenceEqual(_deviceId);
    }

    public SecretBuffer Unseal(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> authValue)
    {
        EnsureAvailable();
        UnsealCalls++;
        if (!IsSealedOnThisDevice(blob))
            throw new TpmUnavailableException("TPM の封印データを読み込めません（テスト: 別の TPM）。");
        if (Failures >= maxFailures) throw new TpmLockedOutException("TPM がロック中（テスト）");
        var r = CryptoPrimitives.AeadDecrypt(_deviceKey, blob.Slice(16, 24), SHA256.HashData(authValue), blob[40..]);
        if (r is null)
        {
            Failures++;
            throw new TpmAuthFailedException("PIN が違います（テスト）");
        }
        return r;
    }

    private void EnsureAvailable()
    {
        if (!Available) throw new TpmUnavailableException("TPM に接続できません（テスト）。");
    }
}
