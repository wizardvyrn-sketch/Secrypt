namespace PwManager.Application.Policies;

/// <summary>
/// 解錠の連続失敗に待ち時間を入れる。3 回目の失敗から 15 秒、以降倍増して最大 300 秒。
/// 主な防御は Argon2id と TPM の総当たり対策で、これは画面からの連打を抑える補助（再起動で初期化される）。
/// </summary>
public sealed class UnlockBackoffPolicy(TimeProvider? time = null)
{
    public const int FreeAttempts = 2;
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(300);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public int ConsecutiveFailures { get; private set; }

    /// <summary>待ち時間の残り。待たなくてよければ null。</summary>
    public TimeSpan? RemainingDelay
    {
        get
        {
            var remaining = _retryAfter - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    public void RegisterFailure()
    {
        ConsecutiveFailures++;
        if (ConsecutiveFailures > FreeAttempts)
        {
            var factor = Math.Pow(2, ConsecutiveFailures - FreeAttempts - 1);
            var delay = TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, BaseDelay.TotalSeconds * factor));
            _retryAfter = _time.GetUtcNow() + delay;
        }
    }

    public void RegisterSuccess()
    {
        ConsecutiveFailures = 0;
        _retryAfter = DateTimeOffset.MinValue;
    }
}
