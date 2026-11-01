using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

public class SlotTests
{
    [Fact]
    public void RecoveryCode_DisplayAndParse_RoundTrip()
    {
        using var code = RecoveryCode.Generate();
        var text = code.ToDisplayString();
        Assert.Equal(RecoveryCode.GroupCount, text.Split('-').Length);
        Assert.Equal(9, RecoveryCode.GroupCount);

        using var parsed = RecoveryCode.Parse(text);
        Assert.Equal(code.Entropy.ReadOnlySpan.ToArray(), parsed.Entropy.ReadOnlySpan.ToArray());

        // 区切りなし・小文字・空白混じりでも読める
        using var loose = RecoveryCode.Parse("  " + text.Replace("-", " ").ToLowerInvariant() + "\n");
        Assert.Equal(code.Entropy.ReadOnlySpan.ToArray(), loose.Entropy.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void RecoveryCode_DetectsTypo()
    {
        using var code = RecoveryCode.Generate();
        var chars = code.ToDisplayString().ToCharArray();
        // 先頭文字を別の文字に変える（チェックサムで検出されるはず）
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        Assert.Throws<FormatException>(() => RecoveryCode.Parse(new string(chars)));
        Assert.Throws<FormatException>(() => RecoveryCode.Parse("ABCD-EFGH"));
    }

    [Fact]
    public void Keyfile_Bytes_And_Text_RoundTrip()
    {
        var vaultId = Guid.NewGuid();
        var (slot, keyfile, kek) = KeyfileSlot.Enroll(vaultId, "USB", "pw"u8, KdfParameters.TestOnly);
        using (kek)
        using (keyfile)
        {
            var bytes = keyfile.ToBytes();
            Assert.Equal(Keyfile.FileSize, bytes.Length);
            using var fromBytes = Keyfile.FromBytes(bytes);
            Assert.Equal(vaultId, fromBytes.VaultId);
            Assert.Equal(slot.Id, fromBytes.SlotId);

            var text = keyfile.ToText();
            Assert.StartsWith("PWMK1:", text);
            using var fromText = Keyfile.FromText(text);
            Assert.Equal(keyfile.Secret.ReadOnlySpan.ToArray(), fromText.Secret.ReadOnlySpan.ToArray());

            bytes[50] ^= 0xFF;
            Assert.Throws<FormatException>(() => Keyfile.FromBytes(bytes));
        }
    }

    [Fact]
    public void Keyfile_KekDependsOnPasswordAndFile()
    {
        var (slot, keyfile, kek) = KeyfileSlot.Enroll(Guid.NewGuid(), "USB", "pw"u8, KdfParameters.TestOnly);
        using (kek)
        using (keyfile)
        {
            using var same = KeyfileSlot.DeriveKek(slot, keyfile, "pw"u8);
            using var wrongPw = KeyfileSlot.DeriveKek(slot, keyfile, "PW"u8);
            Assert.Equal(kek.ReadOnlySpan.ToArray(), same.ReadOnlySpan.ToArray());
            Assert.NotEqual(kek.ReadOnlySpan.ToArray(), wrongPw.ReadOnlySpan.ToArray());

            var (otherSlot, otherFile, otherKek) = KeyfileSlot.Enroll(Guid.NewGuid(), "USB2", "pw"u8, KdfParameters.TestOnly);
            using (otherKek)
            using (otherFile)
            {
                Assert.Throws<VaultUnlockException>(() => KeyfileSlot.DeriveKek(slot, otherFile, "pw"u8));
            }
        }
    }
}
