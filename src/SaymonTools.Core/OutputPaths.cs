using static SaymonTools.Core.Loc;

namespace SaymonTools.Core;

public static class OutputPaths
{
    /// <summary>"dir/name.ext", or "dir/name (2).ext" … if that already exists.</summary>
    public static string Unique(string dir, string baseName, string ext)
    {
        ext = ext.TrimStart('.');
        var suffix = ext.Length == 0 ? "" : "." + ext;
        var candidate = Path.Combine(dir, baseName + suffix);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){suffix}");
        return candidate;
    }

    public static string UniqueDirectory(string dir, string name) => Unique(dir, name, "");

    /// <summary>
    /// Runs <paramref name="produce"/> against a hidden temporary path in <paramref name="dir"/> and,
    /// when it succeeds, moves the file to a unique final name. Partial output is removed on failure.
    /// </summary>
    public static async Task<string> ProduceAsync(string dir, string baseName, string ext, Func<string, Task> produce)
    {
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{baseName}.{Guid.NewGuid().ToString("N")[..8]}.part.{ext}");
        try
        {
            await produce(temp).ConfigureAwait(false);
            if (!File.Exists(temp))
                throw new ConversionException(T("The conversion produced no output.", "Конвертация не создала выходных файлов."));
            // Files of a batch finish side by side; another one may take the free name between the check and the move.
            for (var attempt = 0; ; attempt++)
            {
                var final = Unique(dir, baseName, ext);
                try
                {
                    File.Move(temp, final);
                    return final;
                }
                catch (IOException) when (attempt < 50 && (File.Exists(final) || Directory.Exists(final)))
                {
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SaymonTools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);
