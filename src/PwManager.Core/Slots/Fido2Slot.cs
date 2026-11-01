using PwManager.Core.Crypto;
using PwManager.Core.Vault;

namespace PwManager.Core.Slots;

/// <summary>
/// FIDO2 hmac-secret から得る 32 バイトの秘密を供給する抽象。実装は Platform.Windows（webauthn.dll）で行う。
/// テストではモック実装を使う。
/// </summary>
public interface IFido2SecretProvider
{
    /// <summary>新しいクレデンシャルを作成し、その credential ID を返す（UV 必須、credProtect レベル 2 以上）。</summary>
    byte[] MakeCredential(string rpId, string userLabel);

    /// <summary>credential ID と salt から hmac-secret（32 バイト）を取得する。</summary>
    SecretBuffer GetHmacSecret(string rpId, ReadOnlySpan<byte> credentialId, ReadOnlySpan<byte> salt);
}

/// <summary>
/// FIDO2 キー＋パスワードのスロット（保留機能、ADR-0005）。FeatureFlags.Fido2Enabled が false の間は使用不可。
/// TypeParams = u16 rpIdLen | rpId(UTF-8) | credentialId。hmac-secret の salt にはスロットの Salt を使う。
/// </summary>
public static class Fido2Slot
{
    public const string DefaultRpId = "pwmanager.local";

    public static (KeySlot Slot, SecretBuffer Kek) Enroll(IFido2SecretProvider provider, string label, ReadOnlySpan<byte> password, KdfParameters? kdf = null)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(provider);
        var credId = provider.MakeCredential(DefaultRpId, label);
        var slot = new KeySlot
        {
            Type = SlotType.Fido2,
            Label = label,
            Kdf = kdf ?? KdfParameters.Password,
            TypeParams = EncodeParams(DefaultRpId, credId),
        };
        using var secret = provider.GetHmacSecret(DefaultRpId, credId, slot.Salt);
        return (slot, slot.DeriveKek(secret.ReadOnlySpan, password));
    }

    public static SecretBuffer DeriveKek(KeySlot slot, IFido2SecretProvider provider, ReadOnlySpan<byte> password)
    {
        EnsureEnabled();
        if (slot.Type != SlotType.Fido2) throw new ArgumentException("FIDO2 のスロットではありません。", nameof(slot));
        var (rpId, credId) = DecodeParams(slot.TypeParams);
        using var secret = provider.GetHmacSecret(rpId, credId, slot.Salt);
        return slot.DeriveKek(secret.ReadOnlySpan, password);
    }

    private static void EnsureEnabled()
    {
        if (!FeatureFlags.Fido2Enabled)
            throw new NotSupportedException("FIDO2 スロットは保留機能です。FIDO2 キー入手後に設定で有効化してください。");
    }

    internal static byte[] EncodeParams(string rpId, ReadOnlySpan<byte> credId)
    {
        var rp = System.Text.Encoding.UTF8.GetBytes(rpId);
        var b = new byte[2 + rp.Length + credId.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), (ushort)rp.Length);
        rp.CopyTo(b, 2);
        credId.CopyTo(b.AsSpan(2 + rp.Length));
        return b;
    }

    internal static (string RpId, byte[] CredentialId) DecodeParams(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) throw new VaultFormatException("FIDO2 スロットのパラメータが不正です。");
        int len = BitConverter.ToUInt16(p[..2]);
        if (p.Length < 2 + len + 1) throw new VaultFormatException("FIDO2 スロットのパラメータが不正です。");
        return (System.Text.Encoding.UTF8.GetString(p.Slice(2, len)), p[(2 + len)..].ToArray());
    }
}
