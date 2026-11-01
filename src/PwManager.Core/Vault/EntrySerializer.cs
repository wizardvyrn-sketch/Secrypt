using System.Security.Cryptography;
using System.Text;
using PwManager.Core.Crypto;

namespace PwManager.Core.Vault;

/// <summary>
/// エントリ一覧（暗号化前の本体）のバイナリ形式。平文は SecretBuffer に置き、使用後ゼロ化する。
/// <para>
/// v2（R-10、書き込みは常に v2）: 項目ごとにタグ付きフィールドの並び。知らないタグは読み飛ばさずに保持し、
/// 保存時にそのまま書き戻す（新しい版で追加した項目を古い版で開いて保存しても消えない＝前方互換）。
/// <code>
/// i32 formatVersion=2 | i32 count | entry*
/// entry = i32 fieldCount | field*
/// field = u16 tag | i32 length | bytes[length]
/// tag   = 1 id(16) | 2 title | 3 userName | 4 password | 5 url | 6 notes | 7 tag（複数可）| 8 created(i64) | 9 modified(i64)
///         （文字列は UTF-8、時刻は Unix ミリ秒）
/// </code>
/// </para>
/// <para>v1（MVP）: 固定順。読み込みのみ対応し、次の保存で v2 になる。</para>
/// </summary>
internal static class EntrySerializer
{
    public const int CurrentVersion = 2;
    private const int V1 = 1;
    private const int MaxEntries = 100_000;
    private const int MaxFields = 10_000;
    private const int MaxFieldLength = 16 << 20;

    internal enum Tag : ushort
    {
        Id = 1,
        Title = 2,
        UserName = 3,
        Password = 4,
        Url = 5,
        Notes = 6,
        TagName = 7,
        Created = 8,
        Modified = 9,
    }

