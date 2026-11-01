namespace PwManager.Application.Ports;

/// <summary>
/// クリップボード（R-08）。実装は App（WPF）。履歴・クラウド同期に載せない形式を付け、一定時間後に
/// 「自分が置いた内容のままなら」消去する。
/// </summary>
public interface IClipboardService
{
    void SetText(string text, TimeSpan clearAfter);

    /// <summary>自分が置いた内容のままなら消去する（ロック時・終了時）。</summary>
    void ClearIfOurs();
}

/// <summary>クリップボードを使わない環境（CLI・テスト）用。</summary>
public sealed class NullClipboardService : IClipboardService
{
    public void SetText(string text, TimeSpan clearAfter) { }

    public void ClearIfOurs() { }
}
