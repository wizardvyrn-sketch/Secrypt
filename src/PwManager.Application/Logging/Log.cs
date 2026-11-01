using Microsoft.Extensions.Logging;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Vault;

namespace PwManager.Application.Logging;

/// <summary>
/// ログのイベント一覧（docs/10 §4）。ログはここに定義した型付きメソッドだけで出す（任意の文字列を渡さない）。
/// 秘密（パスワード・PIN・復旧コード・鍵ファイルの中身・鍵）と利用者データ（タイトル・ユーザー名・URL 等）は
/// 引数にしないこと。識別子・件数・種別・所要時間・理由コードのみ。パスは PathRedactor で伏せてから渡す。
/// </summary>
public static partial class Log
{
    // ---- 1xxx アプリ ----
    [LoggerMessage(1001, LogLevel.Information, "AppStarted version={Version} os={Os} runtime={Runtime}")]
    public static partial void AppStarted(ILogger logger, string version, string os, string runtime);

    [LoggerMessage(1002, LogLevel.Information, "AppExiting")]
    public static partial void AppExiting(ILogger logger);

    [LoggerMessage(1003, LogLevel.Warning, "SecondInstanceBlocked")]
    public static partial void SecondInstanceBlocked(ILogger logger);

    [LoggerMessage(1004, LogLevel.Information, "VaultLocation path={Path} exists={Exists}")]
    public static partial void VaultLocation(ILogger logger, string path, bool exists);

    [LoggerMessage(1090, LogLevel.Critical, "UnhandledException source={Source} type={ExceptionType}")]
    public static partial void UnhandledException(ILogger logger, string source, string exceptionType, Exception exception);

    // ---- 2xxx 解錠・ロック ----
    [LoggerMessage(2001, LogLevel.Information, "UnlockStarted method={Method}")]
    public static partial void UnlockStarted(ILogger logger, string method);

    [LoggerMessage(2101, LogLevel.Information, "UnlockSucceeded method={Method} slot={SlotId} elapsedMs={ElapsedMs} counter={Counter}")]
    public static partial void UnlockSucceeded(ILogger logger, string method, Guid slotId, long elapsedMs, ulong counter);

    [LoggerMessage(2201, LogLevel.Warning, "UnlockFailed method={Method} reason={Reason} elapsedMs={ElapsedMs} consecutive={Consecutive}")]
    public static partial void UnlockFailed(ILogger logger, string method, ErrorCode reason, long elapsedMs, int consecutive);

    [LoggerMessage(2202, LogLevel.Error, "UnlockError method={Method} type={ExceptionType}")]
    public static partial void UnlockError(ILogger logger, string method, string exceptionType, Exception exception);

    [LoggerMessage(2301, LogLevel.Information, "Locked reason={Reason}")]
    public static partial void Locked(ILogger logger, LockReason reason);

    [LoggerMessage(2302, LogLevel.Information, "AutoLockDeferred reason={Reason} operation={Operation}")]
    public static partial void AutoLockDeferred(ILogger logger, LockReason reason, string operation);

    [LoggerMessage(2303, LogLevel.Information, "SlotUseRecorded slot={SlotId}")]
    public static partial void SlotUseRecorded(ILogger logger, Guid slotId);

    // ---- 3xxx スロット・初期設定 ----
    [LoggerMessage(3001, LogLevel.Information, "SlotEnrolled type={Type} slot={SlotId}")]
    public static partial void SlotEnrolled(ILogger logger, SlotType type, Guid slotId);

    [LoggerMessage(3002, LogLevel.Information, "SlotRemoved type={Type} slot={SlotId}")]
    public static partial void SlotRemoved(ILogger logger, SlotType type, Guid slotId);

    [LoggerMessage(3003, LogLevel.Information, "SlotReplaced type={Type} old={OldSlotId} new={NewSlotId}")]
    public static partial void SlotReplaced(ILogger logger, SlotType type, Guid oldSlotId, Guid newSlotId);

    [LoggerMessage(3101, LogLevel.Warning, "SlotPolicyRejected operation={Operation}")]
    public static partial void SlotPolicyRejected(ILogger logger, string operation);

    [LoggerMessage(3201, LogLevel.Information, "SetupCompleted vault={VaultId} slots={SlotCount}")]
    public static partial void SetupCompleted(ILogger logger, Guid vaultId, int slotCount);

