using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PwManager.Application.Infrastructure;
using PwManager.Application.Logging;
using PwManager.Application.Policies;
using PwManager.Application.Results;
using PwManager.Application.Services;
using PwManager.Application.Session;
using PwManager.Core.Crypto;

namespace PwManager.Application.Tests.Support;

/// <summary>
/// テスト 1 件分の「世界」: 一時フォルダ、共有の時計（日本時間）、使った秘密の一覧。
/// 1 台の PC ＝ <see cref="TestApp"/>。同じ Vault ファイルを 2 台の PC から開く（同期フォルダ）こともできる。
/// Dispose 時に、すべてのログと設定ファイルに秘密が含まれていないことを確認する（カナリア: docs/10 §3.1）。
/// </summary>
internal sealed class TestWorld : IDisposable
{
    /// <summary>テスト用の軽い KDF（本番の 256 MiB / 64 MiB では遅すぎる）。</summary>
    public static readonly VaultOptions FastKdf = new()
    {
        PasswordKdf = new KdfParameters(8 * 1024, 1),
        PinKdf = new KdfParameters(8 * 1024, 1),
        RecoveryKdf = new KdfParameters(8 * 1024, 1),
    };

    private readonly List<TestApp> _apps = [];
    private readonly HashSet<string> _secrets = [];

    public TestWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "pwm-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(UsbDrive);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(9)));
        Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"));
    }

    public string Root { get; }
    public FakeTimeProvider Time { get; }

    /// <summary>Vault の既定の場所（PC 内のフォルダ）。</summary>
    public string VaultPath => Path.Combine(Root, "data", "vault.pwmv");

    /// <summary>USB メモリ（鍵ファイルの置き場所）。</summary>
    public string UsbDrive => Path.Combine(Root, "usb");

    public string UsbKeyfile(string name = "NewPWManageTool.pwmk") => Path.Combine(UsbDrive, name);

    /// <summary>1 台の PC（アプリ 1 インスタンス）を用意する。</summary>
    public TestApp NewPc(string name = "pc1", FakeTpm? tpm = null, string? vaultPath = null)
    {
        var app = new TestApp(this, name, tpm ?? new FakeTpm(), vaultPath ?? VaultPath);
        _apps.Add(app);
        return app;
    }

    /// <summary>秘密として登録する（ログに出てはいけない文字列）。</summary>
    public string Secret(string value)
    {
        _secrets.Add(value);
        return value;
    }

    /// <summary>秘密の UTF-8 バイト列（呼ぶたびに新しい配列。サービスがゼロ化するため）。</summary>
    public static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public IReadOnlyList<string> AllLogLines()
        => _apps.SelectMany(a => Directory.Exists(a.LogDirectory) ? Directory.GetFiles(a.LogDirectory, "*.jsonl") : [])
            .SelectMany(PwManager.Application.Logging.SharedFileReader.ReadAllLines).ToList();

    public void Dispose()
    {
        foreach (var app in _apps) app.Dispose();
        try
        {
            var texts = _apps.SelectMany(a => (Directory.Exists(a.LogDirectory) ? Directory.GetFiles(a.LogDirectory) : [])
                    .Append(a.SettingsPath).Where(File.Exists))
                .Select(f => (File: f, Text: PwManager.Application.Logging.SharedFileReader.ReadAllText(f)))
                .ToList();
            foreach (var secret in _secrets)
            {
                var variants = new[] { secret, secret.Replace("-", string.Empty, StringComparison.Ordinal) };
                foreach (var (file, text) in texts)
                    foreach (var v in variants.Where(v => v.Length >= 6))
                        Assert.False(text.Contains(v, StringComparison.OrdinalIgnoreCase), $"秘密がログ・設定に出力されています: {Path.GetFileName(file)}");
            }
        }
        finally
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}

/// <summary>1 台の PC で動くアプリ（サービス一式）。</summary>
internal sealed class TestApp : IDisposable
{
    private readonly ILoggerFactory _loggers;

    public TestApp(TestWorld world, string name, FakeTpm tpm, string vaultPath)
    {
        World = world;
        Name = name;
        Tpm = tpm;
        var data = Path.Combine(world.Root, name);
        LogDirectory = Path.Combine(data, "logs");
        SettingsPath = Path.Combine(data, "settings.json");
        LogProvider = new JsonlFileLoggerProvider(LogDirectory, () => LogLevel.Debug, world.Time);
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(LogProvider));

