using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Policies;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Localization;
using PwManager.Core.Slots;
using PwManager.Core.Storage;
using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>初期設定の手順。</summary>
public enum SetupStep
{
    /// <summary>STEP 1: この PC の TPM + PIN。</summary>
    Tpm = 1,
    /// <summary>STEP 2: 鍵ファイル（USB）+ パスワード。</summary>
    Keyfile = 2,
    /// <summary>STEP 3: 復旧コード（紙）。</summary>
    Recovery = 3,
    Done = 4,
}

/// <summary>TPM の利用可否。</summary>
public sealed record TpmStatus(bool Available, string Reason);

/// <summary>復旧コードの表示と、書き写し確認で入力してもらう組（0 始まり）。</summary>
public sealed record RecoveryChallenge(string DisplayCode, int GroupA, int GroupB);

/// <summary>初期設定（S-01）を始める。</summary>
public sealed class SetupService(VaultSession session, ITpmSealer tpm, IKeyfileStore keyfiles, SettingsService settings,
    VaultOptions? options = null, ILogger<SetupService>? logger = null)
{
    public SetupFlow Begin() => new(session, tpm, keyfiles, settings, options ?? new VaultOptions(), logger ?? (ILogger)NullLogger<SetupService>.Instance);

    /// <summary>TPM が使えるか（背景で問い合わせる）。</summary>
    public Task<TpmStatus> CheckTpmAsync() => CheckTpmAsync(tpm, logger ?? (ILogger)NullLogger<SetupService>.Instance);

    internal static Task<TpmStatus> CheckTpmAsync(ITpmSealer tpm, ILogger log) => Task.Run(() =>
    {
        bool ok;
        string reason;
        try { ok = tpm.IsAvailable(out reason); }
        catch (Exception ex) when (ex is TpmUnavailableException or IOException or InvalidOperationException)
        {
            ok = false;
            reason = ex.Message;
        }
        if (ok) Log.TpmAvailable(log);
        else Log.TpmUnavailable(log, reason);
        return new TpmStatus(ok, reason);
    });
}

/// <summary>
/// 初期設定の 1 回分。STEP 1 → 2 → 3 の順に進み、保存は最後の 1 回だけ（途中で離れたら何も保存しない）。
/// Dispose で未完了の Vault を破棄する。
/// </summary>
public sealed class SetupFlow : IDisposable
{
    private readonly VaultSession _session;
    private readonly ITpmSealer _tpm;
    private readonly IKeyfileStore _keyfiles;
    private readonly SettingsService _settings;
    private readonly VaultOptions _options;
    private readonly ILogger _log;
    private PasswordVault? _vault = PasswordVault.CreateNew();
    private RecoveryCode? _recovery;
    private RecoveryChallenge? _challenge;
    private bool _keyfileCreated;
    private string? _keyfilePath;

    internal SetupFlow(VaultSession session, ITpmSealer tpm, IKeyfileStore keyfiles, SettingsService settings, VaultOptions options, ILogger log)
    {
        _session = session;
        _tpm = tpm;
        _keyfiles = keyfiles;
        _settings = settings;
        _options = options;
        _log = log;
    }

    public SetupStep Step { get; private set; } = SetupStep.Tpm;
    public string VaultLocation => _session.Location;
    public Guid VaultId => _vault?.VaultId ?? Guid.Empty;
    public bool TpmEnrolled { get; private set; }
    public bool KeyfileCreated => _keyfileCreated;

    /// <summary>登録済みのスロットの種類（表示・テスト用）。</summary>
    public IReadOnlyList<SlotType> EnrolledSlotTypes => _vault?.Slots.Select(s => s.Type).ToList() ?? [];

    // ---------- STEP 1 ----------

