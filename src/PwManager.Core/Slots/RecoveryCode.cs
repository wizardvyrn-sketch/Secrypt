using System.Security.Cryptography;
using System.Text;
using PwManager.Core.Codec;
using PwManager.Core.Crypto;
using PwManager.Core.Localization;
using PwManager.Core.Vault;

namespace PwManager.Core.Slots;

/// <summary>
/// 紙に書き写す復旧コード。160bit の乱数＋16bit チェックサムを Crockford Base32 で 36 文字（4 文字×9 組）にする。
/// 例: <c>7K3M-Q9XA-...</c>。I/L/O の書き間違い、大文字小文字、区切りの有無を許容する。
/// </summary>
/// <remarks>
/// 設計書では BIP39 風ワードリストも候補に挙げているが、MVP は辞書を同梱しない Base32 で実装する。
/// </remarks>
public sealed class RecoveryCode : IDisposable
{
    public const int EntropySize = 20;   // 160 bit
    private const int ChecksumSize = 2;
    private const int GroupSize = 4;

    internal SecretBuffer Entropy { get; }

    private RecoveryCode(SecretBuffer entropy) => Entropy = entropy;

    public static RecoveryCode Generate() => new(SecretBuffer.Random(EntropySize));

    /// <summary>表示用の文字列。表示後は速やかに破棄し、保存・ログ出力しないこと。</summary>
    public string ToDisplayString()
    {
        Span<byte> raw = stackalloc byte[EntropySize + ChecksumSize];
        Entropy.ReadOnlySpan.CopyTo(raw);
        Checksum(Entropy.ReadOnlySpan, raw[EntropySize..]);
        var encoded = CrockfordBase32.Encode(raw);
        CryptographicOperations.ZeroMemory(raw);
        var sb = new StringBuilder();
        for (int i = 0; i < encoded.Length; i += GroupSize)
        {
            if (i > 0) sb.Append('-');
            sb.Append(encoded, i, Math.Min(GroupSize, encoded.Length - i));
        }
        return sb.ToString();
    }

    public static RecoveryCode Parse(string text)
    {
        byte[] raw;
        try { raw = CrockfordBase32.Decode(text, EntropySize + ChecksumSize); }
        catch (FormatException ex) { throw new FormatException(L.T("復旧コードの形式が正しくありません（文字数・使用文字を確認してください）。", "The recovery code is not in the right format (check the number of characters and the characters used)."), ex); }
        try
        {
            Span<byte> expected = stackalloc byte[ChecksumSize];
            Checksum(raw.AsSpan(0, EntropySize), expected);
            if (!CryptoPrimitives.FixedTimeEquals(expected, raw.AsSpan(EntropySize, ChecksumSize)))
                throw new FormatException(L.T("復旧コードに書き間違いがあります（チェックサム不一致）。", "The recovery code contains a typo (checksum mismatch)."));
            return new RecoveryCode(SecretBuffer.FromSpan(raw.AsSpan(0, EntropySize)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>書き写しの検証用: 指定した組番号（0 始まり）の 4 文字を返す。</summary>
    public string GetGroup(int index)
    {
        var groups = ToDisplayString().Split('-');
        return groups[index];
    }

    /// <summary>
    /// 書き写しの検証: input が指定した組（0 始まり）の 4 文字と一致するか。
    /// 前後の空白・大文字小文字・紛らわしい文字（O→0、I/L→1）の違いは許容する（解錠時の解釈と同じ）。
    /// </summary>
    public bool MatchesGroup(int index, string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var expected = GetGroup(index);
        var t = input.Trim();
        if (t.Length != expected.Length) return false;
        for (int i = 0; i < t.Length; i++)
            if (Normalize(t[i]) != expected[i]) return false;
        return true;
    }

    private static char Normalize(char c) => char.ToUpperInvariant(c) switch
    {
        'O' => '0',
        'I' or 'L' => '1',
        var u => u,
    };

    /// <summary>表示上の組数（22 バイト → 36 文字 → 4 文字×9 組）。</summary>
    public static int GroupCount => (((EntropySize + ChecksumSize) * 8 + 4) / 5 + GroupSize - 1) / GroupSize;

    private static void Checksum(ReadOnlySpan<byte> entropy, Span<byte> output)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(entropy, h);
        h[..ChecksumSize].CopyTo(output);
    }

    public void Dispose() => Entropy.Dispose();

    /// <summary>ログや例外メッセージに中身が出ないよう、固定の文字列を返す（docs/10 §3.1）。</summary>
    public override string ToString() => "***";
}

/// <summary>復旧コードのスロット（必須）。</summary>
public static class RecoverySlot
{
    public static (KeySlot Slot, RecoveryCode Code, SecretBuffer Kek) Enroll(string? label = null, KdfParameters? kdf = null)
    {
        var slot = new KeySlot { Type = SlotType.Recovery, Label = label ?? L.T("復旧コード", "Recovery code"), Kdf = kdf ?? KdfParameters.Recovery };
        var code = RecoveryCode.Generate();
        var kek = slot.DeriveKek([], code.Entropy.ReadOnlySpan);
        return (slot, code, kek);
    }

    public static SecretBuffer DeriveKek(KeySlot slot, RecoveryCode code)
    {
        if (slot.Type != SlotType.Recovery) throw new ArgumentException("復旧コードのスロットではありません。", nameof(slot));
        return slot.DeriveKek([], code.Entropy.ReadOnlySpan);
    }
}
