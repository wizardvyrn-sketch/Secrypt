using PwManager.Application.Session;

namespace PwManager.Application.Policies;

/// <summary>
/// 無操作による自動ロック（F4）。画面は入力があるたびに <see cref="NotifyActivity"/> を呼び、
/// タイマーで定期的に <see cref="Check"/> を呼ぶ。処理中ならロックは完了後になる（VaultSession が保留する）。
/// </summary>
public sealed class AutoLockMonitor
{
    private readonly VaultSession _session;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastActivity;

    public AutoLockMonitor(VaultSession session, TimeProvider? time = null)
    {
        _session = session;
        _time = time ?? TimeProvider.System;
        _lastActivity = _time.GetUtcNow();
        // 解錠した時点から数え直す（ロック画面で放置した時間を数えない）
        _session.StateChanged += (_, e) => { if (e.State == SessionState.Unlocked) NotifyActivity(); };
    }

    /// <summary>無操作とみなすまでの時間。設定変更で更新する。</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public void NotifyActivity() => _lastActivity = _time.GetUtcNow();

    /// <summary>無操作の時間が設定を超えていればロックを要求する。要求したら true。</summary>
    public bool Check()
    {
        if (!_session.IsUnlocked) return false;
        if (_time.GetUtcNow() - _lastActivity < IdleTimeout) return false;
        _session.RequestLock(LockReason.Idle);
        return true;
    }
}
