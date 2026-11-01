namespace PwManager.Core.Crypto;

/// <summary>Argon2id のパラメータ。MemoryKiB は KiB 単位。libsodium の制約により Parallelism は 1 固定。</summary>
public readonly record struct KdfParameters(int MemoryKiB, int Iterations, int Parallelism = 1)
{
    /// <summary>パスワード用（重め）: 256 MiB, 3 回。</summary>
    public static KdfParameters Password { get; } = new(256 * 1024, 3);

    /// <summary>TPM PIN 用（TPM 側で総当たりが止まるため軽め）: 64 MiB, 2 回。</summary>
    public static KdfParameters Pin { get; } = new(64 * 1024, 2);

    /// <summary>復旧コード用（コード自体が 160bit あるため軽め）: 64 MiB, 2 回。</summary>
    public static KdfParameters Recovery { get; } = new(64 * 1024, 2);

    /// <summary>単体テスト専用の最小設定。本番では使わない。</summary>
    internal static KdfParameters TestOnly { get; } = new(8 * 1024, 1);

    /// <summary>ヘッダは解錠前は未認証なので、極端な値による DoS を防ぐ上限。</summary>
    public const int MaxMemoryKiB = 2 * 1024 * 1024; // 2 GiB
    public const int MaxIterations = 20;
    public const int MinMemoryKiB = 8 * 1024;

    public void Validate()
    {
        if (MemoryKiB < MinMemoryKiB || MemoryKiB > MaxMemoryKiB)
            throw new ArgumentOutOfRangeException(nameof(MemoryKiB), MemoryKiB, "Argon2id のメモリ量が範囲外です。");
        if (Iterations < 1 || Iterations > MaxIterations)
            throw new ArgumentOutOfRangeException(nameof(Iterations), Iterations, "Argon2id の反復回数が範囲外です。");
        if (Parallelism != 1)
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "Parallelism は 1 のみ対応です。");
    }
}
