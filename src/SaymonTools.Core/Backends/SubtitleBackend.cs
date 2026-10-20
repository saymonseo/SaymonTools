using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>SRT ↔ WebVTT, and subtitles → plain text.</summary>
public static partial class SubtitleBackend
{
    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    [GeneratedRegex(@"^\s*((?:\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿'), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').ToList();
            var timingIndex = lines.FindIndex(l => TimingRegex().IsMatch(l));
            if (timingIndex < 0)
                continue; // WEBVTT header, NOTE, STYLE, …
            var m = TimingRegex().Match(lines[timingIndex]);
            var body = string.Join("\n", lines.Skip(timingIndex + 1)).Trim();
            cues.Add(new Cue(ParseTimestamp(m.Groups[1].Value), ParseTimestamp(m.Groups[2].Value), body));
        }
        return cues;
    }

    public static TimeSpan ParseTimestamp(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        return new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    private static string Stamp(TimeSpan t, char separator) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}");

    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var c in cues)
        {
            sb.Append(n++).Append('\n')
              .Append(Stamp(c.Start, ',')).Append(" --> ").Append(Stamp(c.End, ',')).Append('\n')
              .Append(c.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append('\n')
              .Append(c.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToText(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var c in cues)
        {
            var line = TagRegex().Replace(c.Text, "").Replace('\n', ' ').Trim();
            if (line.Length == 0 || line == previous)
                continue;
            sb.Append(line).Append('\n');
            previous = line;
        }
        return sb.ToString();
    }

    public static Task<string> ConvertAsync(FileItem item, string format, OperationContext ctx) =>
        OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, async tmp =>
        {
            var cues = Parse(await File.ReadAllTextAsync(item.Path, ctx.CancellationToken).ConfigureAwait(false));
            if (cues.Count == 0)
                throw new ConversionException(T("The file contains no subtitles.", "В файле нет субтитров."));
            var text = format switch
            {
                "srt" => ToSrt(cues),
                "vtt" => ToVtt(cues),
                _ => ToText(cues),
            };
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ctx.CancellationToken).ConfigureAwait(false);
        });
}
