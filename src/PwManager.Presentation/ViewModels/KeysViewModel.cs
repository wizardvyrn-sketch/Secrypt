using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwManager.Application.Policies;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Core.Localization;
using PwManager.Core.Storage;
using PwManager.Core.Vault;
using PwManager.Presentation.Ports;

namespace PwManager.Presentation.ViewModels;

/// <summary>スロット一覧の 1 行（表示用）。</summary>
public sealed class SlotRowViewModel(SlotInfo slot)
{
    public Guid Id { get; } = slot.Id;
    public SlotType Type { get; } = slot.Type;

    public string Icon => Type switch
    {
        SlotType.Tpm => "",      // チップ
        SlotType.Keyfile => "",  // USB
        SlotType.Recovery => "", // 紙
        SlotType.Fido2 => "",    // キー
        _ => "",
    };

    public string Label { get; } = string.IsNullOrWhiteSpace(slot.Label) ? slot.Type.ToString() : slot.Label;

    /// <summary>TPM のスロットが、この PC のものか（他の PC・クリア前の TPM なら false）。</summary>
    public bool? IsThisDevice { get; } = slot.IsThisDevice;

    public string Detail
    {
        get
        {
            var kind = Type switch
            {
                SlotType.Tpm => IsThisDevice switch
                {
                    true => L.T("この PC の TPM + PIN", "This PC's TPM + PIN"),
                    false => L.T("他の PC（またはクリア前）の TPM + PIN", "Another PC's TPM (or before a TPM reset) + PIN"),
                    _ => L.T("TPM + PIN（TPM を確認できません）", "TPM + PIN (the TPM cannot be checked)"),
                },
                SlotType.Keyfile => L.T("鍵ファイル + パスワード", "Key file + password"),
                SlotType.Recovery => L.T("復旧コード", "Recovery code"),
                SlotType.Fido2 => L.T("FIDO2 キー + パスワード", "FIDO2 key + password"),
                _ => Type.ToString(),
            };
            var verified = slot.LastVerified is { } v ? L.T($"最終使用 {v.ToLocalTime():yyyy-MM-dd}", $"Last used {v.ToLocalTime():yyyy-MM-dd}") : L.T("まだ使っていません", "Not used yet");
            return L.T($"{kind}  ·  登録 {slot.Created.ToLocalTime():yyyy-MM-dd}  ·  {verified}", $"{kind}  ·  Added {slot.Created.ToLocalTime():yyyy-MM-dd}  ·  {verified}");
        }
    }
}

/// <summary>鍵と設定（S-04）: 解錠方法の追加・削除と、自動ロック・クリップボードの設定、診断情報。</summary>
public sealed partial class KeysViewModel : ObservableObject
{
    private readonly SlotService _slots;
    private readonly VaultSession _session;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly DiagnosticsInfo _diagnostics;

    public KeysViewModel(SlotService slots, VaultSession session, SettingsService settings, IDialogService dialogs, IShellService shell, DiagnosticsInfo diagnostics)
    {
        _slots = slots;
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _diagnostics = diagnostics;
        var s = settings.Current;
        _autoLockText = s.AutoLockMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _clipboardText = s.ClipboardClearSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _verboseLogging = s.VerboseLogging;
        RefreshInfo();
    }

    [ObservableProperty] private IReadOnlyList<SlotRowViewModel> _slotRows = [];
    [ObservableProperty] private SlotRowViewModel? _selectedSlot;
    [ObservableProperty] private string _autoLockText;
    [ObservableProperty] private string _clipboardText;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private string _vaultInfoText = string.Empty;
    [ObservableProperty] private string _diagnosticsText = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>閉じるとき（設定を反映した後）。</summary>
    public event EventHandler? CloseRequested;

    public async Task LoadAsync()
    {
        SlotRows = (await _slots.GetSlotsAsync()).Select(s => new SlotRowViewModel(s)).ToList();
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        VaultInfoText = L.T($"保管ファイル: {_session.Location}\nVault ID: {_session.VaultId}  ·  保存回数 {_session.Counter}  ·  項目 {_session.Entries.Count} 件",
            $"Vault file: {_session.Location}\nVault ID: {_session.VaultId}  ·  Saves {_session.Counter}  ·  Entries {_session.Entries.Count}");
        DiagnosticsText = L.T($"版 {_diagnostics.Version}  ·  {_diagnostics.Runtime}\nログ: {_diagnostics.LogDirectory}",
            $"Version {_diagnostics.Version}  ·  {_diagnostics.Runtime}\nLogs: {_diagnostics.LogDirectory}");
    }

