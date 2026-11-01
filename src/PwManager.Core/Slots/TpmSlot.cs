using PwManager.Core.Crypto;
using PwManager.Core.Vault;

namespace PwManager.Core.Slots;

/// <summary>TPM の PIN が違う（TPM_RC_AUTH_FAIL）。TPM の失敗カウンタが 1 増えている。</summary>
public sealed class TpmAuthFailedException(string message) : Exception(message);

/// <summary>TPM が総当たり対策でロック中（TPM_RC_LOCKOUT）。時間をおくか、別のスロットで解錠する。</summary>
public sealed class TpmLockedOutException(string message) : Exception(message);

/// <summary>TPM が使えない（未搭載・無効・TBS 接続不可・SRK なし・TPM クリア後など）。</summary>
public sealed class TpmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// TPM シール（ADR-0004）の抽象。実装は Platform.Windows の WindowsTpmSealer。テストではフェイクを使う。
/// </summary>
public interface ITpmSealer
{
    /// <summary>TPM が使えるか。使えない場合は理由を返す。</summary>
    bool IsAvailable(out string reason);

    /// <summary>secret を authValue 付きで封印し、Vault に保存できる不透明な blob を返す。</summary>
    byte[] Seal(ReadOnlySpan<byte> authValue, ReadOnlySpan<byte> secret);

    /// <summary>blob を authValue で解除する。PIN 違いは TpmAuthFailedException、ロック中は TpmLockedOutException。</summary>
    SecretBuffer Unseal(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> authValue);

    /// <summary>
    /// blob がこの PC の TPM で封印したものか（読み込めるか）。PIN を使わないので TPM の失敗回数は増えない。
    /// 別の PC の TPM や、クリア前の TPM で封印した blob は false。TPM が使えない場合は TpmUnavailableException。
    /// </summary>
    bool IsSealedOnThisDevice(ReadOnlySpan<byte> blob);
}

/// <summary>
/// TPM ＋ PIN のスロット（日常用）。
/// <code>
/// stretched = Argon2id(PIN, salt, Pin パラメータ)
/// authValue = HKDF(stretched, salt, "pwm/tpm-auth/v1")       … TPM に渡す PIN 由来値（TPM が総当たりを数える）
/// tpmSecret = TPM に封印した 32B 乱数
/// KEK       = HKDF(tpmSecret ‖ stretched, salt, "pwm/kek/v1/tpm")
/// </code>
/// TypeParams = 封印 blob。TPM がクリアされると blob は使えなくなるため、TPM スロット単独にはしない（SlotPolicy で担保）。
/// </summary>
public static class TpmSlot
{
    public const int SecretSize = 32;
    private const string AuthInfo = "pwm/tpm-auth/v1";

    public static (KeySlot Slot, SecretBuffer Kek) Enroll(ITpmSealer sealer, string label, ReadOnlySpan<byte> pin, KdfParameters? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(sealer);
        if (pin.IsEmpty) throw new ArgumentException("PIN が空です。", nameof(pin));
        if (!sealer.IsAvailable(out var reason)) throw new TpmUnavailableException(reason);

        var parameters = kdf ?? KdfParameters.Pin;
        var salt = CryptoPrimitives.RandomBytes(CryptoPrimitives.SaltSize);
        using var stretched = Stretch(pin, salt, parameters);
        using var auth = CryptoPrimitives.HkdfToSecret(stretched.ReadOnlySpan, salt, AuthInfo);
        using var secret = SecretBuffer.Random(SecretSize);

        var blob = sealer.Seal(auth.ReadOnlySpan, secret.ReadOnlySpan);
        var slot = new KeySlot { Type = SlotType.Tpm, Label = label, Salt = salt, Kdf = parameters, TypeParams = blob };
        return (slot, DeriveKekCore(slot, secret, stretched));
    }

    public static SecretBuffer DeriveKek(KeySlot slot, ITpmSealer sealer, ReadOnlySpan<byte> pin)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(sealer);
        if (slot.Type != SlotType.Tpm) throw new ArgumentException("TPM のスロットではありません。", nameof(slot));

        using var stretched = Stretch(pin, slot.Salt, slot.Kdf);
        using var auth = CryptoPrimitives.HkdfToSecret(stretched.ReadOnlySpan, slot.Salt, AuthInfo);
        using var secret = sealer.Unseal(slot.TypeParams, auth.ReadOnlySpan);
        if (secret.Length != SecretSize) throw new VaultTamperedException("TPM から取り出した秘密の長さが不正です。");
        return DeriveKekCore(slot, secret, stretched);
    }

    private static SecretBuffer Stretch(ReadOnlySpan<byte> pin, byte[] salt, KdfParameters kdf)
    {
        var sb = new SecretBuffer(CryptoPrimitives.KeySize);
        CryptoPrimitives.Argon2id(pin, salt, kdf, sb.Span);
        return sb;
    }

    private static SecretBuffer DeriveKekCore(KeySlot slot, SecretBuffer secret, SecretBuffer stretched)
    {
        using var possession = new SecretBuffer(secret.Length + stretched.Length);
        secret.ReadOnlySpan.CopyTo(possession.Span);
        stretched.ReadOnlySpan.CopyTo(possession.Span[secret.Length..]);
        return slot.DeriveKek(possession.ReadOnlySpan, []);
    }
}
