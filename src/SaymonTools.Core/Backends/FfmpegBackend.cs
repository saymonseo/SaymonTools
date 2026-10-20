using System.Globalization;
using System.Text.RegularExpressions;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>Audio/video work through the ffmpeg command line.</summary>
public static partial class FfmpegBackend
{
    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\btime=\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    /// <summary>Encoder arguments for a target container.</summary>
    public static IReadOnlyList<string> CodecArgs(string format, Settings s, bool hasVideo = true)
    {
        var crf = s.VideoCrf.ToString(CultureInfo.InvariantCulture);
        string[] h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"];
        return format switch
        {
            "mp4" => [.. h264, "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"],
            "mov" => [.. h264, "-c:a", "aac", "-b:a", "160k"],
            "mkv" => [.. h264, "-c:a", "aac", "-b:a", "160k"],
            "webm" => ["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k"],
            "gif" => ["-vf", "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0"],
            "mp3" => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
            "m4a" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
            "aac" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
            "wav" => ["-vn", "-c:a", "pcm_s16le"],
            "aiff" => ["-vn", "-c:a", "pcm_s16be"],
            "flac" => ["-vn", "-c:a", "flac"],
            "ogg" => ["-vn", "-c:a", "libvorbis", "-q:a", "5"],
            "opus" => ["-vn", "-c:a", "libopus", "-b:a", "128k"],
            "wma" => ["-vn", "-c:a", "wmav2", "-b:a", "192k"],
            "avi" => ["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "3"],
            "wmv" => ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2"],
            _ => [.. h264, "-c:a", "aac", "-b:a", "160k"],
        };
    }

    /// <summary>Runs ffmpeg with <c>-i input [args] output</c> and maps its time= output to progress.</summary>
    public static Task RunAsync(
        OperationContext ctx,
        IReadOnlyList<string> inputs,
        IEnumerable<string> args,
        string output,
        IProgress<double> progress,
        IEnumerable<string>? inputArgs = null,
        double? knownDuration = null)
    {
        var ffmpeg = ctx.Tools.Require(ExternalTool.Ffmpeg);
        var all = new List<string> { "-hide_banner", "-nostdin", "-y" };
        if (inputArgs is not null)
            all.AddRange(inputArgs);
        foreach (var i in inputs)
        {
            all.Add("-i");
            all.Add(i);
        }
        all.AddRange(args);
        all.Add(output);

        double total = knownDuration ?? 0;
        var noAudio = false;
        return RunWithHintsAsync();

        async Task RunWithHintsAsync()
        {
            try
            {
                await ProcessRunner.RunAsync(ffmpeg, all, ctx.CancellationToken, OnLine).ConfigureAwait(false);
            }
            catch (ConversionException) when (noAudio)
            {
                throw new ConversionException(T("The file has no audio track.", "В файле нет звуковой дорожки."));
            }
        }

        void OnLine(string line)
        {
            if (line.Contains("does not contain any stream", StringComparison.Ordinal)
                || line.Contains("matches no streams", StringComparison.Ordinal))
                noAudio = true;
            if (knownDuration is null && DurationRegex().Match(line) is { Success: true } d)
                total += Seconds(d); // several inputs (concat) add up
            else if (total > 0 && TimeRegex().Match(line) is { Success: true } t)
                progress.Report(Math.Clamp(Seconds(t) / total, 0, 0.99));
        }
    }

    private static double Seconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
        + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    public static Task<string> ConvertAsync(FileItem item, string format, OperationContext ctx, IProgress<double> progress) =>
        OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, tmp =>
            RunAsync(ctx, [item.Path], CodecArgs(format, ctx.Settings), tmp, progress));

    /// <summary>Re-encodes in the source container with extra filter/encoder args; output gets a suffix.</summary>
    public static Task<string> TransformAsync(
        FileItem item,
        string suffix,
        OperationContext ctx,
        IProgress<double> progress,
        IEnumerable<string> args,
        string? outputFormat = null,
        IEnumerable<string>? inputArgs = null,
        double? duration = null)
    {
        var format = outputFormat ?? SafeContainer(item.Format);
        return OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({suffix})", format, tmp =>
            RunAsync(ctx, [item.Path], args, tmp, progress, inputArgs, duration));
    }

    /// <summary>Containers we can re-encode into; legacy ones become MP4.</summary>
    public static string SafeContainer(string format) => format switch
    {
        "avi" or "wmv" or "flv" or "mpg" or "mpeg" or "3gp" or "ts" or "mts" => "mp4",
        "wma" => "mp3",
        _ => format,
    };

    /// <summary>Parses "90", "1:30", "01:02:03.5" into seconds.</summary>
    public static double ParseTime(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0)
            throw new ConversionException(T("Missing time.", "Время не указано."));
        var parts = text.Split(':');
        double seconds = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                throw new ConversionException(T($"Invalid time: {text}", $"Некорректное время: {text}"));
            seconds = seconds * 60 + v;
        }
        return seconds;
    }

    public static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
