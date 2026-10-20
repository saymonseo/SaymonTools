using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SaymonTools.App.Native;

namespace SaymonTools.App.Shell;

internal static class FileIcons
{
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>The Explorer icon for a file or folder.</summary>
    public static ImageSource? Get(string path, bool large = true)
    {
        var info = new Win32.SHFILEINFO();
        var flags = Win32.SHGFI_ICON | (large ? Win32.SHGFI_LARGEICON : Win32.SHGFI_SMALLICON);
        uint attributes = 0;
        if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
        {
            // Not on disk (yet): ask for the icon of the file type instead.
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = System.IO.Path.HasExtension(path) ? FILE_ATTRIBUTE_NORMAL : FILE_ATTRIBUTE_DIRECTORY;
        }
        var result = Win32.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<Win32.SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }
}
