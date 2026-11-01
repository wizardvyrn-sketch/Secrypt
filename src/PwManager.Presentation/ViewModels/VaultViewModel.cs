using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Core.Generation;
using PwManager.Core.Localization;
using PwManager.Core.Vault;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>一覧の 1 行。</summary>
public sealed class EntryRowViewModel(VaultEntry entry)
{
    public Guid Id { get; } = entry.Id;
    public string Title { get; } = string.IsNullOrWhiteSpace(entry.Title) ? L.T("（無題）", "(untitled)") : entry.Title;
    public string Subtitle { get; } = string.Join("  ·  ", new[] { entry.UserName, HostOf(entry.Url) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

/// <summary>解錠後のメイン画面（S-03: 一覧・検索・編集・生成・コピー・取り込み）。</summary>
public sealed partial class VaultViewModel : ObservableObject, IDisposable
{
    private readonly EntryService _entries;
    private readonly ImportService _import;
    private readonly VaultSession _session;
    private readonly SettingsService _settings;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly Func<KeysViewModel> _newKeys;
    private readonly ILogger _log;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _flash;
    private EntryRowViewModel? _selectedItem;
    private bool _leaveConfirmed;

    public VaultViewModel(EntryService entries, ImportService import, VaultSession session, SettingsService settings,
        IClipboardService clipboard, IDialogService dialogs, IShellService shell, Func<KeysViewModel> newKeys, ILogger<VaultViewModel>? logger = null,
        IUiDispatcher? ui = null)
    {
        _entries = entries;
        _import = import;
        _session = session;
        _settings = settings;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _shell = shell;
        _newKeys = newKeys;
        _log = logger ?? NullLogger<VaultViewModel>.Instance;
        _ui = ui ?? new SynchronizationContextUiDispatcher();
        Refresh();
    }

    public EntryEditorViewModel Editor { get; } = new();
    public GeneratorViewModel Generator { get; } = new();

    [ObservableProperty] private IReadOnlyList<EntryRowViewModel> _items = [];
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchPlaceholder))]
    private string _searchText = string.Empty;

    public bool ShowSearchPlaceholder => SearchText.Length == 0;

