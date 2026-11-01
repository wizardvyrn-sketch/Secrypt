// TPM シール PoC（ロードマップ P0-1b / ADR-0004）
//
// 検証内容:
//   1. 管理者権限なしで TBS 経由で TPM に接続できるか
//   2. Windows が作成済みの SRK（永続ハンドル 0x81000001）の下に、PIN 付きの sealed data object を作れるか
//   3. 正しい PIN で Unseal できるか（パスワードセッション／HMAC セッション＋応答暗号化）
//   4. 間違った PIN を拒否し、TPM の総当たり対策カウンタ（DA lockout counter）が増えるか（試行は 1 回だけ）
//   5. 封印データをファイルに保存し、再起動後も Unseal できるか（verify-saved モード）
//
// 使い方（Windows、リポジトリのルートで）:
//   dotnet run --project poc\TpmSealPoc -c Release              … 1〜4 と保存
//   dotnet run --project poc\TpmSealPoc -c Release -- verify-saved   … 再起動後に 5 を確認
//   オプション: --no-da-test  … 間違った PIN の試行（4）を行わない
//
// 結果はカレントフォルダの tpm-seal-poc-result.md に書き出す。
// 秘密やPINは PoC 用の固定・乱数値で、実データは扱わない。

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Tpm2Lib;

namespace PwManager.Poc.TpmSeal;

internal static class Program
{
    private const uint SrkHandle = 0x81000001;
    private const string TestPin = "poc-pin-1234";
    private const string WrongPin = "poc-pin-0000";
    private static readonly string SavedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PwManagerPoc", "sealed-test.bin");

