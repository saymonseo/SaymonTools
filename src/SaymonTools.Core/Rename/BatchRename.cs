using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaymonTools.Core.Operations;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Rename;

public enum RenameCase
{
    Keep,
    Lower,
    Upper,
    Title,
}

/// <summary>The order files are numbered in.</summary>
public enum RenameOrder
{
    Name,
    Modified,
    Taken,
    Selection,
}

/// <summary>What the batch rename dialog asks for. The extension is always kept.</summary>
public sealed record RenameOptions
{
    /// <summary>
    /// The new name without extension. Tokens: {name}, {n} / {n:000}, {date} / {date:yyyy-MM-dd},
    /// {time}, {taken} / {taken:yyyy-MM-dd} (when the photo was taken), {parent}, {ext}.
    /// </summary>
    public string Template { get; init; } = "{name}";

    public string Find { get; init; } = "";

    public string Replace { get; init; } = "";

    public bool UseRegex { get; init; }

    public bool MatchCase { get; init; }

    public RenameCase Case { get; init; }

    /// <summary>Russian letters to Latin ("Отпуск" → "Otpusk").</summary>
    public bool Transliterate { get; init; }

    public bool SpacesToUnderscores { get; init; }

    public int Start { get; init; } = 1;

    public int Step { get; init; } = 1;

    public RenameOrder Order { get; init; } = RenameOrder.Name;
}

/// <param name="Error">Why this file can't be renamed this way; null when it can.</param>
public sealed record RenameEntry(string Source, string Target, string? Error)
{
    public string OldName => Path.GetFileName(Source);

    public string NewName => Path.GetFileName(Target);

    public bool Changed => !string.Equals(Source, Target, StringComparison.Ordinal);
}

public sealed record RenameMove(string From, string To);

/// <summary>What a rename did, so it can be undone.</summary>
public sealed record RenameJournal(IReadOnlyList<RenameMove> Moves)
{
    public string ToJson() => JsonSerializer.Serialize(Moves.Select(m => new[] { m.From, m.To }));