    public static SecretBuffer Write(IReadOnlyList<VaultEntry> entries)
    {
        // MemoryStream の内部バッファは拡張時にコピーが残るため、最後に明示的にゼロ化する。
        var ms = new MemoryStream();
        try
        {
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CurrentVersion);
                w.Write(entries.Count);
                foreach (var e in entries)
                {
                    int fields = 8 + e.Tags.Count + e.UnknownFields.Count;
                    w.Write(fields);
                    Field(w, Tag.Id, e.Id.ToByteArray());
                    Field(w, Tag.Title, e.Title);
                    Field(w, Tag.UserName, e.UserName);
                    Field(w, Tag.Password, e.Password);
                    Field(w, Tag.Url, e.Url);
                    Field(w, Tag.Notes, e.Notes);
                    foreach (var t in e.Tags) Field(w, Tag.TagName, t);
                    Field(w, Tag.Created, BitConverter.GetBytes(e.Created.ToUnixTimeMilliseconds()));
                    Field(w, Tag.Modified, BitConverter.GetBytes(e.Modified.ToUnixTimeMilliseconds()));
                    foreach (var (tag, value) in e.UnknownFields) Field(w, (Tag)tag, value);
                }
            }
            return SecretBuffer.FromSpan(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ms.GetBuffer());
            ms.Dispose();
        }
    }

    public static List<VaultEntry> Read(ReadOnlySpan<byte> plaintext)
    {
        var copy = plaintext.ToArray();
        try
        {
            using var ms = new MemoryStream(copy, writable: false);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            int version = r.ReadInt32();
            var list = version switch
            {
                CurrentVersion => ReadV2(r),
                V1 => ReadV1(r),
                _ => throw new VaultFormatException($"未対応の本体形式です: {version}") { IsUnsupportedVersion = true },
            };
            if (ms.Position != ms.Length) throw new VaultFormatException("本体の末尾に余分なデータがあります。");
            return list;
        }
        catch (EndOfStreamException ex)
        {
            throw new VaultFormatException("本体が途中で切れています。", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            throw new VaultFormatException("本体の値が不正です。", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static List<VaultEntry> ReadV2(BinaryReader r)
    {
        int count = ReadCount(r, MaxEntries, "エントリ数");
        var list = new List<VaultEntry>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            int fields = ReadCount(r, MaxFields, "フィールド数");
            Guid? id = null;
            var e = new VaultEntry();
            for (int f = 0; f < fields; f++)
            {
                var tag = (Tag)r.ReadUInt16();
                var value = ReadExact(r, ReadCount(r, MaxFieldLength, "フィールド長"));
                switch (tag)
                {
                    case Tag.Id:
                        if (value.Length != 16) throw new VaultFormatException("項目 ID の長さが不正です。");
                        id = new Guid(value);
                        break;
                    case Tag.Title: e.Title = Utf8(value); break;
                    case Tag.UserName: e.UserName = Utf8(value); break;
                    case Tag.Password:
                        CryptographicOperations.ZeroMemory(e.Password);
                        e.Password = value;
                        break;
                    case Tag.Url: e.Url = Utf8(value); break;
                    case Tag.Notes: e.Notes = Utf8(value); break;
                    case Tag.TagName: e.Tags.Add(Utf8(value)); break;
                    case Tag.Created: e.Created = Time(value); break;
                    case Tag.Modified: e.Modified = Time(value); break;
                    default: e.UnknownFields.Add(((ushort)tag, value)); break;
                }
            }
            if (id is null) throw new VaultFormatException("項目 ID がありません。");
            var entry = new VaultEntry { Id = id.Value }; // Id は作成時にしか設定できないため、読んだ内容を写す
            entry.CopyFrom(e);
            e.Wipe();
            list.Add(entry);
        }
        return list;
    }

    /// <summary>v1（MVP の固定順）。</summary>
    private static List<VaultEntry> ReadV1(BinaryReader r)
    {
        int count = ReadCount(r, MaxEntries, "エントリ数");
        var list = new List<VaultEntry>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            var e = new VaultEntry
            {
                Id = VaultFile.ReadGuid(r),
                Title = r.ReadString(),
                UserName = r.ReadString(),
                Password = VaultFile.ReadBytes(r),
                Url = r.ReadString(),
                Notes = r.ReadString(),
            };
            int tagCount = r.ReadInt32();
            if (tagCount < 0 || tagCount > 1000) throw new VaultFormatException("タグ数が不正です。");
            for (int t = 0; t < tagCount; t++) e.Tags.Add(r.ReadString());
            e.Created = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            e.Modified = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            list.Add(e);
        }
        return list;
    }

    /// <summary>v1 で書く（移行のテスト用）。</summary>
    internal static SecretBuffer WriteV1(IReadOnlyList<VaultEntry> entries)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(V1);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                VaultFile.WriteGuid(w, e.Id);
                w.Write(e.Title);
                w.Write(e.UserName);
                VaultFile.WriteBytes(w, e.Password);
                w.Write(e.Url);
                w.Write(e.Notes);
                w.Write(e.Tags.Count);
                foreach (var t in e.Tags) w.Write(t);
                w.Write(e.Created.ToUnixTimeMilliseconds());
                w.Write(e.Modified.ToUnixTimeMilliseconds());
            }
        }
        return SecretBuffer.FromSpan(ms.ToArray());
    }

    private static void Field(BinaryWriter w, Tag tag, string value) => Field(w, tag, Encoding.UTF8.GetBytes(value));

    private static void Field(BinaryWriter w, Tag tag, ReadOnlySpan<byte> value)
    {
        w.Write((ushort)tag);
        w.Write(value.Length);
        w.Write(value);
    }

    private static int ReadCount(BinaryReader r, int max, string what)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > max) throw new VaultFormatException($"{what}が不正です。");
        return n;
    }

    private static byte[] ReadExact(BinaryReader r, int count)
    {
        var b = r.ReadBytes(count);
        if (b.Length != count) throw new EndOfStreamException();
        return b;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string Utf8(byte[] value) => StrictUtf8.GetString(value);

    private static DateTimeOffset Time(byte[] value)
    {
        if (value.Length != 8) throw new VaultFormatException("時刻の長さが不正です。");
        return DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(value));
    }
}
