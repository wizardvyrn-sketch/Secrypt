using System.Text;
using PwManager.Core.Vault;

namespace PwManager.Core.Import;

/// <summary>取り込み結果。</summary>
public sealed record ImportResult(List<VaultEntry> Entries, int SkippedRows, List<string> Warnings);

/// <summary>
/// Chrome / Edge の「パスワードをエクスポート」CSV を取り込む。
/// 列: name,url,username,password[,note]（Edge は note がない場合あり）。列順はヘッダ行で判定する。
/// </summary>
public static class BrowserCsvImporter
{
    public static ImportResult Import(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var warnings = new List<string>();
        var entries = new List<VaultEntry>();
        int skipped = 0;

        var rows = ParseCsv(reader).ToList();
        if (rows.Count == 0) return new ImportResult(entries, 0, ["ファイルが空です。"]);

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int iName = header.IndexOf("name"), iUrl = header.IndexOf("url"), iUser = header.IndexOf("username"),
            iPass = header.IndexOf("password"), iNote = header.IndexOf("note");
        if (iPass < 0 || (iUrl < 0 && iName < 0))
            throw new FormatException("ブラウザのパスワード CSV ではありません（name / url / username / password の列が必要です）。");

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Get(int i) => i >= 0 && i < row.Count ? row[i] : string.Empty;
            var password = Get(iPass);
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue; // 空行
            if (string.IsNullOrEmpty(password))
            {
                skipped++;
                warnings.Add($"{r + 1} 行目: パスワードが空のためスキップしました。");
                continue;
            }

            var url = Get(iUrl);
            var name = Get(iName);
            var e = new VaultEntry
            {
                Title = !string.IsNullOrWhiteSpace(name) ? name : HostOf(url),
                Url = url,
                UserName = Get(iUser),
                Notes = Get(iNote),
                Tags = ["imported"],
            };
            e.SetPassword(Encoding.UTF8.GetBytes(password));
            entries.Add(e);
        }

        // 同じ URL + ユーザー名の重複を通知（取り込み自体は行う）
        foreach (var g in entries.GroupBy(e => (e.Url, e.UserName)).Where(g => g.Count() > 1))
            warnings.Add($"重複: {g.Key.Url} / {g.Key.UserName}（{g.Count()} 件）");

        return new ImportResult(entries, skipped, warnings);
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>RFC 4180 形式（ダブルクォート、改行を含むフィールド）の簡易パーサ。</summary>
    internal static IEnumerable<List<string>> ParseCsv(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            any = true;
            char ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    yield return row;
                    row = [];
                    any = false;
                    break;
                default: field.Append(ch); break;
            }
        }
        if (any || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
