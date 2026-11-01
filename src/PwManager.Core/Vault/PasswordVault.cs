using PwManager.Core.Crypto;

namespace PwManager.Core.Vault;

/// <summary>
/// 解錠済みの Vault。VMK（Vault Master Key）をメモリに保持する。Dispose（＝ロック）で VMK とパスワードをゼロ化する。
/// </summary>
public sealed class PasswordVault : IDisposable
{
    private const string HeaderMacInfo = "pwm/header/v1";
    private const string EntriesKeyInfo = "pwm/entries/v1";

    private SecretBuffer? _vmk;
    private readonly List<KeySlot> _slots;

    public Guid VaultId { get; }
    public DateTimeOffset Created { get; }
    public DateTimeOffset Modified { get; private set; }
    public ulong Counter { get; private set; }
    public IReadOnlyList<KeySlot> Slots => _slots;
    public List<VaultEntry> Entries { get; }
    public bool IsLocked => _vmk is null;

    private PasswordVault(SecretBuffer vmk, Guid vaultId, DateTimeOffset created, DateTimeOffset modified, ulong counter, List<KeySlot> slots, List<VaultEntry> entries)
    {
        _vmk = vmk;
        VaultId = vaultId;
        Created = created;
        Modified = modified;
        Counter = counter;
        _slots = slots;
        Entries = entries;
    }

    /// <summary>新しい Vault を作る。VMK は CSPRNG で生成し、パスワードからは導出しない（ADR-0001）。</summary>
    public static PasswordVault CreateNew()
    {
        var now = DateTimeOffset.UtcNow;
        return new PasswordVault(SecretBuffer.Random(CryptoPrimitives.KeySize), Guid.NewGuid(), now, now, 0, [], []);
    }

    /// <summary>解錠前にヘッダ（スロット一覧）を読む。内容は未認証なので表示と KEK 導出の材料にのみ使う。</summary>
    public static VaultHeader ReadHeader(ReadOnlySpan<byte> file)
    {
        var c = VaultFile.ReadContainer(file);
        return VaultFile.ReadHeader(c.HeaderBytes);
    }

    /// <summary>指定スロットの KEK で解錠する。</summary>
    public static PasswordVault Open(ReadOnlySpan<byte> file, Guid slotId, SecretBuffer kek)
    {
        var c = VaultFile.ReadContainer(file);
        var header = VaultFile.ReadHeader(c.HeaderBytes);
        var slot = header.Slots.FirstOrDefault(s => s.Id == slotId)
                   ?? throw new VaultUnlockException("指定されたスロットが見つかりません。");

        var vmk = CryptoPrimitives.AeadDecrypt(kek.ReadOnlySpan, slot.Nonce, slot.WrapAssociatedData(header.VaultId), slot.WrappedKey)
                  ?? throw new VaultUnlockException("解錠できませんでした。パスワード・キーファイル・復旧コードを確認してください。");
        try
        {
            if (vmk.Length != CryptoPrimitives.KeySize) throw new VaultTamperedException("VMK の長さが不正です。");

            using (var macKey = CryptoPrimitives.HkdfToSecret(vmk.ReadOnlySpan, [], HeaderMacInfo))
            {
                var expected = CryptoPrimitives.HmacSha256(macKey.ReadOnlySpan, c.HeaderBytes);
                if (!CryptoPrimitives.FixedTimeEquals(expected, c.HeaderMac))
                    throw new VaultTamperedException("ヘッダの改ざんを検知しました。");
            }

            List<VaultEntry> entries;
            using (var entriesKey = CryptoPrimitives.HkdfToSecret(vmk.ReadOnlySpan, [], EntriesKeyInfo))
            using (var plain = CryptoPrimitives.AeadDecrypt(entriesKey.ReadOnlySpan, c.BodyNonce, c.HeaderBytes, c.Body)
                               ?? throw new VaultTamperedException("本体の改ざんを検知しました。"))
            {
                entries = EntrySerializer.Read(plain.ReadOnlySpan);
            }

            return new PasswordVault(vmk, header.VaultId, header.Created, header.Modified, header.Counter, header.Slots, entries);
        }
        catch
        {
            vmk.Dispose();
            throw;
        }
    }

