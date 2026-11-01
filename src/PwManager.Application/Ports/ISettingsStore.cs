namespace PwManager.Application.Ports;

/// <summary>秘密を含まない設定（自動ロック時間など）。</summary>
public sealed class AppSettings
{
    public const int MinAutoLockMinutes = 1, MaxAutoLockMinutes = 120, DefaultAutoLockMinutes = 5;
    public const int MinClipboardSeconds = 5, MaxClipboardSeconds = 300, DefaultClipboardSeconds = 20;

    public int AutoLockMinutes { get; set; } = DefaultAutoLockMinutes;
    public int ClipboardClearSeconds { get; set; } = DefaultClipboardSeconds;

    /// <summary>最後に使った鍵ファイルの場所（パスのみ。中身は保存しない）。</summary>
    public string? LastKeyfilePath { get; set; }

    /// <summary>詳細ログ（Debug レベル）を出すか。</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>表示言語（"ja" / "en"）。未設定（null）なら Windows の表示言語に合わせる。</summary>
    public string? Language { get; set; }

    /// <summary>範囲外の値（手で編集された設定ファイルなど）を既定値の範囲に収める。</summary>
    public AppSettings Normalize()
    {
        AutoLockMinutes = AutoLockMinutes is >= MinAutoLockMinutes and <= MaxAutoLockMinutes ? AutoLockMinutes : DefaultAutoLockMinutes;
        ClipboardClearSeconds = ClipboardClearSeconds is >= MinClipboardSeconds and <= MaxClipboardSeconds ? ClipboardClearSeconds : DefaultClipboardSeconds;
        if (string.IsNullOrWhiteSpace(LastKeyfilePath)) LastKeyfilePath = null;
        Language = Core.Localization.L.FromCode(Language) is { } lang ? Core.Localization.L.ToCode(lang) : null;
        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>設定の読み書き。実装は <see cref="Infrastructure.JsonSettingsStore"/>。</summary>
public interface ISettingsStore
{
    /// <summary>読む。ない・壊れている場合は既定値。</summary>
    AppSettings Load();

    /// <summary>書く。失敗しても例外にしない（設定は補助的な情報のため）。成否を返す。</summary>
    bool Save(AppSettings settings);
}
