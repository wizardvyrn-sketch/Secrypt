using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Storage;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

/// <summary>FeatureFlags（静的状態）を変更するため並列実行しない。</summary>
[CollectionDefinition("FeatureFlags", DisableParallelization = true)]
public class FeatureFlagsCollection;

[Collection("FeatureFlags")]
public class Fido2SlotTests
{
    /// <summary>FIDO2 キーの代わりに HMAC で hmac-secret を模擬する。</summary>
    private sealed class FakeFido2 : IFido2SecretProvider
    {
        private readonly byte[] _deviceSecret = CryptoPrimitives.RandomBytes(32);
        public byte[] MakeCredential(string rpId, string userLabel) => CryptoPrimitives.RandomBytes(48);
        public SecretBuffer GetHmacSecret(string rpId, ReadOnlySpan<byte> credentialId, ReadOnlySpan<byte> salt)
            => SecretBuffer.FromSpan(CryptoPrimitives.HmacSha256(_deviceSecret, salt));
    }

    [Fact]
    public void Disabled_ByDefault()
    {
        FeatureFlags.Fido2Enabled = false;
        Assert.Throws<NotSupportedException>(() => Fido2Slot.Enroll(new FakeFido2(), "key", "pw"u8, KdfParameters.TestOnly));

        using var vault = PasswordVault.CreateNew();
        var slot = new KeySlot { Type = SlotType.Fido2, Kdf = KdfParameters.TestOnly };
        using var kek = SecretBuffer.Random(32);
        Assert.Throws<NotSupportedException>(() => vault.AddSlot(slot, kek));
    }

    [Fact]
    public void WhenEnabled_FlowWorksWithMock()
    {
        FeatureFlags.Fido2Enabled = true;
        try
        {
            var device = new FakeFido2();
            using var vault = PasswordVault.CreateNew();
            var (rSlot, code, rKek) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
            using (code) using (rKek) vault.AddSlot(rSlot, rKek);

            var (fSlot, fKek) = Fido2Slot.Enroll(device, "YubiKey #1", "pw"u8, KdfParameters.TestOnly);
            using (fKek) vault.AddSlot(fSlot, fKek);
            var file = vault.Serialize();

            var slot = PasswordVault.ReadHeader(file).Slots.Single(s => s.Type == SlotType.Fido2);
            using var kek = Fido2Slot.DeriveKek(slot, device, "pw"u8);
            using var reopened = PasswordVault.Open(file, slot.Id, kek);
            Assert.Equal(2, reopened.Slots.Count);

            // 別のキー（デバイス秘密が違う）では開けない
            using var otherKek = Fido2Slot.DeriveKek(slot, new FakeFido2(), "pw"u8);
            Assert.Throws<VaultUnlockException>(() => PasswordVault.Open(file, slot.Id, otherKek));
        }
        finally
        {
            FeatureFlags.Fido2Enabled = false;
        }
    }

    [Fact]
    public void Params_RoundTrip()
    {
        var cred = CryptoPrimitives.RandomBytes(40);
        var (rp, c) = Fido2Slot.DecodeParams(Fido2Slot.EncodeParams("pwmanager.local", cred));
        Assert.Equal("pwmanager.local", rp);
        Assert.Equal(cred, c);
    }
}

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pwm-tests-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SaveAtomic_KeepsGenerations()
    {
        var path = Path.Combine(_dir, "vault.pwmv");
        for (int i = 1; i <= 4; i++)
            VaultStorage.SaveAtomic(path, new[] { (byte)i }, generations: 2);

        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(path));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(VaultStorage.BackupPath(path, 1)));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(VaultStorage.BackupPath(path, 2)));
        Assert.False(File.Exists(VaultStorage.BackupPath(path, 3)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void AssessKeyfileLocation_DetectsRisk()
    {
        var vault = Path.Combine(_dir, "vault.pwmv");
        Assert.Equal(LocationRisk.SameDirectory, VaultStorage.AssessKeyfileLocation(vault, Path.Combine(_dir, "vault.key"), []));

        var sync = Path.Combine(_dir, "OneDrive");
        Assert.Equal(LocationRisk.SameSyncFolder, VaultStorage.AssessKeyfileLocation(
            Path.Combine(sync, "a", "vault.pwmv"), Path.Combine(sync, "b", "vault.key"), [sync]));

        Assert.Equal(LocationRisk.SameDrive, VaultStorage.AssessKeyfileLocation(
            Path.Combine(_dir, "a", "vault.pwmv"), Path.Combine(_dir, "b", "vault.key"), []));
    }
}