    /// <summary>KEK で VMK を包んだスロットを追加する。KEK は呼び出し側で Dispose すること。</summary>
    public void AddSlot(KeySlot slot, SecretBuffer kek)
    {
        var vmk = RequireUnlocked();
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Type == SlotType.Fido2 && !FeatureFlags.Fido2Enabled)
            throw new NotSupportedException("FIDO2 スロットは保留機能です。設定で有効化してから使用してください。");
        if (_slots.Any(s => s.Id == slot.Id))
            throw new InvalidOperationException("同じ ID のスロットが既にあります。");
        slot.Nonce = CryptoPrimitives.RandomBytes(CryptoPrimitives.NonceSize);
        slot.WrappedKey = CryptoPrimitives.AeadEncrypt(kek.ReadOnlySpan, slot.Nonce, slot.WrapAssociatedData(VaultId), vmk.ReadOnlySpan);
        _slots.Add(slot);
    }

    /// <summary>スロットを削除（失効）する。ポリシー違反になる削除は拒否する。</summary>
    public void RemoveSlot(Guid slotId)
    {
        RequireUnlocked();
        var remaining = _slots.Where(s => s.Id != slotId).ToList();
        if (remaining.Count == _slots.Count) throw new InvalidOperationException("スロットが見つかりません。");
        SlotPolicy.Validate(remaining);
        _slots.RemoveAll(s => s.Id == slotId);
    }

    /// <summary>
    /// 保存用のファイル内容を作る。この時点では Vault の Counter / Modified は変えない。
    /// 書き込みに成功したら <see cref="CommitSave"/> で確定する（失敗時にメモリとファイルが食い違わないように: R-04）。
    /// contentChanged=true（項目・スロットの変更）は counter を +1 し更新日時を進める。
    /// false（最終使用日の記録など内容の変わらない保存）は counter と更新日時をそのままにする。
    /// counter は「内容の版」として、別の場所での更新の検知（R-16）に使うため、解錠しただけでは進めない。
    /// </summary>
    public PendingSave PrepareSave(DateTimeOffset? now = null, bool contentChanged = true)
        => PrepareSave(now, contentChanged, EntrySerializer.Write);

    /// <summary>本体の書き方を指定する（v1 からの移行のテスト用）。</summary>
    internal PendingSave PrepareSave(DateTimeOffset? now, bool contentChanged, Func<IReadOnlyList<VaultEntry>, SecretBuffer> writeEntries)
    {
        var vmk = RequireUnlocked();
        SlotPolicy.Validate(_slots);

        var counter = contentChanged ? Counter + 1 : Counter;
        var modified = contentChanged ? now ?? DateTimeOffset.UtcNow : Modified;
        var header = VaultFile.WriteHeader(VaultId, Created, modified, counter, _slots);

        byte[] mac;
        using (var macKey = CryptoPrimitives.HkdfToSecret(vmk.ReadOnlySpan, [], HeaderMacInfo))
            mac = CryptoPrimitives.HmacSha256(macKey.ReadOnlySpan, header);

        var nonce = CryptoPrimitives.RandomBytes(CryptoPrimitives.NonceSize);
        byte[] body;
        using (var entriesKey = CryptoPrimitives.HkdfToSecret(vmk.ReadOnlySpan, [], EntriesKeyInfo))
        using (var plain = writeEntries(Entries))
            body = CryptoPrimitives.AeadEncrypt(entriesKey.ReadOnlySpan, nonce, header, plain.ReadOnlySpan);

        return new PendingSave(VaultFile.WriteContainer(header, mac, nonce, body), Counter, counter, modified, this);
    }

    /// <summary>書き込みに成功した保存内容の counter / 更新日時を確定する。</summary>
    public void CommitSave(PendingSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        RequireUnlocked();
        if (save.Owner != this) throw new ArgumentException("別の Vault の保存内容です。", nameof(save));
        if (save.BaseCounter != Counter) throw new InvalidOperationException("保存内容が古くなっています（間に別の保存がありました）。");
        Counter = save.Counter;
        Modified = save.Modified;
    }

    /// <summary>PrepareSave と CommitSave をまとめて行う（メモリ上で完結する用途・テスト用）。</summary>
    public byte[] Serialize()
    {
        var save = PrepareSave();
        CommitSave(save);
        return save.Data;
    }

    /// <summary>
    /// 変更前の状態（項目・スロット構成・ラベル・最終使用日）を記録する。保存に失敗したら <see cref="Restore"/> で戻す。
    /// 戻すときは項目オブジェクトをできるだけそのまま使う（画面が持っている参照を壊さないため）。
    /// </summary>
    public VaultCheckpoint CreateCheckpoint()
    {
        RequireUnlocked();
        return new VaultCheckpoint(this,
            Entries.Select(e => (e, e.Clone())).ToList(),
            _slots.Select(s => (s, s.Label, s.LastVerified)).ToList(),
            Counter, Modified);
    }

    /// <summary>記録した状態に戻す。追加された項目は消去（ゼロ化）し、削除された項目は元に戻す。</summary>
    public void Restore(VaultCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireUnlocked();
        if (checkpoint.Owner != this) throw new ArgumentException("別の Vault のチェックポイントです。", nameof(checkpoint));

        var keep = checkpoint.Entries.Select(x => x.Original).ToHashSet();
        foreach (var added in Entries.Where(e => !keep.Contains(e))) added.Wipe();
        Entries.Clear();
        foreach (var (original, copy) in checkpoint.Entries)
        {
            original.CopyFrom(copy);
            Entries.Add(original);
        }

        _slots.Clear();
        foreach (var (slot, label, lastVerified) in checkpoint.Slots)
        {
            slot.Label = label;
            slot.LastVerified = lastVerified;
            _slots.Add(slot);
        }
        Counter = checkpoint.Counter;
        Modified = checkpoint.Modified;
    }

    /// <summary>ロック: VMK とメモリ上のパスワードを破棄する。</summary>
    public void Dispose()
    {
        _vmk?.Dispose();
        _vmk = null;
        foreach (var e in Entries) e.Wipe();
    }

    private SecretBuffer RequireUnlocked() => _vmk ?? throw new ObjectDisposedException(nameof(PasswordVault), "Vault はロックされています。");
}

