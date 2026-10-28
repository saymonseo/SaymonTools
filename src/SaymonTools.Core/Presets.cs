using System.Globalization;
using SaymonTools.Core.Backends;
using SaymonTools.Core.Operations;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core;

public enum PresetKind
{
    Image,
    Video,
    Audio,
}

/// <summary>How much a video preset may shrink the file.</summary>
public enum VideoQuality
{
    High,
    Medium,
    Small,
}

/// <summary>
/// A slice the user put together (e.g. "JPG 70 %", "MP4 720p", "Half size"). Stored in the settings
/// and turned into an operation by <see cref="PresetOperations"/>.
/// </summary>
public sealed class Preset
{
    /// <summary>Format meaning "keep the source format" (images only).</summary>
    public const string SameFormat = "same";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public PresetKind Kind { get; set; }
    public string Format { get; set; } = "jpg";

    /// <summary>Images: 1–100 for lossy formats.</summary>
    public int Quality { get; set; } = 85;

    /// <summary>Images: longest side in pixels (null = keep).</summary>
    public int? MaxSide { get; set; }

    /// <summary>Images: scale in percent (null = keep); ignored when <see cref="MaxSide"/> is set.</summary>
    public int? Percent { get; set; }

    public bool Grayscale { get; set; }
    public bool StripMetadata { get; set; }

    /// <summary>Video: output height (null = keep).</summary>
    public int? Height { get; set; }

    public VideoQuality VideoQuality { get; set; } = VideoQuality.Medium;
    public bool Mute { get; set; }

    /// <summary>Audio: bitrate in kbit/s for lossy formats.</summary>
    public int Bitrate { get; set; } = 192;

    /// <summary>Video: every setting from the "Custom…" window; when set it replaces the simple video fields.</summary>
    public VideoSettings? Video { get; set; }

    /// <summary>Images: every setting from the "Custom…" window; when set it replaces the simple image fields.</summary>
    public ImageSettings? Image { get; set; }
}

public static class PresetOperations
{
    public static readonly string[] ImageFormats = [Preset.SameFormat, "jpg", "png", "webp", "avif"];
    public static readonly string[] VideoFormats = ["mp4", "webm"];
    public static readonly string[] AudioFormats = ["mp3", "m4a", "opus", "flac"];

    public static IReadOnlyList<string> FormatsFor(PresetKind kind) => kind switch
    {
        PresetKind.Image => ImageFormats,
        PresetKind.Video => VideoFormats,
        _ => AudioFormats,
    };

    public static bool IsLossless(string format) => format is "png" or "flac";

