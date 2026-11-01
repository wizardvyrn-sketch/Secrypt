using System.Security.Cryptography;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;
using PwManager.Platform.Windows;

namespace PwManager.Platform.Windows.Tests;

/// <summary>
/// 実機の TPM を使う統合テスト。CI（TPM なし）では Category=Hardware を除外して実行する。
/// 間違った PIN の試行は TPM の失敗カウンタを増やすため、ここでは行わない（PoC で確認済み）。
/// 手元での実行: dotnet test tests\PwManager.Platform.Windows.Tests --filter Category=Hardware
/// </summary>
[Trait("Category", "Hardware")]
public class WindowsTpmSealerHardwareTests
{
    private readonly WindowsTpmSealer _tpm = new();

    [Fact]
    public void Tpm_IsAvailable()
    {
        Assert.True(_tpm.IsAvailable(out var reason), reason);
    }

    [Fact]
    public void Seal_Unseal_RoundTrip()
    {
        var auth = RandomNumberGenerator.GetBytes(32);
        var secret = RandomNumberGenerator.GetBytes(32);
        var blob = _tpm.Seal(auth, secret);
        using var outData = _tpm.Unseal(blob, auth);
        Assert.Equal(secret, outData.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void Vault_WithTpmSlot_OpensWithPin()
    {
        using var vault = PasswordVault.CreateNew();
        var (rSlot, code, rKek) = RecoverySlot.Enroll(kdf: new KdfParameters(8 * 1024, 1));
        using (code) using (rKek) vault.AddSlot(rSlot, rKek);
        var (tSlot, tKek) = TpmSlot.Enroll(_tpm, "この PC", "246810"u8, new KdfParameters(8 * 1024, 1));
        using (tKek) vault.AddSlot(tSlot, tKek);
        var file = vault.Serialize();

        var slot = PasswordVault.ReadHeader(file).Slots.Single(s => s.Type == SlotType.Tpm);
        using var kek = TpmSlot.DeriveKek(slot, _tpm, "246810"u8);
        using var opened = PasswordVault.Open(file, slot.Id, kek);
        Assert.Equal(vault.VaultId, opened.VaultId);
    }

    [Fact]
    public void IsSealedOnThisDevice_このTPMのblobはtrue_別のTPM相当のprivate部の改変はfalse()
    {
        var blob = _tpm.Seal(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        Assert.True(_tpm.IsSealedOnThisDevice(blob));

        // 別の PC の TPM（別の SRK）で作った blob は完全性検査で読み込めない。private 部の末尾を変えて同じ状況を作る
        var other = blob.ToArray();
        other[^1] ^= 0x5A;
        Assert.False(_tpm.IsSealedOnThisDevice(other));
    }
}

public class WindowsTpmSealerUnitTests
{
    [Fact]
    public void CorruptBlob_IsReportedAsUnavailable()
    {
        var tpm = new WindowsTpmSealer();
        Assert.Throws<TpmUnavailableException>(() => tpm.Unseal(new byte[] { 1, 2, 3 }, new byte[32]));
        Assert.Throws<TpmUnavailableException>(() => WindowsTpmSealer.DecodeBlob("PWT1\0\u0010"u8));
    }

    [Fact]
    public void IsSealedOnThisDevice_形式の違うblobはTPMに問い合わせずfalse()
        => Assert.False(new WindowsTpmSealer().IsSealedOnThisDevice(new byte[] { 1, 2, 3 }));
}
