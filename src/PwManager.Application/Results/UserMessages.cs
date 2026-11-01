using PwManager.Application.Policies;
using PwManager.Core.Localization;

namespace PwManager.Application.Results;

/// <summary>
/// 失敗の種類から利用者向けの文言を作る（R-14: 文言の対応表はここ 1 か所）。
/// 文言の方針（docs/08 §8）: 短く、結果と次の行動を 1〜2 文で。内部用語（Vault・スロット・.bak1 などのファイル名）は使わず、
/// 「保管ファイル」「解錠方法」「自動バックアップ」と言う。日本語と英語を対で書く（§8.1）。
/// </summary>
public static class UserMessages
{
    public static string For(ErrorCode code, string? detail = null) => code switch
    {
        ErrorCode.None => string.Empty,
        ErrorCode.WrongPin => L.T("PIN が違います。（TPM の失敗回数が 1 回増えました。31 回でロックされ、10 分ごとに 1 回減ります）",
            "Wrong PIN. (The TPM failure count went up by one. It locks at 31, and one failure is forgiven every 10 minutes.)"),
        ErrorCode.WrongKey => L.T("解錠できませんでした。パスワードと鍵ファイルを確認してください。",
            "Could not unlock. Check the password and the key file."),
        ErrorCode.WrongRecoveryCode => L.T("復旧コードが違います。紙に書き写したコードを確認してください。",
            "Wrong recovery code. Check the code you wrote down on paper."),
        ErrorCode.RecoveryCodeMalformed => detail ?? L.T("復旧コードの形式が正しくありません（文字数・使用文字を確認してください）。",
            "The recovery code is not in the right format (check the number of characters and the characters used)."),
        ErrorCode.KeyfileNotFound => L.T("鍵ファイルが見つかりません。USB メモリが挿さっているか確認し、「選ぶ」で場所を指定してください。",
            "The key file was not found. Check that the USB drive is plugged in, then use Browse to choose its location."),
        ErrorCode.KeyfileMalformed => L.T("鍵ファイルではないか、壊れています。印刷した QR のテキストでも解錠できます。",
            "This is not a key file, or it is damaged. You can also unlock with the text of the printed QR code."),
        ErrorCode.KeyfileForOtherVault => L.T("この鍵ファイルは、この保管ファイル用ではありません（別の保管ファイル用に作られた鍵ファイルです）。",
            "This key file is not for this vault file (it was created for a different vault file)."),
        ErrorCode.KeyfileRevoked => L.T("この鍵ファイルは削除済み（無効）です。新しい鍵ファイルか、別の方法で解錠してください。",
            "This key file has been removed (disabled). Unlock with a new key file or another method."),
        ErrorCode.TpmLockedOut => L.T("PIN の失敗が続いたため、TPM が一時的にロックされています。時間をおくか、鍵ファイル／復旧コードで解錠してください。",
            "The TPM is temporarily locked after repeated PIN failures. Wait a while, or unlock with the key file or the recovery code."),
        ErrorCode.TpmUnavailable => L.T("TPM を利用できません。", "The TPM is not available. ")
            + (string.IsNullOrWhiteSpace(detail) ? string.Empty : L.T($"（{detail}）", $"({detail}) "))
            + L.T("鍵ファイル／復旧コードで解錠してください。", "Unlock with the key file or the recovery code."),
        ErrorCode.TpmNotRegisteredOnThisDevice => L.T("この PC の TPM は登録されていません（PC の買い替え・TPM のクリア後など）。鍵ファイルか復旧コードで開き、「鍵と設定」でこの PC の TPM を登録してください。",
            "This PC's TPM is not registered (for example after replacing the PC or resetting the TPM). Open with the key file or the recovery code, then register this PC's TPM in Keys and settings."),
        ErrorCode.MethodNotConfigured => L.T("この方法は登録されていません。", "This method is not set up."),
        ErrorCode.RetryLater => L.T("失敗が続いたため、少し待ってから再試行してください。", "Too many failed attempts. Wait a moment and try again."),
        ErrorCode.VaultNotFound => L.T("保管ファイル（パスワードのデータ）が見つかりません。", "The vault file (your password data) was not found."),
        ErrorCode.VaultFormat => L.T("保管ファイルを読み込めません（形式が違うか、壊れています）。", "Cannot read the vault file (wrong format or damaged). ")
            + (string.IsNullOrWhiteSpace(detail) ? string.Empty : L.T($"（{detail}）", $"({detail})")),
        ErrorCode.VaultTampered => L.T("保管ファイルの改ざんまたは破損を検知しました。アプリが自動で残しているバックアップから戻せる場合があります。操作手順書の「困ったとき」を参照してください。",
            "The vault file appears to be tampered with or damaged. You may be able to restore it from the backups the app keeps automatically. See Troubleshooting in the user manual."),
        ErrorCode.IoError => L.T("ファイルを読み書きできませんでした。", "Could not read or write the file. ")
            + (string.IsNullOrWhiteSpace(detail) ? string.Empty : L.T($"（{detail}）", $"({detail})")),
        ErrorCode.SaveConflict => L.T("開いた後に、別の場所（別の PC・同期など）でファイルが更新されています。上書きしないよう保存を中止し、変更を元に戻しました。ロックして開き直してください。",
            "The file was changed elsewhere (another PC, sync, etc.) after you opened it. To avoid overwriting it, the save was cancelled and your change was undone. Lock and open it again."),
        ErrorCode.VaultAlreadyExists => L.T("保存先に既に保管ファイルがあります。上書きしません。アプリを起動し直してください。",
            "A vault file already exists at the save location. It will not be overwritten. Restart the app."),
        ErrorCode.PinTooShort => L.T($"PIN は {CredentialPolicy.MinPinLength} 文字以上にしてください。", $"The PIN must be at least {CredentialPolicy.MinPinLength} characters."),
        ErrorCode.PasswordTooShort => L.T($"パスワードは {CredentialPolicy.MinPasswordLength} 文字以上にしてください。", $"The password must be at least {CredentialPolicy.MinPasswordLength} characters."),
        ErrorCode.ConfirmationMismatch => L.T("確認の入力が一致しません。", "The confirmation does not match."),
        ErrorCode.TitleOrUrlRequired => L.T("タイトルか URL を入力してください。", "Enter a title or a URL."),
        ErrorCode.KeyfileLocationSameDirectory => L.T("鍵ファイルを保管ファイルと同じフォルダには保存できません。USB メモリなど別の場所を選んでください。",
            "The key file cannot be saved in the same folder as the vault file. Choose another place, such as a USB drive."),
        ErrorCode.KeyfileInUse => L.T("この場所には使用中の鍵ファイルがあります。上書きすると、その鍵ファイルで開けなくなります。別の名前・場所を選んでください。",
            "A key file in use already exists here. Overwriting it would stop it from opening the vault. Choose another name or location."),
        ErrorCode.RecoveryVerifyMismatch => L.T("書き写した内容と一致しません。コードを表示して、もう一度確認してください。",
            "This does not match what you wrote down. Show the code and check again."),
        ErrorCode.SlotPolicyViolation => L.T("復旧コードと、ほかに 1 つ以上の解錠方法が必要です。この操作はできません。",
            "The recovery code and at least one other unlock method are required. This operation is not allowed."),
        ErrorCode.NotFound => L.T("対象が見つかりません（既に削除されています）。", "Not found (it has already been deleted)."),
        ErrorCode.ImportNotBrowserCsv => L.T("ブラウザのパスワード CSV ではありません（name / url / username / password の列が必要です）。",
            "This is not a browser password CSV (the name / url / username / password columns are required)."),
        ErrorCode.InvalidStep => L.T("前の手順が終わっていません。", "The previous step is not finished."),
        ErrorCode.Locked => L.T("ロックされています。", "Locked."),
        ErrorCode.Busy => L.T("別の処理を実行中です。終わってから操作してください。", "Another operation is in progress. Try again when it finishes."),
        _ => L.T("予期しないエラーが発生しました。ログフォルダに詳細を記録しました。", "An unexpected error occurred. Details were written to the log folder."),
    };
}
