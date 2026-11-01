using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PwManager.Core.Localization;
using QRCoder;

namespace PwManager.App.Services;

/// <summary>キーファイルの予備を QR コードとして印刷する（USB メモリ紛失・故障時の予備）。</summary>
public static class QrPrinter
{
    public static BitmapImage CreateQrImage(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// 印刷ダイアログを出して印刷する。キャンセルされたら false。
    /// ファイルに保存する仮想プリンター（PDF・XPS・OneNote など）が選ばれたら警告する。鍵ファイルと同じ内容が PC 内のファイルとして残り、
    /// 鍵ファイルを保管ファイルと別の場所に置く意味がなくなるため（docs/02 T4c）。
    /// </summary>
    public static bool Print(string keyfileText, Guid vaultId, Window owner)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        if (IsFileOutputPrinter(dialog.PrintQueue))
        {
            var choice = Views.ThemedDialog.Choose(owner, L.T("紙のプリンターを選んでください", "Choose a paper printer"),
                L.T($"「{dialog.PrintQueue.Name}」は、印刷内容をファイルとして保存するプリンターのようです。\n" +
                    "鍵ファイルと同じ内容が PDF などのファイルとして PC やクラウドに残り、それとパスワードがあれば、保存したパスワードを開けてしまいます。\n" +
                    "紙に印刷できるプリンターを選び直してください。",
                    $"\"{dialog.PrintQueue.Name}\" appears to save the printout as a file.\n" +
                    "The same content as the key file would remain as a file (PDF, etc.) on this PC or in the cloud, and together with the password it can open your saved passwords.\n" +
                    "Please choose a printer that prints on paper."),
                0, L.T("やめる", "Cancel"), L.T("それでも印刷する", "Print anyway"));
            if (choice != 1) return false;
        }

        var page = new StackPanel { Margin = new Thickness(60), Width = 600, Background = Brushes.White };
        page.Children.Add(new TextBlock { Text = L.T("Secrypt キーファイル（予備）", "Secrypt key file (backup)"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.Black });
        page.Children.Add(new TextBlock
        {
            Text = L.T($"Vault ID: {vaultId}    作成日: {DateTime.Now:yyyy-MM-dd}", $"Vault ID: {vaultId}    Created: {DateTime.Now:yyyy-MM-dd}"),
            FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 20),
        });
        page.Children.Add(new Image { Source = CreateQrImage(keyfileText), Width = 320, Height = 320, HorizontalAlignment = HorizontalAlignment.Left });
        page.Children.Add(new TextBlock
        {
            Text = L.T("・この紙は USB メモリの鍵ファイルと同じ働きをします。パスワードと組み合わせて、保存したパスワードを開けます。\n" +
                   "・パソコン・USB メモリとは別の場所（金庫など）に保管してください。\n" +
                   "・復旧コードの紙とは別の場所に保管してください。\n" +
                   "・不要になったら、シュレッダーで処分し、アプリの「鍵と設定」でこの鍵ファイルを解錠方法から削除してください。",
                   "- This paper works the same as the key file on the USB drive. Together with the password, it opens your saved passwords.\n" +
                   "- Keep it somewhere separate from the PC and the USB drive (such as a safe).\n" +
                   "- Keep it separate from the recovery code paper.\n" +
                   "- When no longer needed, shred it and remove this key file from the unlock methods in Keys and settings."),
            FontSize = 13, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 12), LineHeight = 22,
        });
        page.Children.Add(new TextBlock
        {
            Text = keyfileText, FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap,
        });

        page.Measure(new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
        page.Arrange(new Rect(new Point(0, 0), page.DesiredSize));
        dialog.PrintVisual(page, L.T("Secrypt キーファイル予備", "Secrypt key file backup"));
        return true;
    }

    /// <summary>ファイルに出力する（紙に印刷しない）プリンターか。名前とポートで判定する（確実ではないため、紙の印刷を案内する文言と併用）。</summary>
    internal static bool IsFileOutputPrinter(PrintQueue? queue)
    {
        if (queue is null) return false;
        string[] nameHints = ["PDF", "XPS", "OneNote", "Fax", "Document Writer", "Send To", "ファイル"];
        if (nameHints.Any(h => queue.Name.Contains(h, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            var port = queue.QueuePort?.Name ?? string.Empty;
            return port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase)
                || port.Equals("FILE:", StringComparison.OrdinalIgnoreCase)
                || port.Equals("nul:", StringComparison.OrdinalIgnoreCase)
                || port.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase)
                || port.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                || port.EndsWith(".xps", StringComparison.OrdinalIgnoreCase);
        }
        catch (PrintSystemException)
        {
            return false;
        }
    }
}
