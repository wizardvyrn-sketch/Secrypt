using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Infrastructure;
using PwManager.Application.Logging;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Core.Vault;

namespace PwManager.Application.Session;

public enum SessionState
{
    Locked,
    Unlocked,
}

/// <summary>保存の種類。</summary>
public enum SaveKind
{
    /// <summary>項目・スロットの変更。counter を進め、バックアップ世代を回す。</summary>
    Content,
    /// <summary>最終使用日の記録など、内容の変わらない保存。counter もバックアップもそのまま（R-15）。</summary>
    Metadata,
}

/// <summary>ロックの理由（ログの理由コードにも使う）。</summary>
public enum LockReason
{
    Manual,
    /// <summary>無操作が設定時間続いた。</summary>
    Idle,
    /// <summary>画面ロック・ユーザー切替・リモート切断。</summary>
    SessionLock,
    /// <summary>スリープ・休止。</summary>
    Suspend,
    /// <summary>アプリの終了。</summary>
    Exit,
    /// <summary>未処理例外の後（安全側に倒す）。</summary>
    Error,
}

public sealed class SessionStateChangedEventArgs(SessionState state, LockReason? reason) : EventArgs
{
    public SessionState State { get; } = state;

    /// <summary>ロックされた場合の理由。</summary>
    public LockReason? Reason { get; } = reason;
}

/// <summary>
/// 解錠中の Vault を唯一保持する（R-07）。操作は <see cref="RunExclusiveAsync{T}"/> で 1 つずつ実行し、
/// 実行中のロック要求は終わってから実行する（R-02）。保存は counter を照合し（R-16）、失敗したらメモリも元に戻す（R-04）。
/// </summary>
/// <remarks>
/// スレッド: 呼び出しは UI スレッドから行う想定。重い計算（Argon2id・TPM）は各サービスが Task.Run で背景に出し、
/// Vault の変更は await の後（UI スレッドに戻ってから）行う。サービス内で ConfigureAwait(false) を使わないこと。
/// </remarks>
public sealed class VaultSession : IDisposable
{
    private readonly IVaultStore _store;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private PasswordVault? _vault;
    private ulong _diskCounter;
    private string? _busyOperation;
    private LockReason? _pendingLock;

    public VaultSession(IVaultStore store, IClipboardService? clipboard = null, TimeProvider? time = null, ILogger<VaultSession>? logger = null)
    {
        _store = store;
        _clipboard = clipboard ?? new NullClipboardService();
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<VaultSession>.Instance;
    }

    public SessionState State => _vault is null ? SessionState.Locked : SessionState.Unlocked;
    public bool IsUnlocked => _vault is not null;

    /// <summary>実行中の操作名（なければ null）。</summary>
    public string? BusyOperation => _busyOperation;
    public bool IsBusy => _busyOperation is not null;

    /// <summary>ロック・解錠されたとき。ロックは操作の完了後に通知されることがある（保留していた場合）。</summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>操作の開始・終了。</summary>
    public event EventHandler? BusyChanged;

    public IVaultStore Store => _store;
    public string Location => _store.Location;

    // ---------- 読み取り専用の参照（画面表示用） ----------

    public Guid VaultId => _vault?.VaultId ?? Guid.Empty;
    public ulong Counter => _vault?.Counter ?? 0;
    public DateTimeOffset Modified => _vault?.Modified ?? default;
    public IReadOnlyList<VaultEntry> Entries => _vault?.Entries ?? (IReadOnlyList<VaultEntry>)[];
    public IReadOnlyList<KeySlot> Slots => _vault?.Slots ?? [];

    public VaultEntry? FindEntry(Guid id) => _vault?.Entries.FirstOrDefault(e => e.Id == id);

    // ---------- 排他実行 ----------

