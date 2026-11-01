using PwManager.Core.Crypto;
using PwManager.Core.Generation;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Core.Tests;

/// <summary>構造検討書 R-04・R-05・R-15 のために Core に加えた機能。</summary>
public class RefactoringSupportTests
{
    private static PasswordVault NewVault(out Guid keyfileSlot)
    {
        var vault = PasswordVault.CreateNew();
        var (r, code, rk) = RecoverySlot.Enroll(kdf: KdfParameters.TestOnly);
        using (code) using (rk) vault.AddSlot(r, rk);
        var (k, kf, kk) = KeyfileSlot.Enroll(vault.VaultId, "USB", "pw"u8, KdfParameters.TestOnly);
        using (kf) using (kk) vault.AddSlot(k, kk);
        keyfileSlot = k.Id;
        return vault;
    }

    [Fact]
    public void PrepareSave_は確定するまでCounterを変えない_Metadata保存はCounterも更新日時も進めない()
    {
        using var vault = NewVault(out _);
        var p = vault.PrepareSave();
        Assert.Equal(0UL, vault.Counter);
        Assert.Equal(1UL, p.Counter);
        Assert.Equal(1UL, PasswordVault.ReadHeader(p.Data).Counter);
        vault.CommitSave(p);
        Assert.Equal(1UL, vault.Counter);

        var modified = vault.Modified;
        var meta = vault.PrepareSave(DateTimeOffset.UtcNow.AddDays(1), contentChanged: false);
        vault.CommitSave(meta);
        Assert.Equal(1UL, vault.Counter);
        Assert.Equal(modified, vault.Modified);

        var stale = vault.PrepareSave();
        vault.CommitSave(vault.PrepareSave());
        Assert.Throws<InvalidOperationException>(() => vault.CommitSave(stale)); // 間に別の保存
    }

    [Fact]
    public void Checkpoint_変更と追加と削除を元に戻し_元の項目オブジェクトを使い続ける()
    {
        using var vault = NewVault(out var keyfileSlot);
        var a = new VaultEntry { Title = "A" };
        a.SetPassword("pw-a"u8);
        var b = new VaultEntry { Title = "B" };
        vault.Entries.AddRange([a, b]);
        vault.Serialize();
        var slot = vault.Slots.Single(s => s.Id == keyfileSlot);
        slot.LastVerified = null;

        using var cp = vault.CreateCheckpoint();
        a.Title = "A2";
        a.SetPassword("pw-a2"u8);
        vault.Entries.Remove(b);
        var c = new VaultEntry { Title = "C" };
        c.SetPassword("pw-c"u8);
        vault.Entries.Add(c);
        slot.LastVerified = DateTimeOffset.UtcNow;
        var (t, tk) = (RecoverySlot.Enroll(kdf: KdfParameters.TestOnly) is var x ? (x.Slot, x.Kek) : default);
        using (x.Code) using (tk) vault.AddSlot(t, tk);

        vault.Restore(cp);
        Assert.Equal([a, b], vault.Entries);
        Assert.Equal("A", a.Title);
        Assert.Equal("pw-a"u8.ToArray(), a.Password);
        Assert.All(c.Password, v => Assert.Equal(0, v)); // 取り消した追加分はゼロ化
        Assert.Equal(2, vault.Slots.Count);
        Assert.Null(slot.LastVerified);
        Assert.Equal(1UL, vault.Counter);
    }

    [Fact]
    public void Open_は最終使用日を変えない()
    {
        using var vault = NewVault(out var keyfileSlot);
        var file = vault.Serialize();
        var header = PasswordVault.ReadHeader(file);
        Assert.All(header.Slots, s => Assert.Null(s.LastVerified));
    }

    [Fact]
    public void RecoveryCode_MatchesGroup_大小文字と紛らわしい文字と前後の空白を許容し_違う文字は拒否()
    {
        for (int n = 0; n < 20; n++)
        {
            using var code = RecoveryCode.Generate();
            for (int i = 0; i < RecoveryCode.GroupCount; i++)
            {
                var g = code.GetGroup(i);
                var sloppy = " " + g.ToLowerInvariant().Replace('0', 'o').Replace('1', 'l') + "\t";
                Assert.True(code.MatchesGroup(i, sloppy));
                Assert.False(code.MatchesGroup(i, g[..3]));
                Assert.False(code.MatchesGroup(i, g + "0"));
                var other = (g[0] == 'A' ? 'B' : 'A') + g[1..];
                Assert.False(code.MatchesGroup(i, other));
            }
        }
    }

    [Fact]
    public void パスワード強さの目安_除外後の実際の文字数で計算する()
    {
        // 数字のみ・紛らわしい文字を除く → 2〜9 の 8 種（以前は 10 - 6 = 4 種で計算していた）
        var digits = new PasswordOptions { Length = 10, Lowercase = false, Uppercase = false, Symbols = false, Digits = true, ExcludeAmbiguous = true };
        Assert.Equal(10 * Math.Log2(8), PasswordGenerator.EstimateEntropyBits(digits), 6);

        // 既定（小文字・大文字・数字・記号 24 種）= 86 種
        Assert.Equal(20 * Math.Log2(86), PasswordGenerator.EstimateEntropyBits(new PasswordOptions()), 6);

        // 記号セットが英数字と重なっても二重に数えない
        var overlap = new PasswordOptions { Length = 8, Uppercase = false, Digits = false, Symbols = true, SymbolSet = "abc!" };
        Assert.Equal(8 * Math.Log2(27), PasswordGenerator.EstimateEntropyBits(overlap), 6);

        // 生成できない設定は 0（画面で「強さ」を表示しない）
        Assert.Equal(0, PasswordGenerator.EstimateEntropyBits(new PasswordOptions { Length = 3 }));
        Assert.Equal(0, PasswordGenerator.EstimateEntropyBits(new PasswordOptions { Lowercase = false, Uppercase = false, Digits = false, Symbols = false }));
    }

    [Fact]
    public void 生成_記号セットが英数字と重なっても全文字種を含む()
    {
        var o = new PasswordOptions { Length = 6, Uppercase = false, Digits = false, Symbols = true, SymbolSet = "abc!" };
        for (int i = 0; i < 50; i++)
        {
            var pw = System.Text.Encoding.UTF8.GetString(PasswordGenerator.Generate(o));
            Assert.Contains('!', pw);
        }
    }

    [Fact]
    public void 秘密を表す型のToStringは中身を出さない()
    {
        using var buffer = SecretBuffer.FromSpan("secret-value"u8);
        using var code = RecoveryCode.Generate();
        var (_, keyfile, kek) = KeyfileSlot.Enroll(Guid.NewGuid(), "x", "pw"u8, KdfParameters.TestOnly);
        using (keyfile) using (kek)
        {
            Assert.Equal("***", buffer.ToString());
            Assert.Equal("***", code.ToString());
            Assert.Equal("***", keyfile.ToString());
            Assert.Equal("***", $"{kek}");
        }
    }
}
