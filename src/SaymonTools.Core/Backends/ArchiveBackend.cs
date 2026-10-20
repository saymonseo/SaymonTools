using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>Extracting (ZIP, TAR, GZ, BZ2, XZ, RAR, 7z) and packing (ZIP, TAR, TAR.GZ).</summary>
public static class ArchiveBackend
{
    private static readonly ExtractionOptions Options = new() { ExtractFullPath = true, Overwrite = true, PreserveFileTime = true };

    /// <summary>
    /// Extracts next to the archive. When the archive holds exactly one top-level item it is placed
    /// directly in <paramref name="destinationDir"/>; otherwise everything goes into a folder named after the archive.
    /// </summary>
    public static async Task<string> ExtractAsync(string archive, string destinationDir, CancellationToken ct)
    {
        var temp = Path.Combine(destinationDir, $".{Formats.BaseNameOf(archive)}.{Guid.NewGuid().ToString("N")[..8]}.part");
        Directory.CreateDirectory(temp);
        try
        {
            await Task.Run(() => ExtractTo(archive, temp, ct), ct).ConfigureAwait(false);

            // A .tar.gz read as plain gzip yields a single .tar – unpack that too.
            var only = Directory.GetFileSystemEntries(temp);
            if (only.Length == 1 && File.Exists(only[0]) && Formats.KeyOf(only[0]) == "tar")
            {
                var inner = only[0];
                var innerDir = Path.Combine(temp, ".inner");
                Directory.CreateDirectory(innerDir);
                ExtractTo(inner, innerDir, ct);
                File.Delete(inner);
                foreach (var e in Directory.GetFileSystemEntries(innerDir))
                    MoveEntry(e, Path.Combine(temp, Path.GetFileName(e)));
                Directory.Delete(innerDir);
            }

            var entries = Directory.GetFileSystemEntries(temp);
            if (entries.Length == 0)
                throw new ConversionException(T("The archive is empty.", "Архив пуст."));
            if (entries.Length == 1)
            {
                var name = Path.GetFileName(entries[0]);
                var isDir = Directory.Exists(entries[0]);
                var target = isDir
                    ? OutputPaths.UniqueDirectory(destinationDir, name)
                    : OutputPaths.Unique(destinationDir, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name).TrimStart('.'));
                MoveEntry(entries[0], target);
                return target;
            }
            var folder = OutputPaths.UniqueDirectory(destinationDir, Formats.BaseNameOf(archive));
            Directory.Move(temp, folder);
            return folder;
        }
        finally
        {
            OutputPaths.TryDelete(temp);
        }
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static void ExtractTo(string archive, string destination, CancellationToken ct)
    {
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        try
        {
            if (Formats.KeyOf(archive) == "7z")
            {
                using var a = ArchiveFactory.OpenArchive(archive, new ReaderOptions());
                foreach (var entry in a.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory || entry.Key is null)
                        continue;
                    var path = SafeTarget(fullDestination, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.WriteToFile(path, Options);
                }
                return;
            }

            using var stream = File.OpenRead(archive);
            using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory)
                    continue;
                var key = entry.Key;
                if (string.IsNullOrEmpty(key))
                    key = Formats.BaseNameOf(archive); // single-file .gz/.bz2/.xz
                var path = SafeTarget(fullDestination, key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                reader.WriteEntryToFile(path, Options);
            }
        }
        catch (Exception ex) when (ex is InvalidFormatException or ArchiveOperationException or CryptographicException)
        {
            throw new ConversionException(T($"Cannot extract the archive: {ex.Message}", $"Не удалось распаковать архив: {ex.Message}"), ex);
        }
    }

    /// <summary>Rejects entries like "../../evil.exe" that would escape the target folder.</summary>
    public static string SafeTarget(string fullDestination, string entryKey)
    {
        var relative = entryKey.Replace('\\', '/').TrimStart('/');
        var path = Path.GetFullPath(Path.Combine(fullDestination, relative));
        if (!path.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(T($"The archive contains an unsafe path: {entryKey}", $"Архив содержит небезопасный путь: {entryKey}"));
        return path;
    }

    /// <summary>Packs files and folders into one archive ("zip", "tar" or "tar.gz").</summary>
    public static async Task PackAsync(IReadOnlyList<string> items, string output, string format, CancellationToken ct)
    {
        var entries = new List<(string Source, string Name)>();
        foreach (var item in items)
        {
            var trimmed = item.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                var root = Path.GetFileName(trimmed);
                foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories))
                    entries.Add((file, root + "/" + Path.GetRelativePath(trimmed, file).Replace('\\', '/')));
                if (!Directory.EnumerateFileSystemEntries(trimmed).Any())
                    entries.Add((trimmed, root + "/"));
            }
            else
            {
                entries.Add((trimmed, Path.GetFileName(trimmed)));
            }
        }

        await Task.Run(async () =>
        {
            await using var file = File.Create(output);
            if (format == "zip")
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create);
                foreach (var (source, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (name.EndsWith('/'))
                        zip.CreateEntry(name);
                    else
                        zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                }
                return;
            }

            Stream target = format == "tar.gz" ? new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true) : file;
            try
            {
                await using var tar = new TarWriter(target, TarEntryFormat.Pax, leaveOpen: true);
                foreach (var (source, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    await tar.WriteEntryAsync(source, name.TrimEnd('/'), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (target != file)
                    await target.DisposeAsync().ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    public static string ArchiveNameFor(IReadOnlyList<FileItem> items) =>
        items.Count == 1 ? items[0].BaseName : T("Archive", "В архив");

    /// <summary>Repacks an archive into another archive format.</summary>
    public static async Task<string> RepackAsync(FileItem item, string format, OperationContext ctx)
    {
        var temp = OutputPaths.TempDirectory();
        try
        {
            var extracted = await ExtractAsync(item.Path, temp, ctx.CancellationToken).ConfigureAwait(false);
            var contents = Directory.Exists(extracted) && Path.GetFileName(extracted) == item.BaseName
                ? Directory.GetFileSystemEntries(extracted)
                : [extracted];
            return await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, tmp =>
                PackAsync(contents, tmp, format, ctx.CancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(temp);
        }
    }
}
