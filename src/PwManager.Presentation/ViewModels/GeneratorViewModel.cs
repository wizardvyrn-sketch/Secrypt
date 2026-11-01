using CommunityToolkit.Mvvm.ComponentModel;
using PwManager.Core.Generation;
using PwManager.Core.Localization;

namespace PwManager.Presentation.ViewModels;

/// <summary>パスワード生成の設定と強さの目安。</summary>
public sealed partial class GeneratorViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntropyText))]
    private string _lengthText = "20";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(EntropyText))] private bool _lowercase = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EntropyText))] private bool _uppercase = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EntropyText))] private bool _digits = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EntropyText))] private bool _symbols = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EntropyText))] private bool _excludeAmbiguous;

    public PasswordOptions? Options => int.TryParse(LengthText, out var len)
        ? new PasswordOptions { Length = len, Lowercase = Lowercase, Uppercase = Uppercase, Digits = Digits, Symbols = Symbols, ExcludeAmbiguous = ExcludeAmbiguous }
        : null;

    public string EntropyText
    {
        get
        {
            var o = Options;
            if (o is null) return L.T("数字で入力", "Enter a number");
            var bits = PasswordGenerator.EstimateEntropyBits(o);
            return bits <= 0
                ? L.T($"長さ {PasswordGenerator.MinLength}〜{PasswordGenerator.MaxLength}、文字種を 1 つ以上",
                      $"Length {PasswordGenerator.MinLength}–{PasswordGenerator.MaxLength}, at least one character type")
                : L.T($"強さの目安: 約 {bits:0} ビット", $"Estimated strength: about {bits:0} bits");
        }
    }
}
