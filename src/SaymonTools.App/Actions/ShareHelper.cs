using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SaymonTools.App.Native;
using SaymonTools.App.UI;
using SaymonTools.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Actions;

/// <summary>Opens the Windows share sheet (Nearby sharing, Mail, Teams, …) for files.</summary>
internal static class ShareHelper
{
    private static Window? _host;
    private static IReadOnlyList<IStorageItem> _pending = [];
    private static string _title = "";

    public static async Task ShareAsync(IReadOnlyList<FileItem> items)
    {
        var storageItems = new List<IStorageItem>();
        foreach (var item in items)
            storageItems.Add(await StorageFile.GetFileFromPathAsync(item.Path));
        _pending = storageItems;
        _title = items.Count == 1 ? items[0].Name : Items(items.Count);

        var hwnd = EnsureHost();
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }

    /// <summary>The share sheet needs an owner window; a 1×1 invisible one near the cursor is enough.</summary>
    private static IntPtr EnsureHost()
    {
        if (_host is null)
        {
            _host = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Title = "SaymonTools",
            };
            _host.Show();
            var handle = new WindowInteropHelper(_host).Handle;
            var manager = DataTransferManagerInterop.GetForWindow(handle);
            manager.DataRequested += (_, e) =>
            {
                var request = e.Request;
                request.Data.Properties.Title = _title;
                request.Data.Properties.ApplicationName = "SaymonTools";
                request.Data.SetStorageItems(_pending);
            };
        }
        var hwnd = new WindowInteropHelper(_host).Handle;
        Win32.GetCursorPos(out var p);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        ForegroundHelper.Force(_host);
        return hwnd;
    }
}