    /// <summary>One line describing what the preset does, e.g. "JPG · 70 % · max 1920 px".</summary>
    public static string Summary(Preset p)
    {
        if (p.Kind == PresetKind.Video && p.Video is { } video)
            return video.Summary();
        if (p.Kind == PresetKind.Image && p.Image is { } image)
            return image.Summary();
        var parts = new List<string> { p.Format == Preset.SameFormat ? T("same format", "тот же формат") : Formats.Display(p.Format) };
        switch (p.Kind)
        {
            case PresetKind.Image:
                if (!IsLossless(p.Format))
                    parts.Add(T($"quality {p.Quality}", $"качество {p.Quality}"));
                if (p.MaxSide is { } side)
                    parts.Add(T($"max {side} px", $"макс. {side} px"));
                else if (p.Percent is { } percent)
                    parts.Add($"{percent} %");
                if (p.Grayscale)
                    parts.Add(T("grayscale", "оттенки серого"));
                if (p.StripMetadata)
                    parts.Add(T("no metadata", "без метаданных"));
                break;
            case PresetKind.Video:
                parts.Add(p.Height is { } h ? $"{h}p" : T("original size", "исходный размер"));
                parts.Add(p.VideoQuality switch
                {
                    VideoQuality.High => T("high quality", "высокое качество"),
                    VideoQuality.Small => T("small file", "маленький файл"),
                    _ => T("medium quality", "среднее качество"),
                });
                if (p.Mute)
                    parts.Add(T("muted", "без звука"));
                break;
            default:
                if (!IsLossless(p.Format))
                    parts.Add($"{p.Bitrate} kbit/s");
                break;
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The full video settings a simple video preset stands for.</summary>
    public static VideoSettings ToVideoSettings(Preset p) => p.Video ?? new VideoSettings
    {
        Format = VideoSettings.Containers.Contains(p.Format) ? p.Format : "mp4",
        Height = p.Height,
        Quality = p.VideoQuality switch { VideoQuality.High => VideoLevel.High, VideoQuality.Small => VideoLevel.Small, _ => VideoLevel.Medium },
        Sound = p.Mute ? SoundMode.Remove : SoundMode.Keep,
    };

    /// <summary>The full image settings a simple image preset stands for.</summary>
    public static ImageSettings ToImageSettings(Preset p) => p.Image ?? new ImageSettings
    {
        Format = ImageSettings.OutputFormats.Contains(p.Format) ? p.Format : ImageSettings.SameFormat,
        Resize = p.MaxSide is not null ? ImageResize.LongSide : p.Percent is not null ? ImageResize.Percent : ImageResize.Original,
        LongSide = p.MaxSide ?? 1920,
        Percent = p.Percent ?? 50,
        Quality = p.Quality,
        Grayscale = p.Grayscale,
        StripMetadata = p.StripMetadata,
    };

    /// <summary>The wheel slice for a preset: format changes go on Convert, same-format edits on Tools.</summary>
    public static Operation Build(Preset p)
    {
        var name = string.IsNullOrWhiteSpace(p.Name) ? Summary(p) : p.Name.Trim();
        var wheel = p.Format == Preset.SameFormat ? WheelKind.Tools : WheelKind.Convert;
        var suffix = SafeSuffix(name);
        if (p.Kind == PresetKind.Image && p.Image is { } image)
        {
            return new PerFileOperation("preset." + p.Id, image.Format == ImageSettings.SameFormat ? WheelKind.Tools : WheelKind.Convert, name, Glyphs.Photo, Formats.Images,
                async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({suffix})", image.OutputFor(item.Format),
                    tmp => ImageBackend.CustomAsync(item.Path, tmp, image, ctx.CancellationToken))],
                subtitle: Summary(p))
            { MaxParallelism = PerFileOperation.CpuParallelism };
        }
        if (p.Kind == PresetKind.Video && p.Video is { } video)
        {
            return new PerFileOperation("preset." + p.Id, WheelKind.Convert, name, Glyphs.Video, Formats.Video,
                async (item, ctx, progress) => [await FfmpegBackend.CustomAsync(item, video, ctx, progress, suffix)],
                subtitle: Summary(p))
            { Requires = [ExternalTool.Ffmpeg] };
        }
        return p.Kind switch
        {
            PresetKind.Image => new PerFileOperation("preset." + p.Id, wheel, name, Glyphs.Photo, Formats.Images,
                async (item, ctx, _) =>
                {
                    var format = p.Format == Preset.SameFormat ? ImageBackend.OutputFormatFor(item.Format) : p.Format;
                    return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({suffix})", format,
                        tmp => ImageBackend.PresetAsync(item.Path, tmp, format, p.Quality, p.MaxSide, p.Percent, p.Grayscale, p.StripMetadata, ctx.CancellationToken))];
                }, subtitle: Summary(p))
            { MaxParallelism = PerFileOperation.CpuParallelism },
            PresetKind.Video => new PerFileOperation("preset." + p.Id, wheel, name, Glyphs.Video, Formats.Video,
                async (item, ctx, progress) => [await FfmpegBackend.TransformAsync(item, suffix, ctx, progress, VideoArgs(p), p.Format)],
                subtitle: Summary(p))
            { Requires = [ExternalTool.Ffmpeg] },
            _ => new PerFileOperation("preset." + p.Id, wheel, name, Glyphs.Audio, [.. Formats.Audio, .. Formats.Video],
                async (item, ctx, progress) => [await FfmpegBackend.TransformAsync(item, suffix, ctx, progress, AudioArgs(p), p.Format)],
                subtitle: Summary(p))
            { Requires = [ExternalTool.Ffmpeg] },
        };
    }

    private static string[] VideoArgs(Preset p)
    {
        var scale = p.Height is { } h ? $"scale=-2:'min({h},ih)'," : "";
        if (p.Format == "webm")
        {
            var crf = p.VideoQuality switch { VideoQuality.High => 28, VideoQuality.Small => 38, _ => 33 };
            return
            [
                "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0",
                "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-vf", scale + "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                .. p.Mute ? (string[])["-an"] : ["-c:a", "libopus", "-b:a", "128k"],
            ];
        }
        var x264 = p.VideoQuality switch { VideoQuality.High => 20, VideoQuality.Small => 30, _ => 24 };
        return
        [
            "-c:v", "libx264", "-preset", "medium", "-crf", x264.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            "-vf", scale + "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-movflags", "+faststart",
            .. p.Mute ? (string[])["-an"] : ["-c:a", "aac", "-b:a", "160k"],
        ];
    }

    private static string[] AudioArgs(Preset p)
    {
        var bitrate = $"{Math.Clamp(p.Bitrate, 32, 320)}k";
        return p.Format switch
        {
            "m4a" => ["-vn", "-c:a", "aac", "-b:a", bitrate],
            "opus" => ["-vn", "-c:a", "libopus", "-b:a", bitrate],
            "flac" => ["-vn", "-c:a", "flac"],
            _ => ["-vn", "-c:a", "libmp3lame", "-b:a", bitrate],
        };
    }

    /// <summary>The preset name as part of a file name ("photo (Web 1920).jpg").</summary>
    public static string SafeSuffix(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "preset" : clean.Length > 40 ? clean[..40].TrimEnd() : clean;
    }
}
