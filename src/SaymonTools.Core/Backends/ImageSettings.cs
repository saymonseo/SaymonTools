using System.Globalization;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

public enum ImageResize
{
    Original,

    /// <summary>The longer side becomes at most <see cref="ImageSettings.LongSide"/> pixels.</summary>
    LongSide,

    Percent,

    /// <summary>Exactly <see cref="ImageSettings.Width"/> × <see cref="ImageSettings.Height"/>.</summary>
    Exact,
}

/// <summary>
/// Everything the "Custom…" image conversion asks for: format, size, quality or target file size,
/// grayscale and metadata. Also stored in presets.
/// </summary>
public sealed record ImageSettings
{
    public const string OperationId = "image.custom";

    /// <summary>Format meaning "keep the source format".</summary>
    public const string SameFormat = "same";

    public static readonly string[] OutputFormats = [SameFormat, "jpg", "png", "webp", "avif"];

    public static readonly int[] LongSides = [3840, 2560, 1920, 1280, 800];

    public static readonly int[] Percents = [75, 50, 25];

    public string Format { get; init; } = SameFormat;

    public ImageResize Resize { get; init; } = ImageResize.Original;

    public int LongSide { get; init; } = 1920;

    public int Percent { get; init; } = 50;

    public int Width { get; init; } = 1080;

    public int Height { get; init; } = 1080;

    /// <summary>For an exact size: fill it and cut off what sticks out (true) or fit inside it (false).</summary>
    public bool Crop { get; init; } = true;

    /// <summary>1–100 for JPG, WEBP and AVIF.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>When set (lossy formats), quality — and if needed size — go down until the file fits (KB).</summary>
    public int? TargetKb { get; init; }

    public bool StripMetadata { get; init; }

    public bool Grayscale { get; init; }

    public static bool IsLossy(string format) => format is "jpg" or "webp" or "avif";

    /// <summary>The format a file of <paramref name="inputFormat"/> is saved in.</summary>
    public string OutputFor(string inputFormat) => Format == SameFormat ? ImageBackend.OutputFormatFor(inputFormat) : Format;

    /// <summary>"WEBP · 1920 px · quality 80 · no metadata".</summary>
    public string Summary()
    {
        var parts = new List<string> { Format == SameFormat ? T("same format", "тот же формат") : Formats.Display(Format) };
        if (SizePart() is { } size)
            parts.Add(size);
        if (TargetKb is { } kb)
            parts.Add(T($"up to {kb} KB", $"до {kb} КБ"));
        else if (Format == SameFormat || IsLossy(Format))
            parts.Add(T($"quality {Quality}", $"качество {Quality}"));
        if (Grayscale)
            parts.Add(T("grayscale", "ч/б"));
        if (StripMetadata)
            parts.Add(T("no metadata", "без метаданных"));
        return string.Join(" · ", parts);
    }

    /// <summary>For the output name: "photo (1920px, up to 500 KB).jpg"; null when only the format changes.</summary>
    public string? Suffix()
    {
        var parts = new List<string>();
        if (SizePart() is { } size)
            parts.Add(size.Replace(" ", ""));
        if (TargetKb is { } kb)
            parts.Add(T($"up to {kb} KB", $"до {kb} КБ"));
        if (Grayscale)
            parts.Add(T("grayscale", "ч-б"));
        if (parts.Count > 0)
            return string.Join(", ", parts);
        return Format == SameFormat ? T("edited", "изменено") : null;
    }

    private string? SizePart() => Resize switch
    {
        ImageResize.LongSide => $"{LongSide} px",
        ImageResize.Percent => $"{Percent} %",
        ImageResize.Exact => $"{Width}×{Height}",
        _ => null,
    };

    // ---------------------------------------------------------------- parameters (for "repeat last")

    public IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["format"] = Format,
        ["resize"] = Resize.ToString(),
        ["long"] = LongSide.ToString(CultureInfo.InvariantCulture),
        ["percent"] = Percent.ToString(CultureInfo.InvariantCulture),
        ["width"] = Width.ToString(CultureInfo.InvariantCulture),
        ["height"] = Height.ToString(CultureInfo.InvariantCulture),
        ["crop"] = Crop ? "1" : "0",
        ["quality"] = Quality.ToString(CultureInfo.InvariantCulture),
        ["target"] = TargetKb?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["strip"] = StripMetadata ? "1" : "0",
        ["gray"] = Grayscale ? "1" : "0",
    };

    public static ImageSettings FromParameters(IReadOnlyDictionary<string, string> p)
    {
        string Get(string key) => p.TryGetValue(key, out var v) ? v : "";
        int? Int(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        var defaults = new ImageSettings();
        var format = Get("format");
        return new ImageSettings
        {
            Format = OutputFormats.Contains(format) ? format : SameFormat,
            Resize = Enum.TryParse<ImageResize>(Get("resize"), out var r) ? r : ImageResize.Original,
            LongSide = Math.Clamp(Int("long") ?? defaults.LongSide, 16, 20000),
            Percent = Math.Clamp(Int("percent") ?? defaults.Percent, 1, 400),
            Width = Math.Clamp(Int("width") ?? defaults.Width, 1, 20000),
            Height = Math.Clamp(Int("height") ?? defaults.Height, 1, 20000),
            Crop = Get("crop") != "0",
            Quality = Math.Clamp(Int("quality") ?? defaults.Quality, 1, 100),
            TargetKb = Int("target"),
            StripMetadata = Get("strip") == "1",
            Grayscale = Get("gray") == "1",
        };
    }
}