    /// <summary>TPM に鍵を封印して PIN のスロットを登録する。pin・confirm はゼロ化する。</summary>
    public async Task<OpResult> EnrollTpmAsync(byte[] pin, byte[] confirm)
    {
        try
        {
            if (Step != SetupStep.Tpm || _vault is null) return OpResult.Fail(ErrorCode.InvalidStep);
            var check = CredentialPolicy.CheckNewPin(pin, confirm);
            if (!check.Ok) return check;

            var vault = _vault;
            return await _session.RunExclusiveAsync("Setup.Tpm", async _ =>
            {
                var enrolled = await Task.Run(() =>
                {
                    try { return OpResult<(KeySlot, Core.Crypto.SecretBuffer)>.Success(TpmSlot.Enroll(_tpm, TpmLabel(), pin, _options.PinKdf)); }
                    catch (TpmUnavailableException ex) { return OpResult<(KeySlot, Core.Crypto.SecretBuffer)>.Fail(ErrorCode.TpmUnavailable, ex.Message); }
                    catch (TpmLockedOutException) { return OpResult<(KeySlot, Core.Crypto.SecretBuffer)>.Fail(ErrorCode.TpmLockedOut); }
                });
                if (!enrolled.Ok) return OpResult.Fail(enrolled.Error, enrolled.Detail);
                var (slot, kek) = enrolled.Value;
                using (kek) vault.AddSlot(slot, kek);
                Log.SlotEnrolled(_log, slot.Type, slot.Id);
                TpmEnrolled = true;
                Step = SetupStep.Keyfile;
                return OpResult.Success;
            }, requireUnlocked: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pin);
            CryptographicOperations.ZeroMemory(confirm);
        }
    }

    /// <summary>TPM を使わずに進む（TPM がない PC、または利用者が選んだ場合）。</summary>
    public OpResult SkipTpm()
    {
        if (Step != SetupStep.Tpm) return OpResult.Fail(ErrorCode.InvalidStep);
        Step = SetupStep.Keyfile;
        return OpResult.Success;
    }

    // ---------- STEP 2 ----------

    /// <summary>鍵ファイルの保存先が Vault と近すぎないか（画面の警告用）。</summary>
    public LocationRisk AssessKeyfileLocation(string keyfilePath) => VaultStorage.AssessKeyfileLocation(_session.Location, keyfilePath);

