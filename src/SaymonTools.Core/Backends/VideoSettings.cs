using System.Globalization;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

public enum VideoCodec
{
    /// <summary>Plays everywhere.</summary>
    H264,

    /// <summary>About a third smaller at the same quality; older devices may not play it.</summary>
    H265,
}

/// <summary>Visual quality; ignored when a target file size is set.</summary>
public enum VideoLevel
{
    Best,
    High,
    Medium,
    Small,
}

/// <summary>Encoder effort: faster encoding or a smaller file at the same quality.</summary>
public enum EncodeSpeed
{
    Fast,
    Balanced,
    Thorough,
}

public enum SoundMode
{
    Keep,
    Compact,
    Remove,
}

/// <summary>
/// Everything the "Custom…" video conversion asks for: container, resolution, quality or target size,
/// codec, frame rate, sound and encoder effort. Also stored in presets.
/// </summary>
public sealed record VideoSettings
{
    public const string OperationId = "video.custom";

    public static readonly string[] Containers = ["mp4", "mkv", "mov", "webm"];

    /// <summary>Resolutions offered, by the shorter side (1080 = Full HD, also for portrait video).</summary>
    public static readonly int[] Heights = [2160, 1440, 1080, 720, 480, 360];

    public static readonly int[] FrameRates = [60, 30, 24];

    public string Format { get; init; } = "mp4";

    /// <summary>Shorter side in pixels; null keeps the size. Never enlarges.</summary>
    public int? Height { get; init; }

    public VideoLevel Quality { get; init; } = VideoLevel.High;

    /// <summary>When set, the bitrate is chosen so the file comes out about this big (MB).</summary>
    public int? TargetMb { get; init; }

    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Frames per second; null keeps the source rate. Never raises it.</summary>
    public int? Fps { get; init; }

    public SoundMode Sound { get; init; } = SoundMode.Keep;

    public EncodeSpeed Speed { get; init; } = EncodeSpeed.Balanced;

    public bool IsWebm => Format == "webm";

    /// <summary>Audio bitrate in kbit/s (0 without sound).</summary>
    public int AudioKbps => Sound switch
    {
        SoundMode.Remove => 0,
        SoundMode.Compact => IsWebm ? 64 : 96,
        _ => IsWebm ? 128 : 192,
    };

    public static string ResolutionName(int height) => height switch
    {
        2160 => "4K",
        1440 => "2K",
        1080 => "Full HD",
        720 => "HD",
        _ => $"{height}p",
    };

    /// <summary>"MP4 · 1080p · high · no sound".</summary>
    public string Summary()
    {
        var parts = new List<string> { Formats.Display(Format) };
        parts.Add(Height is { } h ? $"{h}p" : T("original size", "исходный размер"));
        parts.Add(TargetMb is { } mb ? T($"≈{mb} MB", $"≈{mb} МБ") : QualityName(Quality));
        if (!IsWebm && Codec == VideoCodec.H265)
            parts.Add("H.265");
        if (Fps is { } fps)
            parts.Add(T($"{fps} fps", $"{fps} к/с"));
        if (Sound == SoundMode.Remove)
            parts.Add(T("no sound", "без звука"));
        else if (Sound == SoundMode.Compact)
            parts.Add(T("compact sound", "сжатый звук"));
        return string.Join(" · ", parts);
    }

    /// <summary>The part of the output file name that says what changed: "video (1080p, no sound).mp4".</summary>
    public string Suffix()
    {
        var parts = new List<string>();
        if (Height is { } h)
            parts.Add($"{h}p");
        if (TargetMb is { } mb)
            parts.Add(T($"{mb} MB", $"{mb} МБ"));
        if (!IsWebm && Codec == VideoCodec.H265)
            parts.Add("H.265");
        if (Fps is { } fps)
            parts.Add($"{fps} fps");
        if (Sound == SoundMode.Remove)
            parts.Add(T("no sound", "без звука"));
        return parts.Count == 0 ? T("converted", "конвертировано") : string.Join(", ", parts);
    }

    public static string QualityName(VideoLevel level) => level switch
    {
        VideoLevel.Best => T("best quality", "максимальное качество"),
        VideoLevel.Medium => T("medium quality", "среднее качество"),
        VideoLevel.Small => T("compact", "компактно"),
        _ => T("high quality", "высокое качество"),
    };