    [LoggerMessage(3202, LogLevel.Information, "SetupAbandoned step={Step}")]
    public static partial void SetupAbandoned(ILogger logger, string step);

    [LoggerMessage(3203, LogLevel.Warning, "KeyfileCleanupFailed type={ExceptionType}")]
    public static partial void KeyfileCleanupFailed(ILogger logger, string exceptionType);

    // ---- 4xxx 項目 ----
    [LoggerMessage(4001, LogLevel.Information, "EntryCreated entry={EntryId}")]
    public static partial void EntryCreated(ILogger logger, Guid entryId);

    [LoggerMessage(4002, LogLevel.Information, "EntryUpdated entry={EntryId}")]
    public static partial void EntryUpdated(ILogger logger, Guid entryId);

    [LoggerMessage(4003, LogLevel.Information, "EntryDeleted entry={EntryId}")]
    public static partial void EntryDeleted(ILogger logger, Guid entryId);

    [LoggerMessage(4101, LogLevel.Information, "UnsavedChangesDiscarded entry={EntryId} reason={Reason}")]
    public static partial void UnsavedChangesDiscarded(ILogger logger, Guid entryId, string reason);

    // ---- 5xxx 保存 ----
    [LoggerMessage(5001, LogLevel.Information, "SaveCompleted operation={Operation} counter={Counter} size={Size} elapsedMs={ElapsedMs} backup={RotatedBackups}")]
    public static partial void SaveCompleted(ILogger logger, string operation, ulong counter, int size, long elapsedMs, bool rotatedBackups);

    [LoggerMessage(5101, LogLevel.Error, "SaveFailed operation={Operation} type={ExceptionType} rolledBack={RolledBack}")]
    public static partial void SaveFailed(ILogger logger, string operation, string exceptionType, bool rolledBack, Exception exception);

    [LoggerMessage(5102, LogLevel.Warning, "SaveConflict operation={Operation} expected={ExpectedCounter}")]
    public static partial void SaveConflict(ILogger logger, string operation, ulong? expectedCounter);

    [LoggerMessage(5201, LogLevel.Debug, "BackupRotated counter={Counter}")]
    public static partial void BackupRotated(ILogger logger, ulong counter);

    [LoggerMessage(5202, LogLevel.Information, "BackupsPurged operation={Operation} deleted={Deleted} failed={Failed}")]
    public static partial void BackupsPurged(ILogger logger, string operation, int deleted, int failed);

    // ---- 6xxx TPM ----
    [LoggerMessage(6001, LogLevel.Information, "TpmAvailable")]
    public static partial void TpmAvailable(ILogger logger);

    [LoggerMessage(6101, LogLevel.Warning, "TpmUnavailable reason={Reason}")]
    public static partial void TpmUnavailable(ILogger logger, string reason);

    [LoggerMessage(6102, LogLevel.Information, "TpmSlotsOnThisDevice total={Total} thisDevice={ThisDevice}")]
    public static partial void TpmSlotsOnThisDevice(ILogger logger, int total, int thisDevice);

    // ---- 7xxx 取り込み ----
    [LoggerMessage(7001, LogLevel.Information, "ImportPreviewed rows={Rows} fresh={Fresh} existing={Existing} duplicateInFile={DuplicateInFile} skipped={Skipped}")]
    public static partial void ImportPreviewed(ILogger logger, int rows, int fresh, int existing, int duplicateInFile, int skipped);

    [LoggerMessage(7002, LogLevel.Information, "ImportCommitted count={Count}")]
    public static partial void ImportCommitted(ILogger logger, int count);

    [LoggerMessage(7003, LogLevel.Information, "ImportSourceDeleted success={Success}")]
    public static partial void ImportSourceDeleted(ILogger logger, bool success);

    [LoggerMessage(7101, LogLevel.Warning, "ImportFailed reason={Reason}")]
    public static partial void ImportFailed(ILogger logger, ErrorCode reason);

    // ---- 8xxx 設定 ----
    [LoggerMessage(8001, LogLevel.Information, "SettingsChanged name={Name} value={Value}")]
    public static partial void SettingsChanged(ILogger logger, string name, int value);

    [LoggerMessage(8101, LogLevel.Warning, "SettingsLoadFailed type={ExceptionType}")]
    public static partial void SettingsLoadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(8102, LogLevel.Warning, "SettingsSaveFailed type={ExceptionType}")]
    public static partial void SettingsSaveFailed(ILogger logger, string exceptionType);
}
