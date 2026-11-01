using System.Windows.Threading;
using PwManager.Presentation.Ports;

namespace PwManager.App.Services;

/// <summary>
/// WPF の Dispatcher に ContextIdle で積む。ListBox の選択処理（マウス操作・モーダルダイアログを含む）が
/// 終わってから実行されるため、ViewModel から選択を戻す通知が画面に反映される（W-04）。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.ContextIdle);
}
