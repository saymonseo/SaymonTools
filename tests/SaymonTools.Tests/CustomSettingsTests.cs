using ImageMagick;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using SaymonTools.Core.Operations;

namespace SaymonTools.Tests;

public class VideoSettingsTests
{
    [Fact]
    public void Full_hd_without_sound_scales_the_short_side_and_drops_audio()
    {
        var args = new VideoSettings { Height = 1080, Sound = SoundMode.Remove }.Args();
        var filter = args[args.ToList().IndexOf("-vf") + 1];
        Assert.Contains("min(1080,ih)", filter);
        Assert.Contains("min(1080,iw)", filter);
        Assert.Contains("-an", args);
        Assert.DoesNotContain("0:a:0?", args);
        Assert.Contains("libx264", args);
    }

    [Fact]
    public void Target_size_sets_a_bitrate_from_the_length()
    {
        // 100 MB over 100 s: 8192 kbit/s minus a little room and 192 kbit/s of sound.
        var args = new VideoSettings { TargetMb = 100 }.Args(durationSeconds: 100).ToList();
        var bitrate = int.Parse(args[args.IndexOf("-b:v") + 1].TrimEnd('k'));
        Assert.InRange(bitrate, 7400, 7900);
        Assert.DoesNotContain("-crf", args);
        Assert.Throws<ConversionException>(() => new VideoSettings { TargetMb = 100 }.Args(durationSeconds: null));
    }

    [Fact]
    public void Codecs_and_containers()
    {
        var h265 = new VideoSettings { Codec = VideoCodec.H265 }.Args();
        Assert.Contains("libx265", h265);
        Assert.Contains("hvc1", h265);
        var webm = new VideoSettings { Format = "webm", Codec = VideoCodec.H265 }.Args();
        Assert.Contains("libvpx-vp9", webm);
        Assert.Contains("libopus", webm);
        Assert.DoesNotContain("-movflags", webm);
    }

    [Fact]
    public void Frame_rate_is_never_raised()
    {
        Assert.Contains("fps=30", string.Join(" ", new VideoSettings { Fps = 30 }.Args(sourceFps: 59.94)));
        Assert.DoesNotContain("fps=30", string.Join(" ", new VideoSettings { Fps = 30 }.Args(sourceFps: 25)));
    }

    [Fact]
    public void Parameters_round_trip()
    {
        var s = new VideoSettings { Format = "mkv", Height = 720, Quality = VideoLevel.Small, Codec = VideoCodec.H265, Fps = 24, Sound = SoundMode.Compact, Speed = EncodeSpeed.Thorough };
        Assert.Equal(s, VideoSettings.FromParameters(s.ToParameters()));
        Assert.Equal(new VideoSettings(), VideoSettings.FromParameters(new Dictionary<string, string>()));
    }

    [Fact]
    public void Summary_and_file_name()
    {
        var s = new VideoSettings { Height = 1080, Sound = SoundMode.Remove };
        Assert.Equal("MP4 · 1080p · high quality · no sound", s.Summary());
        Assert.Equal("1080p, no sound", s.Suffix());
    }

    [Fact]
    public void Reads_ffmpegs_description_of_a_file()
    {
        string[] lines =
        [
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':",
            "  Duration: 00:01:23.45, start: 0.000000, bitrate: 45000 kb/s",
            "  Stream #0:0[0x1](und): Video: hevc (Main 10) (hvc1 / 0x31637668), yuv420p10le(tv, bt2020nc), 3840x2160 [SAR 1:1 DAR 16:9], 44000 kb/s, 59.94 fps, 59.94 tbr, 600 tbn (default)",
            "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 192 kb/s (default)",
        ];
        var info = FfmpegBackend.ParseProbe(lines)!;
        Assert.Equal(83.45, info.Duration!.Value, 2);
        Assert.Equal((3840, 2160), (info.Width, info.Height));
        Assert.Equal(59.94, info.Fps);
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.Equal(2160, info.ShortSide);
    }

    [Fact]
    public void Simple_video_presets_map_to_full_settings()
    {
        var s = PresetOperations.ToVideoSettings(new Preset { Kind = PresetKind.Video, Format = "webm", Height = 720, VideoQuality = VideoQuality.Small, Mute = true });
        Assert.Equal(new VideoSettings { Format = "webm", Height = 720, Quality = VideoLevel.Small, Sound = SoundMode.Remove }, s);
    }

    [Fact]
    public void A_preset_with_full_settings_goes_on_the_convert_wheel()
    {
        var op = PresetOperations.Build(new Preset { Name = "FHD silent", Kind = PresetKind.Video, Video = new VideoSettings { Height = 1080, Sound = SoundMode.Remove } });
        Assert.Equal(WheelKind.Convert, op.Wheel);
        Assert.Equal("MP4 · 1080p · high quality · no sound", op.Subtitle);
    }

    [Fact]
    public void Custom_slice_is_offered_for_videos_and_images()
    {
        var ops = Catalog.Build();
        Assert.True(ops.Single(o => o.Id == VideoSettings.OperationId).AppliesTo([new FileItem("a.mov")]));
        Assert.True(ops.Single(o => o.Id == ImageSettings.OperationId).AppliesTo([new FileItem("a.heic")]));
    }
}

