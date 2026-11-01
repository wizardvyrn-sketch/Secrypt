using PwManager.Application.Ports;
using PwManager.Core.Localization;
using PwManager.Core.Storage;
using PwManager.Core.Vault;

namespace PwManager.Application.Infrastructure;

/// <summary>ローカルファイルの Vault（アトミック保存・世代バックアップ・counter 照合）。</summary>
public sealed class FileVaultStore(string path, int generations = VaultStorage.DefaultGenerations) : IVaultStore
{
    private readonly string _path = Path.GetFullPath(path);

    public string Location => _path;

    public bool Exists() => File.Exists(_path);

    public byte[] ReadAll() => File.ReadAllBytes(_path);

    public void Save(ReadOnlySpan<byte> data, ulong? expectedCounter, bool rotateBackups)
    {
        if (File.Exists(_path))
        {
            if (expectedCounter is null)
                throw new VaultConflictException(L.T("保存先に既に保管ファイルがあります。上書きしません。", "A vault file already exists at the save location. It will not be overwritten."));

            var disk = ReadDiskCounter(_path, out var unsupported);
            if (unsupported)
                throw new VaultConflictException(L.T("保存先のファイルは新しい版のアプリで保存されています。上書きしません。", "The file at the save location was saved by a newer version of the app. It will not be overwritten."));
            // 読めない（壊れている）場合は上書きしてよい。壊れたファイルは .bak1 に残る。
            if (disk is { } counter && counter != expectedCounter)
                throw new VaultConflictException(L.T($"開いた後に別の場所で更新されています（開いたとき {expectedCounter}、現在 {counter}）。上書きしません。",
                $"The file was changed elsewhere after it was opened (when opened: {expectedCounter}, now: {counter}). It will not be overwritten."));
        }
        // ファイルが消されていた場合は作り直す（メモリ上の内容が最新）
        VaultStorage.SaveAtomic(_path, data, rotateBackups ? generations : 0);
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        var list = new List<BackupInfo>();
        for (int g = 1; g <= Math.Max(generations, VaultStorage.DefaultGenerations); g++)
        {
            var p = VaultStorage.BackupPath(_path, g);
            if (!File.Exists(p)) continue;
            var fi = new FileInfo(p);
            list.Add(new BackupInfo(g, p, fi.LastWriteTimeUtc, fi.Length, ReadDiskCounter(p, out _)));
        }
        return list;
    }

    public BackupPurgeResult DeleteBackups()
    {
        int deleted = 0;
        var remaining = new List<string>();
        for (int g = 1; g <= Math.Max(generations, VaultStorage.DefaultGenerations); g++)
        {
            var p = VaultStorage.BackupPath(_path, g);
            if (!File.Exists(p)) continue;
            try
            {
                File.Delete(p);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(p);
            }
        }
        return new BackupPurgeResult(deleted, remaining);
    }

    /// <summary>ヘッダの counter を読む（未認証）。読めなければ null。</summary>
    internal static ulong? ReadDiskCounter(string file, out bool unsupportedVersion)
    {
        unsupportedVersion = false;
        try
        {
            return PasswordVault.ReadHeader(File.ReadAllBytes(file)).Counter;
        }
        catch (VaultFormatException ex)
        {
            unsupportedVersion = ex.IsUnsupportedVersion;
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
