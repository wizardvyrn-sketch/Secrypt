using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PwManager.App.Views;
using PwManager.Core.Localization;
using PwManager.Presentation.Ports;
using PwManager.Presentation.ViewModels;

namespace PwManager.App.Services;

/// <summary>ダイアログの実装（ThemedDialog・標準のファイル選択・印刷）。</summary>
public sealed class WpfDialogService : IDialogService
{
    /// <summary>ダイアログの親（いま前面にあるウィンドウ。「鍵と設定」から開く場合はそちら）。</summary>
    private static Window Owner
    {
        get
        {
            var app = System.Windows.Application.Current;
            return app.Windows.OfType<Window>().LastOrDefault(w => w.IsActive && w.IsVisible)
                   ?? app.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w != app.MainWindow)
                   ?? app.MainWindow;
        }
    }

    public bool Confirm(string title, string message, string okText = "OK") => ThemedDialog.Confirm(Owner, title, message, okText);

    public int Choose(string title, string message, int cancelIndex, params string[] options)
        => ThemedDialog.Choose(Owner, title, message, cancelIndex, options);

    public void Info(string title, string message) => ThemedDialog.Info(Owner, title, message);

    public (bool Ok, bool Option) ConfirmWithOption(string title, string message, string okText, string optionText, bool optionDefault)
    {
        var d = new ThemedDialog(Owner, title);
        d.AddText(message);
        var check = new CheckBox { Content = optionText, IsChecked = optionDefault, Margin = new Thickness(0, 12, 0, 0) };
        d.AddElement(check);
        d.AddButtons(okText);
        return d.ShowDialog() == true ? (true, check.IsChecked == true) : (false, false);
    }

    public string? PromptText(string title, string message, string label, Func<string, string?>? validate = null)
    {
        var d = new ThemedDialog(Owner, title, 520);
        d.AddText(message);
        var box = d.AddTextBox(label);
        d.AddButtons(L.T("使う", "Use"), validate: validate is null ? null : () => validate(box.Text));
        d.Loaded += (_, _) => box.Focus();
        return d.ShowDialog() == true ? box.Text : null;
    }

    public byte[][]? PromptSecrets(string title, string message, IReadOnlyList<string> labels, Func<IReadOnlyList<byte[]>, string?>? validate = null)
    {
        var d = new ThemedDialog(Owner, title);
        d.AddText(message);
        var boxes = labels.Select(d.AddPassword).ToList();
        d.AddButtons(L.T("次へ", "Next"), validate: validate is null ? null : () =>
        {
            var values = boxes.Select(SecureInput.ToUtf8).ToList();
            try { return validate(values); }
            finally { foreach (var v in values) Array.Clear(v); }
        });
        d.Loaded += (_, _) => boxes.FirstOrDefault()?.Focus();
        var ok = d.ShowDialog() == true;
        try { return ok ? boxes.Select(SecureInput.ToUtf8).ToArray() : null; }
        finally { foreach (var b in boxes) b.Clear(); }
    }

    public bool ShowRecoveryCode(string title, string message, string code, string checkLabel, Func<string, string?> validate)
    {
        var d = new ThemedDialog(Owner, title, 520);
        d.AddText(message);
        d.AddCode(code);
        var box = d.AddTextBox(checkLabel);
        box.CharacterCasing = CharacterCasing.Upper;
        box.MaxLength = 4;
        d.AddButtons(L.T("書き写した", "I wrote it down"), validate: () => validate(box.Text));
        d.Loaded += (_, _) => box.Focus();
        return d.ShowDialog() == true;
    }

    public string? PickOpenFile(string title, string filter, string? initialPath = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        if (initialPath is not null)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(initialPath);
                if (dir is not null && System.IO.Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            catch (ArgumentException) { /* パスとして不正（QR のテキスト表示など） */ }
        }
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string defaultFileName, string filter)
    {
        var dlg = new SaveFileDialog { Title = title, FileName = defaultFileName, Filter = filter, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public bool PrintKeyfileQr(string keyfileText, Guid vaultId) => QrPrinter.Print(keyfileText, vaultId, Owner);

    public void ShowKeys(KeysViewModel keys)
    {
        var window = new KeysWindow { Owner = System.Windows.Application.Current.MainWindow, DataContext = keys };
        keys.CloseRequested += (_, _) => window.Close();
        window.Loaded += async (_, _) => await keys.LoadAsync();
        window.ShowDialog();
    }

    public void CloseAll()
    {
        var app = System.Windows.Application.Current;
        foreach (var w in app.Windows.OfType<Window>().Where(w => w != app.MainWindow).Reverse().ToList()) w.Close();
    }
}
