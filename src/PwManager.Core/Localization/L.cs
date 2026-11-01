using System.Globalization;

namespace PwManager.Core.Localization;

/// <summary>画面の表示言語。</summary>
public enum UiLanguage
{
    Ja,
    En,
}

/// <summary>
/// 表示言語の切り替え（docs/08 §8.1）。文言は呼び出し側で日本語と英語を対にして書き、<see cref="T"/> で今の言語の方を選ぶ。
/// 既定は日本語（テストは常に日本語の文言で確かめる）。アプリは起動時に設定から <see cref="Set"/> する。
/// </summary>
public static class L
{
    private static volatile UiLanguage _current = UiLanguage.Ja;

    public static UiLanguage Current => _current;

    /// <summary>言語が変わったとき（表示中の画面を作り直す・XAML の文言を更新する）。</summary>
    public static event EventHandler? Changed;

    public static void Set(UiLanguage language)
    {
        if (_current == language) return;
        _current = language;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>今の言語の文言を返す。</summary>
    public static string T(string ja, string en) => Pick(_current, ja, en);

    public static string Pick(UiLanguage language, string ja, string en) => language == UiLanguage.En ? en : ja;

    /// <summary>設定ファイル上の表記（"ja" / "en"）。</summary>
    public static string ToCode(UiLanguage language) => language == UiLanguage.En ? "en" : "ja";

    /// <summary>"ja" / "en" を読む。それ以外は null。</summary>
    public static UiLanguage? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "ja" => UiLanguage.Ja,
        "en" => UiLanguage.En,
        _ => null,
    };

    /// <summary>設定が無いときの既定: Windows の表示言語が日本語なら日本語、それ以外は英語。</summary>
    public static UiLanguage FromCulture(CultureInfo culture)
        => culture.TwoLetterISOLanguageName == "ja" ? UiLanguage.Ja : UiLanguage.En;
}
