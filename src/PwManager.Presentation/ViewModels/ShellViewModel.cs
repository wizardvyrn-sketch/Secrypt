using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwManager.Application.Policies;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Core.Localization;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>
/// メインウィンドウ（タイトル帯・画面切り替え・処理中の覆い・ロック）。画面は <see cref="CurrentPage"/> の型で決まる。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly VaultSession _session;
    private readonly AutoLockMonitor _autoLock;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly Func<SetupViewModel> _newSetup;
    private readonly Func<UnlockViewModel> _newUnlock;
    private readonly Func<VaultViewModel> _newVault;
    private bool _closePending;
    private Func<string> _status = StatusLocked;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private bool _isUnlocked;

    [ObservableProperty]
    private string _statusText = StatusLocked();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    public ShellViewModel(VaultSession session, AutoLockMonitor autoLock, SettingsService settings, IDialogService dialogs,
        Func<SetupViewModel> newSetup, Func<UnlockViewModel> newUnlock, Func<VaultViewModel> newVault)
    {
        _session = session;
        _autoLock = autoLock;
        _settings = settings;
        _dialogs = dialogs;
        _newSetup = newSetup;
        _newUnlock = newUnlock;
        _newVault = newVault;
        _autoLock.IdleTimeout = TimeSpan.FromMinutes(_settings.Current.AutoLockMinutes);
        _settings.Changed += (_, _) => _autoLock.IdleTimeout = TimeSpan.FromMinutes(_settings.Current.AutoLockMinutes);
        _session.StateChanged += OnStateChanged;
        _session.BusyChanged += OnBusyChanged;
        L.Changed += (_, _) => OnLanguageChanged();
    }

    /// <summary>
    /// 表示言語が変わった（初期設定の最初の手順か解錠画面で切り替えた）。文言を作り直すため、その画面を作り直す。
    /// 初期設定は最初の手順でだけ作り直す（途中まで進んだ内容を捨てないため。切り替えもそこでしか出さない）。
    /// </summary>
    private void OnLanguageChanged()
    {
        StatusText = _status();
        if (CurrentPage is UnlockViewModel) ShowUnlock();
        else if (CurrentPage is SetupViewModel { IsTpmStep: true }) ShowSetup();
    }

    /// <summary>処理が終わったらウィンドウを閉じる（処理中に閉じるボタンが押された）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>起動時: Vault があれば解錠画面、なければ初期設定。</summary>
    public void Start()
    {
        if (_session.Store.Exists()) ShowUnlock();
        else ShowSetup();
    }

    /// <summary>利用者の操作があった（キー・マウス）。無操作タイマーを数え直す。</summary>
    public void NotifyActivity() => _autoLock.NotifyActivity();

    /// <summary>タイマーから定期的に呼ぶ（無操作の自動ロック）。</summary>
    public void Tick() => _autoLock.Check();

    [RelayCommand]
    private void Lock() => RequestLock(LockReason.Manual);

    /// <summary>
    /// ロックを要求する。手動・終了では未保存の編集を確認し（「戻る」なら false）、自動ロックでは破棄する（R-03）。
    /// 処理中なら完了後にロックする（R-02）。
    /// </summary>
    public bool RequestLock(LockReason reason)
    {
        if (!_session.IsUnlocked) return true;
        if (reason is LockReason.Manual or LockReason.Exit && CurrentPage is VaultViewModel vault && !vault.ConfirmLeave())
            return false;
        return _session.RequestLock(reason);
    }

    /// <summary>ウィンドウを閉じてよいか。処理中は完了を待ってから閉じる。</summary>
    public bool CanClose()
    {
        if (_session.IsBusy)
        {
            _closePending = true;
            return false;
        }
        if (CurrentPage is SetupViewModel setup) setup.Abandon();
        return RequestLock(LockReason.Exit);
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.State == SessionState.Unlocked)
        {
            ShowVault();
        }
        else
        {
            _dialogs.CloseAll();
            (CurrentPage as VaultViewModel)?.OnLocked(e.Reason);
            ShowUnlock();
        }
    }

    private void OnBusyChanged(object? sender, EventArgs e)
    {
        var op = _session.BusyOperation;
        BusyText = op is null ? null : BusyMessage(op);
        IsBusy = BusyText is not null;
        if (op is null && _closePending)
        {
            _closePending = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>処理中の覆いに出す文言。短い処理（項目の保存など）は覆いを出さない（ちらつき防止）。</summary>
    internal static string? BusyMessage(string operation) => operation switch
    {
        "Unlock" => L.T("解錠しています…（数秒かかります）", "Unlocking… (this takes a few seconds)"),
        "Setup.Tpm" or "Slot.RegisterTpm" => L.T("TPM に鍵を封印しています…", "Sealing the key in the TPM…"),
        "Setup.Keyfile" or "Slot.AddKeyfile" => L.T("鍵ファイルを作成しています…（数秒かかります）", "Creating the key file… (this takes a few seconds)"),
        "Setup.Recovery" or "Slot.PrepareRecovery" => L.T("復旧コードを作成しています…", "Creating the recovery code…"),
        "Setup.Finish" => L.T("保存しています…", "Saving…"),
        _ => null,
    };

    private static string StatusSetup() => L.T("初期設定", "Initial setup");
    private static string StatusLocked() => L.T("ロック中", "Locked");
    private static string StatusUnlocked() => L.T("解錠中", "Unlocked");

    private void ShowSetup() => Navigate(_newSetup(), StatusSetup);

    private void ShowUnlock()
    {
        var vm = _newUnlock();
        Navigate(vm, StatusLocked);
        _ = vm.InitializeAsync();
    }

    private void ShowVault()
    {
        _autoLock.NotifyActivity();
        Navigate(_newVault(), StatusUnlocked);
    }

    private void Navigate(object page, Func<string> status)
    {
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
        _status = status;
        StatusText = status();
        IsUnlocked = _session.IsUnlocked;
    }
}