    public static RenameJournal? FromJson(string json)
    {
        try
        {
            var pairs = JsonSerializer.Deserialize<string[][]>(json);
            return pairs is null ? null : new RenameJournal([.. pairs.Where(p => p.Length == 2).Select(p => new RenameMove(p[0], p[1]))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Works out new names for a batch of files and renames them safely (and back).</summary>
public static partial class BatchRename
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// New names for <paramref name="paths"/>, in numbering order. Every entry is checked: invalid or
    /// empty names, two files getting the same name, and names taken by other files are reported as errors.
    /// </summary>
    /// <param name="takenAt">When a photo was taken (EXIF); null falls back to the modification time.</param>
    public static IReadOnlyList<RenameEntry> Plan(IReadOnlyList<string> paths, RenameOptions options, Func<string, DateTime?>? takenAt = null)
    {
        Regex? regex = null;
        string? patternError = null;
        if (options.UseRegex && options.Find.Length > 0)
        {
            try
            {
                regex = new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                patternError = T("Invalid regular expression: ", "Неверное регулярное выражение: ") + ex.Message;
            }
        }

        var ordered = Sort(paths, options.Order, takenAt);
        var entries = new List<RenameEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var source = ordered[i];
            var (baseName, ext) = Split(source);
            var counter = options.Start + i * options.Step;
            string name;
            string? error = patternError;
            try
            {
                name = Expand(options.Template, source, baseName, ext, counter, takenAt);
                name = FindReplace(name, options, regex);
            }
            catch (RegexMatchTimeoutException)
            {
                name = baseName;
                error = T("The regular expression takes too long.", "Регулярное выражение выполняется слишком долго.");
            }
            catch (FormatException)
            {
                name = baseName;
                error = T("Invalid date or number format in the template.", "Неверный формат даты или числа в шаблоне.");
            }
            name = ApplyCase(name, options.Case);
            if (options.Transliterate)
                name = ToLatin(name);
            if (options.SpacesToUnderscores)
                name = WhitespaceRun().Replace(name, "_");
            name = Clean(name);

            var dir = Path.GetDirectoryName(source.TrimEnd('\\', '/')) ?? "";
            var target = Path.Combine(dir, name + ext);
            error ??= Validate(name, target);
            entries.Add(new RenameEntry(source, target, error));
        }
        return MarkConflicts(entries);
    }

    /// <summary>
    /// Renames everything in <paramref name="plan"/> (two steps through temporary names, so swaps like
    /// a→b, b→a work). On failure the files already renamed are put back and the error is rethrown.
    /// </summary>
    public static RenameJournal Apply(IReadOnlyList<RenameEntry> plan)
    {
        if (plan.FirstOrDefault(e => e.Error is not null) is { } bad)
            throw new ConversionException($"{bad.OldName}: {bad.Error}");
        return Move([.. plan.Where(e => e.Changed).Select(e => new RenameMove(e.Source, e.Target))]);
    }

    /// <summary>Gives the files of a finished rename their old names back.</summary>
    public static RenameJournal Undo(RenameJournal journal)
    {
        var back = journal.Moves.Select(m => new RenameMove(m.To, m.From)).ToList();
        var targets = new HashSet<string>(back.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        foreach (var move in back)
        {
            if (!Exists(move.From))
                throw new ConversionException(T($"{Path.GetFileName(move.From)} no longer exists.", $"Файл {Path.GetFileName(move.From)} больше не существует."));
            if (Exists(move.To) && !targets.Contains(move.To))
                throw new ConversionException(T($"{Path.GetFileName(move.To)} already exists.", $"Файл {Path.GetFileName(move.To)} уже существует."));
        }
        return Move(back);
    }

    private static RenameJournal Move(IReadOnlyList<RenameMove> moves)
    {
        var parked = new List<(RenameMove Move, string Temp)>();
        var done = new List<RenameMove>();
        try
        {
            // Step 1: every file to a hidden temporary name next to it.
            foreach (var move in moves)
            {
                var temp = Path.Combine(Path.GetDirectoryName(move.From) ?? "", $".saymontools-rename-{Guid.NewGuid():N}");
                MovePath(move.From, temp);
                parked.Add((move, temp));
            }
            // Step 2: from the temporary names to the new ones.
            foreach (var (move, temp) in parked.ToList())
            {
                if (Exists(move.To))
                    throw new ConversionException(T($"{Path.GetFileName(move.To)} already exists.", $"Файл {Path.GetFileName(move.To)} уже существует."));
                MovePath(temp, move.To);
                parked.RemoveAll(p => p.Temp == temp);
                done.Add(move);
            }
            return new RenameJournal(done);
        }
        catch
        {
            foreach (var move in Enumerable.Reverse(done))
                TryMove(move.To, move.From);
            foreach (var (move, temp) in parked)
                TryMove(temp, move.From);
            throw;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (Exists(from) && !Exists(to))
                MovePath(from, to);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- naming

    private static List<string> Sort(IReadOnlyList<string> paths, RenameOrder order, Func<string, DateTime?>? takenAt) => order switch
    {
        RenameOrder.Modified => [.. paths.OrderBy(Modified).ThenBy(p => p, NaturalComparer.Instance)],
        RenameOrder.Taken => [.. paths.OrderBy(p => takenAt?.Invoke(p) ?? Modified(p)).ThenBy(p => p, NaturalComparer.Instance)],
        RenameOrder.Selection => [.. paths],
        _ => [.. paths.OrderBy(p => Path.GetFileName(p.TrimEnd('\\', '/')), NaturalComparer.Instance)],
    };

    private static DateTime Modified(string path) =>
        Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);

    /// <summary>"photo.tar.gz" → ("photo", ".tar.gz"); folders have no extension.</summary>
    private static (string BaseName, string Ext) Split(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Directory.Exists(path))
            return (name, "");
        var baseName = Formats.BaseNameOf(path);
        return (baseName, name[baseName.Length..]);
    }

    private static string Expand(string template, string source, string baseName, string ext, int counter, Func<string, DateTime?>? takenAt) =>
        Token().Replace(template, m =>
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var format = m.Groups["format"].Success ? m.Groups["format"].Value : null;
            return key switch
            {
                "name" => baseName,
                "n" => counter.ToString(format ?? "0", CultureInfo.InvariantCulture),
                "ext" => ext.TrimStart('.'),
                "date" => Modified(source).ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                "time" => Modified(source).ToString(format ?? "HH-mm-ss", CultureInfo.InvariantCulture),
                "taken" => (takenAt?.Invoke(source) ?? Modified(source)).ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                "parent" => Path.GetFileName(Path.GetDirectoryName(source.TrimEnd('\\', '/'))) ?? "",
                _ => m.Value,
            };
        });

    private static string FindReplace(string name, RenameOptions options, Regex? regex)
    {
        if (options.Find.Length == 0)
            return name;
        if (options.UseRegex)
            return regex?.Replace(name, options.Replace) ?? name;
        return name.Replace(options.Find, options.Replace, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyCase(string name, RenameCase mode) => mode switch
    {
        RenameCase.Lower => name.ToLower(CultureInfo.CurrentCulture),
        RenameCase.Upper => name.ToUpper(CultureInfo.CurrentCulture),
        RenameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture)),
        _ => name,
    };

    private static readonly Dictionary<char, string> Latin = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };

    /// <summary>Russian letters to Latin; capitals stay capitals ("Щука" → "Shchuka").</summary>
    public static string ToLatin(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (!Latin.TryGetValue(lower, out var latin))
            {
                result.Append(c);
                continue;
            }
            if (c != lower && latin.Length > 0)
                latin = char.ToUpperInvariant(latin[0]) + latin[1..];
            result.Append(latin);
        }
        return result.ToString();
    }

    /// <summary>Characters Windows doesn't allow become "_"; trailing dots and spaces go.</summary>
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }

    private static string? Validate(string name, string target)
    {
        if (name.Length == 0)
            return T("The new name is empty.", "Новое имя пустое.");
        if (ReservedNames.Contains(name.Split('.')[0]))
            return T("Windows reserves this name.", "Это имя зарезервировано Windows.");
        if (target.Length >= 260)
            return T("The path is too long.", "Слишком длинный путь.");
        return null;
    }

    private static IReadOnlyList<RenameEntry> MarkConflicts(List<RenameEntry> entries)
    {
        var sources = new HashSet<string>(entries.Select(e => e.Source.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
        var counts = entries.GroupBy(e => e.Target, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return entries.Select(e =>
        {
            if (e.Error is not null)
                return e;
            if (counts[e.Target] > 1)
                return e with { Error = T("Another file gets the same name.", "Другой файл получит такое же имя.") };
            // Taken by a file that isn't part of this rename (and so won't move away).
            if (!string.Equals(e.Source, e.Target, StringComparison.OrdinalIgnoreCase) && Exists(e.Target) && !sources.Contains(e.Target))
                return e with { Error = T("A file with this name already exists.", "Файл с таким именем уже существует.") };
            return e;
        }).ToList();
    }

    [GeneratedRegex(@"\{(?<key>[a-zA-Z]+)(?::(?<format>[^}]+))?\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
