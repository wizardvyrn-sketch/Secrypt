using PwManager.Application.Ports;
using PwManager.Core.Localization;

namespace PwManager.Application.Infrastructure;

/// <summary>鍵ファイルをファイルとして読み書きする。</summary>
public sealed class FileKeyfileStore : IKeyfileStore
{
    public bool Exists(string path) => File.Exists(path);

    public byte[] Read(string path) => File.ReadAllBytes(path);

    public void Write(string path, ReadOnlySpan<byte> data)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException(L.T("保存先フォルダが不正です。", "The save folder is invalid."));
        var tmp = Path.Combine(dir, Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { /* USB メモリが抜かれた等。残っても秘密の一部でしかない */ }
        }
    }

    public void Delete(string path) => File.Delete(path);
}
