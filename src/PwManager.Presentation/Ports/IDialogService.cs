namespace PwManager.Presentation.Ports;

/// <summary>確認・入力のダイアログ（R-06）。実装は App の ThemedDialog。テストでは応答を差し込む。</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, string okText = "OK");

    /// <summary>3 択以上。選んだ番号（0 始まり）。閉じた・Esc は cancelIndex。</summary>
    int Choose(string title, string message, int cancelIndex, params string[] options);

    void Info(string title, string message);

    /// <summary>
    /// チェックボックス付きの確認（取り込み後に CSV を削除する、など）。
    /// </summary>
    (bool Ok, bool Option) ConfirmWithOption(string title, string message, string okText, string optionText, bool optionDefault);

    /// <summary>文字列の入力。validate が null 以外を返したら表示して閉じない。キャンセルは null。</summary>
    string? PromptText(string title, string message, string label, Func<string, string?>? validate = null);

    /// <summary>
    /// PIN・パスワードの入力（確認欄を含む複数欄）。validate に各欄の UTF-8 を渡す。戻り値の配列は呼び出し側がゼロ化する。
    /// </summary>
    byte[][]? PromptSecrets(string title, string message, IReadOnlyList<string> labels, Func<IReadOnlyList<byte[]>, string?>? validate = null);

    /// <summary>
    /// 復旧コードを表示し、書き写し確認を入力してもらう。validate が null を返したら閉じて true。
    /// </summary>
    bool ShowRecoveryCode(string title, string message, string code, string checkLabel, Func<string, string?> validate);

    string? PickOpenFile(string title, string filter, string? initialPath = null);

    string? PickSaveFile(string title, string defaultFileName, string filter);

    /// <summary>鍵ファイルの予備を QR コードで印刷する。キャンセルは false。</summary>
    bool PrintKeyfileQr(string keyfileText, Guid vaultId);

    /// <summary>「鍵と設定」を開く（閉じるまで戻らない）。</summary>
    void ShowKeys(ViewModels.KeysViewModel keys);

    /// <summary>開いているダイアログ・子ウィンドウをすべて閉じる（ロック時: 復旧コードなどを画面に残さない）。</summary>
    void CloseAll();
}

/// <summary>OS の機能（ブラウザで開く・フォルダを開く）。</summary>
public interface IShellService
{
    void OpenUrl(Uri url);

    void OpenFolder(string path);
}