    [RelayCommand]
    private async Task AddKeyfileAsync()
    {
        ErrorMessage = null;
        var secrets = _dialogs.PromptSecrets(L.T("鍵ファイルを追加", "Add key file"),
            L.T("新しい鍵ファイルを作ります。USB メモリの紛失時の再発行や、2 本目の予備に使います。", "Creates a new key file. Use it to replace a lost USB drive or as a second backup."),
            [L.T($"パスワード（{CredentialPolicy.MinPasswordLength} 文字以上）", $"Password ({CredentialPolicy.MinPasswordLength} or more characters)"), L.T("パスワード（確認）", "Password (confirm)")],
            v => Message(CredentialPolicy.CheckNewPassword(v[0], v[1])));
        if (secrets is null) return;
        try
        {
            var path = _dialogs.PickSaveFile(L.T("鍵ファイルの保存先", "Where to save the key file"), "Secrypt.pwmk", SetupViewModel.KeyfileSaveFilter);
            if (path is null) return;
            var r = await _slots.AddKeyfileAsync(path, secrets[0], secrets[1]);
            if (!Report(r)) return;
            await LoadAsync();
            var risk = SetupViewModel.RiskText(_slots.AssessKeyfileLocation(path));
            if (_dialogs.Confirm(L.T("鍵ファイルを作成しました", "Key file created"),
                L.T($"保存先: {path}", $"Saved to: {path}") + (risk is null ? string.Empty : "\n" + risk) + L.T("\n予備の QR コードを印刷しますか？", "\nPrint a backup QR code?"),
                L.T("印刷する", "Print")))
                _dialogs.PrintKeyfileQr(r.Value!, _session.VaultId);
        }
        finally
        {
            foreach (var s in secrets) CryptographicOperations.ZeroMemory(s);
        }
    }

    [RelayCommand]
    private async Task RegisterTpmAsync()
    {
        ErrorMessage = null;
        var secrets = _dialogs.PromptSecrets(L.T("この PC の TPM を登録", "Register this PC's TPM"),
            L.T("この PC の TPM に鍵を封印し、PIN で開けるようにします。この PC の TPM の方法が既にある場合は置き換えます（PIN の変更）。" +
                "他の PC の TPM の方法は残ります（使わなくなった PC のものは一覧から削除してください）。",
                "Seals the key in this PC's TPM so that a PIN opens the vault. If this PC's TPM is already registered, it is replaced (PIN change). " +
                "Other PCs' TPM methods are kept (remove those of PCs you no longer use from the list).")
            + PinChangeNotes(),
            [L.T($"PIN（{CredentialPolicy.MinPinLength} 文字以上）", $"PIN ({CredentialPolicy.MinPinLength} or more characters)"), L.T("PIN（確認）", "PIN (confirm)")],
            v => Message(CredentialPolicy.CheckNewPin(v[0], v[1])));
        if (secrets is null) return;
        try
        {
            var r = await _slots.RegisterThisDeviceTpmAsync(secrets[0], secrets[1]);
            if (Report(r))
            {
                ReportRemainingBackups(r.Value);
                await LoadAsync();
            }
        }
        finally
        {
            foreach (var s in secrets) CryptographicOperations.ZeroMemory(s);
        }
    }

    [RelayCommand]
    private async Task RegenerateRecoveryAsync()
    {
        ErrorMessage = null;
        if (!_dialogs.Confirm(L.T("復旧コードを作り直す", "Regenerate recovery code"),
                L.T("新しい復旧コードを作り、古い復旧コードは使えなくします。新しいコードを紙に書き写す準備をしてください。", "Creates a new recovery code and disables the old one. Have paper ready to write down the new code.")
                + RevocationNotes(L.T("古い復旧コード", "the old recovery code")),
                L.T("作り直す", "Regenerate")))
            return;
        var prepared = await _slots.PrepareRecoveryCodeAsync();
        if (!Report(prepared)) return;
        using var pending = prepared.Value!;
        OpResult<BackupPurgeResult>? result = null;
        var shown = _dialogs.ShowRecoveryCode(L.T("新しい復旧コード", "New recovery code"),
            L.T("紙に書き写してください。古い紙は処分してください。", "Write it down on paper. Dispose of the old paper."), pending.DisplayCode,
            L.T($"確認: 左から {pending.CheckGroup + 1} 番目の 4 文字", $"Check: group {pending.CheckGroup + 1} from the left (4 characters)"),
            input =>
            {
                // 確認が合えばこの場で登録する（ダイアログを閉じる前に保存の失敗を表示できる）
                var t = _slots.CommitRecoveryCodeAsync(pending, input);
                result = t.IsCompleted ? t.Result : OpResult<BackupPurgeResult>.Fail(ErrorCode.Busy);
                return result.Ok ? null : UserMessages.For(result.Error, result.Detail);
            });
        if (shown && result is { Ok: true })
        {
            ReportRemainingBackups(result.Value);
            await LoadAsync();
        }
    }

