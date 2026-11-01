using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Core.Localization;
using PwManager.Core.Storage;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>初期設定（S-01）: STEP 1 TPM + PIN → STEP 2 鍵ファイル + パスワード → STEP 3 復旧コード。</summary>
public sealed partial class SetupViewModel : ObservableObject, IDisposable
{
    public static string KeyfileSaveFilter => L.T("鍵ファイル (*.pwmk)|*.pwmk", "Key file (*.pwmk)|*.pwmk");

    private readonly SetupService _setup;
    private readonly IDialogService _dialogs;
    private readonly SetupFlow _flow;
    private string? _keyfileText;
    private RecoveryChallenge? _challenge;

    public SetupViewModel(SetupService setup, IDialogService dialogs, LanguageViewModel language)
    {
        _setup = setup;
        _dialogs = dialogs;
        Language = language;
        _flow = setup.Begin();
        VaultPathText = L.T($"保存先: {_flow.VaultLocation}", $"Saved to: {_flow.VaultLocation}");
    }

    /// <summary>表示言語の切り替え（最初の手順でだけ表示）。</summary>
    public LanguageViewModel Language { get; }

    public SecretInput Pin { get; } = new();
    public SecretInput PinConfirm { get; } = new();
    public SecretInput KeyfilePassword { get; } = new();
    public SecretInput KeyfilePasswordConfirm { get; } = new();

