using System.Text;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

/// <summary>本体形式 v2（タグ付きフィールド）と v1 からの移行（R-10）。</summary>
public class EntryFormatV2Tests
{
    private static (PasswordVault Vault, Guid SlotId, byte[] Keyfile) NewVault()
    {
        var vault = PasswordVault.CreateNew();
        var (r, code, rk) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
        using (code) using (rk) vault.AddSlot(r, rk);
        var (k, kf, kk) = KeyfileSlot.Enroll(vault.VaultId, "USB", "pw"u8, KdfParameters.TestOnly);
        byte[] keyfile;
        using (kf) using (kk)
        {
            vault.AddSlot(k, kk);
            keyfile = kf.ToBytes();
        }
        return (vault, k.Id, keyfile);
    }

    private static PasswordVault Reopen(byte[] file, Guid slotId, byte[] keyfileBytes)
    {
        var slot = PasswordVault.ReadHeader(file).Slots.Single(s => s.Id == slotId);
        using var kf = Keyfile.FromBytes(keyfileBytes);
        using var kek = KeyfileSlot.DeriveKek(slot, kf, "pw"u8);
        return PasswordVault.Open(file, slotId, kek);
    }

    private static VaultEntry Sample()
    {
        var e = new VaultEntry { Title = "日本語のタイトル", UserName = "taro", Url = "https://example.jp", Notes = "1 行目\n2 行目", Tags = ["bank", "家族"] };
        e.SetPassword("p@ss-日本語"u8);
        e.Created = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        e.Modified = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_000);
        return e;
    }

    [Fact]
    public void V1のファイルを開けて_次の保存でV2になり_内容は変わらない()
    {
        var (vault, slotId, keyfile) = NewVault();
        var e = Sample();
        vault.Entries.Add(e);
        var v1 = vault.PrepareSave(null, true, EntrySerializer.WriteV1).Data;
        vault.Dispose();

        using var opened = Reopen(v1, slotId, keyfile);
        var read = Assert.Single(opened.Entries);
        Assert.Equal(e.Id, read.Id);
        Assert.Equal("日本語のタイトル", read.Title);
        Assert.Equal(["bank", "家族"], read.Tags);
        Assert.Equal("p@ss-日本語"u8.ToArray(), read.Password);
        Assert.Equal(e.Created, read.Created);

        var v2 = opened.Serialize();
        using var again = Reopen(v2, slotId, keyfile);
        var r2 = Assert.Single(again.Entries);
        Assert.Equal(read.Title, r2.Title);
        Assert.Equal(read.Notes, r2.Notes);
        Assert.Equal(read.Modified, r2.Modified);
        using var plain = EntrySerializer.Write(again.Entries);
        Assert.Equal(EntrySerializer.CurrentVersion, BitConverter.ToInt32(plain.ReadOnlySpan[..4]));
    }

    [Fact]
    public void 知らないタグは保持して書き戻す_新しい版の項目を古い版で保存しても消えない()
    {
        var e = Sample();
        e.UnknownFields.Add((900, Encoding.UTF8.GetBytes("totp-seed-from-future")));
        e.UnknownFields.Add((901, [1, 2, 3]));
        using var plain = EntrySerializer.Write([e]);
        var read = Assert.Single(EntrySerializer.Read(plain.ReadOnlySpan));
        Assert.Equal(2, read.UnknownFields.Count);
        Assert.Equal((ushort)900, read.UnknownFields[0].Tag);
        Assert.Equal("totp-seed-from-future"u8.ToArray(), read.UnknownFields[0].Value);

        // 編集しても（CopyFrom）残る。ロック（Wipe）ではゼロ化する
        var copy = new VaultEntry { Id = read.Id };
        copy.CopyFrom(read);
        Assert.Equal(read.UnknownFields[1].Value, copy.UnknownFields[1].Value);
        read.Wipe();
        Assert.All(read.UnknownFields[0].Value, b => Assert.Equal(0, b));
    }

    [Fact]
    public void 壊れた本体は形式エラーとして扱う()
    {
        using var plain = EntrySerializer.Write([Sample()]);
        var bytes = plain.ReadOnlySpan.ToArray();

        Assert.Throws<VaultFormatException>(() => EntrySerializer.Read(bytes.AsSpan(0, bytes.Length - 3)));
        Assert.Throws<VaultFormatException>(() => EntrySerializer.Read([.. bytes, 0]));

        var future = bytes.ToArray();
        BitConverter.TryWriteBytes(future.AsSpan(0, 4), 3);
        var ex = Assert.Throws<VaultFormatException>(() => EntrySerializer.Read(future));
        Assert.True(ex.IsUnsupportedVersion);

        // ランダムなバイト列でも VaultFormatException 以外の例外にならない（簡易ファジング）
        var rng = new Random(20261025);
        for (int i = 0; i < 2000; i++)
        {
            var mutated = bytes.ToArray();
            for (int k = 0; k < 1 + rng.Next(4); k++) mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
            try { EntrySerializer.Read(mutated); }
            catch (VaultFormatException) { }
        }
    }
}
