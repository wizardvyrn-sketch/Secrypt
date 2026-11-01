using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Import;
using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>
/// 取り込みの確認内容。確定（CommitAsync）しなかった項目のパスワードは Dispose でゼロ化する。
/// </summary>
public sealed class ImportPreview : IDisposable
{
    internal ImportPreview(List<VaultEntry> toImport, List<VaultEntry> skipped, int sameAsExisting, int conflictsWithExisting, int duplicatesInFile, int emptyPasswordRows)
    {
        ToImport = toImport;
        Skipped = skipped;
        SameAsExisting = sameAsExisting;
        ConflictsWithExisting = conflictsWithExisting;
        DuplicatesInFile = duplicatesInFile;
        EmptyPasswordRows = emptyPasswordRows;
    }

    internal List<VaultEntry> ToImport { get; }
    internal List<VaultEntry> Skipped { get; }
    internal bool Committed { get; set; }

    /// <summary>取り込む件数。</summary>
    public int ImportCount => ToImport.Count;

    /// <summary>登録済みと同じ（URL・ユーザー名・パスワードが一致）ため取り込まない件数。</summary>
    public int SameAsExisting { get; }

    /// <summary>登録済みと URL・ユーザー名が同じでパスワードが違う件数（取り込む。古い方の整理を案内する）。</summary>
    public int ConflictsWithExisting { get; }

    /// <summary>CSV の中で完全に重複していたため 1 件にまとめた件数。</summary>
    public int DuplicatesInFile { get; }

    /// <summary>パスワードが空でスキップした行数。</summary>
    public int EmptyPasswordRows { get; }

    public void Dispose()
    {
        foreach (var e in Skipped) e.SetPassword([]);
        if (!Committed) foreach (var e in ToImport) e.SetPassword([]);
        Skipped.Clear();
    }
}

/// <summary>ブラウザ（Chrome / Edge）のパスワード CSV の取り込み（F7）。</summary>
public sealed class ImportService(VaultSession session, ILogger<ImportService>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<ImportService>.Instance;

    /// <summary>CSV を読み、登録済みとの重複を調べる（まだ Vault は変えない）。</summary>
    public OpResult<ImportPreview> Preview(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!session.IsUnlocked) return OpResult<ImportPreview>.Fail(ErrorCode.Locked);

        ImportResult parsed;
        try { parsed = BrowserCsvImporter.Import(reader); }
        catch (FormatException)
        {
            Log.ImportFailed(_log, ErrorCode.ImportNotBrowserCsv);
            return OpResult<ImportPreview>.Fail(ErrorCode.ImportNotBrowserCsv);
        }

        var toImport = new List<VaultEntry>();
        var skipped = new List<VaultEntry>();
        int same = 0, conflicts = 0, inFile = 0;
        foreach (var e in parsed.Entries)
        {
            if (toImport.Any(x => SameCredential(x, e)))
            {
                inFile++;
                skipped.Add(e);
                continue;
            }
            var existing = session.Entries.Where(x => SameKey(x, e)).ToList();
            if (existing.Any(x => SamePassword(x, e)))
            {
                same++;
                skipped.Add(e);
                continue;
            }
            if (existing.Count > 0) conflicts++;
            toImport.Add(e);
        }

        Log.ImportPreviewed(_log, parsed.Entries.Count + parsed.SkippedRows, toImport.Count, same, inFile, parsed.SkippedRows);
        return OpResult<ImportPreview>.Success(new ImportPreview(toImport, skipped, same, conflicts, inFile, parsed.SkippedRows));
    }

    /// <summary>確認した内容を取り込んで保存する。</summary>
    public async Task<OpResult<int>> CommitAsync(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Committed) return OpResult<int>.Fail(ErrorCode.InvalidStep);
        return await session.RunExclusiveAsync<int>("Import.Commit", ctx =>
        {
            var r = ctx.Commit(v =>
            {
                v.Entries.AddRange(preview.ToImport);
                return OpResult.Success;
            });
            if (!r.Ok) return Task.FromResult(OpResult<int>.Fail(r.Error, r.Detail));
            preview.Committed = true;
            Log.ImportCommitted(_log, preview.ToImport.Count);
            return Task.FromResult(OpResult<int>.Success(preview.ToImport.Count));
        });
    }

    /// <summary>取り込んだ CSV（平文）を削除する。</summary>
    public OpResult DeleteSource(string path)
    {
        try
        {
            File.Delete(path);
            Log.ImportSourceDeleted(_log, true);
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.ImportSourceDeleted(_log, false);
            return OpResult.Fail(ErrorCode.IoError, ex.Message);
        }
    }

    private static bool SameKey(VaultEntry a, VaultEntry b)
        => string.Equals(a.Url.Trim(), b.Url.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.UserName.Trim(), b.UserName.Trim(), StringComparison.Ordinal);

    private static bool SamePassword(VaultEntry a, VaultEntry b) => CryptographicOperations.FixedTimeEquals(a.Password, b.Password);

    private static bool SameCredential(VaultEntry a, VaultEntry b) => SameKey(a, b) && SamePassword(a, b);
}
