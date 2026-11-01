using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Policies;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Crypto;
using PwManager.Core.Localization;
using PwManager.Core.Slots;
using PwManager.Core.Storage;
using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>作り直し中の復旧コード。確認が済むまで古いコードは有効のまま。Dispose で破棄する。</summary>
public sealed class PendingRecoveryCode : IDisposable
{
    internal PendingRecoveryCode(KeySlot slot, RecoveryCode code, SecretBuffer kek, int checkGroup)
    {
        Slot = slot;
        Code = code;
        Kek = kek;
        CheckGroup = checkGroup;
        DisplayCode = code.ToDisplayString();
    }

    internal KeySlot Slot { get; }
    internal RecoveryCode Code { get; }
    internal SecretBuffer Kek { get; }

    /// <summary>表示する新しい復旧コード。</summary>
    public string DisplayCode { get; private set; }

    /// <summary>書き写し確認で入力してもらう組（0 始まり）。</summary>
    public int CheckGroup { get; }

    public bool IsDisposed => Kek.IsDisposed;

    public void Dispose()
    {
        Code.Dispose();
        Kek.Dispose();
        DisplayCode = string.Empty;
    }
}

/// <summary>
/// 解錠方法（スロット）の管理（S-04）。鍵ファイル追加・この PC の TPM 登録・復旧コード作り直し・削除。
/// どの変更も保存に失敗したら元に戻る。
/// </summary>
public sealed class SlotService(VaultSession session, ITpmSealer tpm, IKeyfileStore keyfiles, VaultOptions? options = null, ILogger<SlotService>? logger = null)
{
    private readonly VaultOptions _options = options ?? new VaultOptions();
    private readonly ILogger _log = logger ?? NullLogger<SlotService>.Instance;

    /// <summary>スロット一覧。TPM のスロットは「この PC のものか」を TPM に問い合わせる（背景で実行）。</summary>
    public async Task<IReadOnlyList<SlotInfo>> GetSlotsAsync()
    {
        var slots = session.Slots.ToList();
        var thisDevice = await Task.Run(() =>
        {
            var map = new Dictionary<Guid, bool?>();
            foreach (var s in slots.Where(s => s.Type == SlotType.Tpm))
            {
                try { map[s.Id] = tpm.IsSealedOnThisDevice(s.TypeParams); }
                catch (TpmUnavailableException) { map[s.Id] = null; }
            }
            return map;
        });
        return slots
            .OrderBy(s => s.Type)
            .ThenBy(s => s.Created)
            .Select(s => new SlotInfo(s.Id, s.Type, s.Label, s.Created, s.LastVerified, thisDevice.GetValueOrDefault(s.Id)))
            .ToList();
    }

    public LocationRisk AssessKeyfileLocation(string keyfilePath) => VaultStorage.AssessKeyfileLocation(session.Location, keyfilePath);