        Store = new FaultyVaultStore(vaultPath);
        Keyfiles = new FaultyKeyfileStore();
        Clipboard = new RecordingClipboard();
        Settings = new SettingsService(new JsonSettingsStore(SettingsPath, _loggers.CreateLogger<JsonSettingsStore>()), _loggers.CreateLogger<SettingsService>());
        Session = new VaultSession(Store, Clipboard, world.Time, _loggers.CreateLogger<VaultSession>());
        Backoff = new UnlockBackoffPolicy(world.Time);
        Unlock = new UnlockService(Session, Tpm, Keyfiles, Settings, Backoff, world.Time, _loggers.CreateLogger<UnlockService>());
        Setup = new SetupService(Session, Tpm, Keyfiles, Settings, TestWorld.FastKdf, _loggers.CreateLogger<SetupService>());
        Slots = new SlotService(Session, Tpm, Keyfiles, TestWorld.FastKdf, _loggers.CreateLogger<SlotService>());
        Entries = new EntryService(Session, _loggers.CreateLogger<EntryService>());
        Import = new ImportService(Session, _loggers.CreateLogger<ImportService>());
        AutoLock = new AutoLockMonitor(Session, world.Time);
    }

    public TestWorld World { get; }
    public string Name { get; }
    public FakeTpm Tpm { get; }
    public string LogDirectory { get; }
    public string SettingsPath { get; }
    public JsonlFileLoggerProvider LogProvider { get; }
    public FaultyVaultStore Store { get; }
    public FaultyKeyfileStore Keyfiles { get; }
    public RecordingClipboard Clipboard { get; }
    public SettingsService Settings { get; }
    public VaultSession Session { get; }
    public UnlockBackoffPolicy Backoff { get; }
    public UnlockService Unlock { get; }
    public SetupService Setup { get; }
    public SlotService Slots { get; }
    public EntryService Entries { get; }
    public ImportService Import { get; }
    public AutoLockMonitor AutoLock { get; }

    /// <summary>初期設定を最後まで行う（TPM あり/なし）。復旧コードと鍵ファイルのテキストを返す。</summary>
    public async Task<SetupSecrets> CompleteSetupAsync(string pin, string password, string? keyfilePath = null, bool useTpm = true)
    {
        keyfilePath ??= World.UsbKeyfile();
        using var flow = Setup.Begin();
        if (useTpm) Assert.Equal(ErrorCode.None, (await flow.EnrollTpmAsync(TestWorld.B(pin), TestWorld.B(pin))).Error);
        else Assert.True(flow.SkipTpm().Ok);
        var kf = await flow.CreateKeyfileAsync(keyfilePath, TestWorld.B(password), TestWorld.B(password));
        Assert.Equal(ErrorCode.None, kf.Error);
        var challenge = await flow.CreateRecoveryCodeAsync();
        Assert.True(challenge.Ok);
        var groups = challenge.Value!.DisplayCode.Split('-');
        var finish = await flow.FinishAsync(groups[challenge.Value.GroupA], groups[challenge.Value.GroupB]);
        Assert.Equal(ErrorCode.None, finish.Error);
        World.Secret(challenge.Value.DisplayCode);
        World.Secret(kf.Value!);
        return new SetupSecrets(challenge.Value.DisplayCode, kf.Value!, keyfilePath);
    }

    /// <summary>項目を 1 件追加する。</summary>
    public async Task<Guid> AddEntryAsync(string title, string user, string password, string url = "https://example.com/login")
    {
        World.Secret(password);
        var r = await Entries.CreateAsync(new EntryDraft(title, user, TestWorld.B(password), url, string.Empty, []));
        Assert.Equal(ErrorCode.None, r.Error);
        return r.Value;
    }

    public ILogger<T> Logger<T>() => _loggers.CreateLogger<T>();

    public void Dispose()
    {
        Session.Dispose();
        _loggers.Dispose();
    }
}

internal sealed record SetupSecrets(string RecoveryCode, string KeyfileText, string KeyfilePath);
