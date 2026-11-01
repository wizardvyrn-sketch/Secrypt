using PwManager.Core.Crypto;

namespace PwManager.Core.Vault;

/// <summary>キースロットの種別。値は Vault ファイルに保存されるため変更しないこと。</summary>
public enum SlotType : byte
{
    Recovery = 1,
    Keyfile = 2,
    /// <summary>保留機能（ADR-0005）。FeatureFlags.Fido2Enabled が true の場合のみ登録可能。</summary>
    Fido2 = 3,
    /// <summary>TPM シール（ADR-0004）。Platform.Windows で実装予定。</summary>
    Tpm = 4,
}

/// <summary>
/// VMK を包んだ 1 つの解錠経路。Salt/Kdf/TypeParams は KEK 導出の材料、WrappedKey は KEK で暗号化した VMK。
/// </summary>
public sealed class KeySlot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SlotType Type { get; init; }
    public string Label { get; set; } = string.Empty;
    public byte[] Salt { get; init; } = CryptoPrimitives.RandomBytes(CryptoPrimitives.SaltSize);
    public KdfParameters Kdf { get; init; }
    /// <summary>種別固有のパラメータ（TPM シール blob、FIDO2 credential ID など）。</summary>
    public byte[] TypeParams { get; init; } = [];
    public byte[] Nonce { get; internal set; } = [];
    public byte[] WrappedKey { get; internal set; } = [];
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>最後にこのスロットで解錠できた日時（予備の健全性チェック用）。</summary>
    public DateTimeOffset? LastVerified { get; set; }

    internal byte[] WrapAssociatedData(Guid vaultId)
    {
        var aad = new byte[16 + 16 + 1];
        vaultId.TryWriteBytes(aad.AsSpan(0, 16));
        Id.TryWriteBytes(aad.AsSpan(16, 16));
        aad[32] = (byte)Type;
        return aad;
    }

    /// <summary>
    /// KEK = HKDF( 所持要素 ‖ Argon2id(知識要素) , salt , "pwm/kek/v1/{type}" )。
    /// 所持要素（TPM/キーファイル/FIDO2 の秘密）と知識要素（パスワード/PIN/復旧コード）のどちらかは空でもよいが、両方空は不可。
    /// </summary>
    internal SecretBuffer DeriveKek(ReadOnlySpan<byte> possession, ReadOnlySpan<byte> knowledge)
    {
        if (possession.IsEmpty && knowledge.IsEmpty)
            throw new ArgumentException("KEK の材料がありません。");
        int stretchedLen = knowledge.IsEmpty ? 0 : CryptoPrimitives.KeySize;
        using var ikm = new SecretBuffer(possession.Length + stretchedLen);
        possession.CopyTo(ikm.Span);
        if (!knowledge.IsEmpty)
            CryptoPrimitives.Argon2id(knowledge, Salt, Kdf, ikm.Span.Slice(possession.Length, stretchedLen));
        return CryptoPrimitives.HkdfToSecret(ikm.ReadOnlySpan, Salt, "pwm/kek/v1/" + Type.ToString().ToLowerInvariant());
    }
}
