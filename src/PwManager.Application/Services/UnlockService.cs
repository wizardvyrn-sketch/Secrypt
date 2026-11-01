using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Policies;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>解錠の結果（R-14）。</summary>
public sealed record UnlockResult(ErrorCode Error, UnlockMethod Method, Guid? SlotId = null, TimeSpan? RetryAfter = null, int ConsecutiveFailures = 0)
{
    public bool Ok => Error == ErrorCode.None;
}

/// <summary>
/// 解錠（UC1・UC3・UC5）。方式ごとに KEK を導出して Vault を開き、<see cref="VaultSession"/> に渡す。
/// 最終使用日は日付が変わったときだけ記録し、その保存ではバックアップを回さない（R-15）。
/// </summary>
public sealed class UnlockService
{
    private readonly VaultSession _session;
    private readonly ITpmSealer _tpm;
    private readonly IKeyfileStore _keyfiles;
    private readonly SettingsService _settings;
    private readonly UnlockBackoffPolicy _backoff;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public UnlockService(VaultSession session, ITpmSealer tpm, IKeyfileStore keyfiles, SettingsService settings,
        UnlockBackoffPolicy? backoff = null, TimeProvider? time = null, ILogger<UnlockService>? logger = null)
    {
        _session = session;
        _tpm = tpm;
        _keyfiles = keyfiles;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new UnlockBackoffPolicy(_time);
        _log = logger ?? NullLogger<UnlockService>.Instance;
    }

    /// <summary>連続失敗による待ち時間の残り。</summary>
    public TimeSpan? RemainingDelay => _backoff.RemainingDelay;

    /// <summary>解錠画面に出す方法を調べる（TPM に問い合わせるため背景で実行する）。</summary>
    public async Task<UnlockOptions> GetOptionsAsync()
    {
        var lastKeyfile = _settings.Current.LastKeyfilePath;
        VaultHeader header;
        try
        {
            header = PasswordVault.ReadHeader(_session.Store.ReadAll());
        }
        catch (Exception ex)
        {
            var code = Classify(ex);
            return new UnlockOptions(false, false, false, UnlockMethod.Recovery, lastKeyfile, code, false);
        }

        var tpmSlots = header.Slots.Where(s => s.Type == SlotType.Tpm).ToList();
        int onThisDevice = 0;
        if (tpmSlots.Count > 0)
        {
            onThisDevice = await Task.Run(() =>
            {
                try { return tpmSlots.Count(s => _tpm.IsSealedOnThisDevice(s.TypeParams)); }
                catch (TpmUnavailableException ex)
                {
                    Log.TpmUnavailable(_log, ex.Message);
                    return 0;
                }
            });
            Log.TpmSlotsOnThisDevice(_log, tpmSlots.Count, onThisDevice);
        }

        bool pin = onThisDevice > 0;
        bool keyfile = header.Slots.Any(s => s.Type == SlotType.Keyfile);
        var defaultMethod = pin ? UnlockMethod.Pin : keyfile ? UnlockMethod.Keyfile : UnlockMethod.Recovery;
        return new UnlockOptions(true, pin, keyfile, defaultMethod, lastKeyfile, ErrorCode.None,
            TpmSlotsForOtherDevicesOnly: tpmSlots.Count > 0 && onThisDevice == 0);
    }

    /// <summary>解錠する。成功すると VaultSession が解錠状態になる。request 内の秘密のバイト列はゼロ化する。</summary>
    public async Task<UnlockResult> UnlockAsync(UnlockRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var method = request.Method;
        using var logScope = LogOperation.Begin();
        try
        {
            if (_session.IsUnlocked) return new UnlockResult(ErrorCode.InvalidStep, method);
            if (_backoff.RemainingDelay is { } wait)
                return new UnlockResult(ErrorCode.RetryLater, method, RetryAfter: wait, ConsecutiveFailures: _backoff.ConsecutiveFailures);

            Log.UnlockStarted(_log, method.ToString());
            var sw = Stopwatch.StartNew();
            var r = await _session.RunExclusiveAsync<Guid>("Unlock", ctx => OpenAsync(ctx, request), requireUnlocked: false);

            if (r.Ok)
            {
                _backoff.RegisterSuccess();
                Log.UnlockSucceeded(_log, method.ToString(), r.Value, sw.ElapsedMilliseconds, _session.Counter);
                return new UnlockResult(ErrorCode.None, method, r.Value);
            }

            if (r.Error is ErrorCode.WrongPin or ErrorCode.WrongKey or ErrorCode.WrongRecoveryCode)
                _backoff.RegisterFailure();
            Log.UnlockFailed(_log, method.ToString(), r.Error, sw.ElapsedMilliseconds, _backoff.ConsecutiveFailures);
            return new UnlockResult(r.Error, method, RetryAfter: _backoff.RemainingDelay, ConsecutiveFailures: _backoff.ConsecutiveFailures);
        }
        finally
        {
            Wipe(request);
        }
    }

