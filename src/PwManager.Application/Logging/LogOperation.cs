namespace PwManager.Application.Logging;

/// <summary>
/// 1 つの操作（解錠・保存・スロット追加など）に振る操作 ID。開始から結果までのログを同じ ID でたどれる（docs/10 §1）。
/// </summary>
public static class LogOperation
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>現在の操作 ID（なければ null）。</summary>
    public static string? Current => CurrentId.Value;

    /// <summary>操作を始める。既に操作の中なら同じ ID を使う。Dispose で元に戻す。</summary>
    public static IDisposable Begin()
    {
        if (CurrentId.Value is not null) return NoopScope.Instance;
        CurrentId.Value = Guid.NewGuid().ToString("N")[..8];
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => CurrentId.Value = null;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose() { }
    }
}
