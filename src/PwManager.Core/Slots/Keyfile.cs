using System.Security.Cryptography;
using PwManager.Core.Codec;
using PwManager.Core.Crypto;
using PwManager.Core.Localization;
using PwManager.Core.Vault;

namespace PwManager.Core.Slots;

/// <summary>
/// USB メモリ等に保存するキーファイル（ADR-0003）。74 バイトのバイナリ、または QR 印刷用のテキスト表現を持つ。
/// <code>"PWMK" | u16 version | vaultId[16] | slotId[16] | secret[32] | checksum[4] (= SHA-256(先行 70B) の先頭 4B)</code>
/// </summary>
public sealed class Keyfile : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "PWMK"u8;
    private const ushort Version = 1;
    public const int SecretSize = 32;
    public const int FileSize = 4 + 2 + 16 + 16 + SecretSize + 4;
    private const string TextPrefix = "PWMK1:";

    public Guid VaultId { get; }
    public Guid SlotId { get; }
    internal SecretBuffer Secret { get; }

    internal Keyfile(Guid vaultId, Guid slotId, SecretBuffer secret)
    {
        VaultId = vaultId;
        SlotId = slotId;
        Secret = secret;
    }

    public byte[] ToBytes()
    {
        var b = new byte[FileSize];
        var s = b.AsSpan();
        Magic.CopyTo(s);
        BitConverter.TryWriteBytes(s.Slice(4, 2), Version);
        VaultId.TryWriteBytes(s.Slice(6, 16));
        SlotId.TryWriteBytes(s.Slice(22, 16));
        Secret.ReadOnlySpan.CopyTo(s.Slice(38, SecretSize));
        SHA256.HashData(s[..70])[..4].CopyTo(s.Slice(70, 4));
        return b;
    }

    public static Keyfile FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length != FileSize) throw new FormatException(L.T("鍵ファイルのサイズが不正です。", "The key file has an invalid size."));
        if (!data[..4].SequenceEqual(Magic)) throw new FormatException(L.T("鍵ファイルではありません。", "This is not a key file."));
        if (BitConverter.ToUInt16(data.Slice(4, 2)) != Version) throw new FormatException(L.T("未対応の鍵ファイル形式です。", "Unsupported key file format."));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data[..70], hash);
        if (!CryptoPrimitives.FixedTimeEquals(hash[..4], data.Slice(70, 4)))
            throw new FormatException(L.T("鍵ファイルが壊れています（チェックサム不一致）。", "The key file is damaged (checksum mismatch)."));
        return new Keyfile(new Guid(data.Slice(6, 16)), new Guid(data.Slice(22, 16)), SecretBuffer.FromSpan(data.Slice(38, SecretSize)));
    }

    /// <summary>QR コード印刷・手入力用のテキスト表現。</summary>
    public string ToText()
    {
        var bytes = ToBytes();
        try { return TextPrefix + CrockfordBase32.Encode(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static Keyfile FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (!t.StartsWith(TextPrefix, StringComparison.OrdinalIgnoreCase)) throw new FormatException(L.T("鍵ファイルのテキストではありません（「PWMK1:」で始まる文字列を入力してください）。", "This is not key file text (enter the text starting with \"PWMK1:\")."));
        var bytes = CrockfordBase32.Decode(t[TextPrefix.Length..], FileSize);
        try { return FromBytes(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => Secret.Dispose();

    /// <summary>ログや例外メッセージに中身が出ないよう、固定の文字列を返す（docs/10 §3.1）。</summary>
    public override string ToString() => "***";
}

/// <summary>キーファイル＋パスワードのスロット。</summary>
public static class KeyfileSlot
{
    /// <summary>新しいキーファイルとスロットを作る。戻り値の Keyfile を保存（USB／QR 印刷）し、Kek で vault.AddSlot する。</summary>
    public static (KeySlot Slot, Keyfile Keyfile, SecretBuffer Kek) Enroll(Guid vaultId, string label, ReadOnlySpan<byte> password, KdfParameters? kdf = null)
    {
        if (password.IsEmpty) throw new ArgumentException("パスワードが空です。", nameof(password));
        var slot = new KeySlot { Type = SlotType.Keyfile, Label = label, Kdf = kdf ?? KdfParameters.Password };
        var keyfile = new Keyfile(vaultId, slot.Id, SecretBuffer.Random(Keyfile.SecretSize));
        var kek = slot.DeriveKek(keyfile.Secret.ReadOnlySpan, password);
        return (slot, keyfile, kek);
    }

    public static SecretBuffer DeriveKek(KeySlot slot, Keyfile keyfile, ReadOnlySpan<byte> password)
    {
        if (slot.Type != SlotType.Keyfile) throw new ArgumentException("キーファイルのスロットではありません。", nameof(slot));
        if (keyfile.SlotId != slot.Id) throw new VaultUnlockException(L.T("この鍵ファイルは、この保管ファイル用ではありません。", "This key file is not for this vault file."));
        return slot.DeriveKek(keyfile.Secret.ReadOnlySpan, password);
    }
}
