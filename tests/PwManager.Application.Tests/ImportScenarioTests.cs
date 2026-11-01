using System.Text;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Tests.Support;

namespace PwManager.Application.Tests;

/// <summary>シナリオ S-05: ブラウザ（Chrome / Edge）に保存したパスワードの取り込み（S16）。</summary>
public sealed class ImportScenarioTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly TestApp _pc;

    public ImportScenarioTests() => _pc = _world.NewPc();

    public void Dispose() => _world.Dispose();

    private Task Ready() => _pc.CompleteSetupAsync(_world.Secret("Pk7x9q"), _world.Secret("correct horse battery"));

    /// <summary>実際のエクスポートに近い CSV を書く（BOM・引用符・改行入りのメモ・Android アプリ）。</summary>
    private string WriteCsv(string content, bool bom = true)
    {
        var path = Path.Combine(_world.Root, "Downloads", "Chrome パスワード.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(bom));
        return path;
    }

    private OpResult<ImportPreview> Preview(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return _pc.Import.Preview(reader);
    }

    private const string ChromeCsv =
        "name,url,username,password,note\r\n" +
        "accounts.google.com,https://accounts.google.com/signin,taro@gmail.com,g00gle-secret-pw,\r\n" +
        "\"Amazon, 日本\",https://www.amazon.co.jp/ap/signin,taro,\"amz\"\"quote-secret\",\"1 行目\r\n2 行目\"\r\n" +
        "com.example.bank,android://AbCdEf@com.example.bank/,0123456,android-secret-pw,\r\n" +
        "accounts.google.com,https://accounts.google.com/signin,taro@gmail.com,g00gle-secret-pw,\r\n" +
        "accounts.google.com,https://accounts.google.com/signin,taro@gmail.com,g00gle-other-pw,\r\n" +
        "empty.example,https://empty.example/,someone,,\r\n";

    private void RegisterCsvSecrets()
    {
        foreach (var s in new[] { "g00gle-secret-pw", "amz\"quote-secret", "android-secret-pw", "g00gle-other-pw" }) _world.Secret(s);
    }

    [Fact]
    public async Task S16_ChromeのCSVを取り込む_重複と空のパスワードを数え_メモの改行や引用符を保つ()
    {
        await Ready();
        RegisterCsvSecrets();
        var path = WriteCsv(ChromeCsv);
        using var preview = Preview(path).Value!;

        Assert.Equal(4, preview.ImportCount);        // google×2（パスワード違い）・amazon・android
        Assert.Equal(1, preview.DuplicatesInFile);   // 完全に同じ行
        Assert.Equal(1, preview.EmptyPasswordRows);
        Assert.Equal(0, preview.SameAsExisting);

        Assert.Equal(4, (await _pc.Import.CommitAsync(preview)).Value);
        var amazon = Assert.Single(_pc.Entries.Search("amazon"));
        Assert.Equal("Amazon, 日本", amazon.Title);
        Assert.Equal("1 行目\r\n2 行目", amazon.Notes);
        Assert.Equal(Encoding.UTF8.GetBytes("amz\"quote-secret"), amazon.Password);
        Assert.Contains("imported", amazon.Tags);
        var android = Assert.Single(_pc.Entries.Search("com.example.bank"));
        Assert.Equal("0123456", android.UserName); // 先頭の 0 を落とさない

        Assert.True(_pc.Import.DeleteSource(path).Ok);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task S16_2回目の取り込み_同じものは取り込まず_パスワードが変わったものは取り込んで知らせる()
    {
        await Ready();
        RegisterCsvSecrets();
        using (var first = Preview(WriteCsv(ChromeCsv)).Value!) await _pc.Import.CommitAsync(first);

        var changed = "name,url,username,password\n" +
                      "accounts.google.com,https://accounts.google.com/signin,taro@gmail.com,g00gle-secret-pw\n" +
                      "Amazon,https://www.amazon.co.jp/ap/signin,taro,amazon-new-secret\n";
        _world.Secret("amazon-new-secret");
        using var second = Preview(WriteCsv(changed, bom: false)).Value!; // Edge 形式（note なし・BOM なし）
        Assert.Equal(1, second.SameAsExisting);
        Assert.Equal(1, second.ConflictsWithExisting);
        Assert.Equal(1, second.ImportCount);
        await _pc.Import.CommitAsync(second);
        Assert.Equal(2, _pc.Entries.Search("amazon").Count); // 古い方を利用者が整理する
    }

    [Fact]
    public async Task S16_確認画面でキャンセルすると何も変わらず_読み込んだパスワードは消える()
    {
        await Ready();
        RegisterCsvSecrets();
        var counter = _pc.Session.Counter;
        var preview = Preview(WriteCsv(ChromeCsv)).Value!;
        var held = preview.ToImport.ToList();
        preview.Dispose();
        Assert.Empty(_pc.Session.Entries);
        Assert.Equal(counter, _pc.Session.Counter);
        Assert.All(held, e => Assert.Empty(e.Password));
    }

    [Fact]
    public async Task S16_取り込みの保存に失敗したら1件も増えない()
    {
        await Ready();
        RegisterCsvSecrets();
        using var preview = Preview(WriteCsv(ChromeCsv)).Value!;
        _pc.Store.FailNextSave = new IOException("ディスクの空き容量が不足しています");
        Assert.Equal(ErrorCode.IoError, (await _pc.Import.CommitAsync(preview)).Error);
        Assert.Empty(_pc.Session.Entries);
        // やり直せる
        Assert.Equal(4, (await _pc.Import.CommitAsync(preview)).Value);
    }

    [Fact]
    public async Task S16_ブラウザのCSVではない_空のファイル()
    {
        await Ready();
        Assert.Equal(ErrorCode.ImportNotBrowserCsv, Preview(WriteCsv("日付,金額,摘要\n2026/10/01,1000,食費\n")).Error);
        using var empty = Preview(WriteCsv(string.Empty)).Value!;
        Assert.Equal(0, empty.ImportCount);
    }

    [Fact]
    public async Task ロック中は取り込めない()
    {
        await Ready();
        _pc.Session.RequestLock(Session.LockReason.Manual);
        Assert.Equal(ErrorCode.Locked, Preview(WriteCsv(ChromeCsv)).Error);
    }
}
