using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PwManager.App.Services;

/// <summary>
/// 画面キャプチャ・録画・画面共有に映らないようにする（Windows 10 2004 以降、docs/08 §7）。
/// メインウィンドウだけでなく、復旧コードや鍵ファイルのテキストを表示・入力するダイアログ、「鍵と設定」にも掛ける。
/// 開発用の許可（<see cref="AllowCapture"/>）は Debug ビルドのみ有効（R-20）。起動時に App が設定する。
/// </summary>
public static class CaptureProtection
{
    private const uint WdaExcludeFromCapture = 0x11;

    public static bool AllowCapture { get; set; }

    /// <summary>ウィンドウのハンドルができた後（OnSourceInitialized）に呼ぶ。</summary>
    public static void Apply(Window window)
    {
        if (AllowCapture) return;
        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
