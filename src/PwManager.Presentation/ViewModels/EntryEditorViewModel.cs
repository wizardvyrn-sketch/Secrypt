using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using PwManager.Application.Services;
using PwManager.Core.Localization;
using PwManager.Core.Vault;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>項目の詳細・編集欄（S-03 の右側）。未保存の変更を判定する（R-03）。</summary>
public sealed partial class EntryEditorViewModel : ObservableObject
{
    private VaultEntry? _source;

    public SecretInput Password { get; } = new();

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _header = "ENTRY";
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private string _modifiedText = string.Empty;
    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>表示中の項目の ID（新規は null）。</summary>
    public Guid? EntryId => IsNew ? null : _source?.Id;

    /// <summary>画面に出す名前（確認ダイアログ用）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? L.T("（無題）", "(untitled)") : Title.Trim();

    public void Show(VaultEntry entry)
    {
        _source = entry;
        IsNew = false;
        Header = "ENTRY";
        Fill(entry.Title, entry.UserName, entry.Password, entry.Url, entry.Notes, entry.Tags);
        ModifiedText = L.T($"作成 {entry.Created.ToLocalTime():yyyy-MM-dd HH:mm}  ·  更新 {entry.Modified.ToLocalTime():yyyy-MM-dd HH:mm}",
            $"Created {entry.Created.ToLocalTime():yyyy-MM-dd HH:mm}  ·  Modified {entry.Modified.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    public void ShowBlank()
    {
        _source = null;
        IsNew = true;
        Header = "NEW ENTRY";
        Fill(string.Empty, string.Empty, [], string.Empty, string.Empty, []);
        ModifiedText = L.T("未保存", "Not saved");
    }

    /// <summary>表示を消す（パスワード欄も消去）。</summary>
    public void Hide()
    {
        _source = null;
        IsNew = false;
        IsVisible = false;
        IsRevealed = false;
        Password.Clear();
        Title = UserName = Url = Notes = TagsText = string.Empty;
        ErrorMessage = null;
    }

    private void Fill(string title, string user, ReadOnlySpan<byte> password, string url, string notes, IReadOnlyList<string> tags)
    {
        Title = title;
        UserName = user;
        Url = url;
        Notes = notes;
        TagsText = string.Join(", ", tags);
        IsRevealed = false; // 開き直すと伏せ字に戻る
        Password.SetUtf8(password);
        ErrorMessage = null;
        IsVisible = true;
    }

    /// <summary>
    /// 表示した内容から変わっているか。新規は何か入力されていれば true。
    /// 取り込んだ項目は前後に空白を含むことがあるため、表示した値そのままと比べる。
    /// </summary>
    public bool IsDirty()
    {
        if (!IsVisible) return false;
        var tags = EntryDraft.ParseTags(TagsText);
        if (_source is null)
            return Title.Length > 0 || UserName.Length > 0 || Url.Length > 0 || Notes.Length > 0 || tags.Count > 0 || Password.Length > 0;
        if (Title != _source.Title || UserName != _source.UserName || Url != _source.Url || Notes != _source.Notes || !tags.SequenceEqual(_source.Tags))
            return true;
        var pw = Password.ReadUtf8();
        try { return !CryptographicOperations.FixedTimeEquals(pw, _source.Password); }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    /// <summary>保存用の内容（パスワードのバイト列は EntryService がゼロ化する）。</summary>
    public EntryDraft ToDraft() => new(Title, UserName, Password.ReadUtf8(), Url, Notes, EntryDraft.ParseTags(TagsText));
}