    /// <summary>一覧の選択。編集中の内容が未保存なら確認し、「戻る」なら選択を戻す。</summary>
    public EntryRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(value, _selectedItem)) return;
            if (value is not null && value.Id != Editor.EntryId)
            {
                if (!_leaveConfirmed && !ConfirmLeave())
                {
                    // 画面（ListBox）の選択を元に戻す。バインドの更新中・ListBox の選択処理中は反映されないため、
                    // 一段落してから通知する（WPF では ContextIdle。2026-10-25 実機確認 W-04 で Normal では戻らないことを確認）
                    Post(() => OnPropertyChanged(nameof(SelectedItem)));
                    return;
                }
                if (_session.FindEntry(value.Id) is { } entry) Editor.Show(entry);
            }
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 画面からの選択要求。一覧の選択が変わる「前」に呼ぶ（クリック・矢印キー）。
    /// 未保存の変更があれば確認し、許可されたらこの ViewModel が選択を変える。「戻る」なら何もしない（false）。
    /// WPF では、ListBox が選択を変えた後にバインド経由で戻しても画面に反映されないことがあるため（2026-10-25 実機確認 W-04）、
    /// 画面側は選択の変更を止めてからこれを呼ぶ。
    /// </summary>
    public bool TrySelect(Guid entryId)
    {
        if (_selectedItem?.Id == entryId && Editor.EntryId == entryId) return true;
        if (!ConfirmLeave()) return false;
        _leaveConfirmed = true;
        try
        {
            // 「保存する」を選ぶと一覧が作り直されるため、行は ID で探し直す
            SelectedItem = Items.FirstOrDefault(r => r.Id == entryId);
        }
        finally
        {
            _leaveConfirmed = false;
        }
        return true;
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    /// <summary>一覧を作り直す（検索・保存・取り込みの後）。</summary>
    public void Refresh()
    {
        var rows = _entries.Search(SearchText).Select(e => new EntryRowViewModel(e)).ToList();
        Items = rows;
        var total = _session.Entries.Count;
        CountText = SearchText.Trim().Length == 0 ? $"{total} ITEMS" : $"{rows.Count} / {total} ITEMS";
        _selectedItem = Editor.EntryId is { } id ? rows.FirstOrDefault(r => r.Id == id) : null;
        OnPropertyChanged(nameof(SelectedItem));
    }

    // ---------- 未保存の確認 ----------

    /// <summary>
    /// 編集中の項目から離れてよいか（R-03）。未保存の変更があれば「保存／破棄／戻る」を確認する。
    /// 保存に失敗した場合と「戻る」の場合は false。
    /// </summary>
    public bool ConfirmLeave()
    {
        if (!Editor.IsDirty()) return true;
        switch (_dialogs.Choose(L.T("未保存の変更", "Unsaved changes"), L.T($"「{Editor.DisplayName}」の変更が保存されていません。", $"Changes to \"{Editor.DisplayName}\" have not been saved."), 2,
            L.T("保存する", "Save"), L.T("破棄する", "Discard"), L.T("戻る", "Go back")))
        {
            case 0:
                // ダイアログからの保存は同期的に済む（項目の保存は短い）
                var t = SaveCoreAsync();
                return t.IsCompleted && t.Result;
            case 1:
                if (Editor.EntryId is { } id) Log.UnsavedChangesDiscarded(_log, id, "UserChoice");
                return true;
            default:
                return false;
        }
    }

    /// <summary>ロックされたとき（画面を離れる前）。自動ロックでは未保存の変更を破棄する。</summary>
    internal void OnLocked(LockReason? reason)
    {
        if (Editor.IsVisible && Editor.EntryId is { } id && reason is not LockReason.Manual && Editor.IsDirty())
            Log.UnsavedChangesDiscarded(_log, id, reason?.ToString() ?? "Locked");
        Dispose();
    }

    public void Dispose()
    {
        Editor.Hide();
        _flash?.Cancel();
    }

    // ---------- 編集 ----------

    [RelayCommand]
    private void New()
    {
        if (!ConfirmLeave()) return;
        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        Editor.ShowBlank();
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveCoreAsync();

    private async Task<bool> SaveCoreAsync()
    {
        if (!Editor.IsVisible) return false;
        Editor.ErrorMessage = null;
        var draft = Editor.ToDraft();
        var r = Editor.EntryId is { } id ? await _entries.UpdateAsync(id, draft) : await _entries.CreateAsync(draft);
        if (!r.Ok)
        {
            Editor.ErrorMessage = L.T("保存できませんでした: ", "Could not save: ") + UserMessages.For(r.Error, r.Detail);
            return false;
        }
        var saved = _session.FindEntry(r.Value);
        if (saved is not null) Editor.Show(saved);
        Refresh();
        Flash(L.T("保存しました", "Saved"));
        return true;
    }

    [RelayCommand]
    private void Revert()
    {
        if (!Editor.IsVisible) return;
        if (Editor.EntryId is { } id && _session.FindEntry(id) is { } entry) Editor.Show(entry);
        else Editor.Hide();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!Editor.IsVisible) return;
        if (Editor.EntryId is not { } id)
        {
            Editor.Hide();
            return;
        }
        if (!_dialogs.Confirm(L.T("削除の確認", "Confirm delete"),
                L.T($"「{Editor.DisplayName}」を削除します。この操作は元に戻せません。\n\n念のため、アプリは保存のたびに、保存する前のデータを自動でバックアップしています（直近 5 回分）。誤って削除したときの戻し方は、操作手順書の「困ったとき」を参照してください。",
                    $"\"{Editor.DisplayName}\" will be deleted. This cannot be undone.\n\nAs a safety net, the app automatically backs up your data before every save (the last 5 saves). If you delete something by mistake, see Troubleshooting in the user manual for how to restore it."),
                L.T("削除する", "Delete"))) return;
        var r = await _entries.DeleteAsync(id);
        if (!r.Ok)
        {
            Editor.ErrorMessage = L.T("削除できませんでした: ", "Could not delete: ") + UserMessages.For(r.Error, r.Detail);
            return;
        }
        Editor.Hide();
        Refresh();
        Flash(L.T("削除しました", "Deleted"));
    }

    // ---------- パスワード ----------

    [RelayCommand]
    private void ToggleReveal() => Editor.IsRevealed = !Editor.IsRevealed;

    [RelayCommand]
    private void ToggleGenerator() => Generator.IsOpen = !Generator.IsOpen;

    [RelayCommand]
    private void Generate()
    {
        var o = Generator.Options;
        if (o is null) { Editor.ErrorMessage = L.T("長さを数字で入力してください。", "Enter the length as a number."); return; }
        try
        {
            var pw = PasswordGenerator.Generate(o);
            try { Editor.Password.SetUtf8(pw); }
            finally { CryptographicOperations.ZeroMemory(pw); }
            Editor.IsRevealed = true;
            Editor.ErrorMessage = null;
            Flash(L.T("生成しました。「保存」で確定します", "Generated. Click Save to keep it"));
        }
        catch (ArgumentException ex)
        {
            Editor.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CopyPassword()
    {
        var pw = Editor.Password.ReadUtf8();
        try
        {
            if (pw.Length == 0) { Flash(L.T("パスワードが空です", "The password is empty")); return; }
            var seconds = _settings.Current.ClipboardClearSeconds;
            _clipboard.SetText(Encoding.UTF8.GetString(pw), TimeSpan.FromSeconds(seconds));
            Flash(L.T($"パスワードをコピーしました（{seconds} 秒後に消去）", $"Password copied (cleared in {seconds} seconds)"));
        }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    [RelayCommand]
    private void CopyUser()
    {
        if (string.IsNullOrEmpty(Editor.UserName)) return;
        _clipboard.SetText(Editor.UserName, TimeSpan.FromSeconds(_settings.Current.ClipboardClearSeconds));
        Flash(L.T("ユーザー名をコピーしました", "User name copied"));
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (Uri.TryCreate(Editor.Url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            _shell.OpenUrl(u);
        else
            Flash(L.T("http(s) の URL ではありません", "Not an http(s) URL"));
    }

    // ---------- 取り込み・鍵と設定 ----------

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!_dialogs.Confirm(L.T("ブラウザから取り込む", "Import from browser"),
                L.T("Chrome / Edge でパスワードを CSV にエクスポートし、そのファイルを選んでください。\n" +
                    "  Chrome: 設定 → 自動入力とパスワード → Google パスワード マネージャー → 設定 → エクスポート\n" +
                    "  Edge: 設定 → パスワード → … → パスワードのエクスポート\n\n" +
                    "◆ CSV にはパスワードが平文で入っています。取り込みが終わったら必ず削除してください。",
                    "Export your passwords to a CSV file in Chrome / Edge, then choose that file.\n" +
                    "  Chrome: Settings → Autofill and passwords → Google Password Manager → Settings → Export\n" +
                    "  Edge: Settings → Passwords → … → Export passwords\n\n" +
                    "◆ The CSV contains your passwords in plain text. Be sure to delete it after the import."),
                L.T("ファイルを選ぶ", "Choose file")))
            return;
        var path = _dialogs.PickOpenFile(L.T("パスワードの CSV を選ぶ", "Choose the password CSV"), "CSV (*.csv)|*.csv");
        if (path is null) return;

        OpResult<ImportPreview> preview;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            preview = _import.Preview(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Info(ImportFailedTitle, UserMessages.For(ErrorCode.IoError, ex.Message));
            return;
        }
        if (!preview.Ok)
        {
            _dialogs.Info(ImportFailedTitle, UserMessages.For(preview.Error, preview.Detail));
            return;
        }

        using var p = preview.Value!;
        var (ok, deleteCsv) = _dialogs.ConfirmWithOption(L.T("取り込みの確認", "Confirm import"), ImportSummary(p), L.T("取り込む", "Import"),
            L.T("取り込み後、CSV ファイルを削除する（推奨）", "Delete the CSV file after the import (recommended)"), true);
        if (!ok) return;
        var r = await _import.CommitAsync(p);
        if (!r.Ok)
        {
            _dialogs.Info(ImportFailedTitle, UserMessages.For(r.Error, r.Detail));
            return;
        }
        if (deleteCsv && _import.DeleteSource(path) is { Ok: false } del)
            _dialogs.Info(L.T("CSV を削除できませんでした", "Could not delete the CSV"),
                UserMessages.For(del.Error, del.Detail) + L.T($"\n手動で削除してください: {path}", $"\nPlease delete it manually: {path}"));
        Refresh();
        Flash(L.T($"{r.Value} 件を取り込みました", $"Imported {r.Value} entries"));
    }

    internal static string ImportSummary(ImportPreview p)
    {
        var sb = new StringBuilder(L.T($"{p.ImportCount} 件を取り込みます。", $"{p.ImportCount} entries will be imported."));
        if (p.SameAsExisting > 0) sb.Append(L.T($"\n登録済みと同じ（URL・ユーザー名・パスワードが一致）{p.SameAsExisting} 件は取り込みません。",
                $"\n{p.SameAsExisting} entries identical to existing ones (same URL, user name and password) will be skipped."));
        if (p.ConflictsWithExisting > 0) sb.Append(L.T($"\n登録済みと URL・ユーザー名が同じでパスワードが違う {p.ConflictsWithExisting} 件も取り込みます（同じサイトの項目が 2 件になるので、古い方を削除してください）。",
                $"\n{p.ConflictsWithExisting} entries with the same URL and user name as existing ones but a different password will also be imported (you will have two entries for the same site; delete the older one)."));
        if (p.DuplicatesInFile > 0) sb.Append(L.T($"\nCSV の中の重複 {p.DuplicatesInFile} 件は 1 件にまとめました。", $"\n{p.DuplicatesInFile} duplicates within the CSV were merged into one."));
        if (p.EmptyPasswordRows > 0) sb.Append(L.T($"\nパスワードが空の {p.EmptyPasswordRows} 行はスキップしました。", $"\n{p.EmptyPasswordRows} rows with an empty password were skipped."));
        return sb.ToString();
    }

    [RelayCommand]
    private void OpenKeys()
    {
        _dialogs.ShowKeys(_newKeys());
        if (_session.IsUnlocked) Refresh();
    }

    // ---------- 表示 ----------

    private static string ImportFailedTitle => L.T("取り込めませんでした", "Could not import");

    private void Flash(string message)
    {
        StatusText = message;
        _flash?.Cancel();
        var cts = _flash = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(4), cts.Token).ContinueWith(_ => Post(() => { if (!cts.IsCancellationRequested) StatusText = null; }),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Post(Action action) => _ui.Post(action);
}
