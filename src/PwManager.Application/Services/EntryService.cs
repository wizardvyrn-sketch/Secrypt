using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PwManager.Application.Logging;
using PwManager.Application.Results;
using PwManager.Application.Session;
using PwManager.Core.Vault;

namespace PwManager.Application.Services;

/// <summary>編集画面の入力内容。Password（UTF-8）は EntryService がゼロ化する。</summary>
public sealed record EntryDraft(string Title, string UserName, byte[] Password, string Url, string Notes, IReadOnlyList<string> Tags)
{
    /// <summary>タグ欄（カンマ区切り）を分割する。前後の空白を除き、空と重複を除く。</summary>
    public static IReadOnlyList<string> ParseTags(string? text)
        => (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}

/// <summary>項目の作成・更新・削除（S-03）。保存に失敗したらメモリ上も元に戻る（R-04）。</summary>
public sealed class EntryService(VaultSession session, ILogger<EntryService>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<EntryService>.Instance;

    /// <summary>検索（タイトル・ユーザー名・URL・タグの部分一致、大文字小文字を区別しない）。タイトル順。</summary>
    public IReadOnlyList<VaultEntry> Search(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        return session.Entries
            .Where(e => q.Length == 0 || Matches(e, q))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static bool Matches(VaultEntry e, string q)
    {
        const StringComparison c = StringComparison.CurrentCultureIgnoreCase;
        return e.Title.Contains(q, c) || e.UserName.Contains(q, c) || e.Url.Contains(q, c) || e.Tags.Any(t => t.Contains(q, c));
    }

    public Task<OpResult<Guid>> CreateAsync(EntryDraft draft)
        => Run("Entry.Create", draft, ctx =>
        {
            var entry = new VaultEntry();
            var now = ctx.Time.GetUtcNow();
            var r = ctx.Commit(v =>
            {
                Apply(entry, draft, now);
                entry.Created = now;
                v.Entries.Add(entry);
                return OpResult.Success;
            });
            if (!r.Ok) return OpResult<Guid>.Fail(r.Error, r.Detail);
            Log.EntryCreated(_log, entry.Id);
            return OpResult<Guid>.Success(entry.Id);
        });

    public Task<OpResult<Guid>> UpdateAsync(Guid id, EntryDraft draft)
        => Run("Entry.Update", draft, ctx =>
        {
            var entry = ctx.Vault.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return OpResult<Guid>.Fail(ErrorCode.NotFound);
            var now = ctx.Time.GetUtcNow();
            var r = ctx.Commit(_ =>
            {
                Apply(entry, draft, now);
                return OpResult.Success;
            });
            if (!r.Ok) return OpResult<Guid>.Fail(r.Error, r.Detail);
            Log.EntryUpdated(_log, entry.Id);
            return OpResult<Guid>.Success(entry.Id);
        });

    public async Task<OpResult> DeleteAsync(Guid id)
    {
        var r = await session.RunExclusiveAsync<Guid>("Entry.Delete", ctx =>
        {
            var entry = ctx.Vault.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return Task.FromResult(OpResult<Guid>.Fail(ErrorCode.NotFound));
            var c = ctx.Commit(v =>
            {
                v.Entries.Remove(entry);
                return OpResult.Success;
            });
            if (!c.Ok) return Task.FromResult(OpResult<Guid>.Fail(c.Error, c.Detail));
            entry.SetPassword([]); // 一覧から消えた項目のパスワードは残さない
            Log.EntryDeleted(_log, id);
            return Task.FromResult(OpResult<Guid>.Success(id));
        });
        return r;
    }

    private async Task<OpResult<Guid>> Run(string operation, EntryDraft draft, Func<VaultContext, OpResult<Guid>> work)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            if (string.IsNullOrWhiteSpace(draft.Title) && string.IsNullOrWhiteSpace(draft.Url))
                return OpResult<Guid>.Fail(ErrorCode.TitleOrUrlRequired);
            return await session.RunExclusiveAsync<Guid>(operation, ctx => Task.FromResult(work(ctx)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft.Password);
        }
    }

    private static void Apply(VaultEntry entry, EntryDraft draft, DateTimeOffset now)
    {
        entry.Title = draft.Title.Trim();
        entry.UserName = draft.UserName.Trim();
        entry.Url = draft.Url.Trim();
        entry.Notes = draft.Notes;
        entry.Tags = draft.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (!CryptographicOperations.FixedTimeEquals(entry.Password, draft.Password)) entry.SetPassword(draft.Password);
        entry.Modified = now;
    }
}
