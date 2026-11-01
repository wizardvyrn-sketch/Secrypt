using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Core.Vault;

namespace PwManager.Application.Tests;

/// <summary>シナリオ S-01: 初回の初期設定（docs/test/2026-10-25-debug-report.md の S01〜S04）。</summary>
public sealed class SetupScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task S01_初期設定_TPMあり_3つの解錠方法が登録され_再起動後にPINで開ける()
    {
        var pc = _world.NewPc();
        var pin = _world.Secret("Pk7x9q");
        var password = _world.Secret("correct horse battery");
        await pc.CompleteSetupAsync(pin, password);

        Assert.True(pc.Session.IsUnlocked);
        Assert.Equal([SlotType.Recovery, SlotType.Keyfile, SlotType.Tpm], pc.Session.Slots.Select(s => s.Type).OrderBy(t => t));
        Assert.True(File.Exists(_world.VaultPath));
        Assert.True(File.Exists(_world.UsbKeyfile()));
        Assert.Equal(_world.UsbKeyfile(), pc.Settings.Current.LastKeyfilePath);

        // 再起動（新しいプロセス）→ 解錠画面は PIN が既定
        pc.Session.RequestLock(LockReason.Exit);
        var restarted = _world.NewPc("pc1-restart", pc.Tpm);
        var options = await restarted.Unlock.GetOptionsAsync();
        Assert.True(options.PinAvailable);
        Assert.True(options.KeyfileAvailable);
        Assert.Equal(UnlockMethod.Pin, options.DefaultMethod);
        var r = await restarted.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(pin)));
        Assert.Equal(ErrorCode.None, r.Error);
        Assert.True(restarted.Session.IsUnlocked);
    }

    [Fact]
    public async Task S02_初期設定_TPMなし_鍵ファイルと復旧コードだけで設定でき_PINタブは出ない()
    {
        var tpm = new FakeTpm { Available = false };
        var pc = _world.NewPc(tpm: tpm);
        Assert.False((await pc.Setup.CheckTpmAsync()).Available);

        var password = _world.Secret("usb-only password");
        await pc.CompleteSetupAsync("unused", password, useTpm: false);
        Assert.DoesNotContain(pc.Session.Slots, s => s.Type == SlotType.Tpm);

        pc.Session.RequestLock(LockReason.Manual);
        var options = await pc.Unlock.GetOptionsAsync();
        Assert.False(options.PinAvailable);
        Assert.Equal(UnlockMethod.Keyfile, options.DefaultMethod);
        Assert.Equal(_world.UsbKeyfile(), options.LastKeyfilePath);
        Assert.True((await pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(options.LastKeyfilePath, null, TestWorld.B(password)))).Ok);
    }

    [Fact]
    public async Task S03_初期設定を途中でやめると_Vaultは作られず_鍵ファイルの場所も覚えない()
    {
        var pc = _world.NewPc();
        using (var flow = pc.Setup.Begin())
        {
            Assert.True((await flow.EnrollTpmAsync(TestWorld.B(_world.Secret("Pk7x9q")), TestWorld.B("Pk7x9q"))).Ok);
            Assert.True((await flow.CreateKeyfileAsync(_world.UsbKeyfile(), TestWorld.B(_world.Secret("abandoned password")), TestWorld.B("abandoned password"))).Ok);
            // ここでウィンドウを閉じた
        }
        Assert.False(File.Exists(_world.VaultPath));
        Assert.False(pc.Session.IsUnlocked);
        Assert.Null(pc.Settings.Current.LastKeyfilePath);
        Assert.Contains(_world.AllLogLines(), l => l.Contains("\"SetupAbandoned\"", StringComparison.Ordinal));

        // 次回起動で最初からやり直せる
        await pc.CompleteSetupAsync(_world.Secret("Pk7x9q2"), _world.Secret("second attempt password"));
        Assert.True(File.Exists(_world.VaultPath));
    }

    [Fact]
    public async Task S04_初期設定の完了時に既にVaultがあれば上書きしない()
    {
        // 2 つのウィンドウで同時に初期設定した（多重起動の防止をすり抜けた場合）
        var first = _world.NewPc("a");
        var second = _world.NewPc("b");
        using var flow2 = second.Setup.Begin();
        Assert.True(flow2.SkipTpm().Ok);
        Assert.True((await flow2.CreateKeyfileAsync(_world.UsbKeyfile("b.pwmk"), TestWorld.B(_world.Secret("second window pw")), TestWorld.B("second window pw"))).Ok);
        var challenge = (await flow2.CreateRecoveryCodeAsync()).Value!;
        _world.Secret(challenge.DisplayCode);

        await first.CompleteSetupAsync(_world.Secret("Pk7x9q"), _world.Secret("first window password"));
        var bytes = File.ReadAllBytes(_world.VaultPath);

        var groups = challenge.DisplayCode.Split('-');
        var r = await flow2.FinishAsync(groups[challenge.GroupA], groups[challenge.GroupB]);
        Assert.Equal(ErrorCode.VaultAlreadyExists, r.Error);
        Assert.False(second.Session.IsUnlocked);
        Assert.Equal(bytes, File.ReadAllBytes(_world.VaultPath));
    }

    [Theory]
    [InlineData("12345", "12345", ErrorCode.PinTooShort)]
    [InlineData("123456", "123457", ErrorCode.ConfirmationMismatch)]
    public async Task 入力規則_PIN(string pin, string confirm, ErrorCode expected)
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        Assert.Equal(expected, (await flow.EnrollTpmAsync(TestWorld.B(pin), TestWorld.B(confirm))).Error);
        Assert.Equal(SetupStep.Tpm, flow.Step);
    }

    [Theory]
    [InlineData("short pw!", ErrorCode.PasswordTooShort)]     // 9 文字
    [InlineData("日本語のパスワード十文", ErrorCode.None)]          // 10 文字（バイト数ではなく文字数で数える）
    public async Task 入力規則_パスワードは文字数で数える(string password, ErrorCode expected)
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        flow.SkipTpm();
        _world.Secret(password);
        Assert.Equal(expected, (await flow.CreateKeyfileAsync(_world.UsbKeyfile(), TestWorld.B(password), TestWorld.B(password))).Error);
    }

    [Fact]
    public async Task 鍵ファイルをVaultと同じフォルダには作れない()
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        flow.SkipTpm();
        var samePlace = Path.Combine(Path.GetDirectoryName(_world.VaultPath)!, "key.pwmk");
        var r = await flow.CreateKeyfileAsync(samePlace, TestWorld.B(_world.Secret("long enough password")), TestWorld.B("long enough password"));
        Assert.Equal(ErrorCode.KeyfileLocationSameDirectory, r.Error);
        Assert.False(File.Exists(samePlace));
    }

    [Fact]
    public async Task 鍵ファイルの書き込みに失敗しても_別の場所でやり直せる()
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        flow.SkipTpm();
        pc.Keyfiles.FailNextWrite = new IOException("デバイスの準備ができていません（USB を抜いた）");
        var pw = _world.Secret("usb pulled password");
        var r = await flow.CreateKeyfileAsync(_world.UsbKeyfile(), TestWorld.B(pw), TestWorld.B(pw));
        Assert.Equal(ErrorCode.IoError, r.Error);
        Assert.False(flow.KeyfileCreated);
        Assert.DoesNotContain(SlotType.Keyfile, flow.EnrolledSlotTypes);

        Assert.True((await flow.CreateKeyfileAsync(_world.UsbKeyfile("retry.pwmk"), TestWorld.B(pw), TestWorld.B(pw))).Ok);
        Assert.Single(flow.EnrolledSlotTypes, SlotType.Keyfile);
    }

    [Fact]
    public async Task 手順の順番は飛ばせない()
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        Assert.Equal(ErrorCode.InvalidStep, (await flow.CreateRecoveryCodeAsync()).Error);
        Assert.Equal(ErrorCode.InvalidStep, (await flow.CreateKeyfileAsync(_world.UsbKeyfile(), TestWorld.B("long enough password"), TestWorld.B("long enough password"))).Error);
        flow.SkipTpm();
        Assert.Equal(ErrorCode.InvalidStep, (await flow.CreateRecoveryCodeAsync()).Error); // 鍵ファイル未作成
        Assert.Equal(ErrorCode.InvalidStep, (await flow.FinishAsync("0000", "0000")).Error);
    }

    [Fact]
    public async Task 復旧コードの書き写し確認_小文字やOと0の違いは許容し_違えば保存しない()
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        flow.SkipTpm();
        var pw = _world.Secret("verify test password");
        await flow.CreateKeyfileAsync(_world.UsbKeyfile(), TestWorld.B(pw), TestWorld.B(pw));
        var c = (await flow.CreateRecoveryCodeAsync()).Value!;
        _world.Secret(c.DisplayCode);
        var groups = c.DisplayCode.Split('-');

        Assert.Equal(ErrorCode.RecoveryVerifyMismatch, (await flow.FinishAsync(groups[c.GroupB], groups[c.GroupA])).Error);
        Assert.False(File.Exists(_world.VaultPath));

        static string Sloppy(string g) => " " + g.ToLowerInvariant().Replace('0', 'o').Replace('1', 'l') + " ";
        Assert.Equal(ErrorCode.None, (await flow.FinishAsync(Sloppy(groups[c.GroupA]), Sloppy(groups[c.GroupB]))).Error);
    }

    [Fact]
    public async Task 入力したPINとパスワードのバイト列は使用後にゼロ化される()
    {
        var pc = _world.NewPc();
        using var flow = pc.Setup.Begin();
        var pin = TestWorld.B(_world.Secret("Pk7x9q"));
        var confirm = TestWorld.B("Pk7x9q");
        await flow.EnrollTpmAsync(pin, confirm);
        Assert.All(pin, b => Assert.Equal(0, b));
        Assert.All(confirm, b => Assert.Equal(0, b));

        var pw = TestWorld.B(_world.Secret("zeroize password"));
        await flow.CreateKeyfileAsync(_world.UsbKeyfile(), pw, TestWorld.B("zeroize password"));
        Assert.All(pw, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task TPMが途中で使えなくなった場合はエラーを返し_TPMなしで続けられる()
    {
        var tpm = new FakeTpm();
        var pc = _world.NewPc(tpm: tpm);
        using var flow = pc.Setup.Begin();
        tpm.Available = false;
        var r = await flow.EnrollTpmAsync(TestWorld.B(_world.Secret("Pk7x9q")), TestWorld.B("Pk7x9q"));
        Assert.Equal(ErrorCode.TpmUnavailable, r.Error);
        Assert.True(flow.SkipTpm().Ok);
        Assert.Equal(SetupStep.Keyfile, flow.Step);
    }
}
