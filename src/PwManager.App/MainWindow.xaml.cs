using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PwManager.App.Services;
using PwManager.Application.Session;
using PwManager.Presentation.ViewModels;

namespace PwManager.App;

/// <summary>
/// メインウィンドウ。画面の状態は ShellViewModel が持ち、ここは OS との接点（自動ロックの契機・画面キャプチャ除外・
/// 処理中に他のウィンドウを操作させない）だけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<Window> _disabledWhileBusy = [];

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;

        // 自動ロック: 無操作タイマー、画面ロック・ユーザー切替、スリープ
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _idleTimer.Tick += (_, _) => _shell.Tick();
        _idleTimer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _shell.PropertyChanged += OnShellPropertyChanged;
        _shell.CloseRequested += (_, _) => Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
        _shell.Start();
#if DEBUG
        // 開発用（Debug ビルドかつ PWM_ALLOW_TEST_ERROR=1 のときだけ）: Ctrl+Shift+F12 で予期しないエラーを起こし、
        // 未処理例外の処理（ログ記録 → ロック → 案内、R-19）を実機で確認する（docs/test W-14）。
        if (Environment.GetEnvironmentVariable("PWM_ALLOW_TEST_ERROR") == "1")
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    throw new InvalidOperationException("開発用のテストエラー（Ctrl+Shift+F12）");
            };
#endif
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs) _shell.NotifyActivity();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.IsBusy)) return;
        // 処理中（Argon2id・TPM）は「鍵と設定」なども操作できなくする（同時に 2 つの変更を始めないように）
        if (_shell.IsBusy)
        {
            _disabledWhileBusy = System.Windows.Application.Current.Windows.OfType<Window>().Where(w => w != this && w.IsEnabled).ToList();
            foreach (var w in _disabledWhileBusy) w.IsEnabled = false;
        }
        else
        {
            foreach (var w in _disabledWhileBusy) w.IsEnabled = true;
            _disabledWhileBusy = [];
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureProtection.Apply(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 処理中は完了を待ってから閉じる。未保存の編集があれば確認する
        if (!_shell.CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _idleTimer.Stop();
        base.OnClosed(e);
    }

    // ---------- タイトル帯 ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // WindowStyle=None で最大化すると画面端からはみ出す分を枠で吸収する
        EdgeBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>2 つ目の起動から「前面に出て」と言われた。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------- 自動ロック ----------

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.Invoke(() => _shell.RequestLock(LockReason.SessionLock));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.Invoke(() => _shell.RequestLock(LockReason.Suspend));
    }

}
