using System.Diagnostics;
using System.IO;
using PwManager.Presentation.Ports;

namespace PwManager.App.Services;

/// <summary>既定のブラウザ・エクスプローラーで開く。</summary>
public sealed class ShellService : IShellService
{
    public void OpenUrl(Uri url) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