    /// <summary>
    /// 鍵ファイルを作って保存し、スロットを登録する。戻り値は QR 印刷用のテキスト（表示・印刷後は破棄すること）。
    /// password・confirm はゼロ化する。
    /// </summary>
    public async Task<OpResult<string>> CreateKeyfileAsync(string path, byte[] password, byte[] confirm)
    {
        try
        {
            if (Step != SetupStep.Keyfile || _keyfileCreated || _vault is null) return OpResult<string>.Fail(ErrorCode.InvalidStep);
            var check = CredentialPolicy.CheckNewPassword(password, confirm);
            if (!check.Ok) return OpResult<string>.Fail(check.Error);
            if (string.IsNullOrWhiteSpace(path)) return OpResult<string>.Fail(ErrorCode.KeyfileNotFound);
            if (AssessKeyfileLocation(path) == LocationRisk.SameDirectory) return OpResult<string>.Fail(ErrorCode.KeyfileLocationSameDirectory);

            var vault = _vault;
            return await _session.RunExclusiveAsync<string>("Setup.Keyfile", async _ =>
            {
                var (slot, keyfile, kek) = await Task.Run(() => KeyfileSlot.Enroll(vault.VaultId, L.T("鍵ファイル（USB）+ パスワード", "Key file (USB) + password"), password, _options.PasswordKdf));
                using (kek)
                using (keyfile)
                {
                    var write = KeyfileWriter.Write(_keyfiles, path, keyfile);
                    if (!write.Ok) return OpResult<string>.Fail(write.Error, write.Detail);
                    vault.AddSlot(slot, kek);
                    Log.SlotEnrolled(_log, slot.Type, slot.Id);
                    _keyfileCreated = true;
                    _keyfilePath = path;
                    return OpResult<string>.Success(keyfile.ToText());
                }
            }, requireUnlocked: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(confirm);
        }
    }

    // ---------- STEP 3 ----------

    /// <summary>
    /// 鍵ファイルを保存したことを確認して、復旧コードを作る。書き写し確認では、表示した 9 組のうち 2 組を入力してもらう。
    /// </summary>
    public async Task<OpResult<RecoveryChallenge>> CreateRecoveryCodeAsync()
    {
        if (Step != SetupStep.Keyfile || !_keyfileCreated || _vault is null) return OpResult<RecoveryChallenge>.Fail(ErrorCode.InvalidStep);
        var vault = _vault;
        return await _session.RunExclusiveAsync<RecoveryChallenge>("Setup.Recovery", async _ =>
        {
            var (slot, code, kek) = await Task.Run(() => RecoverySlot.Enroll(kdf: _options.RecoveryKdf));
            using (kek) vault.AddSlot(slot, kek);
            Log.SlotEnrolled(_log, slot.Type, slot.Id);
            _recovery = code;
            int a = RandomNumberGenerator.GetInt32(RecoveryCode.GroupCount), b;
            do { b = RandomNumberGenerator.GetInt32(RecoveryCode.GroupCount); } while (b == a);
            _challenge = new RecoveryChallenge(code.ToDisplayString(), Math.Min(a, b), Math.Max(a, b));
            Step = SetupStep.Recovery;
            return OpResult<RecoveryChallenge>.Success(_challenge);
        }, requireUnlocked: false);
    }

    /// <summary>書き写しを確認して保存し、解錠状態にする（一覧画面へ）。既に Vault ファイルがあれば上書きしない。</summary>
    public async Task<OpResult> FinishAsync(string groupA, string groupB)
    {
        if (Step != SetupStep.Recovery || _vault is null || _recovery is null || _challenge is null) return OpResult.Fail(ErrorCode.InvalidStep);
        if (!_recovery.MatchesGroup(_challenge.GroupA, groupA ?? string.Empty) || !_recovery.MatchesGroup(_challenge.GroupB, groupB ?? string.Empty))
            return OpResult.Fail(ErrorCode.RecoveryVerifyMismatch);

        var vault = _vault;
        var recovery = _recovery;
        var r = await _session.RunExclusiveAsync("Setup.Finish", ctx =>
        {
            var saved = ctx.SaveNew(vault, out var counter);
            if (!saved.Ok) return Task.FromResult(saved);

            // 所有権を VaultSession に移してから解錠を通知する。通知を受けた画面がこの手順（SetupFlow）を
            // 破棄しても、渡した Vault を破棄しないように（2026-10-25 の画面テストで検出）
            _vault = null;
            _recovery = null;
            _challenge = null;
            Step = SetupStep.Done;
            Log.SetupCompleted(_log, vault.VaultId, vault.Slots.Count);
            // 解錠画面で鍵ファイルの場所を自動入力する（完了したときだけ。中断した場合は使われない鍵ファイルなので覚えない）
            if (_keyfilePath is not null) _settings.Update(s => s.LastKeyfilePath = _keyfilePath);
            ctx.Attach(vault, counter);
            return Task.FromResult(OpResult.Success);
        }, requireUnlocked: false);
        if (r.Ok) recovery.Dispose();
        return r;
    }

    public void Dispose()
    {
        if (_vault is not null)
        {
            Log.SetupAbandoned(_log, Step.ToString());
            _vault.Dispose();
            _vault = null;
        }
        _recovery?.Dispose();
        _recovery = null;
        _challenge = null;
    }

    /// <summary>
    /// TPM スロットのラベル。PC 名は利用者名を含むことが多く、ヘッダは暗号化されないため入れない。
    /// どの PC のものかは画面で「この PC」「他の PC」と判定して表示する。
    /// </summary>
    internal static string TpmLabel() => "TPM + PIN";
}

/// <summary>鍵ファイルの書き出し（初期設定と「鍵と設定」で共有）。</summary>
internal static class KeyfileWriter
{
    public static OpResult Write(IKeyfileStore store, string path, Keyfile keyfile)
    {
        var bytes = keyfile.ToBytes();
        try
        {
            store.Write(path, bytes);
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OpResult.Fail(ErrorCode.IoError, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
