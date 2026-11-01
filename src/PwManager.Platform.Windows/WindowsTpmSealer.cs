using System.Buffers.Binary;
using PwManager.Core.Crypto;
using PwManager.Core.Localization;
using PwManager.Core.Slots;
using Tpm2Lib;

namespace PwManager.Platform.Windows;

/// <summary>
/// TPM 2.0 のシール機能による ITpmSealer 実装（ADR-0004、PoC: docs/poc/2026-10-25-tpm-seal.md）。
/// <list type="bullet">
/// <item>親鍵は Windows が作成済みの SRK（永続ハンドル 0x81000001）。管理者権限は不要。</item>
/// <item>解除時は HMAC セッション＋応答パラメータ暗号化を使う。暗号鍵は封印オブジェクトの authValue（Argon2id で
///       引き伸ばした PIN 由来値）から導出されるため、バスを盗聴されても秘密は平文では流れない。</item>
/// <item>封印時もコマンドパラメータ暗号化を指定するが、非ソルトセッションでは親（SRK）の authValue が空のため
///       実質的な保護にならない。fTPM（CPU 内蔵）はバスが外部に出ないため許容し、dTPM 向けのソルト付きセッションは
///       Phase 2 で検討する（docs/poc/2026-10-25-tpm-seal.md）。</item>
/// <item>NoDA を付けないため、PIN 違いは TPM の総当たり対策（DA）でカウントされる。</item>
/// </list>
/// blob 形式: "PWT1" | u16 pubLen | TpmPublic | u16 privLen | TpmPrivate（いずれも TPM 表現、ビッグエンディアン長）
/// </summary>
public sealed class WindowsTpmSealer : ITpmSealer
{
    private const uint SrkHandle = 0x81000001;
    private static ReadOnlySpan<byte> Magic => "PWT1"u8;
    private static readonly object Gate = new();

    public bool IsAvailable(out string reason)
    {
        try
        {
            lock (Gate)
            {
                using var session = TpmSession.Open();
                session.Tpm.ReadPublic(Srk, out _, out _);
            }
            reason = string.Empty;
            return true;
        }
        catch (TpmException ex)
        {
            reason = L.T($"TPM の親鍵（SRK）を利用できません（{Tpm2.GetBaseErrorCode(ex.RawResponse)}）。", $"The TPM storage root key (SRK) is not available ({Tpm2.GetBaseErrorCode(ex.RawResponse)}).");
            return false;
        }
        catch (Exception ex)
        {
            reason = L.T($"TPM に接続できません（{ex.Message}）。", $"Cannot connect to the TPM ({ex.Message}).");
            return false;
        }
    }

    public byte[] Seal(ReadOnlySpan<byte> authValue, ReadOnlySpan<byte> secret)
    {
        var template = new TpmPublic(
            TpmAlgId.Sha256,
            ObjectAttr.UserWithAuth | ObjectAttr.FixedTPM | ObjectAttr.FixedParent,
            Array.Empty<byte>(),
            new KeyedhashParms(new NullSchemeKeyedhash()),
            new Tpm2bDigestKeyedhash());

        var auth = authValue.ToArray();
        var data = secret.ToArray();
        try
        {
            return Execute(tpm =>
            {
                var sess = StartEncryptedSession(tpm, SessionAttr.Decrypt);
                try
                {
                    var priv = tpm[sess].Create(Srk, new SensitiveCreate(auth, data), template,
                        Array.Empty<byte>(), Array.Empty<PcrSelection>(), out TpmPublic pub, out _, out _, out _);
                    return EncodeBlob(pub, priv);
                }
                finally
                {
                    tpm.FlushContext(sess);
                }
            });
        }
        finally
        {
            Array.Clear(auth);
            Array.Clear(data);
        }
    }

    public SecretBuffer Unseal(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> authValue)
    {
        var (pub, priv) = DecodeBlob(blob);
        var auth = authValue.ToArray();
        try
        {
            return Execute(tpm =>
            {
                var handle = tpm.Load(Srk, priv, pub);
                var sess = StartEncryptedSession(tpm, SessionAttr.Encrypt);
                byte[]? outData = null;
                try
                {
                    handle.SetAuth(new AuthValue(auth));
                    outData = tpm[sess].Unseal(handle);
                    return SecretBuffer.FromSpan(outData);
                }
                finally
                {
                    if (outData is not null) Array.Clear(outData);
                    tpm.FlushContext(sess);
                    tpm.FlushContext(handle);
                }
            });
        }
        finally
        {
            Array.Clear(auth);
        }
    }

