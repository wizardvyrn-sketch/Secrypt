using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PwManager.App.Views;

/// <summary>
/// 起動時ロゴ画面（08 §1.1）。進捗線が伸びきるか、クリック・キー操作で飛ばされたら <see cref="BootCompleted"/> を出す。
/// App はそこでメインウィンドウを表示し、<see cref="FadeOutAndClose"/> でこの画面を重ねたまま消す。
/// 秘密は表示しないため、画面キャプチャ除外はしない。
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly TimeSpan HoldAfterBoot = TimeSpan.FromMilliseconds(300);
    private readonly Storyboard _boot;
    private bool _completed;

    public event EventHandler? BootCompleted;

    public SplashWindow()
    {
        InitializeComponent();
        Wordmark.ItemsSource = "SECRYPT";
        ClockText.Text = $"TERMINAL 01 // {DateTime.Now:HH:mm}";
        _boot = (Storyboard)Resources["Boot"];
        _boot.Completed += (_, _) =>
        {
            StatusText.Text = "STATUS // READY";
            var hold = new DispatcherTimer { Interval = HoldAfterBoot };
            hold.Tick += (_, _) => { hold.Stop(); Complete(); };
            hold.Start();
        };
        Loaded += (_, _) => _boot.Begin(this, isControllable: true);
        MouseLeftButtonDown += (_, _) => Skip();
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) Skip(); };
    }

    private void Skip()
    {
        _boot.SkipToFill(this);
        StatusText.Text = "STATUS // READY";
        Complete();
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        BootCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>メインウィンドウの上で薄くなって閉じる。</summary>
    public void FadeOutAndClose()
    {
        // Stop すると Opacity が XAML の初期値 0 に戻るため、今の値から消す
        var from = Opacity;
        _boot.Stop(this);
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