    /// <summary>
    /// 操作を排他的に実行する。requireUnlocked=true でロック中なら Locked、別の操作の実行中なら Busy を返す。
    /// 想定外の例外はログに記録して Unexpected を返す（Vault は変更前に戻す）。
    /// </summary>
    public async Task<OpResult<T>> RunExclusiveAsync<T>(string operation, Func<VaultContext, Task<OpResult<T>>> work, bool requireUnlocked = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (requireUnlocked && _vault is null) return OpResult<T>.Fail(ErrorCode.Locked);
            if (_busyOperation is not null) return OpResult<T>.Fail(ErrorCode.Busy);
            _busyOperation = operation;
        }
        BusyChanged?.Invoke(this, EventArgs.Empty);
        var context = new VaultContext(this, operation);
        using var logScope = LogOperation.Begin();
        try
        {
            return await work(context);
        }
        catch (Exception ex)
        {
            Log.UnhandledException(_log, operation, ex.GetType().Name, ex);
            return OpResult<T>.Fail(ErrorCode.Unexpected);
        }
        finally
        {
            LockReason? pending;
            lock (_gate)
            {
                _busyOperation = null;
                pending = _pendingLock;
                _pendingLock = null;
            }
            BusyChanged?.Invoke(this, EventArgs.Empty);
            if (pending is { } reason) LockNow(reason);
        }
    }

    /// <summary>値を返さない操作用。</summary>
    public async Task<OpResult> RunExclusiveAsync(string operation, Func<VaultContext, Task<OpResult>> work, bool requireUnlocked = true)
    {
        var r = await RunExclusiveAsync<bool>(operation, async ctx =>
        {
            var inner = await work(ctx);
            return inner.Ok ? OpResult<bool>.Success(true) : OpResult<bool>.Fail(inner.Error, inner.Detail);
        }, requireUnlocked);
        return r;
    }

    // ---------- ロック ----------

    /// <summary>
    /// ロックを要求する。操作の実行中なら完了後にロックし false を返す（R-02）。ロックした（または既にロック中）なら true。
    /// </summary>
    public bool RequestLock(LockReason reason)
    {
        string? operation;
        lock (_gate)
        {
            if (_vault is null) return true;
            operation = _busyOperation;
            if (operation is not null)
            {
                if (_pendingLock is null)
                {
                    _pendingLock = reason;
                    Log.AutoLockDeferred(_log, reason, operation);
                }
                return false;
            }
        }
        LockNow(reason);
        return true;
    }

    private void LockNow(LockReason reason)
    {
        PasswordVault? vault;
        lock (_gate)
        {
            vault = _vault;
            _vault = null;
        }
        if (vault is null) return;
        vault.Dispose();
        _clipboard.ClearIfOurs();
        Log.Locked(_log, reason);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Locked, reason));
    }

    public void Dispose() => LockNow(LockReason.Exit);

    // ---------- VaultContext から使う ----------

    internal PasswordVault? CurrentVault => _vault;
    internal TimeProvider Time => _time;
    internal ILogger Logger => _log;

    internal void Attach(PasswordVault vault, ulong diskCounter)
    {
        lock (_gate)
        {
            if (_vault is not null) throw new InvalidOperationException("既に解錠されています。");
            _vault = vault;
            _diskCounter = diskCounter;
        }
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Unlocked, null));
    }

    /// <summary>保存する。expectedCounter=null（新規作成）は Attach 前の初期設定で使う。</summary>
    internal OpResult SaveVault(PasswordVault vault, string operation, ulong? expectedCounter, SaveKind kind, out ulong newCounter)
    {
        newCounter = 0;
        var sw = Stopwatch.StartNew();
        bool rotateBackups = kind == SaveKind.Content;
        try
        {
            var pending = vault.PrepareSave(_time.GetUtcNow(), contentChanged: kind == SaveKind.Content);
            _store.Save(pending.Data, expectedCounter, rotateBackups);
            vault.CommitSave(pending);
            newCounter = pending.Counter;
            Log.SaveCompleted(_log, operation, pending.Counter, pending.Data.Length, sw.ElapsedMilliseconds, rotateBackups);
            if (rotateBackups) Log.BackupRotated(_log, pending.Counter);
            return OpResult.Success;
        }
        catch (VaultConflictException ex)
        {
            Log.SaveConflict(_log, operation, expectedCounter);
            return OpResult.Fail(expectedCounter is null ? ErrorCode.VaultAlreadyExists : ErrorCode.SaveConflict, ex.Message);
        }
        catch (SlotPolicyException ex)
        {
            Log.SlotPolicyRejected(_log, operation);
            return OpResult.Fail(ErrorCode.SlotPolicyViolation, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.SaveFailed(_log, operation, ex.GetType().Name, rolledBack: true, ex);
            return OpResult.Fail(ErrorCode.IoError, ex.Message);
        }
    }

    internal OpResult SaveAttached(string operation, SaveKind kind)
    {
        var vault = _vault ?? throw new InvalidOperationException("ロックされています。");
        var r = SaveVault(vault, operation, _diskCounter, kind, out var counter);
        if (r.Ok) _diskCounter = counter;
        return r;
    }
}

/// <summary>
/// <see cref="VaultSession.RunExclusiveAsync{T}"/> の中で Vault を変更・保存するための入口。
/// 変更は <see cref="Commit"/> を通すと、保存に失敗した場合にメモリ上の変更も元に戻る（R-04）。
/// </summary>
public sealed class VaultContext
{
    private readonly VaultSession _session;

    internal VaultContext(VaultSession session, string operation)
    {
        _session = session;
        Operation = operation;
    }

    public string Operation { get; }

    /// <summary>解錠中の Vault（Application 層の中だけで使う）。</summary>
    internal PasswordVault Vault => _session.CurrentVault ?? throw new InvalidOperationException("ロックされています。");

    internal TimeProvider Time => _session.Time;
    internal ILogger Logger => _session.Logger;

    /// <summary>
    /// mutate で Vault を変更して保存する。mutate が失敗を返した場合・保存に失敗した場合は変更前に戻す。
    /// </summary>
    internal OpResult Commit(Func<PasswordVault, OpResult> mutate, SaveKind kind = SaveKind.Content)
    {
        var vault = Vault;
        using var checkpoint = vault.CreateCheckpoint();
        try
        {
            OpResult r;
            try { r = mutate(vault); }
            catch (SlotPolicyException ex)
            {
                Log.SlotPolicyRejected(Logger, Operation);
                r = OpResult.Fail(ErrorCode.SlotPolicyViolation, ex.Message);
            }
            if (r.Ok) r = _session.SaveAttached(Operation, kind);
            if (!r.Ok) vault.Restore(checkpoint);
            return r;
        }
        catch
        {
            vault.Restore(checkpoint);
            throw;
        }
    }

    internal void Attach(PasswordVault vault, ulong diskCounter) => _session.Attach(vault, diskCounter);

    internal OpResult SaveNew(PasswordVault vault, out ulong counter)
        => _session.SaveVault(vault, Operation, expectedCounter: null, SaveKind.Content, out counter);

    internal IVaultStore Store => _session.Store;
}
