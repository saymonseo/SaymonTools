using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SaymonTools.App.Native;

namespace SaymonTools.App.Shell;

/// <summary>
/// Reads what is selected in the active File Explorer window, or else what is on the clipboard
/// (copied files, or an image such as a Win+Shift+S screenshot, which is saved as PNG first).
/// </summary>
internal static class ExplorerSelection
{
    public static IReadOnlyList<string> Get(IntPtr foreground)
    {
        var fromExplorer = FromExplorer(foreground);
        return fromExplorer.Count > 0 ? fromExplorer : FromClipboard();
    }

    public static IReadOnlyList<string> FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                return Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var name = Core.Loc.T("Screenshot", "Снимок экрана") + $" {DateTime.Now:yyyy-MM-dd HHmmss}";
                var target = Core.OutputPaths.Unique(KnownFolders.Incoming, name, "png");
                Wheel.DropContent.SavePng(image, target);
                return [target];
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException)
        {
            // Clipboard busy or unreadable.
        }
        return [];
    }

    /// <summary>
    /// Walks the open Explorer windows with early-bound shell interfaces (late-bound <c>dynamic</c> COM
    /// does not survive trimming) and returns the selection of the one in the foreground.
    /// </summary>
    private static List<string> FromExplorer(IntPtr foreground)
    {
        var result = new List<string>();
        // Windows 11 tabs share the frame HWND; the first ShellTabWindowClass child is the visible tab.
        var activeTab = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);
        IShellWindows? windows = null;
        try
        {
            windows = (IShellWindows)new ShellWindows();
            if (windows.GetCount(out var count) != 0)
                return result;
            for (var i = 0; i < count; i++)
            {
                if (windows.Item(i, out var window) != 0 || window is null)
                    continue;
                try
                {
                    if (Browser(window) is not { } browser || browser.GetWindow(out var tab) != 0 || GetAncestor(tab, GaRoot) != foreground)
                        continue;
                    var paths = Selected(browser);
                    if (paths.Count > 0)
                    {
                        result = paths;
                        if (activeTab == IntPtr.Zero || tab == activeTab)
                            break;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // Not a file-system window (e.g. Control Panel).
                }
                finally
                {
                    Marshal.ReleaseComObject(window);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
        finally
        {
            if (windows is not null)
                Marshal.ReleaseComObject(windows);
        }
        return result.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    private static IShellBrowser? Browser(object window)
    {
        var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
        var iid = typeof(IShellBrowser).GUID;
        return window is IServiceProvider provider && provider.QueryService(ref sid, ref iid, out var browser) == 0
            ? browser as IShellBrowser
            : null;
    }

    private static List<string> Selected(IShellBrowser browser)
    {
        var paths = new List<string>();
        if (browser.QueryActiveShellView(out var view) != 0 || view is not IFolderView folderView)
            return paths;
        var iid = typeof(IShellItemArray).GUID;
        if (folderView.Items(SvgioSelection, ref iid, out var array) != 0 || array is not IShellItemArray items)
            return paths;
        if (items.GetCount(out var count) != 0)
            return paths;
        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) != 0 || item.GetDisplayName(SigdnFileSysPath, out var name) != 0)
                continue;
            try
            {
                if (Marshal.PtrToStringUni(name) is { Length: > 0 } path)
                    paths.Add(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        return paths;
    }

    private const uint GaRoot = 2;
    private const uint SvgioSelection = 1;
    private const uint SigdnFileSysPath = 0x80058000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [ComImport]
    [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindows
    {
    }

    /// <summary>Dual interface; only the first two methods after IDispatch are needed.</summary>
    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object? folder);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    /// <summary>IOleWindow and IShellBrowser up to QueryActiveShellView; the placeholders keep the vtable order.</summary>
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig] int ContextSensitiveHelp();
        [PreserveSig] int InsertMenusSB();
        [PreserveSig] int SetMenuSB();
        [PreserveSig] int RemoveMenusSB();
        [PreserveSig] int SetStatusTextSB();
        [PreserveSig] int EnableModelessSB();
        [PreserveSig] int TranslateAcceleratorSB();
        [PreserveSig] int BrowseObject();
        [PreserveSig] int GetViewStateStream();
        [PreserveSig] int GetControlWindow();
        [PreserveSig] int SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? view);
    }

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode();
        [PreserveSig] int SetCurrentViewMode();
        [PreserveSig] int GetFolder();
        [PreserveSig] int Item();
        [PreserveSig] int ItemCount();

        [PreserveSig]
        int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? items);
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler();
        [PreserveSig] int GetPropertyStore();
        [PreserveSig] int GetPropertyDescriptionList();
        [PreserveSig] int GetAttributes();

        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetItemAt(uint index, out IShellItem item);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler();
        [PreserveSig] int GetParent();

        [PreserveSig]
        int GetDisplayName(uint sigdn, out IntPtr name);
    }

    public static Win32.POINT CursorOrCenter()
    {
        if (Win32.GetCursorPos(out var p))
            return p;
        return new Win32.POINT { X = 800, Y = 500 };
    }
}
