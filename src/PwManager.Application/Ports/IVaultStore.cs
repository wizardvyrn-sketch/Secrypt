namespace PwManager.Application.Ports;

/// <summary>バックアップ世代の情報（秘密を含まない）。</summary>
public sealed record BackupInfo(int Generation, string Path, DateTimeOffset LastWrite, long Size, ulong? Counter);

/// <summary>保存の結果、ファイルが別の場所で更新されていたため上書きしなかった（R-16）。</summary>
public sealed class VaultConflictException(string message) : Exception(message);

/// <summary>Vault ファイルの読み書き（R-07）。実装は <see cref="Infrastructure.FileVaultStore"/>。</summary>
public interface IVaultStore
{
    /// <summary>保存先（表示・ログ用）。</summary>
    string Location { get; }

    bool Exists();

    /// <summary>ファイル全体を読む。ない場合は FileNotFoundException。</summary>
    byte[] ReadAll();

    /// <summary>
    /// アトミックに保存する。
    /// <paramref name="expectedCounter"/> はこのプロセスが最後に読み書きしたときの counter。ディスク上の counter と
    /// 違えば別の場所で更新されているため上書きせず <see cref="VaultConflictException"/>。null は新規作成（既にあれば同じく例外）。
    /// <paramref name="rotateBackups"/> が false のとき（内容が変わらない保存）はバックアップ世代を回さない（R-15）。
    /// </summary>
    void Save(ReadOnlySpan<byte> data, ulong? expectedCounter, bool rotateBackups);

    /// <summary>バックアップ世代の一覧（新しい順）。</summary>
    IReadOnlyList<BackupInfo> ListBackups();

    /// <summary>
    /// バックアップ世代をすべて削除する（解錠方法を削除・作り直したとき、古い方法で開けるコピーを残さないため: docs/02 T4b）。
    /// 削除できなかったファイルのパスを返す（すべて削除できれば空）。
    /// </summary>
    BackupPurgeResult DeleteBackups();
}

/// <summary>バックアップ世代の削除結果。</summary>
public sealed record BackupPurgeResult(int Deleted, IReadOnlyList<string> Remaining);
