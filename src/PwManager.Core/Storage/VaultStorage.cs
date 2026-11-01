namespace PwManager.Core.Storage;

/// <summary>キーファイルと Vault の置き場所の近さ（同じ場所だと一緒に流出する：脅威モデル T4c）。</summary>
public enum LocationRisk
{
    None = 0,
    /// <summary>同じドライブ（USB メモリではなく PC 内に置いている可能性）。</summary>
    SameDrive = 1,
    /// <summary>同じクラウド同期フォルダ配下。</summary>
    SameSyncFolder = 2,
    /// <summary>同じフォルダ。</summary>
    SameDirectory = 3,
}

/// <summary>Vault ファイルの保存（アトミック書き込み＋世代バックアップ）。</summary>
public static class VaultStorage
{
    public const int DefaultGenerations = 5;

    /// <summary>
    /// 一時ファイルに書いて Flush（ディスクまで）→ 既存ファイルを .bak1 へ（古い世代は繰り下げ）→ 置き換え。
    /// 書き込み途中で落ちても元のファイルは壊れない。
    /// </summary>
    public static void SaveAtomic(string path, ReadOnlySpan<byte> data, int generations = DefaultGenerations)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("保存先フォルダが不正です。", nameof(path));
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(full) && generations > 0)
                RotateBackups(full, generations);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static string BackupPath(string path, int generation) => $"{Path.GetFullPath(path)}.bak{generation}";

    private static void RotateBackups(string full, int generations)
    {
        var oldest = BackupPath(full, generations);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = generations - 1; i >= 1; i--)
        {
            var src = BackupPath(full, i);
            if (File.Exists(src)) File.Move(src, BackupPath(full, i + 1));
        }
        File.Copy(full, BackupPath(full, 1));
    }

    /// <summary>キーファイルの保存先が Vault と近すぎないかを判定する（UI で警告に使う）。</summary>
    public static LocationRisk AssessKeyfileLocation(string vaultPath, string keyfilePath, IEnumerable<string>? syncRoots = null)
    {
        var v = Path.GetFullPath(vaultPath);
        var k = Path.GetFullPath(keyfilePath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(Path.GetDirectoryName(v), Path.GetDirectoryName(k), cmp))
            return LocationRisk.SameDirectory;

        syncRoots ??= DefaultSyncRoots();
        foreach (var root in syncRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath))
        {
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (v.StartsWith(prefix, cmp) && k.StartsWith(prefix, cmp))
                return LocationRisk.SameSyncFolder;
        }

        if (string.Equals(Path.GetPathRoot(v), Path.GetPathRoot(k), cmp))
            return LocationRisk.SameDrive;

        return LocationRisk.None;
    }

    private static IEnumerable<string> DefaultSyncRoots()
    {
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var p = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(p)) yield return p;
        }
    }
}
