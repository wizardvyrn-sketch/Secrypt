using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using PwManager.Core.Localization;

namespace PwManager.App.Controls;

/// <summary>
/// XAML の文言を日本語と英語の対で書く（docs/08 §8.1）: <c>Text="{c:T Ja='解錠する', En='Unlock'}"</c>。
/// 言語を切り替えると表示中の文言も変わる（<see cref="LanguageSource"/> への Binding。弱い参照のため画面を閉じても残らない）。
/// 英語に ' を含めるときは \' と書く。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class T : MarkupExtension
{
    public string Ja { get; set; } = string.Empty;
    public string En { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(LanguageSource.Language))
        {
            Source = LanguageSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new PickConverter(Ja, En),
        }.ProvideValue(serviceProvider);

    private sealed class PickConverter(string ja, string en) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => L.Pick(value is UiLanguage lang ? lang : L.Current, ja, en);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary><see cref="L.Current"/> を WPF の Binding から見えるようにする。</summary>
public sealed class LanguageSource : INotifyPropertyChanged
{
    public static LanguageSource Instance { get; } = new();

    private LanguageSource() => L.Changed += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Binding の対象はインスタンスのプロパティである必要がある")]
    public UiLanguage Language => L.Current;

    public event PropertyChangedEventHandler? PropertyChanged;
}
