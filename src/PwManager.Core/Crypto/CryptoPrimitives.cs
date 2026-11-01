using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace PwManager.Core.Crypto;

/// <summary>
/// 本プロジェクトで使う暗号プリミティブの集約点。自作暗号は行わず、組み合わせのみ。
/// - AEAD: XChaCha20-Poly1305（libsodium / NSec）
/// - パスワード KDF: Argon2id（libsodium / NSec）
/// - 鍵導出: HKDF-SHA256（.NET 標準）
/// - MAC: HMAC-SHA256（.NET 標準）
/// </summary>
public static class CryptoPrimitives
{
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;
    public const int SaltSize = 16;

    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    public static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    // ---------- AEAD ----------

    public static byte[] AeadEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        CheckKeyAndNonce(key, nonce);
        using var k = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        var ciphertext = new byte[plaintext.Length + TagSize];
        Aead.Encrypt(k, nonce, associatedData, plaintext, ciphertext);
        return ciphertext;
    }

    /// <summary>復号に失敗（鍵違い・改ざん）した場合は null を返す。成功時の平文は SecretBuffer で返す。</summary>
    public static SecretBuffer? AeadDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        CheckKeyAndNonce(key, nonce);
        if (ciphertext.Length < TagSize) return null;
        using var k = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        var plaintext = new SecretBuffer(ciphertext.Length - TagSize);
        if (Aead.Decrypt(k, nonce, associatedData, ciphertext, plaintext.Span))
            return plaintext;
        plaintext.Dispose();
        return null;
    }

    private static void CheckKeyAndNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeySize) throw new ArgumentException($"鍵長は {KeySize} バイトである必要があります。", nameof(key));
        if (nonce.Length != NonceSize) throw new ArgumentException($"nonce 長は {NonceSize} バイトである必要があります。", nameof(nonce));
    }

    // ---------- Argon2id ----------

    public static void Argon2id(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, Span<byte> output)
    {
        parameters.Validate();
        if (salt.Length != SaltSize) throw new ArgumentException($"salt は {SaltSize} バイトである必要があります。", nameof(salt));
        var alg = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = parameters.Parallelism,
            MemorySize = parameters.MemoryKiB,
            NumberOfPasses = parameters.Iterations,
        });
        alg.DeriveBytes(password, salt, output);
    }

    // ---------- HKDF / HMAC ----------

    public static void Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, Span<byte> output)
    {
        Span<byte> infoBytes = stackalloc byte[Encoding.UTF8.GetByteCount(info)];
        Encoding.UTF8.GetBytes(info, infoBytes);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, infoBytes);
    }

    public static SecretBuffer HkdfToSecret(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length = KeySize)
    {
        var sb = new SecretBuffer(length);
        Hkdf(ikm, salt, info, sb.Span);
        return sb;
    }

    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);
}