    public string VaultPathText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText), nameof(StepNumber), nameof(IsTpmStep), nameof(IsKeyfileStep), nameof(IsRecoveryStep), nameof(NextText))]
    private SetupStep _step = SetupStep.Tpm;

    [ObservableProperty] private bool _tpmChecked;
    [ObservableProperty] private bool _tpmAvailable;
    [ObservableProperty] private string _tpmStatusText = L.T("TPM を確認しています…", "Checking the TPM…");

    [ObservableProperty] private string _keyfilePath = string.Empty;
    [ObservableProperty] private string? _keyfileRiskText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextText))]
    private bool _keyfileCreated;

    [ObservableProperty] private string? _keyfileDoneText;
    [ObservableProperty] private bool _keyfileStored;

    [ObservableProperty] private string _recoveryCodeText = string.Empty;
    [ObservableProperty] private bool _hideCode;
    [ObservableProperty] private string _verifyALabel = string.Empty;
    [ObservableProperty] private string _verifyBLabel = string.Empty;
    [ObservableProperty] private string _verifyA = string.Empty;
    [ObservableProperty] private string _verifyB = string.Empty;

    [ObservableProperty] private string? _errorMessage;

    public int StepNumber => Math.Min((int)Step, 3);
    public string StepText => $"STEP {StepNumber} / 3";
    public bool IsTpmStep => Step == SetupStep.Tpm;
    public bool IsKeyfileStep => Step == SetupStep.Keyfile;
    public bool IsRecoveryStep => Step == SetupStep.Recovery;
    public string NextText => Step switch
    {
        SetupStep.Tpm => L.T("次へ", "Next"),
        SetupStep.Keyfile => KeyfileCreated ? L.T("次へ", "Next") : L.T("鍵ファイルを作成", "Create key file"),
        _ => L.T("設定を完了する", "Finish setup"),
    };

    /// <summary>画面表示時に TPM の利用可否を調べる。</summary>
    public async Task InitializeAsync()
    {
        var status = await _setup.CheckTpmAsync();
        TpmAvailable = status.Available;
        TpmChecked = true;
        TpmStatusText = status.Available
            ? L.T("● TPM を利用できます。", "● The TPM is available.")
            : L.T($"◆ TPM を利用できません: {status.Reason}\n「TPM を使わずに進む」で、鍵ファイルと復旧コードだけで設定できます。",
                  $"◆ The TPM is not available: {status.Reason}\nChoose \"Continue without TPM\" to set up with only a key file and a recovery code.");
        if (status.Available) Pin.Focus();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        ErrorMessage = null;
        switch (Step)
        {
            case SetupStep.Tpm: await EnrollTpmAsync(); break;
            case SetupStep.Keyfile: await KeyfileStepAsync(); break;
            case SetupStep.Recovery: await FinishAsync(); break;
        }
    }

    private async Task EnrollTpmAsync()
    {
        if (!TpmAvailable)
        {
            ErrorMessage = L.T("TPM を利用できません。「TPM を使わずに進む」を選んでください。", "The TPM is not available. Choose \"Continue without TPM\".");
            return;
        }
        var r = await _flow.EnrollTpmAsync(Pin.ReadUtf8(), PinConfirm.ReadUtf8());
        if (!Report(r)) return;
        Pin.Clear();
        PinConfirm.Clear();
        Step = _flow.Step;
        KeyfilePassword.Focus();
    }

    [RelayCommand]
    private void SkipTpm()
    {
        if (TpmAvailable && !_dialogs.Confirm(L.T("確認", "Confirm"),
                L.T("TPM を使わない場合、毎回 USB メモリの鍵ファイルとパスワードで開くことになります。よろしいですか？（後から「鍵と設定」で TPM を追加できます）",
                    "Without the TPM, you will open the vault with the key file on the USB drive and the password every time. Continue? (You can add the TPM later in \"Keys and settings\".)"),
                L.T("TPM を使わずに進む", "Continue without TPM")))
            return;
        if (Report(_flow.SkipTpm())) Step = _flow.Step;
    }

    [RelayCommand]
    private void ChooseKeyfile()
    {
        var path = _dialogs.PickSaveFile(L.T("鍵ファイルの保存先（USB メモリを推奨）", "Where to save the key file (a USB drive is recommended)"), "Secrypt.pwmk", KeyfileSaveFilter);
        if (path is null) return;
        KeyfilePath = path;
        KeyfileRiskText = RiskText(_flow.AssessKeyfileLocation(path));
    }

    internal static string? RiskText(LocationRisk risk) => risk switch
    {
        LocationRisk.SameDirectory => L.T("◆ 保管ファイル（パスワードのデータ）と同じフォルダです。一緒に流出すると守りが弱くなります。USB メモリなど別の場所を選んでください。",
            "◆ This is the same folder as the vault file (your password data). If both leak together, protection is weaker. Choose another place, such as a USB drive."),
        LocationRisk.SameSyncFolder => L.T("◆ 保管ファイルと同じクラウド同期フォルダです。別の場所（USB メモリ）を推奨します。",
            "◆ This is the same cloud-synced folder as the vault file. Another place (a USB drive) is recommended."),
        LocationRisk.SameDrive => L.T("◆ PC 内のドライブです。USB メモリなど取り外せる場所を推奨します（このまま進むこともできます）。",
            "◆ This is an internal drive. A removable place such as a USB drive is recommended (you can still continue)."),
        _ => null,
    };

    private async Task KeyfileStepAsync()
    {
        if (KeyfileCreated)
        {
            if (!KeyfileStored)
            {
                ErrorMessage = L.T("鍵ファイルを保存したことを確認してチェックを入れてください。", "Make sure the key file is saved, then tick the check box.");
                return;
            }
            var challenge = await _flow.CreateRecoveryCodeAsync();
            if (!Report(challenge)) return;
            _challenge = challenge.Value!;
            _keyfileText = null; // 以後は不要
            RecoveryCodeText = _challenge.DisplayCode.Replace("-", "  ", StringComparison.Ordinal);
            VerifyALabel = GroupLabel(_challenge.GroupA);
            VerifyBLabel = GroupLabel(_challenge.GroupB);
            Step = _flow.Step;
            return;
        }

        if (string.IsNullOrWhiteSpace(KeyfilePath))
        {
            ErrorMessage = L.T("鍵ファイルの保存先を選んでください。", "Choose where to save the key file.");
            return;
        }
        var r = await _flow.CreateKeyfileAsync(KeyfilePath, KeyfilePassword.ReadUtf8(), KeyfilePasswordConfirm.ReadUtf8());
        if (!Report(r)) return;
        _keyfileText = r.Value;
        KeyfilePassword.Clear();
        KeyfilePasswordConfirm.Clear();
        KeyfileCreated = true;
        KeyfileDoneText = L.T($"● 鍵ファイルを作成しました: {KeyfilePath}", $"● Key file created: {KeyfilePath}");
    }

    private static string GroupLabel(int group)
        => L.T($"左から {group + 1} 番目の 4 文字", $"Group {group + 1} from the left (4 characters)");

    [RelayCommand]
    private void PrintQr()
    {
        if (_keyfileText is null) return;
        try
        {
            if (_dialogs.PrintKeyfileQr(_keyfileText, _flow.VaultId)) KeyfileStored = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = L.T($"印刷できませんでした: {ex.Message}", $"Could not print: {ex.Message}");
        }
    }

    private async Task FinishAsync()
    {
        var r = await _flow.FinishAsync(VerifyA, VerifyB);
        if (!Report(r))
        {
            if (r.Error == ErrorCode.RecoveryVerifyMismatch) HideCode = false;
            return;
        }
        RecoveryCodeText = string.Empty;
        _challenge = null;
        Step = SetupStep.Done; // 画面は VaultSession の解錠通知で切り替わる
    }

    /// <summary>完了せずに離れる（ウィンドウを閉じた）。</summary>
    public void Abandon()
    {
        _flow.Dispose();
        _keyfileText = null;
        RecoveryCodeText = string.Empty;
    }

    public void Dispose() => Abandon();

    private bool Report(OpResult r)
    {
        if (r.Ok) return true;
        ErrorMessage = UserMessages.For(r.Error, r.Detail);
        return false;
    }

    private bool Report<T>(OpResult<T> r) => Report((OpResult)r);
}