    private static readonly StringBuilder Report = new();
    private static int _failures;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("この PoC は Windows でのみ動作します。");
            return 2;
        }

        bool daTest = !args.Contains("--no-da-test");
        string mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "all";

        Line("# TPM シール PoC 結果");
        Line();
        Line($"- 実行日時: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"- モード: {mode}");
        Line($"- OS: {Environment.OSVersion.VersionString}");
        Line($"- .NET: {Environment.Version}");
        Line($"- 管理者として実行: {IsAdmin()}");
        Line();
        Line("| 項目 | 結果 | 詳細 |");
        Line("|---|---|---|");

        Tpm2Device device;
        Tpm2 tpm;
        try
        {
            device = new TbsDevice();
            device.Connect();
            tpm = new Tpm2(device);
            Row("TBS 接続", true, "管理者権限なしで接続できるか");
        }
        catch (Exception ex)
        {
            Row("TBS 接続", false, Describe(ex));
            return Finish();
        }

        try
        {
            Run("TPM 情報", () => TpmInfo(tpm));
            Run("DA（総当たり対策）設定", () => DaInfo(tpm));
            Run("SRK (0x81000001) の存在", () => SrkInfo(tpm));

            if (mode == "verify-saved")
            {
                Run("保存済み封印データの Unseal（再起動後）", () => VerifySaved(tpm));
            }
            else
            {
                RunSealTests(tpm, daTest);
            }
        }
        finally
        {
            tpm.Dispose();
            try { device.Close(); } catch (Exception) { /* Dispose 済みの場合 */ }
        }

        return Finish();
    }

    private static void RunSealTests(Tpm2 tpm, bool daTest)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var auth = DeriveAuth(TestPin, salt);
        TpmPublic? pub = null;
        TpmPrivate? priv = null;

        Run("封印（TPM2_Create, sealed data object）", () =>
        {
            var sw = Stopwatch.StartNew();
            (pub, priv) = Seal(tpm, auth, secret);
            return $"{sw.ElapsedMilliseconds} ms / private blob {priv.buffer.Length} B";
        });
        if (pub is null || priv is null) return;

        Run("Unseal（正しい PIN、パスワードセッション）", () =>
        {
            var sw = Stopwatch.StartNew();
            var outData = Unseal(tpm, pub, priv, auth, null);
            Expect(outData.AsSpan().SequenceEqual(secret), "取り出した秘密が一致しません");
            return $"{sw.ElapsedMilliseconds} ms、秘密が一致";
        });

        Run("Unseal（正しい PIN、HMAC セッション＋応答暗号化 AES-128-CFB）", () =>
        {
            var sess = tpm.StartAuthSessionEx(TpmSe.Hmac, TpmAlgId.Sha256,
                SessionAttr.ContinueSession | SessionAttr.Encrypt, new SymDef(TpmAlgId.Aes, 128, TpmAlgId.Cfb));
            try
            {
                var sw = Stopwatch.StartNew();
                var outData = Unseal(tpm, pub, priv, auth, sess);
                Expect(outData.AsSpan().SequenceEqual(secret), "取り出した秘密が一致しません");
                return $"{sw.ElapsedMilliseconds} ms、秘密が一致（PIN と秘密がバス上を平文で流れない）";
            }
            finally
            {
                tpm.FlushContext(sess);
            }
        });

        if (daTest)
        {
            Run("間違った PIN を拒否（1 回だけ試行）", () =>
            {
                uint before = GetProp(tpm, Pt.LockoutCounter);
                var wrong = DeriveAuth(WrongPin, salt);
                try
                {
                    Unseal(tpm, pub, priv, wrong, null);
                    throw new InvalidOperationException("間違った PIN で Unseal できてしまいました");
                }
                catch (TpmException ex)
                {
                    var rc = Tpm2.GetBaseErrorCode(ex.RawResponse);
                    uint after = GetProp(tpm, Pt.LockoutCounter);
                    Expect(rc is TpmRc.AuthFail or TpmRc.BadAuth or TpmRc.Lockout, $"想定外のエラー: {rc}");
                    return $"拒否された（{rc}）。DA カウンタ {before} → {after}";
                }
            });

            Run("間違えた後も正しい PIN で Unseal できる", () =>
            {
                var outData = Unseal(tpm, pub, priv, auth, null);
                Expect(outData.AsSpan().SequenceEqual(secret), "取り出した秘密が一致しません");
                return "OK";
            });
        }
        else
        {
            Row("間違った PIN を拒否", true, "スキップ（--no-da-test）");
        }

        Run("封印データの保存（再起動後の確認用）", () =>
        {
            Save(pub, priv, salt, secret);
            return $"{SavedPath} に保存。再起動後に `verify-saved` で確認";
        });
    }

    // ---------- TPM 操作 ----------

    private static (TpmPublic Pub, TpmPrivate Priv) Seal(Tpm2 tpm, byte[] auth, byte[] secret)
    {
        // UserWithAuth: authValue（PIN）で使用可能 / FixedTPM・FixedParent: この TPM・この親から移動不可
        // NoDA を付けない → 認証失敗が DA（総当たり対策）の対象になる
        var template = new TpmPublic(
            TpmAlgId.Sha256,
            ObjectAttr.UserWithAuth | ObjectAttr.FixedTPM | ObjectAttr.FixedParent,
            Array.Empty<byte>(),
            new KeyedhashParms(new NullSchemeKeyedhash()),
            new Tpm2bDigestKeyedhash());

        var priv = tpm.Create(new TpmHandle(SrkHandle), new SensitiveCreate(auth, secret), template,
            Array.Empty<byte>(), Array.Empty<PcrSelection>(), out TpmPublic pub, out _, out _, out _);
        return (pub, priv);
    }

    private static byte[] Unseal(Tpm2 tpm, TpmPublic pub, TpmPrivate priv, byte[] auth, SessionBase? session)
    {
        var handle = tpm.Load(new TpmHandle(SrkHandle), priv, pub);
        try
        {
            handle.SetAuth(new AuthValue(auth));
            return session is null ? tpm.Unseal(handle) : tpm[session].Unseal(handle);
        }
        finally
        {
            tpm.FlushContext(handle);
        }
    }

    private static string TpmInfo(Tpm2 tpm)
    {
        uint mfr = GetProp(tpm, Pt.Manufacturer);
        uint fw1 = GetProp(tpm, Pt.FirmwareVersion1);
        var mfrText = Encoding.ASCII.GetString(new[] { (byte)(mfr >> 24), (byte)(mfr >> 16), (byte)(mfr >> 8), (byte)mfr }).TrimEnd('\0', ' ');
        return $"製造元 {mfrText}、ファームウェア {fw1 >> 16}.{fw1 & 0xFFFF}";
    }

    private static string DaInfo(Tpm2 tpm)
    {
        uint counter = GetProp(tpm, Pt.LockoutCounter);
        uint max = GetProp(tpm, Pt.MaxAuthFail);
        uint interval = GetProp(tpm, Pt.LockoutInterval);
        uint recovery = GetProp(tpm, Pt.LockoutRecovery);
        return $"現在の失敗カウンタ {counter} / 上限 {max} 回、{interval} 秒ごとに 1 減少、ロックアウト解除まで {recovery} 秒";
    }

    private static string SrkInfo(Tpm2 tpm)
    {
        var pub = tpm.ReadPublic(new TpmHandle(SrkHandle), out _, out _);
        // TpmAlgId.Rsa(0x0001) は列挙子 First と同じ値のため ToString() では "First" になる。数値で判定する。
        string alg = (ushort)pub.type switch { 0x0001 => "RSA", 0x0023 => "ECC", var v => $"0x{v:X4}" };
        return $"あり（{alg}、nameAlg {pub.nameAlg}）";
    }

    private static string VerifySaved(Tpm2 tpm)
    {
        if (!File.Exists(SavedPath)) throw new FileNotFoundException("保存データがありません。先に通常モードを実行してください。", SavedPath);
        var (pub, priv, salt, secretHash) = Load();
        var outData = Unseal(tpm, pub, priv, DeriveAuth(TestPin, salt), null);
        Expect(SHA256.HashData(outData).AsSpan().SequenceEqual(secretHash), "取り出した秘密が一致しません");
        return $"一致（保存日時 {File.GetLastWriteTime(SavedPath):yyyy-MM-dd HH:mm}）";
    }

    private static uint GetProp(Tpm2 tpm, Pt prop)
    {
        tpm.GetCapability(Cap.TpmProperties, (uint)prop, 1, out ICapabilitiesUnion caps);
        var arr = (TaggedTpmPropertyArray)caps;
        var p = arr.tpmProperty.FirstOrDefault(x => x.property == prop)
                ?? throw new InvalidOperationException($"プロパティ {prop} を取得できません");
        return p.value;
    }

    // ---------- 補助 ----------

    /// <summary>本番では Argon2id(PIN) を使う。PoC では SHA-256(salt‖PIN) で代用。</summary>
    private static byte[] DeriveAuth(string pin, byte[] salt)
        => SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(pin)).ToArray());

    private static void Save(TpmPublic pub, TpmPrivate priv, byte[] salt, byte[] secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
        using var w = new BinaryWriter(File.Create(SavedPath));
        w.Write("PWMTPOC1"u8);
        var p = pub.GetTpmRepresentation();
        var q = priv.GetTpmRepresentation();
        w.Write(p.Length); w.Write(p);
        w.Write(q.Length); w.Write(q);
        w.Write(salt);
        w.Write(SHA256.HashData(secret));
    }

    private static (TpmPublic, TpmPrivate, byte[], byte[]) Load()
    {
        using var r = new BinaryReader(File.OpenRead(SavedPath));
        if (!r.ReadBytes(8).AsSpan().SequenceEqual("PWMTPOC1"u8)) throw new InvalidDataException("形式が違います");
        var pub = Marshaller.FromTpmRepresentation<TpmPublic>(r.ReadBytes(r.ReadInt32()));
        var priv = Marshaller.FromTpmRepresentation<TpmPrivate>(r.ReadBytes(r.ReadInt32()));
        return (pub, priv, r.ReadBytes(16), r.ReadBytes(32));
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Expect(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Func<string> action)
    {
        try { Row(name, true, action()); }
        catch (Exception ex) { Row(name, false, Describe(ex)); }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TpmException t => $"TPM エラー {Tpm2.GetBaseErrorCode(t.RawResponse)} (0x{(uint)t.RawResponse:X}): {t.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static void Row(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Line($"| {name} | {(ok ? "✅ OK" : "❌ NG")} | {detail.Replace("|", "\\|").Replace("\n", " ")} |");
    }

    private static void Line(string text = "")
    {
        Console.WriteLine(text);
        Report.AppendLine(text);
    }

    private static int Finish()
    {
        Line();
        Line(_failures == 0 ? "**結果: すべて成功**" : $"**結果: {_failures} 件失敗**");
        var path = Path.GetFullPath("tpm-seal-poc-result.md");
        File.WriteAllText(path, Report.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"結果を書き出しました: {path}");
        return _failures == 0 ? 0 : 1;
    }
}
