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

    /// <summary>
    /// Windows が標準ユーザーの TPM コマンドをブロックしている状態（TPM イベント ID 23）。実機では、PIN の誤りだけでなく
    /// 封印・読み込みも含む認証付きコマンドがすべて TpmLockedOutException になる（2026-11-21 に実機で確認）。
    /// </summary>
    public bool OsBlocked { get; set; }

    /// <summary>TPM への問い合わせ（認証付きコマンド）の回数。ブロック中・PIN 停止中に問い合わせないことの確認に使う。</summary>
    public int CommandCalls { get; private set; }

    /// <summary>想定外の例外（TPM のドライバの不具合など）を再現する。</summary>
    public bool ThrowUnexpected { get; set; }

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
        EnsureCommandAllowed();
        var nonce = CryptoPrimitives.RandomBytes(24);
        var ct = CryptoPrimitives.AeadEncrypt(_deviceKey, nonce, SHA256.HashData(authValue), secret);
        return [.. _deviceId, .. nonce, .. ct];
    }

    public bool IsSealedOnThisDevice(ReadOnlySpan<byte> blob)
    {
        EnsureCommandAllowed();
        return blob.Length > 16 + 24 && blob[..16].SequenceEqual(_deviceId);
    }

    public SecretBuffer Unseal(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> authValue)
    {
        EnsureCommandAllowed();
        UnsealCalls++;
        if (!IsSealedOnThisDevice(blob))
            throw new TpmUnavailableException("TPM の封印データを読み込めません（テスト: 別の TPM）。");
        var r = CryptoPrimitives.AeadDecrypt(_deviceKey, blob.Slice(16, 24), SHA256.HashData(authValue), blob[40..]);
        if (r is null)
        {
            Failures++;
            throw new TpmAuthFailedException("PIN が違います（テスト）");
        }
        return r;
    }

    /// <summary>認証付きコマンドの共通の入口。ロックアウト中（OS のブロック、または DA の上限）は何も実行できない。</summary>
    private void EnsureCommandAllowed()
    {
        CommandCalls++;
        if (ThrowUnexpected) throw new InvalidOperationException("想定外の TPM の失敗（テスト）");
        if (!Available) throw new TpmUnavailableException("TPM に接続できません（テスト）。");
        if (OsBlocked || Failures >= maxFailures) throw new TpmLockedOutException("TPM がロック中（テスト）");
    }
}
