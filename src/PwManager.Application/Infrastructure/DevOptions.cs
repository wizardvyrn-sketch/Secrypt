namespace PwManager.Application.Infrastructure;

/// <summary>
/// 開発用の設定（R-20、docs/10 §7）。画面キャプチャの許可は Debug ビルドのときだけ有効で、Release では環境変数を設定しても無効。
/// </summary>
public sealed record DevOptions(bool AllowCapture)
{
    public const string AllowCaptureVariable = "PWM_ALLOW_CAPTURE";

    /// <param name="isDebugBuild">呼び出し側（App）が #if DEBUG で渡す。</param>
    public static DevOptions Create(Func<string, string?> environment, bool isDebugBuild)
        => new(AllowCapture: isDebugBuild && environment(AllowCaptureVariable) == "1");
}
