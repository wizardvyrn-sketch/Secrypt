using PwManager.Application.Infrastructure;
using PwManager.Application.Ports;

namespace PwManager.Application.Tests.Support;

/// <summary>保存の失敗（ディスク満杯・USB の取り外し・読み取り専用）を差し込める Vault の保存先。</summary>
internal sealed class FaultyVaultStore(string path) : IVaultStore
{
    private readonly FileVaultStore _inner = new(path);

    /// <summary>次の保存で投げる例外（1 回だけ）。</summary>
    public Exception? FailNextSave { get; set; }

    /// <summary>すべての保存を失敗させる（読み取り専用のメディアなど）。</summary>
    public bool ReadOnly { get; set; }

    public int SaveCount { get; private set; }
    public List<bool> RotateHistory { get; } = [];

    public string Location => _inner.Location;

    public bool Exists() => _inner.Exists();

    public byte[] ReadAll() => _inner.ReadAll();

    public void Save(ReadOnlySpan<byte> data, ulong? expectedCounter, bool rotateBackups)
    {
        if (ReadOnly) throw new UnauthorizedAccessException("書き込み禁止（テスト）");
        if (FailNextSave is { } ex)
        {
            FailNextSave = null;
            throw ex;
        }
        _inner.Save(data, expectedCounter, rotateBackups);
        SaveCount++;
        RotateHistory.Add(rotateBackups);
    }

    public IReadOnlyList<BackupInfo> ListBackups() => _inner.ListBackups();

    public BackupPurgeResult DeleteBackups() => _inner.DeleteBackups();
}

/// <summary>鍵ファイルの書き込み失敗（USB メモリの取り外し・書き込み禁止）を差し込める。</summary>
internal sealed class FaultyKeyfileStore : IKeyfileStore
{
    private readonly FileKeyfileStore _inner = new();

    public Exception? FailNextWrite { get; set; }

    /// <summary>
    /// 設定すると、書き込みがこのゲートが開くまで止まる（処理中の状態をテストで確実に作る。
    /// テストには UI の SynchronizationContext がないため、await の続きは別スレッドで進む）。
    /// </summary>
    public ManualResetEventSlim? HoldWrite
    {
        get => _holdWrite;
        set
        {
            _holdWrite = value;
            WriteStarted.Reset(); // 初期設定などの前の書き込みで立った合図を消す
        }
    }

    private ManualResetEventSlim? _holdWrite;

    /// <summary>書き込みが始まった（止まっている）ことを知らせる。</summary>
    public ManualResetEventSlim WriteStarted { get; } = new();

    public bool Exists(string path) => _inner.Exists(path);

    public byte[] Read(string path) => _inner.Read(path);

    public void Write(string path, ReadOnlySpan<byte> data)
    {
        WriteStarted.Set();
        _holdWrite?.Wait(TimeSpan.FromSeconds(30));
        if (FailNextWrite is { } ex)
        {
            FailNextWrite = null;
            throw ex;
        }
        _inner.Write(path, data);
    }

    public void Delete(string path) => _inner.Delete(path);
}

/// <summary>ロック時の消去を確認するためのクリップボード。</summary>
internal sealed class RecordingClipboard : IClipboardService
{
    public string? Text { get; private set; }
    public int ClearCount { get; private set; }

    public void SetText(string text, TimeSpan clearAfter) => Text = text;

    public void ClearIfOurs()
    {
        Text = null;
        ClearCount++;
    }
}
