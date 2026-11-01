using Microsoft.Extensions.DependencyInjection;
using PwManager.Application.Infrastructure;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Core.Slots;
using PwManager.Core.Vault;
using PwManager.Presentation;
using PwManager.Presentation.Ports;
using PwManager.Presentation.ViewModels;

namespace PwManager.Application.Tests;

/// <summary>
/// 画面（ViewModel）を通した利用シーン。ボタン操作＝コマンド実行、ダイアログの応答は ScriptedDialogs で与える。
/// WPF の描画とバインドそのものは Windows の実機確認で見る（docs/test/2026-10-25-debug-report.md）。
/// </summary>
public sealed class ViewModelScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly TestApp _pc;
    private readonly ScriptedDialogs _dialogs = new();
    private readonly RecordingShell _shellService = new();
    private readonly ShellViewModel _shell;
    private readonly string _pin;
    private readonly string _password;

    public ViewModelScenarioTests()
    {
        _pc = _world.NewPc();
        _pin = _world.Secret("Pk7x9q");
        _password = _world.Secret("correct horse battery");
        _shell = new ShellViewModel(_pc.Session, _pc.AutoLock, _pc.Settings, _dialogs, NewSetup, NewUnlock, NewVault);
    }

    public void Dispose() => _world.Dispose();

    private SetupViewModel NewSetup() => new(_pc.Setup, _dialogs, new LanguageViewModel(_pc.Settings));
    private UnlockViewModel NewUnlock() => new(_pc.Unlock, _pc.Session, _dialogs, new LanguageViewModel(_pc.Settings));
    private VaultViewModel NewVault()
    {
        var vm = new VaultViewModel(_pc.Entries, _pc.Import, _pc.Session, _pc.Settings, _pc.Clipboard, _dialogs, _shellService, NewKeys, _pc.Logger<VaultViewModel>());
        EditorPassword = FakeSecretBox.Attach(vm.Editor.Password); // 画面の読み込み時に PasswordBox がつながる
        return vm;
    }

    /// <summary>一覧画面の詳細のパスワード欄。</summary>
    private FakeSecretBox EditorPassword { get; set; } = new();
    private KeysViewModel NewKeys() => new(_pc.Slots, _pc.Session, _pc.Settings, _dialogs, _shellService, new DiagnosticsInfo("test", "net10", _pc.LogDirectory));

    /// <summary>初期設定画面を最後まで操作する。</summary>
    private async Task<string> SetupThroughUiAsync()
    {
        _shell.Start();
        var setup = Assert.IsType<SetupViewModel>(_shell.CurrentPage);
        await setup.InitializeAsync();
        Assert.True(setup.TpmAvailable);
        Assert.Equal("STEP 1 / 3", setup.StepText);

        FakeSecretBox.Attach(setup.Pin, _pin);
        FakeSecretBox.Attach(setup.PinConfirm, _pin);
        await setup.NextCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        Assert.True(setup.IsKeyfileStep);
        Assert.Equal("鍵ファイルを作成", setup.NextText);

        FakeSecretBox.Attach(setup.KeyfilePassword, _password);
        FakeSecretBox.Attach(setup.KeyfilePasswordConfirm, _password);
        _dialogs.Then(_world.UsbKeyfile());
        setup.ChooseKeyfileCommand.Execute(null);
        Assert.NotNull(setup.KeyfileRiskText); // テスト環境では同じドライブ → 注意を表示
        await setup.NextCommand.ExecuteAsync(null);
        Assert.True(setup.KeyfileCreated);
        Assert.Equal("次へ", setup.NextText);

        await setup.NextCommand.ExecuteAsync(null); // 「保存した」チェックなし
        Assert.Contains("チェック", setup.ErrorMessage, StringComparison.Ordinal);
        setup.PrintQrCommand.Execute(null);         // 印刷すると自動でチェック
        Assert.True(setup.KeyfileStored);
        await setup.NextCommand.ExecuteAsync(null);
        Assert.True(setup.IsRecoveryStep);

        var code = setup.RecoveryCodeText.Replace("  ", "-", StringComparison.Ordinal);
        _world.Secret(code);
        var groups = code.Split('-');
        int a = int.Parse(setup.VerifyALabel.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture) - 1;
        int b = int.Parse(setup.VerifyBLabel.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture) - 1;
        setup.HideCode = true;
        setup.VerifyA = groups[b];
        setup.VerifyB = groups[a];
        await setup.NextCommand.ExecuteAsync(null);
        Assert.False(setup.HideCode); // 違ったらコードを再表示
        setup.VerifyA = groups[a].ToLowerInvariant();
        setup.VerifyB = groups[b];
        await setup.NextCommand.ExecuteAsync(null);
        Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        Assert.True(_shell.IsUnlocked);
        Assert.Equal("解錠中", _shell.StatusText);
        return code;
    }

    private async Task<UnlockViewModel> LockAndShowUnlockAsync()
    {
        Assert.True(_shell.RequestLock(LockReason.Manual));
        var unlock = Assert.IsType<UnlockViewModel>(_shell.CurrentPage);
        await unlock.InitializeAsync();
        return unlock;
    }

    [Fact]
    public async Task 画面_初期設定から一覧_ロック_PINで解錠()
    {
        await SetupThroughUiAsync();
        var unlock = await LockAndShowUnlockAsync();
        Assert.Equal("ロック中", _shell.StatusText);
        Assert.False(_shell.IsUnlocked);
        Assert.True(unlock.IsPinSelected);
        Assert.Equal(1, _dialogs.CloseAllCount); // ロック時に子ウィンドウ・ダイアログを閉じる

        var pinBox = FakeSecretBox.Attach(unlock.Pin, "wrong-1");
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Contains("PIN が違います", unlock.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, pinBox.Text); // 失敗したら入力欄を消す

        pinBox.Text = _pin;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        Assert.Equal(string.Empty, pinBox.Text);
    }

    [Fact]
    public async Task 画面_連続失敗の待ち時間を表示する()
    {
        await SetupThroughUiAsync();
        var unlock = await LockAndShowUnlockAsync();
        var box = FakeSecretBox.Attach(unlock.Pin);
        for (int i = 0; i < 3; i++)
        {
            box.Text = "wrong-" + i;
            await unlock.UnlockCommand.ExecuteAsync(null);
        }
        Assert.Contains("次は 15 秒後", unlock.ErrorMessage, StringComparison.Ordinal);
        box.Text = _pin;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Contains("15 秒待って", unlock.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 画面_鍵ファイルタブ_前回の場所を自動入力_QRテキストでも開ける()
    {
        await SetupThroughUiAsync();
        var unlock = await LockAndShowUnlockAsync();
        Assert.Equal(_world.UsbKeyfile(), unlock.KeyfilePath);
        unlock.IsKeyfileSelected = true;
        Assert.True(unlock.IsKeyfileSelected);
        Assert.False(unlock.IsPinSelected);

        var text = File.ReadAllBytes(_world.UsbKeyfile());
        using var kf = Keyfile.FromBytes(text);
        var qr = _world.Secret(kf.ToText());
        _dialogs.Then("not a keyfile");
        unlock.EnterKeyfileTextCommand.Execute(null);
        Assert.Contains(_dialogs.Calls, c => c.StartsWith("ValidationError:", StringComparison.Ordinal));
        _dialogs.Then(qr);
        unlock.EnterKeyfileTextCommand.Execute(null);
        Assert.Equal("（印刷した QR のテキストを使用）", unlock.KeyfilePath);

        FakeSecretBox.Attach(unlock.KeyfilePassword, _password);
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.IsType<VaultViewModel>(_shell.CurrentPage);
    }

    [Fact]
    public async Task 画面_編集中に別の項目を選ぶと確認_戻るなら選択を戻し_破棄なら移る()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        await _pc.AddEntryAsync("Amazon", "taro", "amazon-pw-01");
        await _pc.AddEntryAsync("Google", "taro", "google-pw-02");
        vault.Refresh();
        Assert.Equal("2 ITEMS", vault.CountText);

        vault.SelectedItem = vault.Items[0];
        var pwBox = EditorPassword;
        vault.SelectedItem = vault.Items[1];
        vault.SelectedItem = vault.Items[0];
        Assert.Equal("Amazon", vault.Editor.Title);
        Assert.False(vault.Editor.IsDirty());
        Assert.Equal("amazon-pw-01", pwBox.Text);

        vault.Editor.Title = "Amazon（編集中）";
        Assert.True(vault.Editor.IsDirty());
        _dialogs.Then(2); // 戻る
        vault.SelectedItem = vault.Items[1];
        Assert.Equal("Amazon（編集中）", vault.Editor.Title);
        Assert.Equal("Amazon", vault.SelectedItem!.Title);

        _dialogs.Then(1); // 破棄
        vault.SelectedItem = vault.Items[1];
        Assert.Equal("Google", vault.Editor.Title);
        Assert.Equal("Amazon", _pc.Session.Entries.Single(e => e.Title.StartsWith("Amazon", StringComparison.Ordinal)).Title);
    }

    [Fact]
    public async Task 画面_一覧のクリック前に確認_TrySelect_戻る_破棄_保存()
    {
        // 画面（VaultView）はクリック・矢印キーの時点で ListBox の選択を止め、TrySelect を呼ぶ（W-04）
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        await _pc.AddEntryAsync("Amazon", "taro", "amazon-pw-01");
        await _pc.AddEntryAsync("Google", "taro", "google-pw-02");
        vault.Refresh();
        var amazon = vault.Items[0].Id;
        var google = vault.Items[1].Id;

        Assert.True(vault.TrySelect(amazon));
        Assert.Equal(amazon, vault.SelectedItem!.Id);
        Assert.True(vault.TrySelect(amazon)); // 同じ項目は確認なし

        vault.Editor.Title = "Amazon（編集中）";
        _dialogs.Then(2); // 戻る
        Assert.False(vault.TrySelect(google));
        Assert.Equal(amazon, vault.SelectedItem!.Id);
        Assert.Equal("Amazon（編集中）", vault.Editor.Title);

        _dialogs.Then(0); // 保存する → 一覧が作り直されても ID で選び直す
        Assert.True(vault.TrySelect(google));
        Assert.Equal(google, vault.SelectedItem!.Id);
        Assert.Equal("Google", vault.Editor.Title);
        Assert.Contains(_pc.Session.Entries, e => e.Title == "Amazon（編集中）");

        vault.Editor.Notes = "破棄するメモ";
        _dialogs.Then(1); // 破棄する（確認は 1 回だけ）
        Assert.True(vault.TrySelect(amazon));
        Assert.Equal(amazon, vault.SelectedItem!.Id);
        Assert.Equal(string.Empty, _pc.Session.Entries.Single(e => e.Title == "Google").Notes);
    }

    [Fact]
    public async Task 画面_編集中に新規_保存を選ぶと保存してから新規()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        await _pc.AddEntryAsync("Amazon", "taro", "amazon-pw-01");
        vault.Refresh();
        vault.SelectedItem = vault.Items[0];
        vault.Editor.Notes = "メモを追加";
        _dialogs.Then(0); // 保存する
        vault.NewCommand.Execute(null);
        Assert.True(vault.Editor.IsNew);
        Assert.Equal("メモを追加", _pc.Session.Entries.Single().Notes);
    }

    [Fact]
    public async Task 画面_手動ロックでは未保存を確認し_自動ロックでは破棄して記録する()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        await _pc.AddEntryAsync("Amazon", "taro", "amazon-pw-01");
        vault.Refresh();
        vault.SelectedItem = vault.Items[0];
        vault.Editor.UserName = "jiro";

        _dialogs.Then(2); // 戻る
        vault.Editor.UserName = "jiro";
        Assert.False(_shell.RequestLock(LockReason.Manual));
        Assert.True(_pc.Session.IsUnlocked);

        _world.Time.Advance(TimeSpan.FromMinutes(6));
        _shell.Tick(); // 自動ロック: 確認せずに破棄
        Assert.False(_pc.Session.IsUnlocked);
        Assert.IsType<UnlockViewModel>(_shell.CurrentPage);
        Assert.Contains(_world.AllLogLines(), l => l.Contains("\"UnsavedChangesDiscarded\"", StringComparison.Ordinal) && l.Contains("Idle", StringComparison.Ordinal));
        Assert.False(vault.Editor.IsVisible); // 画面上のパスワード欄も消した
    }

    [Fact]
    public async Task 画面_保存に失敗したら理由を表示し_入力は残す()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        vault.NewCommand.Execute(null);
        EditorPassword.Text = _world.Secret("new-entry-pw-03");
        vault.Editor.Title = "銀行";
        _pc.Store.FailNextSave = new IOException("ディスクの空き容量が不足しています");
        await vault.SaveCommand.ExecuteAsync(null);
        Assert.Contains("保存できませんでした", vault.Editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("銀行", vault.Editor.Title);
        Assert.True(vault.Editor.IsNew);
        Assert.Empty(vault.Items);

        await vault.SaveCommand.ExecuteAsync(null);
        Assert.Null(vault.Editor.ErrorMessage);
        Assert.False(vault.Editor.IsNew);
        Assert.Single(vault.Items);
        Assert.Equal("保存しました", vault.StatusText);
    }

    [Fact]
    public async Task 画面_パスワード生成_コピー_URLを開く()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        vault.NewCommand.Execute(null);
        var pw = EditorPassword;
        vault.Generator.LengthText = "24";
        vault.Generator.Symbols = false;
        Assert.StartsWith("強さの目安: 約 143", vault.Generator.EntropyText, StringComparison.Ordinal);
        vault.GenerateCommand.Execute(null);
        Assert.Equal(24, pw.Text.Length);
        Assert.True(vault.Editor.IsRevealed);
        Assert.True(vault.Editor.IsDirty());
        _world.Secret(pw.Text);

        vault.CopyPasswordCommand.Execute(null);
        Assert.Equal(pw.Text, _pc.Clipboard.Text);
        Assert.Contains("20 秒後に消去", vault.StatusText, StringComparison.Ordinal);

        vault.Generator.LengthText = "abc";
        Assert.Equal("数字で入力", vault.Generator.EntropyText);
        vault.Generator.LengthText = "2";
        Assert.StartsWith("長さ 4〜256", vault.Generator.EntropyText, StringComparison.Ordinal);

        vault.Editor.Url = "javascript:alert(1)";
        vault.OpenUrlCommand.Execute(null);
        Assert.Empty(_shellService.Opened);
        vault.Editor.Url = " https://example.com/login ";
        vault.OpenUrlCommand.Execute(null);
        Assert.Equal(["https://example.com/login"], _shellService.Opened);
    }

    [Fact]
    public async Task 画面_取り込み_確認内容を表示し_CSVを削除する()
    {
        await SetupThroughUiAsync();
        var vault = Assert.IsType<VaultViewModel>(_shell.CurrentPage);
        var csv = Path.Combine(_world.Root, "export.csv");
        File.WriteAllText(csv, "name,url,username,password\nA,https://a.example,u,a-secret-pw-1\nB,https://b.example,u,\n");
        _world.Secret("a-secret-pw-1");
        _dialogs.Then(true).Then(csv).Then(new[] { true, true });
        await vault.ImportCommand.ExecuteAsync(null);
        var summary = _dialogs.Calls.Single(c => c.StartsWith("ConfirmWithOption", StringComparison.Ordinal));
        Assert.Contains("1 件を取り込みます", summary, StringComparison.Ordinal);
        Assert.Contains("パスワードが空の 1 行", summary, StringComparison.Ordinal);
        Assert.False(File.Exists(csv));
        Assert.Single(vault.Items);
        Assert.Equal("1 件を取り込みました", vault.StatusText);
    }

    [Fact]
    public async Task 画面_鍵と設定_鍵ファイル追加_TPM登録_復旧コード作り直し_削除_設定()
    {
        var oldCode = await SetupThroughUiAsync();
        var keys = NewKeys();
        await keys.LoadAsync();
        Assert.Equal(3, keys.SlotRows.Count);
        Assert.Contains(keys.SlotRows, r => r.Detail.StartsWith("この PC の TPM", StringComparison.Ordinal));

        // 鍵ファイル: 短いパスワードは入力欄で止める → 正しく入れて保存先を選ぶ → 印刷する
        var pw2 = _world.Secret("second usb password");
        _dialogs.Then(new[] { "short", "short" });
        await keys.AddKeyfileCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Calls, c => c.Contains("10 文字以上", StringComparison.Ordinal));
        _dialogs.Then(new[] { pw2, pw2 }).Then(_world.UsbKeyfile("second.pwmk")).Then(true);
        await keys.AddKeyfileCommand.ExecuteAsync(null);
        Assert.Null(keys.ErrorMessage);
        Assert.Equal(4, keys.SlotRows.Count);
        Assert.Contains("PrintKeyfileQr", _dialogs.Calls);
        using (var kf = Keyfile.FromBytes(File.ReadAllBytes(_world.UsbKeyfile("second.pwmk")))) _world.Secret(kf.ToText());

        // TPM の PIN を変える（置き換えなので 4 件のまま）
        var newPin = _world.Secret("N3wp1n");
        _dialogs.Then(new[] { newPin, newPin });
        await keys.RegisterTpmCommand.ExecuteAsync(null);
        Assert.Equal(4, keys.SlotRows.Count);

        // 復旧コード: 1 回目は書き間違い → 2 回目に正しく
        _dialogs.Then(true)
            .Then((Func<string, string>)(_ => "ZZZZ"))
            .Then((Func<string, string>)(code =>
            {
                _world.Secret(code);
                int g = int.Parse(_dialogs.LastCheckLabel!.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture) - 1;
                return code.Split('-')[g];
            }));
        await keys.RegenerateRecoveryCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Calls, c => c.Contains("書き写した内容と一致しません", StringComparison.Ordinal));
        Assert.Equal(4, keys.SlotRows.Count);
        var newCode = _dialogs.LastRecoveryCode!;

        // 削除: 選ばずに押す → 案内。復旧コードは削除できない
        await keys.RemoveSlotCommand.ExecuteAsync(null);
        Assert.Contains("選んでください", keys.ErrorMessage, StringComparison.Ordinal);
        keys.SelectedSlot = keys.SlotRows.Single(r => r.Type == SlotType.Recovery);
        _dialogs.Then(true);
        await keys.RemoveSlotCommand.ExecuteAsync(null);
        Assert.Contains("復旧コードと、ほかに 1 つ以上", keys.ErrorMessage, StringComparison.Ordinal);

        // 設定: 範囲外の値は採用しない
        keys.AutoLockText = "0";
        keys.ClipboardText = "30";
        keys.CloseCommand.Execute(null);
        Assert.Equal(5, _pc.Settings.Current.AutoLockMinutes);
        Assert.Equal(30, _pc.Settings.Current.ClipboardClearSeconds);

        // 古い復旧コードは無効、新しいコードで開ける
        _shell.RequestLock(LockReason.Manual);
        Assert.Equal(ErrorCode.WrongRecoveryCode, (await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(oldCode))).Error);
        Assert.True((await _pc.Unlock.UnlockAsync(new RecoveryUnlockRequest(newCode))).Ok);
    }

    [Fact]
    public async Task 画面_処理中は閉じず_終わってから閉じる()
    {
        await SetupThroughUiAsync();
        var closed = 0;
        _shell.CloseRequested += (_, _) => closed++;
        var pw = _world.Secret("closing while busy pw");
        using var gate = new ManualResetEventSlim();
        _pc.Keyfiles.HoldWrite = gate;
        var adding = _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("busy.pwmk"), TestWorld.B(pw), TestWorld.B(pw));
        Assert.True(_pc.Keyfiles.WriteStarted.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(_shell.IsBusy);
        Assert.Equal("鍵ファイルを作成しています…（数秒かかります）", _shell.BusyText);
        Assert.False(_shell.CanClose());
        gate.Set();
        var r = await adding;
        _world.Secret(r.Value!);
        Assert.False(_shell.IsBusy);
        Assert.Equal(1, closed);
        Assert.True(_shell.CanClose());
        Assert.False(_pc.Session.IsUnlocked);
    }

    [Fact]
    public async Task 画面_PC買い替え後の解錠画面は_PINタブを無効にして案内する()
    {
        await SetupThroughUiAsync();
        _shell.RequestLock(LockReason.Exit);
        _pc.Tpm.Clear();
        var unlock = NewUnlock();
        await unlock.InitializeAsync();
        Assert.False(unlock.PinAvailable);
        Assert.True(unlock.IsKeyfileSelected);
        Assert.Contains("この PC の TPM は登録されていません", unlock.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 画面_Vaultが壊れていれば解錠ボタンを無効にして理由を出す()
    {
        await SetupThroughUiAsync();
        _shell.RequestLock(LockReason.Exit);
        File.WriteAllBytes(_world.VaultPath, [1, 2, 3]);
        var unlock = NewUnlock();
        await unlock.InitializeAsync();
        Assert.False(unlock.CanUnlock);
        Assert.Contains("読み込めません", unlock.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 組み立て_すべての画面と依存を解決できる()
    {
        var paths = new AppDataPaths(Path.Combine(_world.Root, "di"));
        var s = new ServiceCollection();
        s.AddPwManager(paths, Path.Combine(_world.Root, "di", "vault.pwmv"), new DevOptions(false), typeof(ViewModelScenarioTests).Assembly);
        s.AddSingleton<ITpmSealer>(new FakeTpm());
        s.AddSingleton<IClipboardService>(new RecordingClipboard());
        s.AddSingleton<IDialogService>(_dialogs);
        s.AddSingleton<IShellService>(_shellService);
        using var provider = s.Build();
        var shell = provider.GetRequiredService<ShellViewModel>();
        shell.Start();
        Assert.IsType<SetupViewModel>(shell.CurrentPage);
        Assert.NotNull(provider.GetRequiredService<KeysViewModel>());
        Assert.NotNull(provider.GetRequiredService<VaultViewModel>());
    }

    [Fact]
    public void 開発用設定_Releaseではキャプチャ許可にならない()
    {
        Assert.False(DevOptions.Create(_ => "1", isDebugBuild: false).AllowCapture);
        Assert.True(DevOptions.Create(_ => "1", isDebugBuild: true).AllowCapture);
        Assert.False(DevOptions.Create(_ => null, isDebugBuild: true).AllowCapture);
    }
}
