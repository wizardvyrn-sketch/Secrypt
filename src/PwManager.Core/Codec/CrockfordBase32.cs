namespace PwManager.Core.Codec;

/// <summary>
/// Crockford Base32。手書き・読み上げでの取り違えに強い（I/L→1、O→0 として解釈、U は不使用）。
/// 復旧コードとキーファイルのテキスト表現（QR 用）に使う。
/// </summary>
public static class CrockfordBase32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        int outLen = (data.Length * 8 + 4) / 5;
        var chars = new char[outLen];
        int buffer = 0, bits = 0, idx = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[idx++] = Alphabet[(buffer >> (bits - 5)) & 0x1F];
                bits -= 5;
            }
        }
        if (bits > 0)
            chars[idx++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        return new string(chars, 0, idx);
    }

    /// <summary>区切り（- と空白）を無視し、大文字小文字・紛らわしい文字を正規化して復号する。</summary>
    public static byte[] Decode(string text, int expectedBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new byte[expectedBytes];
        int buffer = 0, bits = 0, idx = 0;
        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue;
            int v = Value(raw);
            if (v < 0) throw new FormatException($"使用できない文字が含まれています: '{raw}'");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                if (idx >= expectedBytes)
                {
                    // 余剰ビットが 8 以上ある＝文字数が多すぎる
                    throw new FormatException("文字数が多すぎます。");
                }
                output[idx++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        if (idx != expectedBytes) throw new FormatException("文字数が足りません。");
        return output;
    }

    private static int Value(char c)
    {
        c = char.ToUpperInvariant(c);
        switch (c)
        {
            case 'O': return 0;
            case 'I':
            case 'L': return 1;
        }
        return Alphabet.IndexOf(c);
    }
}
