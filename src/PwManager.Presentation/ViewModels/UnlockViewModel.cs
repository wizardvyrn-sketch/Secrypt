using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Core.Localization;
using PwManager.Core.Slots;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>解錠画面（S-02）。PIN（この PC の TPM）・鍵ファイル・復旧コードのいずれかで開く。</summary>
public sealed partial class UnlockViewModel : ObservableObject
{
    public static string KeyfileFilter => L.T("鍵ファイル (*.pwmk)|*.pwmk|すべてのファイル (*.*)|*.*", "Key file (*.pwmk)|*.pwmk|All files (*.*)|*.*");
    private static string QrTextPlaceholder => L.T("（印刷した QR のテキストを使用）", "(using the text of the printed QR code)");

    private readonly UnlockService _unlock;
    private readonly IDialogService _dialogs;
    private string? _keyfileText;

    public UnlockViewModel(UnlockService unlock, VaultSession session, IDialogService dialogs, LanguageViewModel language)
    {
        _unlock = unlock;
        _dialogs = dialogs;
        Language = language;
        VaultPath = session.Location;
    }

    /// <summary>表示言語の切り替え。</summary>
    public LanguageViewModel Language { get; }

    public SecretInput Pin { get; } = new();
    public SecretInput KeyfilePassword { get; } = new();

    public string VaultPath { get; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _canUnlock;
    [ObservableProperty] private bool _pinAvailable;
    [ObservableProperty] private bool _keyfileAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinSelected), nameof(IsKeyfileSelected), nameof(IsRecoverySelected))]
    private UnlockMethod _selectedMethod = UnlockMethod.Pin;

    [ObservableProperty] private string _keyfilePath = string.Empty;
    [ObservableProperty] private string _recoveryText = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>画面上部の補足（この PC の TPM が未登録など）。</summary>
    [ObservableProperty] private string? _hint;

    public bool IsPinSelected
    {
        get => SelectedMethod == UnlockMethod.Pin;
        set { if (value) SelectedMethod = UnlockMethod.Pin; }
    }

    public bool IsKeyfileSelected
    {
        get => SelectedMethod == UnlockMethod.Keyfile;
        set { if (value) SelectedMethod = UnlockMethod.Keyfile; }
    }

    public bool IsRecoverySelected
    {
        get => SelectedMethod == UnlockMethod.Recovery;
        set { if (value) SelectedMethod = UnlockMethod.Recovery; }
    }

    /// <summary>使える方法を調べて、既定のタブを選ぶ。</summary>
    public async Task InitializeAsync()
    {
        var o = await _unlock.GetOptionsAsync();
        PinAvailable = o.PinAvailable;
        KeyfileAvailable = o.KeyfileAvailable;
        KeyfilePath = o.LastKeyfilePath ?? string.Empty;
        CanUnlock = o.VaultReadable;
        SelectedMethod = o.DefaultMethod;
        if (!o.VaultReadable) ErrorMessage = UserMessages.For(o.HeaderError);
        else if (o.TpmSlotsForOtherDevicesOnly) Hint = UserMessages.For(ErrorCode.TpmNotRegisteredOnThisDevice);
        IsLoaded = true;
        FocusCurrent();
    }

    partial void OnSelectedMethodChanged(UnlockMethod value)
    {
        ErrorMessage = null;
        FocusCurrent();
    }

    [RelayCommand]
    private void ChooseKeyfile()
    {
        var path = _dialogs.PickOpenFile(L.T("鍵ファイルを選ぶ", "Choose the key file"), KeyfileFilter, string.IsNullOrWhiteSpace(KeyfilePath) ? null : KeyfilePath);
        if (path is null) return;
        KeyfilePath = path;
        _keyfileText = null;
    }

    [RelayCommand]
    private void EnterKeyfileText()
    {
        var text = _dialogs.PromptText(L.T("印刷した鍵ファイルのテキスト", "Text of the printed key file"),
            L.T("QR コードの下に印刷されている「PWMK1:」で始まる文字列を入力してください（QR コード読み取りアプリでも取得できます）。",
                "Enter the text starting with \"PWMK1:\" printed below the QR code (you can also get it with a QR code reader app)."),
            L.T("テキスト", "Text"),
            t =>
            {
                try { using var _ = Keyfile.FromText(t); return null; }
                catch (FormatException ex) { return ex.Message; }
            });
        if (text is null) return;
        _keyfileText = text.Trim();
        KeyfilePath = QrTextPlaceholder;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (!CanUnlock) return;
        ErrorMessage = null;
        UnlockRequest request = SelectedMethod switch
        {
            UnlockMethod.Pin => new PinUnlockRequest(Pin.ReadUtf8()),
            UnlockMethod.Keyfile => new KeyfileUnlockRequest(_keyfileText is null ? KeyfilePath : null, _keyfileText, KeyfilePassword.ReadUtf8()),
            _ => new RecoveryUnlockRequest(RecoveryText),
        };
        var r = await _unlock.UnlockAsync(request);
        if (r.Ok)
        {
            Pin.Clear();
            KeyfilePassword.Clear();
            RecoveryText = string.Empty;
            return; // 画面は VaultSession の解錠通知で切り替わる
        }

        ErrorMessage = r.Error == ErrorCode.RetryLater
            ? L.T($"失敗が続いたため、{Math.Ceiling(r.RetryAfter?.TotalSeconds ?? 1)} 秒待ってから再試行してください。",
                  $"Too many failed attempts. Wait {Math.Ceiling(r.RetryAfter?.TotalSeconds ?? 1)} seconds and try again.")
            : UserMessages.For(r.Error) + (r.RetryAfter is { } wait
                ? L.T($"\n失敗が続いたため、次は {Math.Ceiling(wait.TotalSeconds)} 秒後から試せます。",
                      $"\nToo many failed attempts. You can try again in {Math.Ceiling(wait.TotalSeconds)} seconds.")
                : string.Empty);
        Pin.Clear();
        KeyfilePassword.Clear();
        FocusCurrent();
    }

    private void FocusCurrent()
    {
        if (SelectedMethod == UnlockMethod.Pin) Pin.Focus();
        else if (SelectedMethod == UnlockMethod.Keyfile) KeyfilePassword.Focus();
    }
}
