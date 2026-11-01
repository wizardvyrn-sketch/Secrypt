using System.Security.Cryptography;
using System.Text;

namespace PwManager.App.Services;

/// <summary>
/// 多重起動の防止（R-16）。同じ Vault を 2 つのウィンドウで開くと、後から保存した方が他方の変更を消すため。
/// Vault のパスごとに名前付き Mutex を取る（テスト用 Vault を別に開くことはできる）。
/// 2 つ目の起動は、既にあるウィンドウに「前面に出て」と知らせて終了する。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>最初のインスタンスなら取得して返す。既に起動していれば相手を前面に出させて null。</summary>
    public static SingleInstance? TryAcquire(string vaultPath, Action onActivate)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vaultPath.ToUpperInvariant())))[..16];
        // 名前は旧名のまま（Secrypt への改名前の exe と同じ Vault を同時に開かせないため）
        var mutex = new Mutex(initiallyOwned: true, $@"Local\NewPWManageTool-{id}", out bool created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\NewPWManageTool-{id}-activate");
        if (!created)
        {
            activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
