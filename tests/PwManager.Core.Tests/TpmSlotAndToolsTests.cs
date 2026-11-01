using System.Security.Cryptography;
using System.Text;
using PwManager.Core.Crypto;
using PwManager.Core.Generation;
using PwManager.Core.Import;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

/// <summary>TPM の振る舞い（authValue 検証・失敗回数でロック）を模擬するフェイク。</summary>
internal sealed class FakeTpmSealer(int maxFailures = 3) : ITpmSealer
{
    private readonly byte[] _deviceKey = CryptoPrimitives.RandomBytes(32);
    public int Failures { get; private set; }
    public bool Available { get; set; } = true;

    public bool IsAvailable(out string reason)
    {
        reason = Available ? string.Empty : "TPM がありません（テスト）";
        return Available;
    }

    public byte[] Seal(ReadOnlySpan<byte> authValue, ReadOnlySpan<byte> secret)
    {
        // blob = nonce(24) | AEAD(deviceKey, nonce, aad=SHA256(auth), secret)
        var nonce = CryptoPrimitives.RandomBytes(24);
        var ct = CryptoPrimitives.AeadEncrypt(_deviceKey, nonce, SHA256.HashData(authValue), secret);
        return [.. nonce, .. ct];
    }

    public bool IsSealedOnThisDevice(ReadOnlySpan<byte> blob) => blob.Length > 24;

    public SecretBuffer Unseal(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> authValue)
    {
        if (Failures >= maxFailures) throw new TpmLockedOutException("ロック中（テスト）");
        var r = CryptoPrimitives.AeadDecrypt(_deviceKey, blob[..24], SHA256.HashData(authValue), blob[24..]);
        if (r is null)
        {
            Failures++;
            throw new TpmAuthFailedException("PIN が違います（テスト）");
        }
        return r;
    }
}

public class TpmSlotTests
{
    [Fact]
    public void Enroll_And_Open_WithPin()
    {
        var tpm = new FakeTpmSealer();
        using var vault = PasswordVault.CreateNew();
        var (rSlot, code, rKek) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
        using (code) using (rKek) vault.AddSlot(rSlot, rKek);
        var (tSlot, tKek) = TpmSlot.Enroll(tpm, "この PC", "1234"u8, KdfParameters.TestOnly);
        using (tKek) vault.AddSlot(tSlot, tKek);
        var file = vault.Serialize();

        var slot = PasswordVault.ReadHeader(file).Slots.Single(s => s.Type == SlotType.Tpm);
        using var kek = TpmSlot.DeriveKek(slot, tpm, "1234"u8);
        using var opened = PasswordVault.Open(file, slot.Id, kek);
        Assert.Equal(2, opened.Slots.Count);
    }

    [Fact]
    public void WrongPin_IsCountedByTpm_ThenLocksOut()
    {
        var tpm = new FakeTpmSealer(maxFailures: 2);
        var (slot, kek) = TpmSlot.Enroll(tpm, "PC", "1234"u8, KdfParameters.TestOnly);
        kek.Dispose();
        Assert.Throws<TpmAuthFailedException>(() => TpmSlot.DeriveKek(slot, tpm, "0000"u8));
        Assert.Throws<TpmAuthFailedException>(() => TpmSlot.DeriveKek(slot, tpm, "1111"u8));
        Assert.Throws<TpmLockedOutException>(() => TpmSlot.DeriveKek(slot, tpm, "1234"u8));
        Assert.Equal(2, tpm.Failures);
    }

    [Fact]
    public void Unavailable_Throws()
    {
        var tpm = new FakeTpmSealer { Available = false };
        Assert.Throws<TpmUnavailableException>(() => TpmSlot.Enroll(tpm, "PC", "1234"u8, KdfParameters.TestOnly));
    }

    [Fact]
    public void TpmOnly_DoesNotSatisfyPolicy()
    {
        var tpm = new FakeTpmSealer();
        using var vault = PasswordVault.CreateNew();
        var (tSlot, tKek) = TpmSlot.Enroll(tpm, "PC", "1234"u8, KdfParameters.TestOnly);
        using (tKek) vault.AddSlot(tSlot, tKek);
        Assert.Throws<SlotPolicyException>(() => vault.Serialize());
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generates_RequestedLength_WithAllClasses()
    {
        var opts = new PasswordOptions { Length = 24 };
        for (int n = 0; n < 50; n++)
        {
            var pw = Encoding.UTF8.GetString(PasswordGenerator.Generate(opts));
            Assert.Equal(24, pw.Length);
            Assert.Contains(pw, char.IsLower);
            Assert.Contains(pw, char.IsUpper);
            Assert.Contains(pw, char.IsDigit);
            Assert.Contains(pw, c => opts.SymbolSet.Contains(c));
        }
    }

    [Fact]
    public void ExcludeAmbiguous_Works()
    {
        var opts = new PasswordOptions { Length = 200, Symbols = false, ExcludeAmbiguous = true };
        var pw = Encoding.UTF8.GetString(PasswordGenerator.Generate(opts));
        Assert.DoesNotContain(pw, c => "Il1O0o".Contains(c));
    }

    [Fact]
    public void Rejects_InvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(new PasswordOptions { Length = 2 }));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(new PasswordOptions { Lowercase = false, Uppercase = false, Digits = false, Symbols = false }));
        Assert.True(PasswordGenerator.EstimateEntropyBits(new PasswordOptions()) > 120);
    }
}

public class BrowserCsvImporterTests
{
    [Fact]
    public void Imports_ChromeFormat()
    {
        const string csv = "name,url,username,password,note\r\n" +
                           "example.com,https://example.com/login,alice,\"p,a\"\"ss\",\"line1\nline2\"\r\n" +
                           ",https://foo.test/,bob,secret,\r\n" +
                           "empty.test,https://empty.test/,carol,,\r\n";
        var result = BrowserCsvImporter.Import(new StringReader(csv));

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(1, result.SkippedRows);
        var a = result.Entries[0];
        Assert.Equal("example.com", a.Title);
        Assert.Equal("alice", a.UserName);
        Assert.Equal("p,a\"ss", Encoding.UTF8.GetString(a.Password));
        Assert.Equal("line1\nline2", a.Notes);
        Assert.Equal("foo.test", result.Entries[1].Title); // name が空なら URL のホスト名
    }

    [Fact]
    public void Imports_EdgeFormat_WithoutNote()
    {
        const string csv = "name,url,username,password\nsite,https://site.test,u,p\n";
        var result = BrowserCsvImporter.Import(new StringReader(csv));
        Assert.Single(result.Entries);
        Assert.Equal(string.Empty, result.Entries[0].Notes);
    }

    [Fact]
    public void Rejects_NonBrowserCsv()
    {
        Assert.Throws<FormatException>(() => BrowserCsvImporter.Import(new StringReader("a,b,c\n1,2,3\n")));
    }
}