    public bool IsSealedOnThisDevice(ReadOnlySpan<byte> blob)
    {
        TpmPublic pub;
        TpmPrivate priv;
        try { (pub, priv) = DecodeBlob(blob); }
        catch (TpmUnavailableException) { return false; }

        // Load は封印オブジェクトの authValue を使わないため、TPM の失敗回数（DA）は増えない。
        // 別の TPM（別の PC・クリア前）の SRK 配下で作った blob は完全性検査で失敗する。
        return Execute(tpm =>
        {
            TpmHandle handle;
            try { handle = tpm.Load(Srk, priv, pub); }
            catch (TpmException ex) when (Tpm2.GetBaseErrorCode(ex.RawResponse) is TpmRc.Integrity or TpmRc.Size or TpmRc.Value)
            {
                return false;
            }
            tpm.FlushContext(handle);
            return true;
        });
    }

    // ---------- 内部 ----------

    private static TpmHandle Srk => new(SrkHandle);

    private static AuthSession StartEncryptedSession(Tpm2 tpm, SessionAttr direction)
        => tpm.StartAuthSessionEx(TpmSe.Hmac, TpmAlgId.Sha256,
            SessionAttr.ContinueSession | direction, new SymDef(TpmAlgId.Aes, 128, TpmAlgId.Cfb));

    /// <summary>TPM 呼び出しを直列化し、TPM エラーをアプリの例外に変換する。</summary>
    private static T Execute<T>(Func<Tpm2, T> action)
    {
        lock (Gate)
        {
            TpmSession session;
            try { session = TpmSession.Open(); }
            catch (Exception ex) { throw new TpmUnavailableException(L.T("TPM に接続できません。", "Cannot connect to the TPM."), ex); }

            using (session)
            {
                try
                {
                    return action(session.Tpm);
                }
                catch (TpmException ex)
                {
                    var rc = Tpm2.GetBaseErrorCode(ex.RawResponse);
                    throw rc switch
                    {
                        TpmRc.AuthFail or TpmRc.BadAuth => new TpmAuthFailedException(L.T("PIN が違います。", "Wrong PIN.")),
                        TpmRc.Lockout => new TpmLockedOutException(L.T("PIN の失敗が続いたため、TPM が一時的にロックされています。時間をおくか、鍵ファイル／復旧コードで解錠してください。",
                "The TPM is temporarily locked after repeated PIN failures. Wait a while, or unlock with the key file or the recovery code.")),
                        TpmRc.Integrity or TpmRc.Size or TpmRc.Value =>
                            new TpmUnavailableException(L.T("TPM の封印データを読み込めません。TPM がクリアされた可能性があります。鍵ファイル／復旧コードで解錠し、TPM を登録し直してください。",
                "Cannot load the data sealed in the TPM. The TPM may have been reset. Unlock with the key file or the recovery code, then register the TPM again."), ex),
                        _ => new TpmUnavailableException(L.T($"TPM エラー: {rc}", $"TPM error: {rc}"), ex),
                    };
                }
            }
        }
    }

    internal static byte[] EncodeBlob(TpmPublic pub, TpmPrivate priv)
    {
        var p = pub.GetTpmRepresentation();
        var q = priv.GetTpmRepresentation();
        var b = new byte[4 + 2 + p.Length + 2 + q.Length];
        Magic.CopyTo(b);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), checked((ushort)p.Length));
        p.CopyTo(b, 6);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6 + p.Length), checked((ushort)q.Length));
        q.CopyTo(b, 8 + p.Length);
        return b;
    }

    internal static (TpmPublic Pub, TpmPrivate Priv) DecodeBlob(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length < 8 || !b[..4].SequenceEqual(Magic)) throw new FormatException();
            int pl = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
            int ql = BinaryPrimitives.ReadUInt16BigEndian(b[(6 + pl)..]);
            if (b.Length != 8 + pl + ql) throw new FormatException();
            var pub = Marshaller.FromTpmRepresentation<TpmPublic>(b.Slice(6, pl).ToArray());
            var priv = Marshaller.FromTpmRepresentation<TpmPrivate>(b.Slice(8 + pl, ql).ToArray());
            return (pub, priv);
        }
        catch (Exception ex) when (ex is not TpmUnavailableException)
        {
            throw new TpmUnavailableException(L.T("TPM の解錠方法のデータが壊れています。", "The data of the TPM unlock method is damaged."), ex);
        }
    }

    /// <summary>TBS 接続と Tpm2 オブジェクトの寿命管理。</summary>
    private sealed class TpmSession : IDisposable
    {
        private readonly Tpm2Device _device;
        public Tpm2 Tpm { get; }

        private TpmSession(Tpm2Device device, Tpm2 tpm)
        {
            _device = device;
            Tpm = tpm;
        }

        public static TpmSession Open()
        {
            var device = new TbsDevice();
            device.Connect();
            return new TpmSession(device, new Tpm2(device));
        }

        public void Dispose()
        {
            Tpm.Dispose();
            try { _device.Close(); } catch (Exception) { /* Dispose 済み */ }
        }
    }
}
