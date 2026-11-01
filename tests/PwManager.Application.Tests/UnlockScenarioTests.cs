using System.Security.Cryptography;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Core.Storage;

namespace PwManager.Application.Tests;

/// <summary>シナリオ S-02: 日常の解錠と、失敗したときの案内（S05〜S07、S09、S12、S18）。</summary>
public sealed class UnlockScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly TestApp _pc;
    private readonly string _pin;
    private readonly string _password;
    private SetupSecrets _secrets = null!;

    public UnlockScenarioTests()
    {
        _pc = _world.NewPc();
        _pin = _world.Secret("Pk7x9q");
        _password = _world.Secret("correct horse battery");
    }

    public void Dispose() => _world.Dispose();

    private async Task SetupAndLockAsync()
    {
        _secrets = await _pc.CompleteSetupAsync(_pin, _password);
        await _pc.AddEntryAsync("ネット銀行", "taro", "bank-secret-01");
        _pc.Session.RequestLock(LockReason.Manual);
    }

    private Task<UnlockResult> Pin(string pin) => _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(pin)));

    [Fact]
    public async Task S05_PINで開いて項目を追加し_ロックして開き直すと残っている()
    {
        await SetupAndLockAsync();
        Assert.True((await Pin(_pin)).Ok);
        Assert.Single(_pc.Entries.Search("銀行"));
        await _pc.AddEntryAsync("メール", "taro@example.jp", "mail-secret-02", "https://mail.example.jp");
        _pc.Session.RequestLock(LockReason.Manual);
        Assert.Empty(_pc.Session.Entries);

        Assert.True((await Pin(_pin)).Ok);
        Assert.Equal(["ネット銀行", "メール"], _pc.Entries.Search(null).Select(e => e.Title));
    }

    [Fact]
    public async Task S06_PINを3回間違えると待ち時間_時間がたてば再試行でき_成功で数え直す()
    {
        await SetupAndLockAsync();
        for (int i = 1; i <= 2; i++)
        {
            var r = await Pin("wrong-" + i);
            Assert.Equal(ErrorCode.WrongPin, r.Error);
            Assert.Null(r.RetryAfter);
        }
        var third = await Pin("wrong-3");
        Assert.Equal(ErrorCode.WrongPin, third.Error);
        Assert.Equal(TimeSpan.FromSeconds(15), third.RetryAfter);
        Assert.Equal(3, _pc.Tpm.Failures); // TPM 側でも数えられている

        // 待ち時間中は TPM に問い合わせない（失敗回数を無駄に増やさない）
        var calls = _pc.Tpm.UnsealCalls;
        var waiting = await Pin(_pin);
        Assert.Equal(ErrorCode.RetryLater, waiting.Error);
        Assert.Equal(calls, _pc.Tpm.UnsealCalls);

        _world.Time.Advance(TimeSpan.FromSeconds(16));
        var fourth = await Pin("wrong-4");
        Assert.Equal(TimeSpan.FromSeconds(30), fourth.RetryAfter); // 倍増

        _world.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await Pin(_pin)).Ok);
        _pc.Session.RequestLock(LockReason.Manual);
        Assert.Equal(ErrorCode.WrongPin, (await Pin("wrong-5")).Error);
        Assert.Null(_pc.Unlock.RemainingDelay); // 成功で数え直した
    }

    [Fact]
    public async Task S06b_TPMがロックされても鍵ファイルと復旧コードで開ける()
    {
        var tpm = new FakeTpm(maxFailures: 2);
        var pc = _world.NewPc("locked", tpm);
        var secrets = await pc.CompleteSetupAsync(_pin, _password);
        pc.Session.RequestLock(LockReason.Manual);
        await pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B("bad-1")));
        await pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B("bad-2")));
        _world.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ErrorCode.TpmLockedOut, (await pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Error);

        Assert.True((await pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(secrets.KeyfilePath, null, TestWorld.B(_password)))).Ok);
    }

    [Fact]
    public async Task S07_同じ日に何度解錠しても_バックアップは押し出されない()
    {
        await SetupAndLockAsync();
        // 内容を変えた保存を 1 回（.bak1 = 変更前）
        Assert.True((await Pin(_pin)).Ok);
        await _pc.AddEntryAsync("変更", "u", "change-secret-03");
        var bak1 = File.ReadAllBytes(VaultStorage.BackupPath(_world.VaultPath, 1));
        _pc.Session.RequestLock(LockReason.Manual);

        var saves = _pc.Store.SaveCount;
        for (int i = 0; i < 6; i++)
        {
            Assert.True((await Pin(_pin)).Ok);
            _pc.Session.RequestLock(LockReason.Idle);
        }
        Assert.Equal(saves, _pc.Store.SaveCount); // 同じ日は保存しない
        Assert.Equal(bak1, File.ReadAllBytes(VaultStorage.BackupPath(_world.VaultPath, 1)));

        // 日付が変わったら最終使用日だけ記録（バックアップは回さない）
        _world.Time.Advance(TimeSpan.FromDays(1));
        Assert.True((await Pin(_pin)).Ok);
        Assert.Equal(saves + 1, _pc.Store.SaveCount);
        Assert.False(_pc.Store.RotateHistory[^1]);
        Assert.Equal(bak1, File.ReadAllBytes(VaultStorage.BackupPath(_world.VaultPath, 1)));
        var slot = _pc.Session.Slots.Single(s => s.Type == Core.Vault.SlotType.Tpm);
        Assert.Equal(_world.Time.GetUtcNow(), slot.LastVerified);
    }

    [Fact]
    public async Task S07b_最終使用日を記録できなくても_解錠はできる()
    {
        await SetupAndLockAsync();
        _pc.Store.ReadOnly = true; // 読み取り専用のメディアに置いた Vault
        var r = await Pin(_pin);
        Assert.True(r.Ok);
        Assert.True(_pc.Session.IsUnlocked);
        Assert.Null(_pc.Session.Slots.Single(s => s.Type == Core.Vault.SlotType.Tpm).LastVerified); // 記録は戻した
    }

    [Fact]
    public async Task S09_鍵ファイル_USBが挿さっていない()
    {
        await SetupAndLockAsync();
        File.Move(_secrets.KeyfilePath, _secrets.KeyfilePath + ".moved");
        var r = await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(_secrets.KeyfilePath, null, TestWorld.B(_password)));
        Assert.Equal(ErrorCode.KeyfileNotFound, r.Error);
        Assert.Equal(0, r.ConsecutiveFailures); // 鍵の間違いではないので数えない
    }

    [Fact]
    public async Task S09_鍵ファイル_パスワード違いは数え_正しければ開いて場所を覚える()
    {
        await SetupAndLockAsync();
        var other = _world.UsbKeyfile("copy.pwmk");
        File.Copy(_secrets.KeyfilePath, other);
        var wrong = await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(other, null, TestWorld.B("wrong password!!")));
        Assert.Equal(ErrorCode.WrongKey, wrong.Error);
        Assert.Equal(1, wrong.ConsecutiveFailures);

        Assert.True((await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(other, null, TestWorld.B(_password)))).Ok);
        Assert.Equal(other, _pc.Settings.Current.LastKeyfilePath);
    }

    [Fact]
    public async Task S09_印刷したQRのテキストで開ける_小文字や空白が混じっても可()
    {
        await SetupAndLockAsync();
        var typed = "  " + _secrets.KeyfileText.ToLowerInvariant().Insert(20, " ") + "\n";
        Assert.True((await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(null, typed, TestWorld.B(_password)))).Ok);
        Assert.Equal(_secrets.KeyfilePath, _pc.Settings.Current.LastKeyfilePath); // テキストの場合は場所を変えない
    }

    [Fact]
    public async Task S09_鍵ファイル_壊れたファイル_別のVault用_削除済みを見分ける()
    {
        await SetupAndLockAsync();

        var broken = _world.UsbKeyfile("broken.pwmk");
        File.WriteAllBytes(broken, RandomNumberGenerator.GetBytes(74));
        Assert.Equal(ErrorCode.KeyfileMalformed, (await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(broken, null, TestWorld.B(_password)))).Error);

        var csv = _world.UsbKeyfile("passwords.csv"); // 違うファイルを選んだ
        File.WriteAllText(csv, "name,url,username,password\n");
        Assert.Equal(ErrorCode.KeyfileMalformed, (await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(csv, null, TestWorld.B(_password)))).Error);

        // 別の Vault（家族の PC など）の鍵ファイル
        var otherWorldPc = _world.NewPc("other", vaultPath: Path.Combine(_world.Root, "other", "vault.pwmv"));
        var otherSecrets = await otherWorldPc.CompleteSetupAsync(_world.Secret("Zz9yy8"), _world.Secret("other vault password"), _world.UsbKeyfile("other.pwmk"));
        Assert.Equal(ErrorCode.KeyfileForOtherVault, (await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(otherSecrets.KeyfilePath, null, TestWorld.B(_password)))).Error);

        Assert.Equal(0, _pc.Backoff.ConsecutiveFailures);
    }

    [Fact]
    public async Task S12_復旧コード_書き方の揺れは許容し_書き間違いと別のコードを見分ける()
    {
        await SetupAndLockAsync();
        var sloppy = _secrets.RecoveryCode.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant().Replace('0', 'o');
        Assert.True((await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(sloppy))).Ok);
        _pc.Session.RequestLock(LockReason.Manual);

        // 1 文字の書き間違い → チェックサムで検出（鍵の間違いとしては数えない）
        var chars = _secrets.RecoveryCode.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        var typo = await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(new string(chars)));
        Assert.Equal(ErrorCode.RecoveryCodeMalformed, typo.Error);
        Assert.Equal(0, typo.ConsecutiveFailures);

        Assert.Equal(ErrorCode.RecoveryCodeMalformed, (await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest("1234-5678"))).Error);

        // 形式は正しいが別のコード（古い紙など）
        using var other = Core.Slots.RecoveryCode.Generate();
        var wrong = await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(other.ToDisplayString()));
        Assert.Equal(ErrorCode.WrongRecoveryCode, wrong.Error);
        Assert.Equal(1, wrong.ConsecutiveFailures);
    }

    [Fact]
    public async Task S18_ファイルが途中で切れている_改ざん_見つからないを見分ける()
    {
        await SetupAndLockAsync();
        var good = File.ReadAllBytes(_world.VaultPath);

        File.WriteAllBytes(_world.VaultPath, good[..(good.Length / 2)]); // 同期の中断などで途中まで
        var options = await _pc.Unlock.GetOptionsAsync();
        Assert.False(options.VaultReadable);
        Assert.Equal(ErrorCode.VaultFormat, options.HeaderError);
        Assert.Equal(ErrorCode.VaultFormat, (await Pin(_pin)).Error);

        var tampered = good.ToArray();
        tampered[^5] ^= 0x01; // 本体の 1 ビット
        File.WriteAllBytes(_world.VaultPath, tampered);
        Assert.Equal(ErrorCode.VaultTampered, (await Pin(_pin)).Error);
        Assert.False(_pc.Session.IsUnlocked);

        File.Delete(_world.VaultPath);
        Assert.Equal(ErrorCode.VaultNotFound, (await Pin(_pin)).Error);

        // バックアップから戻せば開ける
        var backups = _pc.Store.ListBackups();
        Assert.NotEmpty(backups);
        File.Copy(backups[0].Path, _world.VaultPath);
        Assert.True((await Pin(_pin)).Ok);
    }

    [Fact]
    public async Task 解錠ボタンの連打_2つ目は実行中として断る()
    {
        await SetupAndLockAsync();
        var first = Pin(_pin);
        var second = Pin(_pin);
        var results = await Task.WhenAll(first, second);
        Assert.Contains(results, r => r.Ok);
        Assert.Contains(results, r => r.Error is ErrorCode.Busy or ErrorCode.InvalidStep);
        Assert.Equal(1, _pc.Tpm.UnsealCalls); // TPM に問い合わせたのは 1 回だけ
    }

    [Fact]
    public async Task 解錠に使ったPINとパスワードのバイト列はゼロ化される()
    {
        await SetupAndLockAsync();
        var pin = TestWorld.B(_pin);
        await _pc.Unlock.UnlockAsync(new PinUnlockRequest(pin));
        Assert.All(pin, b => Assert.Equal(0, b));
        _pc.Session.RequestLock(LockReason.Manual);

        var pw = TestWorld.B("wrong password!!");
        await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(_secrets.KeyfilePath, null, pw));
        Assert.All(pw, b => Assert.Equal(0, b));
    }
}
