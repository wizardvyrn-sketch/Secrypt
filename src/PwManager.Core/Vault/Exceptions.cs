namespace PwManager.Core.Vault;

/// <summary>ファイル形式が壊れている・未対応バージョン。</summary>
public sealed class VaultFormatException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>新しい版のアプリで保存されたなど、形式のバージョンに対応していない（壊れているわけではない）。</summary>
    public bool IsUnsupportedVersion { get; init; }
}

/// <summary>鍵（パスワード・キーファイル・復旧コード等）が違うため解錠できない。</summary>
public sealed class VaultUnlockException(string message) : Exception(message);

/// <summary>ヘッダまたは本体の改ざんを検知した。</summary>
public sealed class VaultTamperedException(string message) : Exception(message);

/// <summary>スロット構成がポリシー（復旧スロット＋他 1 つ以上）を満たさない。</summary>
public sealed class SlotPolicyException(string message) : Exception(message);
