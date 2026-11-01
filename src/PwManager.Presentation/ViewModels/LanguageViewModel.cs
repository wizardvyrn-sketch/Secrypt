using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PwManager.Application.Services;
using PwManager.Core.Localization;

namespace PwManager.Presentation.ViewModels;

/// <summary>
/// 表示言語の切り替え（docs/08 §8.1）。初期設定と解錠の画面に置く「日本語 / English」から使い、選んだ言語を設定に保存する。
/// 切り替えると <see cref="L.Changed"/> が出て、ShellViewModel が表示中の画面を作り直す。
/// </summary>
public sealed partial class LanguageViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public LanguageViewModel(SettingsService settings)
    {
        _settings = settings;
        L.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsJapanese));
            OnPropertyChanged(nameof(IsEnglish));
        };
    }

    public bool IsJapanese
    {
        get => L.Current == UiLanguage.Ja;
        set { if (value) Switch(UiLanguage.Ja); }
    }

    public bool IsEnglish
    {
        get => L.Current == UiLanguage.En;
        set { if (value) Switch(UiLanguage.En); }
    }

    /// <summary>起動時（App から）: 保存した言語、無ければ Windows の表示言語に合わせる。テストでは呼ばない（日本語のまま）。</summary>
    public void ApplySaved(CultureInfo uiCulture)
        => L.Set(L.FromCode(_settings.Current.Language) ?? L.FromCulture(uiCulture));

    private void Switch(UiLanguage language)
    {
        if (L.Current == language) return;
        _settings.Update(s => s.Language = L.ToCode(language));
        L.Set(language);
    }
}