    /// <summary>
    /// 解錠方法を削除・作り直すときの注意（docs/02 T4b）。保管ファイルの主鍵（VMK）は作り直さないため、
    /// 古い方法が載っている保管ファイルのコピーがあると、そのコピー経由で今の保管ファイルまで開けてしまう。
    /// PC 内の自動バックアップは削除するが、PC の外のコピーは消せないことを正直に伝える。
    /// </summary>
    private static string RevocationNotes(string oldMethod) => L.T(
        $"\n\n◆ {oldMethod}がまだ使える、PC 内の自動バックアップ（直近 5 回分）も削除します。これより前の状態には戻せなくなります。" +
        $"\n◆ PC の外にある保管ファイルのコピー（クラウド同期・ファイル履歴・外付けディスクへのバックアップなど）では、{oldMethod}でまだ開けます。" +
        "失くした・人に見られたおそれがある場合は、そうしたコピーも削除してください。",
        $"\n\n◆ Automatic backups on this PC (the last 5 saves) that {oldMethod} can still open will also be deleted. You will no longer be able to go back to earlier states." +
        $"\n◆ Copies of the vault file outside this PC (cloud sync, File History, backups on external drives, etc.) can still be opened with {oldMethod}. " +
        "If it may have been lost or seen by someone, delete those copies too.");

    /// <summary>PIN の変更（この PC の TPM を登録し直す）のときの注意。初めての登録では何も無効にしないので、変更のときだけ当てはまる。</summary>
    private static string PinChangeNotes() => L.T(
        "\n\n◆ すでにこの PC の TPM を登録している場合（PIN の変更）は、古い PIN で開ける PC 内の自動バックアップ（直近 5 回分）も削除します。これより前の状態には戻せなくなります。" +
        "\n◆ PC の外にある保管ファイルのコピー（クラウド同期・ファイル履歴・外付けディスクへのバックアップなど）では、古い PIN でまだ開けます。PIN を人に知られたおそれがある場合は、そうしたコピーも削除してください。",
        "\n\n◆ If this PC's TPM is already registered (a PIN change), automatic backups on this PC (the last 5 saves) that the old PIN can still open will also be deleted. You will no longer be able to go back to earlier states." +
        "\n◆ Copies of the vault file outside this PC (cloud sync, File History, backups on external drives, etc.) can still be opened with the old PIN. If someone may have learned the PIN, delete those copies too.");

    /// <summary>自動バックアップを削除できなかった場合は、手動での削除を案内する。</summary>
    private void ReportRemainingBackups(BackupPurgeResult? purge)
    {
        if (purge is not { Remaining.Count: > 0 }) return;
        _dialogs.Info(L.T("バックアップを削除できませんでした", "Could not delete backups"),
            L.T("次の自動バックアップを削除できませんでした。古い方法でまだ開けるため、アプリを閉じてから手動で削除してください。\n",
                "The following automatic backups could not be deleted. They can still be opened with the old method, so close the app and delete them manually.\n")
            + string.Join("\n", purge.Remaining));
    }

    [RelayCommand]
    private async Task RemoveSlotAsync()
    {
        ErrorMessage = null;
        if (SelectedSlot is not { } row) { ErrorMessage = L.T("削除する方法を一覧から選んでください。", "Select the method to remove from the list."); return; }
        if (!_dialogs.Confirm(L.T("削除の確認", "Confirm removal"),
                L.T($"「{row.Label}」で開けなくなります。紛失した鍵ファイルや、使わなくなった PC の TPM を無効にするときに使います。",
                    $"\"{row.Label}\" will no longer open the vault. Use this to disable a lost key file or the TPM of a PC you no longer use.")
                + RevocationNotes(L.T("削除した方法", "the removed method")),
                L.T("削除する", "Remove"))) return;
        var r = await _slots.RemoveAsync(row.Id);
        if (!Report(r)) return;
        ReportRemainingBackups(r.Value);
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_diagnostics.LogDirectory);

    [RelayCommand]
    private void Close()
    {
        _settings.Update(s =>
        {
            if (int.TryParse(AutoLockText, out var m) && m is >= AppSettings.MinAutoLockMinutes and <= AppSettings.MaxAutoLockMinutes) s.AutoLockMinutes = m;
            if (int.TryParse(ClipboardText, out var c) && c is >= AppSettings.MinClipboardSeconds and <= AppSettings.MaxClipboardSeconds) s.ClipboardClearSeconds = c;
            s.VerboseLogging = VerboseLogging;
        });
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string? Message(OpResult r) => r.Ok ? null : UserMessages.For(r.Error, r.Detail);

    private bool Report(OpResult r)
    {
        if (r.Ok) return true;
        ErrorMessage = UserMessages.For(r.Error, r.Detail);
        return false;
    }

    private bool Report<T>(OpResult<T> r) => Report((OpResult)r);
}

/// <summary>診断情報（版・ログの場所）。秘密を含まない。</summary>
public sealed record DiagnosticsInfo(string Version, string Runtime, string LogDirectory);
