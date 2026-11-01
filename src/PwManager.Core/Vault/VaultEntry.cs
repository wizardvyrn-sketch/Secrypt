using System.Security.Cryptography;

namespace PwManager.Core.Vault;

/// <summary>1 件のパスワード項目。Password は UTF-8 バイト列で保持し、string 化はUI表示時の最小限にとどめる。</summary>
public sealed class VaultEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public byte[] Password { get; set; } = [];
    public string Url { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// この版が知らない項目（新しい版で追加されたフィールド）。読み込んだまま保持し、保存時に書き戻す（R-10）。
    /// 秘密を含むかもしれないため、ロック時にパスワードと同様にゼロ化する。
    /// </summary>
    internal List<(ushort Tag, byte[] Value)> UnknownFields { get; } = [];

    /// <summary>パスワードを置き換え、古いバイト列をゼロ化する。</summary>
    public void SetPassword(ReadOnlySpan<byte> utf8Password)
    {
        CryptographicOperations.ZeroMemory(Password);
        Password = utf8Password.ToArray();
        Modified = DateTimeOffset.UtcNow;
    }

    internal void Wipe()
    {
        CryptographicOperations.ZeroMemory(Password);
        foreach (var (_, value) in UnknownFields) CryptographicOperations.ZeroMemory(value);
    }

    /// <summary>パスワードのバイト列も含めて複製する。</summary>
    internal VaultEntry Clone()
    {
        var c = new VaultEntry { Id = Id };
        c.CopyFrom(this);
        return c;
    }

    /// <summary>other の内容（Id 以外）を写す。パスワードは複製し、元のバイト列はゼロ化する。</summary>
    internal void CopyFrom(VaultEntry other)
    {
        Title = other.Title;
        UserName = other.UserName;
        if (!ReferenceEquals(Password, other.Password))
        {
            CryptographicOperations.ZeroMemory(Password);
            Password = other.Password.ToArray();
        }
        Url = other.Url;
        Notes = other.Notes;
        Tags = [.. other.Tags];
        foreach (var (_, value) in UnknownFields) CryptographicOperations.ZeroMemory(value);
        UnknownFields.Clear();
        UnknownFields.AddRange(other.UnknownFields.Select(f => (f.Tag, f.Value.ToArray())));
        Created = other.Created;
        Modified = other.Modified;
    }
}
