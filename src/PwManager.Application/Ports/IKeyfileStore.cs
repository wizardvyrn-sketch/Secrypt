namespace PwManager.Application.Ports;

/// <summary>鍵ファイルの読み書き（USB メモリ等）。テストでは書き込み失敗などを差し込める。</summary>
public interface IKeyfileStore
{
    bool Exists(string path);

    /// <summary>読む。ない場合は FileNotFoundException（USB メモリが挿さっていない等）。</summary>
    byte[] Read(string path);

    /// <summary>一時ファイルに書いてから置き換える（書きかけの鍵ファイルを残さない）。</summary>
    void Write(string path, ReadOnlySpan<byte> data);

    void Delete(string path);
}
