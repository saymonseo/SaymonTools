using SaymonTools.Core.Backends;

namespace SaymonTools.Core.Operations;

/// <summary>
/// Wheel slices that can show the "Custom…" window before they run, and what that window starts with:
/// choosing MP4 opens it set to MP4, "Mute" with the sound off, "Shrink" at 50 %, and so on.
/// </summary>
public static class QuickSettings
{
    /// <summary>Starting video settings for <paramref name="operationId"/>; null when it has no settings window.</summary>
    public static VideoSettings? Video(string operationId) => operationId switch
    {
        "video.to.mp4" or "video.to.mkv" or "video.to.mov" or "video.to.webm" => new VideoSettings { Format = operationId["video.to.".Length..] },
        "video.compress" => new VideoSettings { Quality = VideoLevel.Small, Speed = EncodeSpeed.Thorough },
        "video.720p" => new VideoSettings { Height = 720 },
        "video.mute" => new VideoSettings { Sound = SoundMode.Remove },
        _ => null,
    };

    /// <summary>Starting image settings for <paramref name="operationId"/>; null when it has no settings window.</summary>
    public static ImageSettings? Image(string operationId) => operationId switch
    {
        "image.to.jpg" or "image.to.png" or "image.to.webp" or "image.to.avif" => new ImageSettings { Format = operationId["image.to.".Length..] },
        "image.compress" => new ImageSettings { Quality = 70, StripMetadata = true },
        "image.half" => new ImageSettings { Resize = ImageResize.Percent, Percent = 50 },
        "image.grayscale" => new ImageSettings { Grayscale = true },
        "image.strip" => new ImageSettings { StripMetadata = true },
        _ => null,
    };

    public static bool Has(string operationId) => Video(operationId) is not null || Image(operationId) is not null;
}
