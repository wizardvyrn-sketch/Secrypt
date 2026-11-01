namespace PwManager.Application.Ports;

/// <summary>
/// 設定ファイルに平文で置きたくない値（最後に使った鍵ファイルの場所）を暗号化する。
/// 実装は App の DpapiSettingsProtector（Windows の DPAPI、現在のユーザーだけが復号できる）。
/// 登録がない場合（OS 非依存のテスト）は平文のまま保存する。
/// </summary>
public interface ISettingsProtector
{
    string Protect(string plainText);

    /// <summary>復号できない（別のユーザー・別の PC・壊れている）場合は null。</summary>
    string? Unprotect(string protectedText);
}
