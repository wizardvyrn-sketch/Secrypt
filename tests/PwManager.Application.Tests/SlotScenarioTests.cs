using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Core.Vault;

namespace PwManager.Application.Tests;

/// <summary>シナリオ S-04: 解錠方法の管理（USB 紛失・PC 買い替え・TPM クリア・復旧コード作り直し: S08、S10〜S12、S17、S19）。</summary>
public sealed class SlotScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly TestApp _pc;
    private readonly string _pin;
    private readonly string _password;

    public SlotScenarioTests()
    {
        _pc = _world.NewPc();
        _pin = _world.Secret("Pk7x9q");
        _password = _world.Secret("correct horse battery");
    }

    public void Dispose() => _world.Dispose();

    private Task<UnlockResult> UnlockKeyfile(TestApp pc, string path, string password)
        => pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(path, null, TestWorld.B(password)));

    [Fact]
    public async Task S08_USBを紛失_復旧コードで開き_古い鍵ファイルを削除して新しく作る_古い鍵ファイルでは開けない()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        var lostCopy = Path.Combine(_world.Root, "attacker-copy.pwmk");
        File.Copy(secrets.KeyfilePath, lostCopy); // 紛失した USB（誰かがコピーしたかもしれない）
        File.Delete(secrets.KeyfilePath);
        _pc.Session.RequestLock(LockReason.Manual);

        Assert.True((await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(secrets.RecoveryCode))).Ok);
        var old = _pc.Session.Slots.Single(s => s.Type == SlotType.Keyfile);
        var newPassword = _world.Secret("brand new usb password");
        var added = await _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("new.pwmk"), TestWorld.B(newPassword), TestWorld.B(newPassword));
        Assert.True(added.Ok);
        _world.Secret(added.Value!);
        Assert.True((await _pc.Slots.RemoveAsync(old.Id)).Ok);
        _pc.Session.RequestLock(LockReason.Manual);

        Assert.Equal(ErrorCode.KeyfileRevoked, (await UnlockKeyfile(_pc, lostCopy, _password)).Error);
        Assert.True((await UnlockKeyfile(_pc, _world.UsbKeyfile("new.pwmk"), newPassword)).Ok);
    }

    [Fact]
    public async Task 解錠方法を削除すると_古い方法で開ける自動バックアップも残らない()
    {
        // VMK は作り直さないため、削除前の保管ファイル（.bak1〜）が残ると、紛失した鍵ファイルで VMK を取り出せてしまう（docs/02 T4b）
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        await _pc.AddEntryAsync("example", "taro", _world.Secret("entry-password-1"));
        var old = _pc.Session.Slots.Single(s => s.Type == SlotType.Keyfile);
        var newPassword = _world.Secret("brand new usb password");
        var added = await _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("new.pwmk"), TestWorld.B(newPassword), TestWorld.B(newPassword));
        _world.Secret(added.Value!);
        Assert.NotEmpty(_pc.Store.ListBackups());

        var r = await _pc.Slots.RemoveAsync(old.Id);

        Assert.True(r.Ok);
        Assert.Empty(r.Value!.Remaining);
        Assert.Empty(_pc.Store.ListBackups());
        Assert.False(File.Exists(_world.VaultPath + ".bak1"));
        Assert.True(File.Exists(secrets.KeyfilePath)); // 鍵ファイル自体は消さない（利用者の持ち物）
    }

    [Fact]
    public async Task 復旧コードを作り直すと_古いコードで開ける自動バックアップも残らない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        await _pc.AddEntryAsync("example", "taro", _world.Secret("entry-password-1"));
        Assert.NotEmpty(_pc.Store.ListBackups());
        using var pending = (await _pc.Slots.PrepareRecoveryCodeAsync()).Value!;
        _world.Secret(pending.DisplayCode);

        var r = await _pc.Slots.CommitRecoveryCodeAsync(pending, pending.DisplayCode.Split('-')[pending.CheckGroup]);

        Assert.True(r.Ok);
        Assert.Empty(_pc.Store.ListBackups());
    }

    [Fact]
    public async Task S10_PC買い替え_新しいPCは鍵ファイルで開いてTPMを登録_古いPCのスロットは残り_後で削除できる()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        _pc.Session.RequestLock(LockReason.Exit);

        // 新しい PC に Vault ファイルをコピーした（同じパスを使う）
        var newPc = _world.NewPc("new-pc", new FakeTpm());
        var options = await newPc.Unlock.GetOptionsAsync();
        Assert.False(options.PinAvailable);
        Assert.True(options.TpmSlotsForOtherDevicesOnly);
        Assert.Equal(UnlockMethod.Keyfile, options.DefaultMethod);

        // それでも PIN を試した → 「この PC は未登録」と案内（古い PC の TPM の失敗回数には影響しない）
        Assert.Equal(ErrorCode.TpmNotRegisteredOnThisDevice, (await newPc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Error);
        Assert.Equal(0, newPc.Tpm.Failures);
        Assert.Equal(0, newPc.Backoff.ConsecutiveFailures);

        Assert.True((await UnlockKeyfile(newPc, secrets.KeyfilePath, _password)).Ok);
        var newPin = _world.Secret("Nw3pc8");
        Assert.True((await newPc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(newPin), TestWorld.B(newPin))).Ok);
        var slots = await newPc.Slots.GetSlotsAsync();
        var tpmSlots = slots.Where(s => s.Type == SlotType.Tpm).ToList();
        Assert.Equal(2, tpmSlots.Count);                    // 古い PC のスロットは消さない
        Assert.Single(tpmSlots, s => s.IsThisDevice == true);
        newPc.Session.RequestLock(LockReason.Manual);

        Assert.True((await newPc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(newPin)))).Ok);

        // 古い PC もまだ PIN で開ける（2 台で使う場合）
        newPc.Session.RequestLock(LockReason.Manual);
        var oldPc = _world.NewPc("old-pc", _pc.Tpm);
        Assert.True((await oldPc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Ok);
        oldPc.Session.RequestLock(LockReason.Manual);

        // 古い PC を処分 → 新しい PC から古い PC のスロットを削除
        Assert.True((await newPc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(newPin)))).Ok);
        var oldSlot = (await newPc.Slots.GetSlotsAsync()).Single(s => s.Type == SlotType.Tpm && s.IsThisDevice == false);
        Assert.True((await newPc.Slots.RemoveAsync(oldSlot.Id)).Ok);
        newPc.Session.RequestLock(LockReason.Manual);
        Assert.Equal(ErrorCode.TpmNotRegisteredOnThisDevice, (await oldPc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Error);
    }

    [Fact]
    public async Task PINを変えると_古いPINで開ける自動バックアップも残らない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        await _pc.AddEntryAsync("example", "taro", _world.Secret("entry-password-1"));
        Assert.NotEmpty(_pc.Store.ListBackups());
        var changed = _world.Secret("Ch4ng3d");

        var r = await _pc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(changed), TestWorld.B(changed));

        Assert.True(r.Ok);
        Assert.Empty(r.Value!.Remaining);
        Assert.Empty(_pc.Store.ListBackups());
    }

    [Fact]
    public async Task 新しいPCへ初めてTPMを登録しても_何も無効にしないのでバックアップは残る()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        await _pc.AddEntryAsync("example", "taro", _world.Secret("entry-password-1"));
        _pc.Session.RequestLock(LockReason.Manual);
        var newPc = _world.NewPc("new-pc");
        Assert.True((await UnlockKeyfile(newPc, secrets.KeyfilePath, _password)).Ok);
        var newPin = _world.Secret("Nw3pc8");

        var r = await newPc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(newPin), TestWorld.B(newPin));

        Assert.True(r.Ok);
        Assert.Equal(0, r.Value!.Deleted);
        Assert.NotEmpty(newPc.Store.ListBackups());
    }

    [Fact]
    public async Task S10b_同じPCでTPMを登録し直すとPINの変更になる_古いPINでは開けない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var changed = _world.Secret("Ch4ng3d");
        Assert.True((await _pc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(changed), TestWorld.B(changed))).Ok);
        Assert.Single(_pc.Session.Slots, s => s.Type == SlotType.Tpm);
        _pc.Session.RequestLock(LockReason.Manual);

        Assert.Equal(ErrorCode.WrongPin, (await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Error);
        Assert.True((await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(changed)))).Ok);
    }

    [Fact]
    public async Task S11_TPMクリア後_PINでは開けないと案内し_再登録すれば開ける()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        _pc.Session.RequestLock(LockReason.Manual);
        _pc.Tpm.Clear(); // BIOS 更新などで TPM がクリアされた

        var options = await _pc.Unlock.GetOptionsAsync();
        Assert.True(options.TpmSlotsForOtherDevicesOnly);
        Assert.Equal(ErrorCode.TpmNotRegisteredOnThisDevice, (await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Error);

        Assert.True((await UnlockKeyfile(_pc, secrets.KeyfilePath, _password)).Ok);
        Assert.True((await _pc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(_pin), TestWorld.B(_pin))).Ok);
        var tpm = (await _pc.Slots.GetSlotsAsync()).Where(s => s.Type == SlotType.Tpm).ToList();
        Assert.Equal(2, tpm.Count);
        Assert.Single(tpm, s => s.IsThisDevice == false); // クリア前のものは「この PC ではない」と表示して削除を促す
        _pc.Session.RequestLock(LockReason.Manual);
        Assert.True((await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Ok);
    }

    [Fact]
    public async Task S12_復旧コードを作り直す_確認が違えば古いコードのまま_確定すると古いコードは無効()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        using var pending = (await _pc.Slots.PrepareRecoveryCodeAsync()).Value!;
        _world.Secret(pending.DisplayCode);
        var groups = pending.DisplayCode.Split('-');
        var wrongGroup = groups[(pending.CheckGroup + 1) % groups.Length];

        Assert.Equal(ErrorCode.RecoveryVerifyMismatch, (await _pc.Slots.CommitRecoveryCodeAsync(pending, wrongGroup)).Error);
        Assert.Single(_pc.Session.Slots, s => s.Type == SlotType.Recovery);

        Assert.True((await _pc.Slots.CommitRecoveryCodeAsync(pending, groups[pending.CheckGroup].ToLowerInvariant())).Ok);
        Assert.Single(_pc.Session.Slots, s => s.Type == SlotType.Recovery);
        _pc.Session.RequestLock(LockReason.Manual);

        Assert.Equal(ErrorCode.WrongRecoveryCode, (await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(secrets.RecoveryCode))).Error);
        Assert.True((await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(string.Join("-", groups)))).Ok);
    }

    [Fact]
    public async Task S17_復旧コードと_最後の1つの解錠方法は削除できない()
    {
        await _pc.CompleteSetupAsync("unused", _password, useTpm: false);
        var file = File.ReadAllBytes(_world.VaultPath);
        var recovery = _pc.Session.Slots.Single(s => s.Type == SlotType.Recovery);
        var keyfile = _pc.Session.Slots.Single(s => s.Type == SlotType.Keyfile);

        Assert.Equal(ErrorCode.SlotPolicyViolation, (await _pc.Slots.RemoveAsync(recovery.Id)).Error);
        Assert.Equal(ErrorCode.SlotPolicyViolation, (await _pc.Slots.RemoveAsync(keyfile.Id)).Error);
        Assert.Equal(2, _pc.Session.Slots.Count);
        Assert.Equal(file, File.ReadAllBytes(_world.VaultPath));
        Assert.Equal(ErrorCode.NotFound, (await _pc.Slots.RemoveAsync(Guid.NewGuid())).Error);
    }

    [Fact]
    public async Task S19_使用中の鍵ファイルに上書きして作ろうとすると止める()
    {
        var secrets = await _pc.CompleteSetupAsync(_pin, _password);
        var original = File.ReadAllBytes(secrets.KeyfilePath);
        var pw = _world.Secret("overwrite attempt pw");
        var r = await _pc.Slots.AddKeyfileAsync(secrets.KeyfilePath, TestWorld.B(pw), TestWorld.B(pw));
        Assert.Equal(ErrorCode.KeyfileInUse, r.Error);
        Assert.Equal(original, File.ReadAllBytes(secrets.KeyfilePath));

        // 削除済み（失効した）鍵ファイルの上には作り直せる
        var second = await _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("second.pwmk"), TestWorld.B(pw), TestWorld.B(pw));
        _world.Secret(second.Value!);
        var oldSlot = _pc.Session.Slots.First(s => s.Type == SlotType.Keyfile);
        await _pc.Slots.RemoveAsync(oldSlot.Id);
        var again = await _pc.Slots.AddKeyfileAsync(secrets.KeyfilePath, TestWorld.B(pw), TestWorld.B(pw));
        Assert.True(again.Ok);
        _world.Secret(again.Value!);
    }

    [Fact]
    public async Task 鍵ファイル追加の保存に失敗したら_スロットは増えず_書いた鍵ファイルも消す()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var slots = _pc.Session.Slots.Count;
        _pc.Store.FailNextSave = new IOException("ディスクの空き容量が不足しています");
        var pw = _world.Secret("save fails password");
        var path = _world.UsbKeyfile("unsaved.pwmk");
        Assert.Equal(ErrorCode.IoError, (await _pc.Slots.AddKeyfileAsync(path, TestWorld.B(pw), TestWorld.B(pw))).Error);
        Assert.Equal(slots, _pc.Session.Slots.Count);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 鍵ファイルの書き込みに失敗したら何も変えない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var counter = _pc.Session.Counter;
        _pc.Keyfiles.FailNextWrite = new UnauthorizedAccessException("書き込み禁止の USB メモリ");
        var pw = _world.Secret("write protect password");
        Assert.Equal(ErrorCode.IoError, (await _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("x.pwmk"), TestWorld.B(pw), TestWorld.B(pw))).Error);
        Assert.Equal(counter, _pc.Session.Counter);
        Assert.Single(_pc.Session.Slots, s => s.Type == SlotType.Keyfile);
    }

    [Fact]
    public async Task TPM登録_PC内の別ウィンドウで処理中なら断る()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var pw = _world.Secret("concurrent add password");
        using var gate = new ManualResetEventSlim();
        _pc.Keyfiles.HoldWrite = gate;
        var first = _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("c1.pwmk"), TestWorld.B(pw), TestWorld.B(pw));
        var second = await _pc.Slots.RegisterThisDeviceTpmAsync(TestWorld.B(_pin), TestWorld.B(_pin));
        Assert.Equal(ErrorCode.Busy, second.Error);
        gate.Set();
        var r = await first;
        Assert.True(r.Ok);
        _world.Secret(r.Value!);
    }
}
