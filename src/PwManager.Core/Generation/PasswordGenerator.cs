using System.Security.Cryptography;
using System.Text;

namespace PwManager.Core.Generation;

/// <summary>パスワード生成の設定。</summary>
public sealed record PasswordOptions
{
    public int Length { get; init; } = 20;
    public bool Lowercase { get; init; } = true;
    public bool Uppercase { get; init; } = true;
    public bool Digits { get; init; } = true;
    public bool Symbols { get; init; } = true;
    /// <summary>I l 1 O 0 など紛らわしい文字を除く（手入力・読み上げ向け）。</summary>
    public bool ExcludeAmbiguous { get; init; }
    public string SymbolSet { get; init; } = "!@#$%^&*()-_=+[]{};:,.?/";
}

/// <summary>CSPRNG（RandomNumberGenerator）による偏りのないパスワード生成。</summary>
public static class PasswordGenerator
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digit = "0123456789";
    private const string Ambiguous = "Il1O0o";

    public const int MinLength = 4;
    public const int MaxLength = 256;

    /// <summary>
    /// 選択した文字種をそれぞれ 1 文字以上含むパスワードを UTF-8 バイト列で返す（string を経由しない）。
    /// </summary>
    public static byte[] Generate(PasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length < MinLength || options.Length > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(options), $"長さは {MinLength}〜{MaxLength} 文字です。");

        var sets = CharacterSets(options);
        if (sets.Count == 0) throw new ArgumentException("文字種を 1 つ以上選んでください。", nameof(options));
        if (sets.Count > options.Length) throw new ArgumentException("長さが文字種の数より短いです。", nameof(options));

        var all = string.Concat(sets);
        var chars = new char[options.Length];
        try
        {
            // 各文字種から 1 文字ずつ確保し、残りは全体から選ぶ。最後に Fisher-Yates でシャッフル。
            int i = 0;
            foreach (var set in sets) chars[i++] = set[RandomNumberGenerator.GetInt32(set.Length)];
            for (; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
            for (int j = chars.Length - 1; j > 0; j--)
            {
                int k = RandomNumberGenerator.GetInt32(j + 1);
                (chars[j], chars[k]) = (chars[k], chars[j]);
            }
            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    /// <summary>
    /// エントロピー（ビット）の目安。実際に使う文字の種類数（重複・除外後）から計算する。
    /// 生成できない設定（文字種なし・長さが範囲外）は 0。
    /// </summary>
    public static double EstimateEntropyBits(PasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length < MinLength || options.Length > MaxLength) return 0;
        var sets = CharacterSets(options);
        if (sets.Count == 0 || sets.Count > options.Length) return 0;
        int pool = sets.Sum(s => s.Length);
        return pool <= 1 ? 0 : options.Length * Math.Log2(pool);
    }

    /// <summary>使う文字種ごとの文字集合。紛らわしい文字の除外と、文字種間・文字種内の重複除去を行う。</summary>
    private static List<string> CharacterSets(PasswordOptions options)
    {
        var sets = new List<string>();
        if (options.Lowercase) sets.Add(Lower);
        if (options.Uppercase) sets.Add(Upper);
        if (options.Digits) sets.Add(Digit);
        if (options.Symbols && !string.IsNullOrEmpty(options.SymbolSet)) sets.Add(options.SymbolSet);

        var used = new HashSet<char>();
        var result = new List<string>();
        foreach (var set in sets)
        {
            var chars = set.Where(c => !(options.ExcludeAmbiguous && Ambiguous.Contains(c)) && used.Add(c)).ToArray();
            if (chars.Length > 0) result.Add(new string(chars));
        }
        return result;
    }
}