    /// <summary>
    /// ffmpeg output arguments. <paramref name="durationSeconds"/> is needed only for a target size;
    /// <paramref name="sourceFps"/> keeps a lower source frame rate from being raised.
    /// </summary>
    public IReadOnlyList<string> Args(double? durationSeconds = null, double? sourceFps = null)
    {
        var args = new List<string> { "-map", "0:v:0" };
        if (Sound != SoundMode.Remove)
            args.AddRange(["-map", "0:a:0?"]);

        var filters = new List<string>();
        if (Fps is { } fps && (sourceFps is null || sourceFps > fps + 0.5))
            filters.Add($"fps={fps}");
        if (Height is { } h)
        {
            // The shorter side becomes h (landscape: height, portrait: width), never larger than the source.
            filters.Add($"scale=w='if(gt(iw,ih),-2,min({h},iw))':h='if(gt(iw,ih),min({h},ih),-2)'");
        }
        filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        args.AddRange(["-vf", string.Join(",", filters)]);

        int? videoKbps = null;
        if (TargetMb is { } mb)
        {
            if (durationSeconds is not > 0)
                throw new ConversionException(T("The video's length is unknown, so a file size can't be targeted.", "Длительность видео неизвестна, поэтому подобрать размер файла не получится."));
            // Megabytes to kilobits per second, a few percent left for the container.
            var totalKbps = mb * 8.0 * 1024 / durationSeconds.Value * 0.96;
            videoKbps = (int)Math.Max(80, totalKbps - AudioKbps);
        }

        if (IsWebm)
        {
            var cpuUsed = Speed switch { EncodeSpeed.Fast => "6", EncodeSpeed.Thorough => "2", _ => "4" };
            args.AddRange(["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-row-mt", "1", "-deadline", "good", "-cpu-used", cpuUsed]);
            if (videoKbps is { } kbps)
                args.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"]);
            else
                args.AddRange(["-crf", Crf().ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        }
        else
        {
            var preset = Speed switch { EncodeSpeed.Fast => "veryfast", EncodeSpeed.Thorough => "slow", _ => "medium" };
            args.AddRange(Codec == VideoCodec.H265
                ? ["-c:v", "libx265", "-preset", preset, "-tag:v", "hvc1", "-x265-params", "log-level=error"]
                : ["-c:v", "libx264", "-preset", preset]);
            args.AddRange(["-pix_fmt", "yuv420p"]);
            if (videoKbps is { } kbps)
                args.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"]);
            else
                args.AddRange(["-crf", Crf().ToString(CultureInfo.InvariantCulture)]);
        }

        if (Sound == SoundMode.Remove)
            args.Add("-an");
        else
            args.AddRange(["-c:a", IsWebm ? "libopus" : "aac", "-b:a", $"{AudioKbps}k"]);
        if (Format is "mp4" or "mov")
            args.AddRange(["-movflags", "+faststart"]);
        return args;
    }

    /// <summary>Constant-quality value per codec, chosen so each level looks alike across codecs.</summary>
    private int Crf() => (IsWebm, Codec, Quality) switch
    {
        (true, _, VideoLevel.Best) => 24,
        (true, _, VideoLevel.High) => 30,
        (true, _, VideoLevel.Medium) => 35,
        (true, _, _) => 40,
        (_, VideoCodec.H265, VideoLevel.Best) => 20,
        (_, VideoCodec.H265, VideoLevel.High) => 24,
        (_, VideoCodec.H265, VideoLevel.Medium) => 28,
        (_, VideoCodec.H265, _) => 32,
        (_, _, VideoLevel.Best) => 17,
        (_, _, VideoLevel.High) => 20,
        (_, _, VideoLevel.Medium) => 23,
        _ => 28,
    };

    // ---------------------------------------------------------------- parameters (for "repeat last")

    public IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["format"] = Format,
        ["height"] = Height?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["quality"] = Quality.ToString(),
        ["target"] = TargetMb?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["codec"] = Codec.ToString(),
        ["fps"] = Fps?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["sound"] = Sound.ToString(),
        ["speed"] = Speed.ToString(),
    };

    public static VideoSettings FromParameters(IReadOnlyDictionary<string, string> p)
    {
        string Get(string key) => p.TryGetValue(key, out var v) ? v : "";
        int? Int(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        TEnum Enum<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum =>
            System.Enum.TryParse<TEnum>(Get(key), out var v) ? v : fallback;
        var format = Get("format");
        return new VideoSettings
        {
            Format = Containers.Contains(format) ? format : "mp4",
            Height = Int("height"),
            Quality = Enum("quality", VideoLevel.High),
            TargetMb = Int("target"),
            Codec = Enum("codec", VideoCodec.H264),
            Fps = Int("fps"),
            Sound = Enum("sound", SoundMode.Keep),
            Speed = Enum("speed", EncodeSpeed.Balanced),
        };
    }
}

/// <summary>What ffmpeg reports about a media file.</summary>
public sealed record MediaInfo(double? Duration, int? Width, int? Height, double? Fps, bool HasVideo, bool HasAudio)
{
    /// <summary>"3840×2160 · 59.94 fps · 01:23 · with sound".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Width is { } w && Height is { } h)
            parts.Add($"{w}×{h}");
        if (Fps is { } fps)
            parts.Add(T($"{fps:0.##} fps", $"{fps:0.##} к/с"));
        if (Duration is { } d)
            parts.Add(TimeSpan.FromSeconds(d).ToString(d >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture));
        if (HasVideo)
            parts.Add(HasAudio ? T("with sound", "со звуком") : T("no sound", "без звука"));
        return string.Join(" · ", parts);
    }

    /// <summary>The shorter side, which is what "1080p" means.</summary>
    public int? ShortSide => Width is { } w && Height is { } h ? Math.Min(w, h) : null;
}
