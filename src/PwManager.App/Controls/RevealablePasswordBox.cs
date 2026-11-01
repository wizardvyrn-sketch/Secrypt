using System.Text;
using System.Windows;
using System.Windows.Controls;
using PwManager.App.Services;
using PwManager.Presentation.Ports;

namespace PwManager.App.Controls;

/// <summary>
/// 伏せ字（PasswordBox）と表示（TextBox）を切り替えられるパスワード欄。IsRevealed をバインドする。
/// 内容は <see cref="Secret.InputProperty"/> 経由で ViewModel に UTF-8 で渡す。
/// </summary>
public sealed class RevealablePasswordBox : Grid, ISecretInput
{
    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(RevealablePasswordBox),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((RevealablePasswordBox)d).ApplyReveal((bool)e.NewValue)));

    private readonly PasswordBox _hidden = new();
    private readonly TextBox _plain = new() { Visibility = Visibility.Collapsed };

    public RevealablePasswordBox()
    {
        Children.Add(_hidden);
        Children.Add(_plain);
        _hidden.PasswordChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _plain.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    /// <summary>入力欄の書体（コード用の等幅風）。</summary>
    public System.Windows.Media.FontFamily FieldFontFamily
    {
        get => _hidden.FontFamily;
        set { _hidden.FontFamily = value; _plain.FontFamily = value; }
    }

    public double FieldFontSize
    {
        get => _hidden.FontSize;
        set { _hidden.FontSize = value; _plain.FontSize = value; }
    }

    public int Length => IsRevealed ? _plain.Text.Length : SecureInput.Length(_hidden);

    public byte[] ReadUtf8() => IsRevealed ? Encoding.UTF8.GetBytes(_plain.Text) : SecureInput.ToUtf8(_hidden);

    public void SetUtf8(ReadOnlySpan<byte> utf8)
    {
        var s = Encoding.UTF8.GetString(utf8);
        _hidden.Password = s;
        _plain.Text = IsRevealed ? s : string.Empty;
    }

    public void Clear()
    {
        _hidden.Clear();
        _plain.Clear();
    }

    void ISecretInput.Focus() => Secret.FocusLater(IsRevealed ? _plain : _hidden);

    private void ApplyReveal(bool reveal)
    {
        if (reveal)
        {
            _plain.Text = _hidden.Password;
            _plain.Visibility = Visibility.Visible;
            _hidden.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (_plain.Visibility == Visibility.Visible) _hidden.Password = _plain.Text;
            _plain.Text = string.Empty;
            _plain.Visibility = Visibility.Collapsed;
            _hidden.Visibility = Visibility.Visible;
        }
    }
}