    /// <summary>
    /// 鍵ファイルを追加する（USB 紛失時の再発行・2 本目）。戻り値は QR 印刷用のテキスト。
    /// 使用中の鍵ファイルへの上書きは拒否する（保存に失敗したときに、使える鍵ファイルを失わないように）。
    /// </summary>
    public async Task<OpResult<string>> AddKeyfileAsync(string path, byte[] password, byte[] confirm, string? label = null)
    {
        try
        {
            var check = CredentialPolicy.CheckNewPassword(password, confirm);
            if (!check.Ok) return OpResult<string>.Fail(check.Error);
            if (string.IsNullOrWhiteSpace(path)) return OpResult<string>.Fail(ErrorCode.KeyfileNotFound);
            if (AssessKeyfileLocation(path) == LocationRisk.SameDirectory) return OpResult<string>.Fail(ErrorCode.KeyfileLocationSameDirectory);

            return await session.RunExclusiveAsync<string>("Slot.AddKeyfile", async ctx =>
            {
                if (IsKeyfileInUse(ctx.Vault, path)) return OpResult<string>.Fail(ErrorCode.KeyfileInUse);

                var vaultId = ctx.Vault.VaultId;
                var created = TimeZoneInfo.ConvertTime(ctx.Time.GetUtcNow(), ctx.Time.LocalTimeZone);
        var slotLabel = label ?? L.T($"鍵ファイル（{created:yyyy-MM-dd}）", $"Key file ({created:yyyy-MM-dd})");
                var (slot, keyfile, kek) = await Task.Run(() => KeyfileSlot.Enroll(vaultId, slotLabel, password, _options.PasswordKdf));
                using (kek)
                using (keyfile)
                {
                    var write = KeyfileWriter.Write(keyfiles, path, keyfile);
                    if (!write.Ok) return OpResult<string>.Fail(write.Error, write.Detail);

                    var r = ctx.Commit(v =>
                    {
                        v.AddSlot(slot, kek);
                        return OpResult.Success;
                    });
                    if (!r.Ok)
                    {
                        // 登録できなかった鍵ファイルは使えないので消す（残すと「この鍵ファイルは登録されていません」の元になる）
                        TryDelete(path);
                        return OpResult<string>.Fail(r.Error, r.Detail);
                    }
                    Log.SlotEnrolled(_log, slot.Type, slot.Id);
                    return OpResult<string>.Success(keyfile.ToText());
                }
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(confirm);
        }
    }

    /// <summary>
    /// この PC の TPM を登録する。この PC の TPM で作った既存のスロットは置き換える（PIN の変更）。
    /// 他の PC・クリア前の TPM のスロットは残す（docs/03: PC ごとに 1 つ）。不要なものは一覧から削除する。
    /// </summary>
    public async Task<OpResult<BackupPurgeResult>> RegisterThisDeviceTpmAsync(byte[] pin, byte[] confirm)
    {
        try
        {
            var check = CredentialPolicy.CheckNewPin(pin, confirm);
            if (!check.Ok) return OpResult<BackupPurgeResult>.Fail(check.Error, check.Detail);

            return await session.RunExclusiveAsync<BackupPurgeResult>("Slot.RegisterTpm", async ctx =>
            {
                var existing = ctx.Vault.Slots.Where(s => s.Type == SlotType.Tpm).ToList();
                var enrolled = await Task.Run(() =>
                {
                    try
                    {
                        var replaced = existing.Where(s => tpm.IsSealedOnThisDevice(s.TypeParams)).Select(s => s.Id).ToList();
                        var (slot, kek) = TpmSlot.Enroll(tpm, SetupFlow.TpmLabel(), pin, _options.PinKdf);
                        return OpResult<(KeySlot, SecretBuffer, List<Guid>)>.Success((slot, kek, replaced));
                    }
                    catch (TpmUnavailableException ex) { return OpResult<(KeySlot, SecretBuffer, List<Guid>)>.Fail(ErrorCode.TpmUnavailable, ex.Message); }
                    catch (TpmLockedOutException) { return OpResult<(KeySlot, SecretBuffer, List<Guid>)>.Fail(ErrorCode.TpmLockedOut); }
                });
                if (!enrolled.Ok) return OpResult<BackupPurgeResult>.Fail(enrolled.Error, enrolled.Detail);

                var (newSlot, newKek, replacedIds) = enrolled.Value;
                using (newKek)
                {
                    var r = ctx.Commit(v =>
                    {
                        v.AddSlot(newSlot, newKek);
                        foreach (var id in replacedIds) v.RemoveSlot(id);
                        return OpResult.Success;
                    });
                    if (!r.Ok) return OpResult<BackupPurgeResult>.Fail(r.Error, r.Detail);
                }
                if (replacedIds.Count == 0) Log.SlotEnrolled(_log, SlotType.Tpm, newSlot.Id);
                foreach (var id in replacedIds) Log.SlotReplaced(_log, SlotType.Tpm, id, newSlot.Id);
                // PIN の変更（この PC の古い TPM 登録を置き換えた）ときだけ、古い PIN で開ける自動バックアップを削除する。
                // 初めての登録（置き換えなし）では何も無効にしていないので、バックアップは残す
                var purged = replacedIds.Count > 0 ? PurgeBackups("Slot.RegisterTpm") : new BackupPurgeResult(0, []);
                return OpResult<BackupPurgeResult>.Success(purged);
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pin);
            CryptographicOperations.ZeroMemory(confirm);
        }
    }

    /// <summary>新しい復旧コードを作る（まだ登録しない）。書き写しを確認してから <see cref="CommitRecoveryCodeAsync"/>。</summary>
    public Task<OpResult<PendingRecoveryCode>> PrepareRecoveryCodeAsync()
        => session.RunExclusiveAsync<PendingRecoveryCode>("Slot.PrepareRecovery", async _ =>
        {
            var (slot, code, kek) = await Task.Run(() => RecoverySlot.Enroll(kdf: _options.RecoveryKdf));
            return OpResult<PendingRecoveryCode>.Success(new PendingRecoveryCode(slot, code, kek, RandomNumberGenerator.GetInt32(RecoveryCode.GroupCount)));
        });

    /// <summary>
    /// 書き写しを確認して新しい復旧コードを登録し、古い復旧コードを無効にする。
    /// 古い復旧コードで開ける自動バックアップも削除する（<see cref="PurgeBackups"/>）。値は削除できなかったバックアップ。
    /// </summary>
    public async Task<OpResult<BackupPurgeResult>> CommitRecoveryCodeAsync(PendingRecoveryCode pending, string checkInput)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.IsDisposed) return OpResult<BackupPurgeResult>.Fail(ErrorCode.InvalidStep);
        if (!pending.Code.MatchesGroup(pending.CheckGroup, checkInput ?? string.Empty)) return OpResult<BackupPurgeResult>.Fail(ErrorCode.RecoveryVerifyMismatch);

        return await session.RunExclusiveAsync<BackupPurgeResult>("Slot.RegenerateRecovery", ctx =>
        {
            var old = ctx.Vault.Slots.Where(s => s.Type == SlotType.Recovery).Select(s => s.Id).ToList();
            var r = ctx.Commit(v =>
            {
                v.AddSlot(pending.Slot, pending.Kek);
                foreach (var id in old) v.RemoveSlot(id);
                return OpResult.Success;
            });
            if (!r.Ok) return Task.FromResult(OpResult<BackupPurgeResult>.Fail(r.Error, r.Detail));
            foreach (var id in old) Log.SlotReplaced(_log, SlotType.Recovery, id, pending.Slot.Id);
            pending.Dispose();
            return Task.FromResult(OpResult<BackupPurgeResult>.Success(PurgeBackups("Slot.RegenerateRecovery")));
        });
    }

    /// <summary>
    /// スロットを削除（失効）する。「復旧コード＋他 1 つ以上」を満たさなくなる削除は拒否する。
    /// 削除した方法で開ける自動バックアップも削除する（<see cref="PurgeBackups"/>）。値は削除できなかったバックアップ。
    /// </summary>
    public Task<OpResult<BackupPurgeResult>> RemoveAsync(Guid slotId)
        => session.RunExclusiveAsync<BackupPurgeResult>("Slot.Remove", ctx =>
        {
            var slot = ctx.Vault.Slots.FirstOrDefault(s => s.Id == slotId);
            if (slot is null) return Task.FromResult(OpResult<BackupPurgeResult>.Fail(ErrorCode.NotFound));
            var r = ctx.Commit(v =>
            {
                v.RemoveSlot(slotId);
                return OpResult.Success;
            });
            if (!r.Ok) return Task.FromResult(OpResult<BackupPurgeResult>.Fail(r.Error, r.Detail));
            Log.SlotRemoved(_log, slot.Type, slot.Id);
            return Task.FromResult(OpResult<BackupPurgeResult>.Success(PurgeBackups("Slot.Remove")));
        });

    /// <summary>
    /// 自動バックアップをすべて削除する（docs/02 T4b、docs/03 §5）。VMK は作り直していないため、削除した方法がまだ載っている
    /// 古い保管ファイル（.bak1〜、今の保存で回した .bak1 も含む）があると、その方法で VMK を取り出され、今の保管ファイルも開けてしまう。
    /// PC の外にあるコピー（同期・バックアップ）は消せないため、画面で利用者に伝える。
    /// </summary>
    private BackupPurgeResult PurgeBackups(string operation)
    {
        var result = session.Store.DeleteBackups();
        Log.BackupsPurged(_log, operation, result.Deleted, result.Remaining.Count);
        return result;
    }

    /// <summary>保存先に、この Vault で登録中の鍵ファイルがあるか。</summary>
    private bool IsKeyfileInUse(PasswordVault vault, string path)
    {
        try
        {
            if (!keyfiles.Exists(path)) return false;
            var bytes = keyfiles.Read(path);
            try
            {
                using var existing = Keyfile.FromBytes(bytes);
                return existing.VaultId == vault.VaultId && vault.Slots.Any(s => s.Id == existing.SlotId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return false; // 鍵ファイルではない・読めない（上書きの確認は保存ダイアログで行う）
        }
    }

    private void TryDelete(string path)
    {
        try { keyfiles.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.KeyfileCleanupFailed(_log, ex.GetType().Name);
        }
    }
}
