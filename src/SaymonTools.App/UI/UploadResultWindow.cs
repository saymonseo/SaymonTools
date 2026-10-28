using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using SaymonTools.Core;
using SaymonTools.Core.Upload;
using static SaymonTools.App.UI.Controls;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.UI;

/// <summary>The link of a finished upload: QR code to open it on a phone, the link itself and quick buttons.</summary>
internal sealed class UploadResultWindow : Window
{
    private readonly string _link;
    private readonly TextBlock _copied;

    public UploadResultWindow(UploadResult result, string fileName)
    {
        _link = result.Link;
        Theme.Refresh();
        Title = T("SaymonTools – link", "SaymonTools — ссылка");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/SaymonTools.ico"));
        Background = Theme.Page;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        var title = Text(T("Link ready", "Ссылка готова"), 22, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        root.Children.Add(title);
        var detail = Text(fileName, 13, brush: Theme.Muted, wrap: false);
        detail.Margin = new Thickness(0, 2, 0, 18);
        root.Children.Add(detail);

        // Dark on white whatever the theme: phone cameras read that best. Sharp pixels, no smoothing.
        var qr = new Image { Source = QrImage(result.Link), Width = 240, Height = 240, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = qr,
        });
        var scan = Text(T("Scan with your phone's camera", "Наведите камеру телефона"), 12, brush: Theme.Muted);
        scan.HorizontalAlignment = HorizontalAlignment.Center;
        scan.Margin = new Thickness(0, 8, 0, 16);
        root.Children.Add(scan);

        var link = TextField(result.Link, 2000);
        link.IsReadOnly = true;
        link.FontSize = 13;
        link.GotKeyboardFocus += (_, _) => link.SelectAll();
        root.Children.Add(link);
        var info = Text(T($"{result.Host.Name} · kept {result.Retention} · anyone with the link can download it.", $"{result.Host.Name} · хранится {result.Retention} · скачать может любой, у кого есть ссылка."), 12, brush: Theme.Muted);
        info.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(info);
        _copied = Text(T("Copied to the clipboard.", "Скопировано в буфер обмена."), 12, brush: Theme.Success);
        _copied.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_copied);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var copy = Button(T("Copy link", "Копировать ссылку"), () => CopyLink(), primary: true, glyph: Glyphs.Copy);
        var open = Button(T("Open", "Открыть"), OpenLink, glyph: Glyphs.Globe);
        var close = Button(T("Close", "Закрыть"), Close);
        open.Margin = new Thickness(8, 0, 0, 0);
        close.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(copy);
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>Copies the link and shows the window.</summary>
    public static void Show(UploadResult result, string fileName)
    {
        var window = new UploadResultWindow(result, fileName);
        if (!window.CopyLink())
            window._copied.Visibility = Visibility.Collapsed;
        window.Show();
        ForegroundHelper.Force(window);
    }

    internal static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10, drawQuietZones: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool CopyLink()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(_link);
                _copied.Visibility = Visibility.Visible;
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another app holds the clipboard open; try again shortly.
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private void OpenLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_link) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