/// <summary>書き込み前の保存内容。<see cref="PasswordVault.CommitSave"/> で確定する。</summary>
public sealed class PendingSave
{
    internal PendingSave(byte[] data, ulong baseCounter, ulong counter, DateTimeOffset modified, PasswordVault owner)
    {
        Data = data;
        BaseCounter = baseCounter;
        Counter = counter;
        Modified = modified;
        Owner = owner;
    }

    /// <summary>ファイルに書く内容（暗号化済み）。</summary>
    public byte[] Data { get; }
    public ulong Counter { get; }
    public DateTimeOffset Modified { get; }
    /// <summary>作ったときの Vault の counter（この間に別の保存があれば確定できない）。</summary>
    internal ulong BaseCounter { get; }
    internal PasswordVault Owner { get; }
}

/// <summary>変更前の状態。破棄（Dispose）で複製したパスワードをゼロ化する。</summary>
public sealed class VaultCheckpoint : IDisposable
{
    internal VaultCheckpoint(PasswordVault owner, List<(VaultEntry Original, VaultEntry Copy)> entries,
        List<(KeySlot Slot, string Label, DateTimeOffset? LastVerified)> slots, ulong counter, DateTimeOffset modified)
    {
        Owner = owner;
        Entries = entries;
        Slots = slots;
        Counter = counter;
        Modified = modified;
    }

    internal PasswordVault Owner { get; }
    internal List<(VaultEntry Original, VaultEntry Copy)> Entries { get; }
    internal List<(KeySlot Slot, string Label, DateTimeOffset? LastVerified)> Slots { get; }
    internal ulong Counter { get; }
    internal DateTimeOffset Modified { get; }

    public void Dispose()
    {
        foreach (var (_, copy) in Entries) copy.Wipe();
    }
}
