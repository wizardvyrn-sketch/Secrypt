namespace PwManager.Application.Results;

/// <summary>操作の結果。想定内の失敗は例外ではなく <see cref="ErrorCode"/> で返す（R-14）。</summary>
public sealed record OpResult(ErrorCode Error, string? Detail = null)
{
    public static OpResult Success { get; } = new(ErrorCode.None);

    public bool Ok => Error == ErrorCode.None;

    public static OpResult Fail(ErrorCode error, string? detail = null) => new(error, detail);
}

/// <summary>値を返す操作の結果。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "結果型のファクトリ")]
public sealed record OpResult<T>(T? Value, ErrorCode Error, string? Detail = null)
{
    public bool Ok => Error == ErrorCode.None;

    public static OpResult<T> Success(T value) => new(value, ErrorCode.None);

    public static OpResult<T> Fail(ErrorCode error, string? detail = null) => new(default, error, detail);

    public static implicit operator OpResult(OpResult<T> r) => new(r.Error, r.Detail);
}
