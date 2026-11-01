using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PwManager.Application.Infrastructure;
using PwManager.Core.Crypto;
using PwManager.Core.Slots;
using PwManager.Core.Vault;

namespace PwManager.Application.Diagnostics;

/// <summary>
/// 秘密を使わない診断（R-18、docs/10 §6）。CLI `pwm` と「鍵と設定」の診断情報で使う。
/// 出力に秘密（鍵・TPM の封印データ・salt）や利用者データは含めない。パスはユーザー名を伏せる。
/// </summary>
public static class VaultDiagnostics
{
    /// <summary>`pwm diag vault`: ヘッダ（未認証）から分かることを表示する。</summary>
    public static string DescribeVault(string path, ITpmSealer? tpm = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Inv($"Vault: {PathRedactor.Redact(Path.GetFullPath(path))}"));
        if (!File.Exists(path))
        {
            sb.AppendLine("状態: ファイルが見つかりません");
            return sb.ToString();
        }
        var bytes = File.ReadAllBytes(path);
        sb.AppendLine(Inv($"サイズ: {bytes.Length:N0} バイト  ·  更新 {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}"));
        VaultHeader header;
        try
        {
            header = PasswordVault.ReadHeader(bytes);
        }
        catch (VaultFormatException ex)
        {
            sb.AppendLine(Inv($"状態: ヘッダを読めません（{ex.Message}）{(ex.IsUnsupportedVersion ? "  ※ 新しい版のアプリで保存されています" : "  ※ 壊れている可能性があります。pwm backups でバックアップを確認してください")}"));
            return sb.ToString();
        }

        sb.AppendLine(Inv($"状態: ヘッダを読めました（ファイル形式 v{BitConverter.ToUInt16(bytes, 4)}）"));
        sb.AppendLine("※ 鍵を使わないため、改ざんの検知（MAC の検証）と、暗号化された本体（項目・本体の形式の版）の確認はできません。");
        sb.AppendLine(Inv($"Vault ID: {header.VaultId}"));
        sb.AppendLine(Inv($"保存回数: {header.Counter}"));
        sb.AppendLine(Inv($"作成: {header.Created.ToLocalTime():yyyy-MM-dd HH:mm}  ·  更新: {header.Modified.ToLocalTime():yyyy-MM-dd HH:mm}"));
        sb.AppendLine(Inv($"解錠方法: {header.Slots.Count} 件  ·  方針（復旧コード＋他 1 つ以上）: {(SlotPolicy.IsSatisfied(header.Slots) ? "OK" : "満たしていません")}"));
        foreach (var s in header.Slots)
        {
            var device = string.Empty;
            if (s.Type == SlotType.Tpm && tpm is not null)
            {
                try { device = tpm.IsSealedOnThisDevice(s.TypeParams) ? "  ·  この PC" : "  ·  他の PC／クリア前"; }
                catch (TpmUnavailableException) { device = "  ·  TPM を確認できません"; }
            }
            var used = s.LastVerified is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "未使用";
            sb.AppendLine(Inv($"  - {s.Type,-8} {s.Id}  「{s.Label}」  登録 {s.Created.ToLocalTime():yyyy-MM-dd}  最終使用 {used}  KDF {s.Kdf.MemoryKiB / 1024} MiB × {s.Kdf.Iterations}{device}"));
        }
        return sb.ToString();
    }

    /// <summary>`pwm backups`: バックアップ世代の一覧。</summary>
    public static string DescribeBackups(string path)
    {
        var store = new FileVaultStore(path);
        var sb = new StringBuilder();
        sb.AppendLine(Inv($"Vault: {PathRedactor.Redact(store.Location)}"));
        if (store.Exists())
            sb.AppendLine(Inv($"  現在  保存回数 {FileVaultStore.ReadDiskCounter(store.Location, out _)?.ToString(CultureInfo.InvariantCulture) ?? "（読めません）"}  ·  {File.GetLastWriteTime(store.Location):yyyy-MM-dd HH:mm}"));
        var backups = store.ListBackups();
        if (backups.Count == 0) sb.AppendLine("  バックアップはありません");
        foreach (var b in backups)
            sb.AppendLine(Inv($"  .bak{b.Generation}  保存回数 {b.Counter?.ToString(CultureInfo.InvariantCulture) ?? "（読めません）"}  ·  {b.LastWrite.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {b.Size:N0} バイト"));
        sb.AppendLine("戻すには、アプリを閉じてから .bakN を vault.pwmv にコピーしてください（現在のファイルは別名で残してください）。");
        return sb.ToString();
    }

    /// <summary>`pwm diag tpm`。</summary>
    public static string DescribeTpm(ITpmSealer? tpm)
    {
        if (tpm is null) return "TPM: この OS では診断できません（Windows でのみ利用できます）。";
        try
        {
            return tpm.IsAvailable(out var reason) ? "TPM: 利用できます（SRK を読み込めました）。" : $"TPM: 利用できません（{reason}）";
        }
        catch (Exception ex) when (ex is TpmUnavailableException or InvalidOperationException)
        {
            return $"TPM: 利用できません（{ex.Message}）";
        }
    }

    /// <summary>`pwm bench argon2`: KDF の所要時間（パラメータの見直し用）。</summary>
    public static string BenchArgon2(IEnumerable<(string Name, KdfParameters Kdf)> profiles)
    {
        var sb = new StringBuilder();
        var salt = CryptoPrimitives.RandomBytes(CryptoPrimitives.SaltSize);
        Span<byte> output = stackalloc byte[32];
        foreach (var (name, kdf) in profiles)
        {
            var sw = Stopwatch.StartNew();
            CryptoPrimitives.Argon2id("benchmark"u8, salt, kdf, output);
            sb.AppendLine(Inv($"{name,-10} {kdf.MemoryKiB / 1024,5} MiB × {kdf.Iterations}  {sw.ElapsedMilliseconds,6} ms"));
        }
        sb.AppendLine("目安: 解錠 1 回あたり 0.5〜2 秒。遅すぎる・速すぎる場合は KDF の設定を見直してください。");
        return sb.ToString();
    }

    /// <summary>`pwm logs`: 直近のログの要約（警告以上の件数と種類）。</summary>
    public static string SummarizeLogs(string logDirectory, int days, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var sb = new StringBuilder();
        sb.AppendLine(Inv($"ログ: {PathRedactor.Redact(logDirectory)}（直近 {days} 日）"));
        if (!Directory.Exists(logDirectory))
        {
            sb.AppendLine("  ログはありません");
            return sb.ToString();
        }
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int lines = 0, broken = 0;
        foreach (var f in Directory.GetFiles(logDirectory, "pwm-*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) >= now.UtcDateTime.AddDays(-days)).Order(StringComparer.Ordinal))
        {
            foreach (var line in Logging.SharedFileReader.ReadLines(f))
            {
                lines++;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var lv = doc.RootElement.GetProperty("lv").GetString();
                    if (lv is "Warning" or "Error" or "Critical")
                    {
                        var key = $"{lv} {doc.RootElement.GetProperty("ev").GetInt32()} {(doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "")}"
                                  + (doc.RootElement.TryGetProperty("fields", out var fl) && fl.TryGetProperty("Reason", out var r) ? $" ({r})" : "");
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    broken++;
                }
            }
        }
        sb.AppendLine(Inv($"  {lines} 行{(broken > 0 ? $"（読めない行 {broken}）" : "")}"));
        if (counts.Count == 0) sb.AppendLine("  警告・エラーはありません");
        foreach (var (k, c) in counts) sb.AppendLine(Inv($"  {c,4} 件  {k}"));
        return sb.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
