using System.IO;
using System.Runtime.InteropServices;

namespace SaymonTools.App.Shell;

internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0)
                    return path;
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>
    /// Where files that only existed in a drag or the clipboard (web images, screenshots, e-mail attachments)
    /// are saved before converting, so the results end up somewhere the user can find them.
    /// </summary>
    public static string Incoming
    {
        get
        {
            var dir = Path.Combine(Downloads, "SaymonTools");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out string pszPath);
}