    private async Task<OpResult<Guid>> OpenAsync(VaultContext ctx, UnlockRequest request)
    {
        byte[] file;
        VaultHeader header;
        try
        {
            file = _session.Store.ReadAll();
            header = PasswordVault.ReadHeader(file);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return OpResult<Guid>.Fail(Classify(ex));
        }

        OpResult<(PasswordVault Vault, Guid SlotId)> opened = request switch
        {
            PinUnlockRequest pin => await OpenWithPinAsync(file, header, pin.Pin),
            KeyfileUnlockRequest kf => await OpenWithKeyfileAsync(file, header, kf),
            RecoveryUnlockRequest rc => await OpenWithRecoveryAsync(file, header, rc.Code),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        if (!opened.Ok) return OpResult<Guid>.Fail(opened.Error, opened.Detail);

        var (vault, slotId) = opened.Value;
        ctx.Attach(vault, header.Counter);
        RecordSlotUse(ctx, slotId);
        if (request is KeyfileUnlockRequest { KeyfilePath: { } path } && !string.Equals(path, _settings.Current.LastKeyfilePath, StringComparison.Ordinal))
            _settings.Update(s => s.LastKeyfilePath = path);
        return OpResult<Guid>.Success(slotId);
    }

    private Task<OpResult<(PasswordVault, Guid)>> OpenWithPinAsync(byte[] file, VaultHeader header, byte[] pin)
    {
        var tpmSlots = header.Slots.Where(s => s.Type == SlotType.Tpm).ToList();
        if (tpmSlots.Count == 0) return Task.FromResult(Fail(ErrorCode.MethodNotConfigured));
        return Run(() =>
        {
            // 別の PC・クリア前の TPM のスロットは読み込めない。この PC のものを使う（PIN を試すのは 1 つだけ）
            var slot = tpmSlots.FirstOrDefault(s => _tpm.IsSealedOnThisDevice(s.TypeParams));
            if (slot is null) return Fail(ErrorCode.TpmNotRegisteredOnThisDevice);
            using var kek = TpmSlot.DeriveKek(slot, _tpm, pin);
            return Success(PasswordVault.Open(file, slot.Id, kek), slot.Id);
        });
    }

    private async Task<OpResult<(PasswordVault, Guid)>> OpenWithKeyfileAsync(byte[] file, VaultHeader header, KeyfileUnlockRequest request)
    {
        Keyfile keyfile;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.KeyfileText))
            {
                keyfile = Keyfile.FromText(request.KeyfileText);
            }
            else if (!string.IsNullOrWhiteSpace(request.KeyfilePath))
            {
                var bytes = _keyfiles.Read(request.KeyfilePath);
                try { keyfile = Keyfile.FromBytes(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else
            {
                return Fail(ErrorCode.KeyfileNotFound);
            }
        }
        catch (FormatException) { return Fail(ErrorCode.KeyfileMalformed); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Fail(ErrorCode.KeyfileNotFound); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail(ErrorCode.IoError, ex.Message); }

        using (keyfile)
        {
            if (keyfile.VaultId != header.VaultId) return Fail(ErrorCode.KeyfileForOtherVault);
            var slot = header.Slots.FirstOrDefault(s => s.Id == keyfile.SlotId && s.Type == SlotType.Keyfile);
            if (slot is null) return Fail(ErrorCode.KeyfileRevoked);
            var password = request.Password;
            return await Run(() =>
            {
                using var kek = KeyfileSlot.DeriveKek(slot, keyfile, password);
                return Success(PasswordVault.Open(file, slot.Id, kek), slot.Id);
            });
        }
    }

    private static Task<OpResult<(PasswordVault, Guid)>> OpenWithRecoveryAsync(byte[] file, VaultHeader header, string text)
    {
        RecoveryCode code;
        try { code = RecoveryCode.Parse(text ?? string.Empty); }
        catch (FormatException ex) { return Task.FromResult(Fail(ErrorCode.RecoveryCodeMalformed, ex.Message)); }

        var slots = header.Slots.Where(s => s.Type == SlotType.Recovery).ToList();
        if (slots.Count == 0)
        {
            code.Dispose();
            return Task.FromResult(Fail(ErrorCode.MethodNotConfigured));
        }
        return Run(() =>
        {
            using (code)
            {
                foreach (var slot in slots)
                {
                    try
                    {
                        using var kek = RecoverySlot.DeriveKek(slot, code);
                        return Success(PasswordVault.Open(file, slot.Id, kek), slot.Id);
                    }
                    catch (VaultUnlockException) { /* 次の復旧スロットを試す */ }
                }
                return Fail(ErrorCode.WrongRecoveryCode);
            }
        });
    }

    /// <summary>最終使用日を日付が変わったときだけ記録する。保存できなくても解錠は続ける（次回に記録される）。</summary>
    private void RecordSlotUse(VaultContext ctx, Guid slotId)
    {
        var slot = ctx.Vault.Slots.First(s => s.Id == slotId);
        var now = _time.GetUtcNow();
        var today = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).Date;
        if (slot.LastVerified is { } last && TimeZoneInfo.ConvertTime(last, _time.LocalTimeZone).Date == today) return;

        var r = ctx.Commit(_ =>
        {
            slot.LastVerified = now;
            return OpResult.Success;
        }, SaveKind.Metadata);
        if (r.Ok) Log.SlotUseRecorded(_log, slotId);
    }

