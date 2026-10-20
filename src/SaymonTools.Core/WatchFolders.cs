namespace SaymonTools.Core;

/// <summary>"Whatever lands in this folder gets this operation." Stored in the settings.</summary>
public sealed class WatchFolder
{
    public string Path { get; set; } = "";
    public string OperationId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubfolders { get; set; }
}

public static class WatchRules
{
    /// <summary>Downloads and editors write these first and rename them when they are done.</summary>
    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut", ".aria2", ".lock",
    };

    /// <summary>
    /// False for files that are not finished yet or are not the user's: temporary downloads, Office lock
    /// files (~$), hidden dot files and SaymonTools's own half-written results (".name.xxxx.part.ext").
    /// </summary>
    public static bool IsCandidate(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal))
            return false;
        if (name.Contains(".part.", StringComparison.OrdinalIgnoreCase))
            return false;
        if (TemporaryExtensions.Contains(System.IO.Path.GetExtension(name)))
            return false;
        return !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && !name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="file"/> is in <paramref name="folder"/> (or below it, if allowed).</summary>
    public static bool IsInside(WatchFolder folder, string file)
    {
        var root = System.IO.Path.GetFullPath(folder.Path).TrimEnd('\\', '/');
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))?.TrimEnd('\\', '/') ?? "";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(dir, root, comparison))
            return true;
        return folder.IncludeSubfolders && dir.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison);
    }
}
