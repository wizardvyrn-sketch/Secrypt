using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PwManager.Application.Infrastructure;
using PwManager.Application.Logging;
using PwManager.Application.Ports;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Tests.Support;
using PwManager.Core.Vault;

namespace PwManager.Application.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pwm-infra-" + Guid.NewGuid().ToString("N"));

    public InfrastructureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---------- パスの伏せ字（D-4） ----------

    [Theory]
    [InlineData(@"C:\Users\taro\AppData\Local\NewPWManageTool\vault.pwmv", @"%USERPROFILE%\AppData\Local\NewPWManageTool\vault.pwmv")]
    [InlineData(@"D:\Users\taro\vault.pwmv", @"D:\Users\%USERNAME%\vault.pwmv")]
    [InlineData(@"E:\NewPWManageTool.pwmk", @"E:\NewPWManageTool.pwmk")]
    [InlineData(@"C:\Users\taro", "%USERPROFILE%")]
    public void PathRedactor_ユーザー名を伏せる(string input, string expected)
        => Assert.Equal(expected, PathRedactor.Redact(input, @"C:\Users\taro", "taro"));

    [Fact]
    public void PathRedactor_文章中のパスも伏せる()
        => Assert.Equal(@"Could not find file '%USERPROFILE%\usb\a.pwmk'.",
            PathRedactor.RedactText(@"Could not find file 'C:\Users\taro\usb\a.pwmk'.", @"C:\Users\taro", "taro"));

    // ---------- ログ ----------

    [Fact]
    public void JSONLログ_1行1JSONで項目と操作IDを持ち_例外のパスは伏せる()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 8, 33, 1, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"));
        using var provider = new JsonlFileLoggerProvider(_dir, time: time);
        var log = provider.CreateLogger("Test");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using (LogOperation.Begin())
        {
            Log.UnlockFailed(log, "Pin", ErrorCode.WrongPin, 123, 2);
            Log.SaveFailed(log, "Entry.Create", "IOException", true, new IOException($"disk full: {Path.Combine(profile, "vault.pwmv")}"));
        }
        Log.SlotEnrolled(log, SlotType.Keyfile, Guid.Empty);

        var lines = PwManager.Application.Logging.SharedFileReader.ReadAllLines(Path.Combine(_dir, "pwm-20261025.jsonl"));
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        Assert.Equal("2026-10-25T17:33:01.000+09:00", root.GetProperty("t").GetString());
        Assert.Equal(2201, root.GetProperty("ev").GetInt32());
        Assert.Equal("UnlockFailed", root.GetProperty("name").GetString());
        Assert.Equal("WrongPin", root.GetProperty("fields").GetProperty("Reason").GetString());
        Assert.Equal(2, root.GetProperty("fields").GetProperty("Consecutive").GetInt32());
        var op = root.GetProperty("op").GetString();
        Assert.False(string.IsNullOrEmpty(op));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(op, second.RootElement.GetProperty("op").GetString()); // 同じ操作
        Assert.Contains("%USERPROFILE%", second.RootElement.GetProperty("ex").GetProperty("msg").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(profile, lines[1], StringComparison.Ordinal);

        using var third = JsonDocument.Parse(lines[2]);
        Assert.False(third.RootElement.TryGetProperty("op", out _));
    }

    [Fact]
    public void JSONLログ_既定はInformation以上_詳細ログでDebugも出す()
    {
        bool verbose = false;
        using var provider = new JsonlFileLoggerProvider(_dir, () => verbose ? LogLevel.Debug : LogLevel.Information);
        var log = provider.CreateLogger("Test");
        Log.BackupRotated(log, 1); // Debug
        verbose = true;
        Log.BackupRotated(log, 2);
        var all = Directory.GetFiles(_dir).SelectMany(PwManager.Application.Logging.SharedFileReader.ReadAllLines).ToList();
        Assert.Single(all);
    }

    [Fact]
    public void JSONLログ_5MBで分割し_14日より古いファイルは消す()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var old = Path.Combine(_dir, "pwm-20261001.jsonl");
        File.WriteAllText(old, "{}\n");
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = Path.Combine(_dir, "pwm-20261020.jsonl");
        File.WriteAllText(recent, "{}\n");
        File.SetLastWriteTimeUtc(recent, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));

        using var provider = new JsonlFileLoggerProvider(_dir, time: time, maxFileBytes: 400);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));

        var log = provider.CreateLogger("Test");
        for (int i = 0; i < 10; i++) Log.EntryCreated(log, Guid.NewGuid());
        var files = Directory.GetFiles(_dir, "pwm-20261025*.jsonl");
        Assert.True(files.Length > 1);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 400));
        Assert.Equal(10, files.SelectMany(PwManager.Application.Logging.SharedFileReader.ReadAllLines).Count());
    }

    [Fact]
    public void JSONLログ_書けなくてもアプリは止まらない()
    {
        var blocker = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(blocker, "x"); // ログフォルダの場所にファイルがある
        using var provider = new JsonlFileLoggerProvider(blocker);
        Log.AppStarted(provider.CreateLogger("Test"), "1.0", "test", "net10");
        Assert.Equal(1, provider.WriteFailures);
    }

    [Fact]
    public void カナリア検査そのものが機能する_ログに秘密があれば失敗する()
    {
        var world = new TestWorld();
        var pc = world.NewPc();
        var secret = world.Secret("leaked-secret-value");
        Directory.CreateDirectory(pc.LogDirectory);
        File.WriteAllText(Path.Combine(pc.LogDirectory, "pwm-20261025.jsonl"), $"{{\"msg\":\"{secret}\"}}\n");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(world.Dispose);
    }

    // ---------- 設定 ----------

    [Fact]
    public void 設定_壊れたファイルは既定値_範囲外の値は既定値に戻す_保存できなくても続ける()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.Equal(AppSettings.DefaultAutoLockMinutes, new JsonSettingsStore(path).Load().AutoLockMinutes);

        File.WriteAllText(path, "{\"AutoLockMinutes\": 0, \"ClipboardClearSeconds\": 99999, \"LastKeyfilePath\": \"  \"}");
        var s = new JsonSettingsStore(path).Load();
        Assert.Equal(AppSettings.DefaultAutoLockMinutes, s.AutoLockMinutes);
        Assert.Equal(AppSettings.DefaultClipboardSeconds, s.ClipboardClearSeconds);
        Assert.Null(s.LastKeyfilePath);

        var service = new SettingsService(new JsonSettingsStore(path));
        int changed = 0;
        service.Changed += (_, _) => changed++;
        Assert.True(service.Update(x => x.AutoLockMinutes = 15));
        Assert.Equal(15, new JsonSettingsStore(path).Load().AutoLockMinutes);
        Assert.Equal(1, changed);

        var blocked = new SettingsService(new JsonSettingsStore(Path.Combine(path, "child.json")));
        Assert.False(blocked.Update(x => x.AutoLockMinutes = 30));
        Assert.Equal(30, blocked.Current.AutoLockMinutes); // メモリ上は反映
    }

    [Fact]
    public void 鍵ファイルの場所は暗号化して保存し_以前の平文は読み込み時に暗号化し直す()
    {
        var path = Path.Combine(_dir, "settings.json");
        var protector = new ReversingProtector();
        const string keyfile = @"E:\Secrypt.pwmk";

        new JsonSettingsStore(path, protector: protector).Save(new AppSettings { LastKeyfilePath = keyfile, AutoLockMinutes = 7 });
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Secrypt.pwmk", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LastKeyfilePath\"", text, StringComparison.Ordinal);
        var loaded = new JsonSettingsStore(path, protector: protector).Load();
        Assert.Equal(keyfile, loaded.LastKeyfilePath);
        Assert.Equal(7, loaded.AutoLockMinutes);

        // 以前の版が平文で保存した設定
        File.WriteAllText(path, "{\"AutoLockMinutes\": 9, \"LastKeyfilePath\": \"E:\\\\Secrypt.pwmk\"}");
        Assert.Equal(keyfile, new JsonSettingsStore(path, protector: protector).Load().LastKeyfilePath);
        Assert.DoesNotContain("Secrypt.pwmk", File.ReadAllText(path), StringComparison.Ordinal);

        // 復号できない（別のユーザー・別の PC）場合は場所を覚えていないだけで、他の設定は読める
        Assert.Null(new JsonSettingsStore(path, protector: new FailingProtector()).Load().LastKeyfilePath);
        Assert.Equal(9, new JsonSettingsStore(path, protector: new FailingProtector()).Load().AutoLockMinutes);
    }

    private sealed class ReversingProtector : ISettingsProtector
    {
        public string Protect(string plainText) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plainText.Reverse().ToArray())));
        public string? Unprotect(string protectedText)
            => protectedText.StartsWith("enc:", StringComparison.Ordinal)
                ? new string(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[4..])).Reverse().ToArray())
                : null;
    }

    private sealed class FailingProtector : ISettingsProtector
    {
        public string Protect(string plainText) => throw new System.Security.Cryptography.CryptographicException();
        public string? Unprotect(string protectedText) => null;
    }

    [Fact]
    public void データフォルダは環境変数で切り替えられる()
    {
        var before = Environment.GetEnvironmentVariable(AppDataPaths.DataEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataEnvironmentVariable, _dir);
            var paths = AppDataPaths.FromEnvironment();
            Assert.Equal(Path.Combine(_dir, "settings.json"), paths.SettingsPath);
            Assert.Equal(Path.Combine(_dir, "logs"), paths.LogDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataEnvironmentVariable, before);
        }
    }

    // ---------- Vault の保存先 ----------

    [Fact]
    public void 保存先_新しい版のアプリで保存されたファイルは上書きしない()
    {
        var path = Path.Combine(_dir, "vault.pwmv");
        File.WriteAllBytes(path, [.. "PWMV"u8, 9, 0, 0, 0, 0, 0]); // version 9
        var store = new FileVaultStore(path);
        Assert.Throws<VaultConflictException>(() => store.Save([1, 2, 3], expectedCounter: 3, rotateBackups: true));

        File.WriteAllBytes(path, [1, 2, 3]); // 壊れている（Vault ですらない）→ 上書きしてよく、.bak1 に残る
        store.Save([4, 5, 6], expectedCounter: 3, rotateBackups: true);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path + ".bak1"));
        var backup = Assert.Single(store.ListBackups());
        Assert.Null(backup.Counter);
    }
}
