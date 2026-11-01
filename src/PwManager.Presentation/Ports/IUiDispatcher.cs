namespace PwManager.Presentation.Ports;

/// <summary>
/// 画面の処理が一段落してから実行したい処理を予約する（例: 一覧の選択を「戻る」で元に戻す）。
/// WPF の実装は Dispatcher に ContextIdle で積む。バインドの更新中に通知しても画面に反映されないため。
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>既定（テスト・WPF 以外）: 生成時のスレッドの SynchronizationContext に積む。無ければその場で実行する。</summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }
}
