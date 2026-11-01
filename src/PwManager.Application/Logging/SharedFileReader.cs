using System.Text;

namespace PwManager.Application.Logging;

/// <summary>
/// 他のプロセス・スレッドが書き込み中かもしれないファイル（ログ）を読む。
/// Windows では、書き込み側が開いている間に FileShare.Read だけで開くと IOException になるため、
/// 読み取り側も FileShare.ReadWrite | Delete で開く（Linux ではこの問題は起きないので CI では見えにくい）。
/// </summary>
public static class SharedFileReader
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    public static string ReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string[] ReadAllLines(string path) => ReadLines(path).ToArray();
}