    // ---------- 共通 ----------

    /// <summary>KEK 導出（Argon2id・TPM）を背景で実行し、想定内の例外を結果に変換する。</summary>
    private static Task<OpResult<(PasswordVault, Guid)>> Run(Func<OpResult<(PasswordVault, Guid)>> work)
        => Task.Run(() =>
        {
            try { return work(); }
            catch (Exception ex) when (IsExpected(ex)) { return Fail(Classify(ex), ex is IOException ? ex.Message : null); }
        });

    private static bool IsExpected(Exception ex) => ex is TpmAuthFailedException or TpmLockedOutException or TpmUnavailableException
        or VaultUnlockException or VaultTamperedException or VaultFormatException
        or FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException;

    internal static ErrorCode Classify(Exception ex) => ex switch
    {
        TpmAuthFailedException => ErrorCode.WrongPin,
        TpmLockedOutException => ErrorCode.TpmLockedOut,
        TpmUnavailableException => ErrorCode.TpmUnavailable,
        VaultUnlockException => ErrorCode.WrongKey,
        VaultTamperedException => ErrorCode.VaultTampered,
        VaultFormatException => ErrorCode.VaultFormat,
        FileNotFoundException or DirectoryNotFoundException => ErrorCode.VaultNotFound,
        IOException or UnauthorizedAccessException => ErrorCode.IoError,
        _ => ErrorCode.Unexpected,
    };

    private static OpResult<(PasswordVault, Guid)> Success(PasswordVault vault, Guid slotId) => OpResult<(PasswordVault, Guid)>.Success((vault, slotId));

    private static OpResult<(PasswordVault, Guid)> Fail(ErrorCode code, string? detail = null) => OpResult<(PasswordVault, Guid)>.Fail(code, detail);

    private static void Wipe(UnlockRequest request)
    {
        switch (request)
        {
            case PinUnlockRequest p: CryptographicOperations.ZeroMemory(p.Pin); break;
            case KeyfileUnlockRequest k: CryptographicOperations.ZeroMemory(k.Password); break;
        }
    }
}
