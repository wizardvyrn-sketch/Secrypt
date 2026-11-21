namespace PwManager.Application.Policies;

/// <summary>
/// PIN の失敗を数え、Windows に TPM をブロックされる前に、アプリ側で PIN の試行を止める。
///
/// 背景（2026-11-21 の調査）: 管理者でないユーザーが TPM に認証失敗を続けると、Windows は PIN の誤りだけでなく
/// 封印・読み込みを含む認証付きの TPM コマンドをすべて、そのユーザーについて一定時間ブロックする
/// （TPM イベント ID 23。TPM のハードウェア DA の上限 31 回とは別の仕組み）。既定は、失敗を数える窓が 480 分で、
/// 個別のしきい値は 4 回前後（Microsoft の資料では未確認の値）。ブロック中は正しい PIN でも解錠できない。
/// そこで、窓の中の失敗が <see cref="MaxFailuresInWindow"/> 回に達したら、アプリが PIN の試行を止める
/// （しきい値より 1 回少なく止めて、しきい値の不確かさと、他のプロセスの失敗の余裕を残す）。
/// 失敗は成功しても数え直さない（Windows 側の数え方に合わせる）。
/// </summary>
public static class PinAttemptPolicy
{
    public const int MaxFailuresInWindow = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(480);

    /// <summary>窓の中の失敗だけを、古い順に返す。時計が戻って未来になった記録は「いま」として扱う。</summary>
    public static List<DateTimeOffset> Recent(IEnumerable<DateTimeOffset> failures, DateTimeOffset now)
        => failures.Select(t => t > now ? now : t).Where(t => now - t < Window).OrderBy(t => t).ToList();

    /// <summary>あと何回、間違えられるか（0 なら PIN は停止中）。</summary>
    public static int AttemptsLeft(IReadOnlyCollection<DateTimeOffset> recent) => Math.Max(0, MaxFailuresInWindow - recent.Count);

    /// <summary>PIN を再開できる時刻。停止中でなければ null。窓から古い失敗が外れて、上限を下回る時刻。</summary>
    public static DateTimeOffset? ResumeAt(IReadOnlyList<DateTimeOffset> recent)
        => recent.Count >= MaxFailuresInWindow ? recent[recent.Count - MaxFailuresInWindow] + Window : null;
}
