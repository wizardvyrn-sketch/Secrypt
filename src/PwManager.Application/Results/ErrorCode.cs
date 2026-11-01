namespace PwManager.Application.Results;

/// <summary>
/// 想定内の失敗の種類（R-14）。画面の文言は <see cref="UserMessages"/> で 1 か所にまとめて対応づける。
/// 値はログ（理由コード）にも出るため、意味を変えないこと。
/// </summary>
public enum ErrorCode
{
    None = 0,

    // ---- 解錠 ----
    /// <summary>TPM の PIN が違う（TPM の失敗回数が 1 増えた）。</summary>
    WrongPin,
    /// <summary>パスワードまたは鍵ファイルが違う。</summary>
    WrongKey,
    /// <summary>復旧コードが違う（形式は正しいが、どの復旧スロットでも開けない）。</summary>
    WrongRecoveryCode,
    /// <summary>復旧コードの形式が違う・書き間違い（チェックサム不一致）。</summary>
    RecoveryCodeMalformed,
    /// <summary>鍵ファイルが見つからない（USB メモリが挿さっていない等）。</summary>
    KeyfileNotFound,
    /// <summary>鍵ファイルではない・壊れている。</summary>
    KeyfileMalformed,
    /// <summary>別の Vault 用の鍵ファイル。</summary>
    KeyfileForOtherVault,
    /// <summary>この Vault 用だが、スロットが削除済み（失効した鍵ファイル）。</summary>
    KeyfileRevoked,
    /// <summary>TPM が総当たり対策でロック中。</summary>
    TpmLockedOut,
    /// <summary>TPM が使えない（未搭載・無効・接続不可）。</summary>
    TpmUnavailable,
    /// <summary>この PC の TPM で登録した解錠方法がない（PC 買い替え・TPM クリア後・別の PC）。</summary>
    TpmNotRegisteredOnThisDevice,
    /// <summary>この方法の解錠スロットがない。</summary>
    MethodNotConfigured,
    /// <summary>失敗が続いたため待ち時間中。</summary>
    RetryLater,

    // ---- ファイル ----
    VaultNotFound,
    /// <summary>形式が違う・壊れている・未対応のバージョン。</summary>
    VaultFormat,
    /// <summary>改ざん・破損を検知した（MAC / AEAD の検証失敗）。</summary>
    VaultTampered,
    /// <summary>読み書きできない（ディスク満杯・権限・USB の取り外しなど）。</summary>
    IoError,
    /// <summary>開いた後に別の場所（別の PC・同期）でファイルが更新されていた。上書きしていない。</summary>
    SaveConflict,
    /// <summary>新規作成しようとしたが、既にファイルがある。</summary>
    VaultAlreadyExists,

    // ---- 入力 ----
    PinTooShort,
    PasswordTooShort,
    ConfirmationMismatch,
    TitleOrUrlRequired,
    /// <summary>鍵ファイルの保存先が Vault と同じフォルダ。</summary>
    KeyfileLocationSameDirectory,
    /// <summary>この Vault で使用中の鍵ファイルを上書きしようとした。</summary>
    KeyfileInUse,
    /// <summary>復旧コードの書き写し確認が一致しない。</summary>
    RecoveryVerifyMismatch,
    /// <summary>「復旧コード＋他 1 つ以上」を満たさなくなる。</summary>
    SlotPolicyViolation,
    /// <summary>項目・スロットが見つからない（別の操作で削除済み）。</summary>
    NotFound,
    /// <summary>ブラウザのパスワード CSV ではない。</summary>
    ImportNotBrowserCsv,
    /// <summary>前の手順が終わっていない（初期設定の順序違い等）。</summary>
    InvalidStep,

    // ---- 状態 ----
    /// <summary>ロックされている。</summary>
    Locked,
    /// <summary>別の処理の実行中。</summary>
    Busy,
    /// <summary>想定外のエラー（ログに詳細を記録）。</summary>
    Unexpected,
}