public sealed class ImageSettingsTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    /// <summary>A photo-like picture: noise doesn't compress, so file sizes behave like real photos.</summary>
    private string Photo(string name, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.AddNoise(NoiseType.Gaussian);
        var path = Path.Combine(_dir, name);
        image.Quality = 95;
        image.Write(path);
        return path;
    }

    private static (uint, uint) Size(string path)
    {
        using var image = new MagickImage(path);
        return (image.Width, image.Height);
    }

    [Fact]
    public async Task Longer_side_shrinks_but_never_enlarges()
    {
        var big = Photo("big.jpg", 1200, 800);
        var output = Path.Combine(_dir, "out.jpg");
        await ImageBackend.CustomAsync(big, output, new ImageSettings { Resize = ImageResize.LongSide, LongSide = 600 }, default);
        Assert.Equal((600u, 400u), Size(output));
        await ImageBackend.CustomAsync(big, output, new ImageSettings { Resize = ImageResize.LongSide, LongSide = 3000 }, default);
        Assert.Equal((1200u, 800u), Size(output));
    }

    [Fact]
    public async Task Exact_size_crops_or_fits()
    {
        var photo = Photo("wide.jpg", 1200, 800);
        var output = Path.Combine(_dir, "out.png");
        await ImageBackend.CustomAsync(photo, output, new ImageSettings { Format = "png", Resize = ImageResize.Exact, Width = 300, Height = 300 }, default);
        Assert.Equal((300u, 300u), Size(output));
        await ImageBackend.CustomAsync(photo, output, new ImageSettings { Format = "png", Resize = ImageResize.Exact, Width = 300, Height = 300, Crop = false }, default);
        Assert.Equal((300u, 200u), Size(output));
    }

    [Fact]
    public async Task Target_file_size_is_met()
    {
        var photo = Photo("photo.jpg", 1600, 1200);
        Assert.True(new FileInfo(photo).Length > 300 * 1024, "the sample should start out bigger than the target");
        var output = Path.Combine(_dir, "small.jpg");
        await ImageBackend.CustomAsync(photo, output, new ImageSettings { TargetKb = 150 }, default);
        Assert.InRange(new FileInfo(output).Length, 1, 150 * 1024);
    }

    [Fact]
    public void Parameters_round_trip_and_names()
    {
        var s = new ImageSettings { Format = "webp", Resize = ImageResize.Exact, Width = 1080, Height = 1350, Crop = false, Quality = 70, TargetKb = 400, StripMetadata = true, Grayscale = true };
        Assert.Equal(s, ImageSettings.FromParameters(s.ToParameters()));
        Assert.Equal("WEBP · 1080×1350 · up to 400 KB · grayscale · no metadata", s.Summary());
        Assert.Equal("1080×1350, up to 400 KB, grayscale", s.Suffix());
        Assert.Null(new ImageSettings { Format = "png" }.Suffix());
    }

    [Fact]
    public void Simple_image_presets_map_to_full_settings()
    {
        var s = PresetOperations.ToImageSettings(new Preset { Kind = PresetKind.Image, Format = "jpg", Quality = 80, MaxSide = 1920, StripMetadata = true });
        Assert.Equal(new ImageSettings { Format = "jpg", Resize = ImageResize.LongSide, LongSide = 1920, Quality = 80, StripMetadata = true }, s);
    }
}

/// <summary>Real ffmpeg runs; skipped (pass trivially) where ffmpeg isn't installed.</summary>
public sealed class CustomVideoIntegrationTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();
    private readonly ToolLocator _tools = new();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    private async Task<string> Sample(string name, string size, int seconds)
    {
        var path = Path.Combine(_dir, name);
        await ProcessRunner.RunAsync(_tools.Ffmpeg!, ["-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", path], default);
        return path;
    }

    private OperationContext Context() => new() { Tools = _tools, OutputDirectory = _dir };

    [Fact]
    public async Task Downscales_and_removes_sound_like_4k_to_full_hd()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        // Scaled-down stand-in for 3840×2160 → 1080p: 1280×720 → 360p.
        var source = await Sample("wide.mp4", "1280x720", 2);
        var output = await FfmpegBackend.CustomAsync(new FileItem(source), new VideoSettings { Height = 360, Sound = SoundMode.Remove, Speed = EncodeSpeed.Fast }, Context(), new Progress<double>());
        var info = await FfmpegBackend.ProbeAsync(_tools.Ffmpeg!, output, default);
        Assert.Equal((640, 360), (info!.Width, info.Height));
        Assert.False(info.HasAudio);
        Assert.Equal("wide (360p, no sound).mp4", Path.GetFileName(output));
    }

    [Fact]
    public async Task Portrait_video_keeps_its_orientation()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        var source = await Sample("tall.mp4", "360x640", 1);
        var output = await FfmpegBackend.CustomAsync(new FileItem(source), new VideoSettings { Height = 240, Codec = VideoCodec.H265, Speed = EncodeSpeed.Fast }, Context(), new Progress<double>());
        var info = await FfmpegBackend.ProbeAsync(_tools.Ffmpeg!, output, default);
        Assert.Equal(240, info!.Width);
        Assert.InRange(info.Height!.Value, 424, 428);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task Target_size_is_about_right()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        var source = await Sample("clip.mp4", "1280x720", 8);
        var output = await FfmpegBackend.CustomAsync(new FileItem(source), new VideoSettings { TargetMb = 2, Speed = EncodeSpeed.Fast }, Context(), new Progress<double>());
        Assert.InRange(new FileInfo(output).Length, 1, (long)(2 * 1024 * 1024 * 1.15));
    }
}
