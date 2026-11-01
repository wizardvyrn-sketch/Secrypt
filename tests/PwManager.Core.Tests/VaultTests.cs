using System.Text;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

public class VaultTests
{
    private static readonly byte[] Password = "my-pass phrase"u8.ToArray();

    /// <summary>復旧コード＋キーファイルの 2 スロットを持つ Vault を作り、1 件登録してシリアライズする。</summary>
    private static (byte[] File, string RecoveryText, byte[] KeyfileBytes, Guid KeyfileSlotId, Guid RecoverySlotId) CreateSample()
    {
        using var vault = PasswordVault.CreateNew();

        var (rSlot, code, rKek) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
        string recoveryText;
        using (code) using (rKek)
        {
            vault.AddSlot(rSlot, rKek);
            recoveryText = code.ToDisplayString();
        }

        var (kSlot, keyfile, kKek) = KeyfileSlot.Enroll(vault.VaultId, "USB", Password, KdfParameters.TestOnly);
        byte[] keyfileBytes;
        using (keyfile) using (kKek)
        {
            vault.AddSlot(kSlot, kKek);
            keyfileBytes = keyfile.ToBytes();
        }

        var e = new VaultEntry { Title = "Example", UserName = "alice", Url = "https://example.com", Notes = "メモ", Tags = ["web"] };
        e.SetPassword("p@ssw0rd-日本語"u8);
        vault.Entries.Add(e);

        return (vault.Serialize(), recoveryText, keyfileBytes, kSlot.Id, rSlot.Id);
    }

    [Fact]
    public void Open_WithKeyfileAndPassword()
    {
        var s = CreateSample();
        var header = PasswordVault.ReadHeader(s.File);
        Assert.Equal(2, header.Slots.Count);

        var slot = header.Slots.Single(x => x.Id == s.KeyfileSlotId);
        using var keyfile = Keyfile.FromBytes(s.KeyfileBytes);
        using var kek = KeyfileSlot.DeriveKek(slot, keyfile, Password);
        using var vault = PasswordVault.Open(s.File, slot.Id, kek);

        var entry = Assert.Single(vault.Entries);
        Assert.Equal("Example", entry.Title);
        Assert.Equal("alice", entry.UserName);
        Assert.Equal("p@ssw0rd-日本語", Encoding.UTF8.GetString(entry.Password));
        Assert.Equal("メモ", entry.Notes);
        Assert.Equal(new[] { "web" }, entry.Tags);
        Assert.Equal(1UL, vault.Counter);
    }

    [Fact]
    public void Open_WithRecoveryCode()
    {
        var s = CreateSample();
        var header = PasswordVault.ReadHeader(s.File);
        var slot = header.Slots.Single(x => x.Id == s.RecoverySlotId);
        using var code = RecoveryCode.Parse(s.RecoveryText);
        using var kek = RecoverySlot.DeriveKek(slot, code);
        using var vault = PasswordVault.Open(s.File, slot.Id, kek);
        Assert.Single(vault.Entries);
    }

    [Fact]
    public void Open_FailsWithWrongPassword()
    {
        var s = CreateSample();
        var slot = PasswordVault.ReadHeader(s.File).Slots.Single(x => x.Id == s.KeyfileSlotId);
        using var keyfile = Keyfile.FromBytes(s.KeyfileBytes);
        using var kek = KeyfileSlot.DeriveKek(slot, keyfile, "wrong"u8);
        Assert.Throws<VaultUnlockException>(() => PasswordVault.Open(s.File, slot.Id, kek));
    }

