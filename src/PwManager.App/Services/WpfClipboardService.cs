using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PwManager.Application.Ports;

namespace PwManager.App.Services;

/// <summary>
/// パスワードのコピー用クリップボード（R-08: 静的クラスから DI で注入するサービスに変更）。
/// - Windows のクリップボード履歴（Win+V）とクラウド同期に載せない形式を付ける
/// - 指定時間後、まだ自分が置いた内容のままなら消去する
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    private DispatcherTimer? _timer;
    private byte[]? _ourHash;

    public void SetText(string text, TimeSpan clearAfter)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        // クリップボード監視ツール・履歴・クラウド同期から除外（Windows 10 1809 以降）
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));

        Retry(() => Clipboard.SetDataObject(data, copy: true));
        _ourHash = Hash(text);

        _timer?.Stop();
        if (clearAfter <= TimeSpan.Zero) return;
        _timer = new DispatcherTimer { Interval = clearAfter };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            ClearIfOurs();
        };
        _timer.Start();
    }

    /// <summary>クリップボードが自分の置いた内容のままなら消去する（他アプリでコピーし直した内容は消さない）。</summary>
    public void ClearIfOurs()
    {
        if (_ourHash is null) return;
        try
        {
            if (Clipboard.ContainsText() && CryptographicOperations.FixedTimeEquals(Hash(Clipboard.GetText()), _ourHash))
                Retry(Clipboard.Clear);
        }
        catch (COMException)
        {
            // 他アプリがクリップボードを開いている。次の機会に任せる
        }
        _ourHash = null;
    }

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.Unicode.GetBytes(s));

    private static void Retry(Action action)
    {
        for (int i = 0; ; i++)
        {
            try { action(); return; }
            catch (COMException) when (i < 5) { Thread.Sleep(50); }
        }
    }
}
