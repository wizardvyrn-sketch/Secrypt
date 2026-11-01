using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Application.Tests.Support;
using PwManager.Core.Vault;

namespace PwManager.Application.Tests;

/// <summary>シナリオ S-03: 自動ロック・項目の編集・保存の失敗と競合（S13〜S15、S26、S27）。</summary>
public sealed class LockAndEntryScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly TestApp _pc;
    private readonly string _pin;
    private readonly string _password;

    public LockAndEntryScenarioTests()
    {
        _pc = _world.NewPc();
        _pin = _world.Secret("Pk7x9q");
        _password = _world.Secret("correct horse battery");
    }

    public void Dispose() => _world.Dispose();

    private static EntryDraft Draft(string title, string user = "taro", string password = "draft-secret-pw", string url = "https://example.com", string notes = "", params string[] tags)
        => new(title, user, TestWorld.B(password), url, notes, tags);

    // ---------- 自動ロック ----------

    [Fact]
    public async Task S13_鍵ファイル作成中に自動ロックの時間になっても_終わってからロックし登録は保存される()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var locks = new List<LockReason?>();
        _pc.Session.StateChanged += (_, e) => { if (e.State == SessionState.Locked) locks.Add(e.Reason); };

        var pw = _world.Secret("second usb password");
        using var gate = new ManualResetEventSlim();
        _pc.Keyfiles.HoldWrite = gate;
        var adding = _pc.Slots.AddKeyfileAsync(_world.UsbKeyfile("second.pwmk"), TestWorld.B(pw), TestWorld.B(pw));
        Assert.True(_pc.Keyfiles.WriteStarted.Wait(TimeSpan.FromSeconds(30))); // 鍵ファイルを書いている最中
        Assert.True(_pc.Session.IsBusy);

        // 処理中に 5 分たった・画面ロックされた
        _world.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.True(_pc.AutoLock.Check());
        Assert.False(_pc.Session.RequestLock(LockReason.SessionLock)); // 保留
        Assert.True(_pc.Session.IsUnlocked);
        Assert.Empty(locks);

        gate.Set();
        var r = await adding;
        Assert.Equal(ErrorCode.None, r.Error);
        _world.Secret(r.Value!);
        Assert.False(_pc.Session.IsUnlocked);           // 完了後にロック
        Assert.Equal([LockReason.Idle], locks);          // 最初の理由で 1 回だけ
        Assert.Contains(_world.AllLogLines(), l => l.Contains("\"AutoLockDeferred\"", StringComparison.Ordinal));

        // 追加した鍵ファイルは保存されていて、それで開ける
        Assert.True((await _pc.Unlock.UnlockAsync(new KeyfileUnlockRequest(_world.UsbKeyfile("second.pwmk"), null, TestWorld.B(pw)))).Ok);
        Assert.Equal(2, _pc.Session.Slots.Count(s => s.Type == SlotType.Keyfile));
    }

    [Fact]
    public async Task S26_無操作5分で自動ロック_操作があれば延びる_ロック後は操作できない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        _pc.AutoLock.IdleTimeout = TimeSpan.FromMinutes(5);
        _world.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(_pc.AutoLock.Check());
        _pc.AutoLock.NotifyActivity();
        _world.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(_pc.AutoLock.Check());
        _world.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(_pc.AutoLock.Check());
        Assert.False(_pc.Session.IsUnlocked);

        Assert.Equal(ErrorCode.Locked, (await _pc.Entries.CreateAsync(Draft("x"))).Error);
        Assert.Empty(_pc.Session.Entries);
    }

    [Fact]
    public async Task S27_ロックするとクリップボードと画面上のパスワードを消す()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var id = await _pc.AddEntryAsync("ネット銀行", "taro", "bank-secret-01");
        var held = _pc.Session.FindEntry(id)!; // 画面が持っている参照
        _pc.Clipboard.SetText("bank-secret-01", TimeSpan.FromSeconds(20));

        _pc.Session.RequestLock(LockReason.Suspend);
        Assert.Null(_pc.Clipboard.Text);
        Assert.All(held.Password, b => Assert.Equal(0, b));
    }

    // ---------- 項目 ----------

    [Fact]
    public async Task 項目の作成_更新_削除_検索()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var a = (await _pc.Entries.CreateAsync(Draft("  Amazon ", " taro ", _world.Secret("amazon-pw-01"), " https://www.amazon.co.jp ", tags: ["shop", " shop ", ""]))).Value;
        var b = (await _pc.Entries.CreateAsync(Draft("ゆうちょ", "taro", _world.Secret("yucho-pw-02"), "", tags: "bank"))).Value;
        var entry = _pc.Session.FindEntry(a)!;
        Assert.Equal("Amazon", entry.Title);
        Assert.Equal("taro", entry.UserName);
        Assert.Equal("https://www.amazon.co.jp", entry.Url);
        Assert.Equal(["shop"], entry.Tags);

        Assert.Equal(["Amazon"], _pc.Entries.Search("AMAZON").Select(e => e.Title));
        Assert.Equal(["ゆうちょ"], _pc.Entries.Search("bank").Select(e => e.Title));
        Assert.Equal(2, _pc.Entries.Search("taro").Count);

        Assert.True((await _pc.Entries.UpdateAsync(b, Draft("ゆうちょ銀行", "taro", _world.Secret("yucho-pw-03"), "https://direct.jp-bank.japanpost.jp"))).Ok);
        Assert.True((await _pc.Entries.DeleteAsync(a)).Ok);
        Assert.Equal(ErrorCode.NotFound, (await _pc.Entries.DeleteAsync(a)).Error);

        _pc.Session.RequestLock(LockReason.Manual);
        Assert.True((await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)))).Ok);
        var only = Assert.Single(_pc.Session.Entries);
        Assert.Equal("ゆうちょ銀行", only.Title);
        Assert.Equal(TestWorld.B("yucho-pw-03"), only.Password);
    }

    [Fact]
    public async Task タイトルとURLが両方空の項目は保存できない()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        Assert.Equal(ErrorCode.TitleOrUrlRequired, (await _pc.Entries.CreateAsync(Draft("  ", url: " "))).Error);
        Assert.True((await _pc.Entries.CreateAsync(Draft("", url: "https://only-url.example"))).Ok);
    }

    [Fact]
    public async Task S14_保存に失敗したら_新規も更新も削除もメモリ上で元に戻る()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var id = await _pc.AddEntryAsync("ネット銀行", "taro", "bank-secret-01");
        var entry = _pc.Session.FindEntry(id)!;
        var counter = _pc.Session.Counter;
        var file = File.ReadAllBytes(_world.VaultPath);

        _pc.Store.FailNextSave = new IOException("ディスクの空き容量が不足しています");
        var created = await _pc.Entries.CreateAsync(Draft("新規", password: _world.Secret("new-secret-04")));
        Assert.Equal(ErrorCode.IoError, created.Error);
        Assert.Single(_pc.Session.Entries);

        _pc.Store.FailNextSave = new IOException("ディスクの空き容量が不足しています");
        var updated = await _pc.Entries.UpdateAsync(id, Draft("書き換え", "jiro", _world.Secret("changed-secret-05")));
        Assert.Equal(ErrorCode.IoError, updated.Error);
        Assert.Same(entry, _pc.Session.FindEntry(id)); // 画面が持つ参照のまま
        Assert.Equal("ネット銀行", entry.Title);
        Assert.Equal("taro", entry.UserName);
        Assert.Equal(TestWorld.B("bank-secret-01"), entry.Password);

        _pc.Store.FailNextSave = new UnauthorizedAccessException("アクセスが拒否されました");
        Assert.Equal(ErrorCode.IoError, (await _pc.Entries.DeleteAsync(id)).Error);
        Assert.Same(entry, Assert.Single(_pc.Session.Entries));

        Assert.Equal(counter, _pc.Session.Counter); // 保存回数も進んでいない
        Assert.Equal(file, File.ReadAllBytes(_world.VaultPath));

        // 次の保存が成功すれば、失敗した変更は混ざらない
        Assert.True((await _pc.Entries.CreateAsync(Draft("成功", password: _world.Secret("ok-secret-06")))).Ok);
        _pc.Session.RequestLock(LockReason.Manual);
        await _pc.Unlock.UnlockAsync(new PinUnlockRequest(TestWorld.B(_pin)));
        Assert.Equal(["ネット銀行", "成功"], _pc.Entries.Search(null).Select(e => e.Title));
        Assert.Equal(counter + 1, _pc.Session.Counter); // 解錠時の最終使用日の記録では進まない
    }

    [Fact]
    public async Task S15_同期フォルダの別PCで更新されていたら_上書きせずに知らせる()
    {
        // 2 台の PC が OneDrive 上の同じ Vault を開いている
        var shared = Path.Combine(_world.Root, "OneDrive", "vault.pwmv");
        var pcA = _world.NewPc("A", vaultPath: shared);
        var secrets = await pcA.CompleteSetupAsync(_pin, _password);
        var pcB = _world.NewPc("B", vaultPath: shared);
        Assert.True((await pcB.Unlock.UnlockAsync(new KeyfileUnlockRequest(secrets.KeyfilePath, null, TestWorld.B(_password)))).Ok);

        // B は解錠しただけ（最終使用日の記録で書き込みはあるが、内容の版 counter は進まない）→ A は保存できる
        await pcA.AddEntryAsync("A で追加", "a", "pc-a-secret-07");
        var afterA = File.ReadAllBytes(shared);

        var r = await pcB.Entries.CreateAsync(Draft("B で追加", password: _world.Secret("pc-b-secret-08")));
        Assert.Equal(ErrorCode.SaveConflict, r.Error);
        Assert.Empty(pcB.Session.Entries);                  // B の変更は戻した
        Assert.Equal(afterA, File.ReadAllBytes(shared));    // A の変更は残っている

        // B はロックして開き直せば A の変更が見える
        pcB.Session.RequestLock(LockReason.Manual);
        Assert.True((await pcB.Unlock.UnlockAsync(new KeyfileUnlockRequest(secrets.KeyfilePath, null, TestWorld.B(_password)))).Ok);
        Assert.Equal(["A で追加"], pcB.Entries.Search(null).Select(e => e.Title));
        Assert.True((await pcB.Entries.CreateAsync(Draft("B で追加", password: "pc-b-secret-08"))).Ok);
    }

    [Fact]
    public async Task 開いている間にVaultファイルが消された場合は_次の保存で作り直す()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        File.Delete(_world.VaultPath);
        Assert.True((await _pc.Entries.CreateAsync(Draft("消された後", password: _world.Secret("recreated-secret-09")))).Ok);
        Assert.True(File.Exists(_world.VaultPath));
    }

    [Fact]
    public async Task 編集内容のパスワードのバイト列は保存後にゼロ化される()
    {
        await _pc.CompleteSetupAsync(_pin, _password);
        var draft = Draft("ゼロ化", password: _world.Secret("wipe-me-secret-10"));
        await _pc.Entries.CreateAsync(draft);
        Assert.All(draft.Password, b => Assert.Equal(0, b));
    }
}
