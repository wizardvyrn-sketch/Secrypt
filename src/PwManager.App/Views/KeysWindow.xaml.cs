using System.Windows;
using PwManager.App.Services;

namespace PwManager.App.Views;

/// <summary>鍵と設定（S-04）。処理は KeysViewModel。</summary>
public partial class KeysWindow : Window
{
    public KeysWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureProtection.Apply(this);
    }
}
