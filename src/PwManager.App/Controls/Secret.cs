using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PwManager.App.Services;
using PwManager.Presentation.Ports;

namespace PwManager.App.Controls;

/// <summary>
/// PasswordBox を ViewModel の <see cref="SecretInput"/> につなぐ添付プロパティ（バインドで内容を渡さない: ADR-0006）。
/// 例: <c>&lt;PasswordBox c:Secret.Input="{Binding Pin}"/&gt;</c>
/// </summary>
public static class Secret
{
    public static readonly DependencyProperty InputProperty = DependencyProperty.RegisterAttached(
        "Input", typeof(SecretInput), typeof(Secret), new PropertyMetadata(null, OnInputChanged));

    public static SecretInput? GetInput(DependencyObject d) => (SecretInput?)d.GetValue(InputProperty);

    public static void SetInput(DependencyObject d, SecretInput? value) => d.SetValue(InputProperty, value);

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        (e.OldValue as SecretInput)?.Attach(null);
        if (e.NewValue is not SecretInput input) return;
        switch (d)
        {
            case RevealablePasswordBox r:
                input.Attach(r);
                r.Changed += (_, _) => input.RaiseChanged();
                break;
            case PasswordBox box:
                input.Attach(new PasswordBoxSecret(box));
                box.PasswordChanged += (_, _) => input.RaiseChanged();
                break;
        }
    }

    /// <summary>PasswordBox を ISecretInput として扱う。</summary>
    private sealed class PasswordBoxSecret(PasswordBox box) : ISecretInput
    {
        public int Length => SecureInput.Length(box);

        public byte[] ReadUtf8() => SecureInput.ToUtf8(box);

        public void SetUtf8(ReadOnlySpan<byte> utf8) => box.Password = Encoding.UTF8.GetString(utf8);

        public void Clear() => box.Clear();

        public void Focus() => FocusLater(box);
    }

    /// <summary>表示の切り替え直後や画面の読み込み前でもフォーカスできるよう、後で移す。</summary>
    internal static void FocusLater(UIElement element)
        => element.Dispatcher.BeginInvoke(() => element.Focus(), DispatcherPriority.Input);
}
