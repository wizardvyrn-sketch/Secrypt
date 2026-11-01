using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using PwManager.Core.Localization;

namespace PwManager.App.Views;

/// <summary>
/// テーマに合わせた小さなダイアログ（PIN・パスワードの入力、確認、復旧コード表示）。コードで組み立てる。
/// </summary>
public sealed class ThemedDialog : Window
{
    private readonly StackPanel _body = new() { Margin = new Thickness(20, 14, 20, 8) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 4, 14, 14) };
    private readonly TextBlock _error;

    public ThemedDialog(Window owner, string title, double width = 440)
    {
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brush("Bg");
        Foreground = Brush("Text");
        FontFamily = (FontFamily)System.Windows.Application.Current.FindResource("UiFont");
        FontSize = 13;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 34, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var caption = new Grid { Height = 34, Background = Brush("Panel") };
        caption.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Brush("Accent"),
        });
        caption.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 1, Background = Brush("AccentGradient"), Opacity = 0.55 });

        _error = new TextBlock { Style = (Style)System.Windows.Application.Current.FindResource("WarnText"), Visibility = Visibility.Collapsed };

        var root = new DockPanel();
        DockPanel.SetDock(caption, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        root.Children.Add(caption);
        root.Children.Add(_buttons);
        var bodyWrap = new StackPanel();
        bodyWrap.Children.Add(_body);
        var errWrap = new Border { Margin = new Thickness(20, 0, 20, 6), Child = _error };
        bodyWrap.Children.Add(errWrap);
        root.Children.Add(bodyWrap);
        Content = new Border { BorderThickness = new Thickness(1), BorderBrush = Brush("Line"), Child = root };
    }

    public TextBlock AddText(string text, string style = "Body")
    {
        var tb = new TextBlock { Text = text, Style = (Style)System.Windows.Application.Current.FindResource(style) };
        _body.Children.Add(tb);
        return tb;
    }

    public PasswordBox AddPassword(string label)
    {
        AddText(label, "FieldLabel");
        var pb = new PasswordBox();
        _body.Children.Add(pb);
        return pb;
    }

    public TextBox AddTextBox(string label, string initial = "")
    {
        AddText(label, "FieldLabel");
        var tb = new TextBox { Text = initial };
        _body.Children.Add(tb);
        return tb;
    }

    /// <summary>復旧コードなどを大きく表示する。</summary>
    public void AddCode(string code)
    {
        var border = new Border { Style = (Style)System.Windows.Application.Current.FindResource("Card"), Margin = new Thickness(0, 10, 0, 4) };
        border.Child = new TextBlock
        {
            Text = code.Replace("-", "  "),
            FontFamily = (FontFamily)System.Windows.Application.Current.FindResource("CodeFont"),
            FontSize = 22,
            Foreground = Brush("Accent"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 34,
        };
        _body.Children.Add(border);
    }

    public void AddElement(UIElement element) => _body.Children.Add(element);

    /// <summary>復旧コードの表示・鍵ファイルのテキスト入力などに使うため、メインウィンドウと同じくキャプチャから除外する。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.CaptureProtection.Apply(this);
    }

    /// <summary>OK ボタン。validate が null 以外を返したらエラーとして表示し、閉じない。</summary>
    public void AddButtons(string okText, string? cancelText = null, Func<string?>? validate = null, bool showCancel = true)
    {
        if (showCancel)
        {
            var cancel = new Button { Content = cancelText ?? L.T("キャンセル", "Cancel"), Style = (Style)System.Windows.Application.Current.FindResource("QuietButton"), IsCancel = true };
            cancel.Click += (_, _) => DialogResult = false;
            _buttons.Children.Add(cancel);
        }
        var ok = new Button { Content = okText, Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        ok.Click += (_, _) =>
        {
            var err = validate?.Invoke();
            if (err is not null) { ShowError(err); return; }
            DialogResult = true;
        };
        _buttons.Children.Add(ok);
    }

    public void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    /// <summary>確認ダイアログ。</summary>
    public static bool Confirm(Window owner, string title, string message, string okText = "OK")
    {
        var d = new ThemedDialog(owner, title);
        d.AddText(message);
        d.AddButtons(okText);
        return d.ShowDialog() == true;
    }

    /// <summary>
    /// 3 択以上の確認ダイアログ。選んだボタンの番号（0 始まり）を返す。Esc・ウィンドウを閉じた場合は cancelIndex。
    /// 先頭のボタンが既定（Enter）。
    /// </summary>
    public static int Choose(Window owner, string title, string message, int cancelIndex, params string[] options)
    {
        var d = new ThemedDialog(owner, title, 480);
        d.AddText(message);
        int chosen = cancelIndex;
        for (int i = options.Length - 1; i >= 0; i--)
        {
            int index = i;
            var b = new Button
            {
                Content = options[i],
                Style = (Style)System.Windows.Application.Current.FindResource(i == 0 ? "PrimaryButton" : "QuietButton"),
                IsDefault = i == 0,
                IsCancel = i == cancelIndex,
            };
            b.Click += (_, _) => { chosen = index; d.DialogResult = true; };
            d._buttons.Children.Add(b);
        }
        return d.ShowDialog() == true ? chosen : cancelIndex;
    }

    /// <summary>お知らせダイアログ。</summary>
    public static void Info(Window owner, string title, string message)
    {
        var d = new ThemedDialog(owner, title);
        d.AddText(message);
        d.AddButtons("OK", showCancel: false);
        d.ShowDialog();
    }

    private static Brush Brush(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
}
