using PwManager.Core.Crypto;

namespace PwManager.Core.Vault;

/// <summary>解錠前に読めるヘッダ情報（未認証）。スロット一覧の表示と KEK 導出に使う。</summary>
public sealed class VaultHeader
{
    public Guid VaultId { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public ulong Counter { get; init; }
    public List<KeySlot> Slots { get; init; } = [];
}

/// <summary>
/// Vault ファイル v1 のバイナリ形式（リトルエンディアン）。
/// <code>
/// "PWMV" | u16 version | i32 headerLen | header | headerMac[32] | bodyNonce[24] | i32 bodyLen | body
/// header = vaultId[16] | created i64 | modified i64 | counter u64 | i32 slotCount | slot*
/// slot   = id[16] | type u8 | label str | salt bytes | kdf(m i32, t i32, p i32) | typeParams bytes
///          | nonce bytes | wrapped bytes | created i64 | lastVerified i64 (0 = 未検証)
/// headerMac = HMAC-SHA256( HKDF(VMK,"pwm/header/v1"), header )
/// body      = XChaCha20-Poly1305( HKDF(VMK,"pwm/entries/v1"), bodyNonce, aad = header, entries )
/// </code>
/// </summary>
internal static class VaultFile
{
    public static ReadOnlySpan<byte> Magic => "PWMV"u8;
    public const ushort Version = 1;
    public const int HeaderMacSize = 32;

    private const int MaxSlots = 64;
    private const int MaxBlob = 1 << 20;        // 1 MiB（スロット内パラメータ上限）
    private const int MaxHeader = 16 << 20;     // 16 MiB
    private const int MaxBody = 256 << 20;      // 256 MiB

    public sealed record Container(byte[] HeaderBytes, byte[] HeaderMac, byte[] BodyNonce, byte[] Body);

    // ---------- container ----------

    public static byte[] WriteContainer(byte[] headerBytes, byte[] headerMac, byte[] bodyNonce, byte[] body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(headerBytes.Length);
        w.Write(headerBytes);
        w.Write(headerMac);
        w.Write(bodyNonce);
        w.Write(body.Length);
        w.Write(body);
        w.Flush();
        return ms.ToArray();
    }

    public static Container ReadContainer(ReadOnlySpan<byte> file)
    {
        try
        {
            using var ms = new MemoryStream(file.ToArray(), writable: false);
            using var r = new BinaryReader(ms);
            var magic = r.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw new VaultFormatException("Vault ファイルではありません。");
            var version = r.ReadUInt16();
            if (version != Version) throw new VaultFormatException($"未対応のバージョンです: {version}") { IsUnsupportedVersion = true };
            var header = ReadExact(r, ReadLength(r, MaxHeader));
            var mac = ReadExact(r, HeaderMacSize);
            var nonce = ReadExact(r, CryptoPrimitives.NonceSize);
            var body = ReadExact(r, ReadLength(r, MaxBody));
            if (ms.Position != ms.Length) throw new VaultFormatException("ファイル末尾に余分なデータがあります。");
            return new Container(header, mac, nonce, body);
        }
        catch (EndOfStreamException ex)
        {
            throw new VaultFormatException("ファイルが途中で切れています。", ex);
        }
    }

    // ---------- header ----------

    public static byte[] WriteHeader(Guid vaultId, DateTimeOffset created, DateTimeOffset modified, ulong counter, IReadOnlyList<KeySlot> slots)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteGuid(w, vaultId);
        w.Write(created.ToUnixTimeMilliseconds());
        w.Write(modified.ToUnixTimeMilliseconds());
        w.Write(counter);
        w.Write(slots.Count);
        foreach (var s in slots)
        {
            WriteGuid(w, s.Id);
            w.Write((byte)s.Type);
            w.Write(s.Label);
            WriteBytes(w, s.Salt);
            w.Write(s.Kdf.MemoryKiB);
            w.Write(s.Kdf.Iterations);
            w.Write(s.Kdf.Parallelism);
            WriteBytes(w, s.TypeParams);
            WriteBytes(w, s.Nonce);
            WriteBytes(w, s.WrappedKey);
            w.Write(s.Created.ToUnixTimeMilliseconds());
            w.Write(s.LastVerified?.ToUnixTimeMilliseconds() ?? 0L);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static VaultHeader ReadHeader(byte[] headerBytes)
    {
        try
        {
            using var ms = new MemoryStream(headerBytes, writable: false);
            using var r = new BinaryReader(ms);
            var vaultId = ReadGuid(r);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            var modified = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            var counter = r.ReadUInt64();
            int count = r.ReadInt32();
            if (count < 0 || count > MaxSlots) throw new VaultFormatException("スロット数が不正です。");
            var slots = new List<KeySlot>(count);
            for (int i = 0; i < count; i++)
            {
                var id = ReadGuid(r);
                var type = (SlotType)r.ReadByte();
                if (!Enum.IsDefined(type)) throw new VaultFormatException($"不明なスロット種別です: {(byte)type}");
                var label = r.ReadString();
                var salt = ReadBytes(r);
                if (salt.Length != CryptoPrimitives.SaltSize) throw new VaultFormatException("salt 長が不正です。");
                var kdf = new KdfParameters(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                try { kdf.Validate(); }
                catch (ArgumentOutOfRangeException ex) { throw new VaultFormatException("KDF パラメータが不正です。", ex); }
                var typeParams = ReadBytes(r);
                var nonce = ReadBytes(r);
                var wrapped = ReadBytes(r);
                if (nonce.Length != CryptoPrimitives.NonceSize || wrapped.Length != CryptoPrimitives.KeySize + CryptoPrimitives.TagSize)
                    throw new VaultFormatException("スロットの暗号データ長が不正です。");
                var slotCreated = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
                var lv = r.ReadInt64();
                slots.Add(new KeySlot
                {
                    Id = id,
                    Type = type,
                    Label = label,
                    Salt = salt,
                    Kdf = kdf,
                    TypeParams = typeParams,
                    Nonce = nonce,
                    WrappedKey = wrapped,
                    Created = slotCreated,
                    LastVerified = lv == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lv),
                });
            }
            if (ms.Position != ms.Length) throw new VaultFormatException("ヘッダ末尾に余分なデータがあります。");
            return new VaultHeader { VaultId = vaultId, Created = created, Modified = modified, Counter = counter, Slots = slots };
        }
        catch (EndOfStreamException ex)
        {
            throw new VaultFormatException("ヘッダが途中で切れています。", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new VaultFormatException("ヘッダの値が範囲外です。", ex);
        }
    }

    // ---------- helpers ----------

    internal static void WriteGuid(BinaryWriter w, Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        w.Write(b);
    }

    internal static Guid ReadGuid(BinaryReader r) => new(ReadExact(r, 16));

    internal static void WriteBytes(BinaryWriter w, ReadOnlySpan<byte> data)
    {
        w.Write(data.Length);
        w.Write(data);
    }

    internal static byte[] ReadBytes(BinaryReader r, int max = MaxBlob) => ReadExact(r, ReadLength(r, max));

    private static int ReadLength(BinaryReader r, int max)
    {
        int len = r.ReadInt32();
        if (len < 0 || len > max) throw new VaultFormatException("長さフィールドが不正です。");
        return len;
    }

    private static byte[] ReadExact(BinaryReader r, int count)
    {
        var b = r.ReadBytes(count);
        if (b.Length != count) throw new EndOfStreamException();
        return b;
    }
}
