using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SaymonTools.App.Native;

namespace SaymonTools.App.UI;

/// <summary>Bringing our windows to the front although the user is working in another app.</summary>
internal static class ForegroundHelper
{
    private const byte VK_MENU = 0x12;

    public static void Force(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!Win32.SetForegroundWindow(hwnd))
        {
            // Windows only lets the active app change the foreground window; a synthetic Alt press lifts that lock.
            Win32.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.SetForegroundWindow(hwnd);
            Win32.keybd_event(VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        window.Activate();
    }

    /// <summary>An invisible window near the cursor that owns a dialog and is in the foreground.</summary>
    public static Window Host()
    {
        Win32.GetCursorPos(out var p);
        var host = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Title = "SaymonTools",
        };
        host.Show();
        var hwnd = new WindowInteropHelper(host).Handle;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        Force(host);
        return host;
    }
}
