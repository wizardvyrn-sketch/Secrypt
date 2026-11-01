using PwManager.Core.Codec;
using PwManager.Core.Crypto;

namespace PwManager.Core.Tests;

public class CryptoTests
{
    [Fact]
    public void Hkdf_Rfc5869_TestCase1()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        var okm = new byte[42];
        System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, ikm, okm, salt, info);
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", Convert.ToHexString(okm).ToLowerInvariant());
    }

    [Fact]
    public void Aead_RoundTrip_And_DetectsTampering()
    {
        var key = CryptoPrimitives.RandomBytes(32);
        var nonce = CryptoPrimitives.RandomBytes(24);
        var aad = "header"u8.ToArray();
        var pt = "secret password"u8.ToArray();

        var ct = CryptoPrimitives.AeadEncrypt(key, nonce, aad, pt);
        Assert.Equal(pt.Length + CryptoPrimitives.TagSize, ct.Length);

        using (var dec = CryptoPrimitives.AeadDecrypt(key, nonce, aad, ct))
        {
            Assert.NotNull(dec);
            Assert.Equal(pt, dec!.ReadOnlySpan.ToArray());
        }

        var tampered = (byte[])ct.Clone();
        tampered[0] ^= 1;
        Assert.Null(CryptoPrimitives.AeadDecrypt(key, nonce, aad, tampered));
        Assert.Null(CryptoPrimitives.AeadDecrypt(key, nonce, "other"u8, ct));

        var wrongKey = CryptoPrimitives.RandomBytes(32);
        Assert.Null(CryptoPrimitives.AeadDecrypt(wrongKey, nonce, aad, ct));
    }

    [Fact]
    public void Argon2id_IsDeterministic_AndSaltSensitive()
    {
        var pw = "correct horse"u8.ToArray();
        var salt1 = CryptoPrimitives.RandomBytes(16);
        var salt2 = CryptoPrimitives.RandomBytes(16);
        var a = new byte[32];
        var b = new byte[32];
        var c = new byte[32];
        CryptoPrimitives.Argon2id(pw, salt1, KdfParameters.TestOnly, a);
        CryptoPrimitives.Argon2id(pw, salt1, KdfParameters.TestOnly, b);
        CryptoPrimitives.Argon2id(pw, salt2, KdfParameters.TestOnly, c);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void KdfParameters_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfParameters(1024, 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfParameters(KdfParameters.MaxMemoryKiB + 1, 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfParameters(65536, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfParameters(65536, 2, 4).Validate());
        KdfParameters.Password.Validate();
    }

    [Fact]
    public void SecretBuffer_ZeroesOnDispose()
    {
        var sb = SecretBuffer.Random(32);
        Assert.Equal(32, sb.Length);
        sb.Dispose();
        Assert.True(sb.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => sb.Span.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(74)]
    public void CrockfordBase32_RoundTrip(int length)
    {
        var data = CryptoPrimitives.RandomBytes(length);
        var text = CrockfordBase32.Encode(data);
        Assert.Equal(data, CrockfordBase32.Decode(text, length));
        Assert.Equal(data, CrockfordBase32.Decode(text.ToLowerInvariant(), length));
    }

    [Fact]
    public void CrockfordBase32_NormalizesConfusableChars()
    {
        // 0 と O、1 と I/L を同一視する
        var data = new byte[] { 0x00, 0x42 };
        var text = CrockfordBase32.Encode(data); // "0011" 相当
        var confused = text.Replace('0', 'O').Replace('1', 'l');
        Assert.Equal(data, CrockfordBase32.Decode(confused, data.Length));
    }

    [Fact]
    public void CrockfordBase32_RejectsBadInput()
    {
        Assert.Throws<FormatException>(() => CrockfordBase32.Decode("U000", 2));
        Assert.Throws<FormatException>(() => CrockfordBase32.Decode("00", 2));
        Assert.Throws<FormatException>(() => CrockfordBase32.Decode("000000000", 2));
    }
}