    [Fact]
    public void FileAlone_ContainsNoPlaintext()
    {
        var s = CreateSample();
        var text = Encoding.UTF8.GetString(s.File);
        Assert.DoesNotContain("p@ssw0rd", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void DetectsHeaderTampering()
    {
        var s = CreateSample();
        var c = VaultFile.ReadContainer(s.File);
        var header = VaultFile.ReadHeader(c.HeaderBytes);

        // スロットの label を書き換えた偽ヘッダ（ヘッダ MAC で検出される）
        header.Slots[0].Label = "evil";
        var forgedHeader = VaultFile.WriteHeader(header.VaultId, header.Created, header.Modified, header.Counter, header.Slots);
        var forged = VaultFile.WriteContainer(forgedHeader, c.HeaderMac, c.BodyNonce, c.Body);

        var slot = PasswordVault.ReadHeader(forged).Slots.Single(x => x.Id == s.KeyfileSlotId);
        using var keyfile = Keyfile.FromBytes(s.KeyfileBytes);
        using var kek = KeyfileSlot.DeriveKek(slot, keyfile, Password);
        Assert.Throws<VaultTamperedException>(() => PasswordVault.Open(forged, slot.Id, kek));
    }

    [Fact]
    public void DetectsBodyTampering()
    {
        var s = CreateSample();
        var file = (byte[])s.File.Clone();
        file[^1] ^= 0x01; // 本体（末尾）の 1 ビット反転
        var slot = PasswordVault.ReadHeader(file).Slots.Single(x => x.Id == s.KeyfileSlotId);
        using var keyfile = Keyfile.FromBytes(s.KeyfileBytes);
        using var kek = KeyfileSlot.DeriveKek(slot, keyfile, Password);
        Assert.Throws<VaultTamperedException>(() => PasswordVault.Open(file, slot.Id, kek));
    }

    [Fact]
    public void RejectsNonVaultFile()
    {
        Assert.Throws<VaultFormatException>(() => PasswordVault.ReadHeader("hello world"u8));
        Assert.Throws<VaultFormatException>(() => PasswordVault.ReadHeader(CreateSample().File.AsSpan(0, 40)));
    }

    [Fact]
    public void SlotPolicy_RequiresRecoveryAndAnother()
    {
        using var vault = PasswordVault.CreateNew();
        Assert.Throws<SlotPolicyException>(() => vault.Serialize());

        var (rSlot, code, rKek) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
        using (code) using (rKek) vault.AddSlot(rSlot, rKek);
        Assert.Throws<SlotPolicyException>(() => vault.Serialize()); // 復旧のみ

        var (kSlot, keyfile, kKek) = KeyfileSlot.Enroll(vault.VaultId, "USB", Password, KdfParameters.TestOnly);
        using (keyfile) using (kKek) vault.AddSlot(kSlot, kKek);
        vault.Serialize(); // OK

        Assert.Throws<SlotPolicyException>(() => vault.RemoveSlot(kSlot.Id)); // 削除すると復旧のみになる
        Assert.Throws<SlotPolicyException>(() => vault.RemoveSlot(rSlot.Id)); // 復旧は外せない
    }

    [Fact]
    public void RemovedSlot_CanNoLongerOpen()
    {
        var s = CreateSample();
        var header = PasswordVault.ReadHeader(s.File);
        var kSlot = header.Slots.Single(x => x.Id == s.KeyfileSlotId);

        byte[] updated;
        byte[] newKeyfileBytes;
        using (var keyfile = Keyfile.FromBytes(s.KeyfileBytes))
        using (var kek = KeyfileSlot.DeriveKek(kSlot, keyfile, Password))
        using (var vault = PasswordVault.Open(s.File, kSlot.Id, kek))
        {
            // 新しいキーファイルを登録してから古いものを失効させる（USB 紛失時の手順）
            var (newSlot, newFile, newKek) = KeyfileSlot.Enroll(vault.VaultId, "USB-new", Password, KdfParameters.TestOnly);
            using (newFile) using (newKek)
            {
                vault.AddSlot(newSlot, newKek);
                newKeyfileBytes = newFile.ToBytes();
            }
            vault.RemoveSlot(kSlot.Id);
            updated = vault.Serialize();
            Assert.Equal(2UL, vault.Counter);
        }

        Assert.DoesNotContain(PasswordVault.ReadHeader(updated).Slots, x => x.Id == s.KeyfileSlotId);
        using var oldKeyfile = Keyfile.FromBytes(s.KeyfileBytes);
        using var oldKek = KeyfileSlot.DeriveKek(kSlot, oldKeyfile, Password);
        Assert.Throws<VaultUnlockException>(() => PasswordVault.Open(updated, kSlot.Id, oldKek));

        using var nf = Keyfile.FromBytes(newKeyfileBytes);
        var ns = PasswordVault.ReadHeader(updated).Slots.Single(x => x.Id == nf.SlotId);
        using var nk = KeyfileSlot.DeriveKek(ns, nf, Password);
        using var reopened = PasswordVault.Open(updated, ns.Id, nk);
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public void Lock_WipesKeyAndPasswords()
    {
        var vault = PasswordVault.CreateNew();
        var e = new VaultEntry();
        e.SetPassword("abc"u8);
        vault.Entries.Add(e);
        vault.Dispose();
        Assert.True(vault.IsLocked);
        Assert.All(e.Password, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => vault.Serialize());
    }
}
