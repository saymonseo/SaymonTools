using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using SaymonTools.App.Shell;
using SaymonTools.Core;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace SaymonTools.App.Wheel;

/// <summary>
/// What was dragged, as files. Besides ordinary files this understands things that are not on disk yet:
/// virtual files (browser images, Outlook attachments, files inside a ZIP folder), raw images and web links.
/// Those are saved to Downloads\SaymonTools first.
/// </summary>
internal sealed record DropContent(IReadOnlyList<FileItem> Items, Func<Task>? Prepare = null)
{
    private const string GroupDescriptor = "FileGroupDescriptorW";
    private const string FileContents = "FileContents";
    private const string UrlFormat = "UniformResourceLocatorW";
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// Reads a drag. With <paramref name="materialize"/> false only names are worked out (to fill the wheel
    /// while dragging); on drop it is true and the data is written to disk.
    /// </summary>
    public static DropContent? Read(IDataObject data, bool materialize)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
                return new DropContent(paths.Select(p => new FileItem(materialize ? RescueFromTemp(p) : p)).ToList());
            if (data.GetDataPresent(GroupDescriptor) && ReadVirtualFiles(data, materialize) is { Count: > 0 } virtualFiles)
                return new DropContent(virtualFiles);
            if (ReadImage(data, materialize) is { } image)
                return new DropContent([image]);
            if (ReadUrl(data) is { } url)
                return Download(url, materialize);
        }
        catch (Exception ex) when (ex is COMException or IOException or ExternalException or InvalidOperationException)
        {
            App.Log(ex);
        }
        return null;
    }

    // ------------------------------------------------------------------ virtual files

    private static List<FileItem> ReadVirtualFiles(IDataObject data, bool materialize)
    {
        var names = ReadDescriptorNames(data);
        var items = new List<FileItem>();
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is not { } name)
                continue; // folder entry
            if (!materialize)
            {
                items.Add(new FileItem(Path.Combine(Path.GetTempPath(), name)));
                continue;
            }
            var target = Target(name);
            using (var output = File.Create(target))
                CopyContents(data, i, output);
            items.Add(new FileItem(target));
        }
        return items;
    }

    /// <summary>File names from FILEGROUPDESCRIPTORW; null for folders.</summary>
    private static List<string?> ReadDescriptorNames(IDataObject data)
    {
        var names = new List<string?>();
        if (data.GetData(GroupDescriptor) is not MemoryStream stream)
            return names;
        var bytes = stream.ToArray();
        const int descriptorSize = 592;
        const int attributesOffset = 36;
        const int nameOffset = 72;
        var count = BitConverter.ToInt32(bytes, 0);
        for (var i = 0; i < count && 4 + (i + 1) * descriptorSize <= bytes.Length; i++)
        {
            var start = 4 + i * descriptorSize;
            var attributes = BitConverter.ToUInt32(bytes, start + attributesOffset);
            var raw = Encoding.Unicode.GetString(bytes, start + nameOffset, 520);
            var name = Path.GetFileName(raw[..Math.Max(0, raw.IndexOf('\0') is var z and >= 0 ? z : raw.Length)]);
            names.Add((attributes & 0x10) != 0 || name.Length == 0 ? null : Sanitize(name));
        }
        return names;
    }

    /// <summary>FileContents for item <paramref name="index"/>; needs the COM interface to pass the index.</summary>
    private static void CopyContents(IDataObject data, int index, Stream output)
    {
        if (data is ComDataObject com)
        {
            var format = new FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat(FileContents).Id,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
            };
            try
            {
                com.GetData(ref format, out var medium);
                try
                {
                    CopyMedium(medium, output);
                    return;
                }
                finally
                {
                    ReleaseStgMedium(ref medium);
                }
            }
            catch (Exception ex) when ((ex is COMException or NotImplementedException) && index == 0)
            {
                // Some sources only answer the plain request; fall through.
            }
        }
        if (data.GetData(FileContents) is Stream stream)
            stream.CopyTo(output);
        else
            throw new IOException("No file contents in the drag.");
    }

    private static void CopyMedium(STGMEDIUM medium, Stream output)
    {
        if (medium.tymed == TYMED.TYMED_ISTREAM)
        {
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            var buffer = new byte[81920];
            var readPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPtr);
                    var read = Marshal.ReadInt32(readPtr);
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(readPtr);
                Marshal.ReleaseComObject(stream);
            }
        }
        else if (medium.tymed == TYMED.TYMED_HGLOBAL)
        {
            var size = (int)GlobalSize(medium.unionmember);
            var pointer = GlobalLock(medium.unionmember);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                output.Write(bytes, 0, size);
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        else
        {
            throw new IOException($"Unsupported storage medium {medium.tymed}.");
        }
    }

    // ------------------------------------------------------------------ images and links

    private static FileItem? ReadImage(IDataObject data, bool materialize)
    {
        var hasPng = data.GetDataPresent("PNG");
        if (!hasPng && !data.GetDataPresent(DataFormats.Bitmap))
            return null;
        var name = T("Image", "Изображение") + $" {DateTime.Now:yyyy-MM-dd HHmmss}.png";
        if (!materialize)
            return new FileItem(Path.Combine(Path.GetTempPath(), name));
        var target = Target(name);
        if (hasPng && data.GetData("PNG") is MemoryStream png)
        {
            File.WriteAllBytes(target, png.ToArray());
        }
        else if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            SavePng(bitmap, target);
        }
        else
        {
            return null;
        }
        return new FileItem(target);
    }

    public static void SavePng(BitmapSource bitmap, string target)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(target);
        encoder.Save(file);
    }

    private static Uri? ReadUrl(IDataObject data)
    {
        string? text = null;
        if (data.GetDataPresent(UrlFormat) && data.GetData(UrlFormat) is MemoryStream stream)
            text = Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
        else if (data.GetDataPresent(DataFormats.UnicodeText))
            text = data.GetData(DataFormats.UnicodeText) as string;
        text = text?.Trim().Split('\n')[0].Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    private static DropContent Download(Uri url, bool materialize)
    {
        var name = Sanitize(Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath)));
        if (name.Length == 0)
            name = "download";
        if (!materialize)
            return new DropContent([new FileItem(Path.Combine(Path.GetTempPath(), name))]);
        var target = Target(name);
        return new DropContent([new FileItem(target)], async () =>
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(target);
            await input.CopyToAsync(output);
        });
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaymonTools/1.0");
        return client;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Browsers (e.g. Firefox) hand over images as files in the temp folder, which is also where the result
    /// would go. Copy such files to Downloads\SaymonTools so the user can find the output.
    /// </summary>
    private static string RescueFromTemp(string path)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!File.Exists(path) || !Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            return path;
        var target = Target(Path.GetFileName(path));
        File.Copy(path, target);
        return target;
    }

    private static string T(string en, string ru) => Loc.T(en, ru);

    private static string Target(string name) =>
        OutputPaths.Unique(KnownFolders.Incoming, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name).TrimStart('.'));

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
}
