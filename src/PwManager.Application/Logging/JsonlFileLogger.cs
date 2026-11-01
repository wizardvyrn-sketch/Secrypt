using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PwManager.Application.Infrastructure;

namespace PwManager.Application.Logging;

/// <summary>
/// ローカルの JSONL ファイルにログを書く（ADR-0007、docs/10 §2）。外部には送らない。
/// <list type="bullet">
/// <item>場所: {logs}/pwm-YYYYMMDD.jsonl（5 MB を超えたら -1, -2 … と分割）。14 日より古いファイルは起動時に削除。</item>
/// <item>1 行 1 JSON: t（時刻）・lv・ev（イベント ID）・name・cat・op（操作 ID）・fields・ex（例外）。</item>
/// <item>文字列の値・例外のメッセージとスタックは、パスのユーザー名を伏せてから書く（D-4）。</item>
/// <item>書き込みに失敗してもアプリは止めない（ログは補助）。</item>
/// </list>
/// </summary>
public sealed class JsonlFileLoggerProvider : ILoggerProvider
{
    public const int DefaultRetentionDays = 14;
    public const long DefaultMaxFileBytes = 5L * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Func<LogLevel> _minimumLevel;
    private readonly long _maxFileBytes;
    private readonly object _gate = new();

    public JsonlFileLoggerProvider(string directory, Func<LogLevel>? minimumLevel = null, TimeProvider? time = null,
        int retentionDays = DefaultRetentionDays, long maxFileBytes = DefaultMaxFileBytes)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _minimumLevel = minimumLevel ?? (() => LogLevel.Information);
        _maxFileBytes = maxFileBytes;
        TryDeleteOld(retentionDays);
    }

    /// <summary>ログフォルダ（「ログフォルダを開く」用）。</summary>
    public string Directory => _directory;

    /// <summary>書き込みに失敗した回数（診断用）。</summary>
    public int WriteFailures { get; private set; }

    public ILogger CreateLogger(string categoryName) => new JsonlFileLogger(this, categoryName);

    public void Dispose() { }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel();

    internal void Write(string category, LogLevel level, EventId eventId, IReadOnlyList<KeyValuePair<string, object?>>? fields, Exception? exception)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
            var line = Format(now, category, level, eventId, fields, exception);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                var path = CurrentFile(now, Encoding.UTF8.GetByteCount(line) + 1);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                fs.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            WriteFailures++;
        }
    }

    private string CurrentFile(DateTimeOffset now, int nextBytes)
    {
        var stem = $"pwm-{now:yyyyMMdd}";
        for (int i = 0; ; i++)
        {
            var path = Path.Combine(_directory, i == 0 ? $"{stem}.jsonl" : $"{stem}-{i}.jsonl");
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length + nextBytes <= _maxFileBytes) return path;
        }
    }

    internal static string Format(DateTimeOffset time, string category, LogLevel level, EventId eventId,
        IReadOnlyList<KeyValuePair<string, object?>>? fields, Exception? exception)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("t", time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            w.WriteString("lv", level.ToString());
            w.WriteNumber("ev", eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name)) w.WriteString("name", eventId.Name);
            w.WriteString("cat", category);
            if (LogOperation.Current is { } op) w.WriteString("op", op);

            var values = fields?.Where(f => f.Key != "{OriginalFormat}").ToList();
            if (values is { Count: > 0 })
            {
                w.WriteStartObject("fields");
                foreach (var (key, value) in values) WriteValue(w, key, value);
                w.WriteEndObject();
            }
            if (exception is not null)
            {
                w.WriteStartObject("ex");
                w.WriteString("type", exception.GetType().FullName);
                w.WriteString("msg", PathRedactor.RedactText(exception.Message));
                if (exception.StackTrace is { } st) w.WriteString("stack", PathRedactor.RedactText(st));
                if (exception.InnerException is { } inner) w.WriteString("inner", inner.GetType().FullName + ": " + PathRedactor.RedactText(inner.Message));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, string key, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(key); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case ulong u: w.WriteNumber(key, u); break;
            case double d: w.WriteNumber(key, d); break;
            case Guid g: w.WriteString(key, g.ToString()); break;
            case Enum e: w.WriteString(key, e.ToString()); break;
            case IFormattable f: w.WriteString(key, f.ToString(null, CultureInfo.InvariantCulture)); break;
            default: w.WriteString(key, PathRedactor.RedactText(value.ToString() ?? string.Empty)); break;
        }
    }

    private void TryDeleteOld(int retentionDays)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;
            var limit = _time.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
            foreach (var f in new DirectoryInfo(_directory).GetFiles("pwm-*.jsonl"))
                if (f.LastWriteTimeUtc < limit) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteFailures++;
        }
    }

    private sealed class JsonlFileLogger(JsonlFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // 型付きメソッド（LoggerMessage）の引数だけを項目として書く。整形済みの文章は書かない
            provider.Write(category, logLevel, eventId, state as IReadOnlyList<KeyValuePair<string, object?>>, exception);
        }
    }
}
