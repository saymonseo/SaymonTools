using SaymonTools.Core;
using SaymonTools.Core.Backends;
using SaymonTools.Core.Operations;

namespace SaymonTools.Tests;

/// <summary>Real ffmpeg runs; skipped (pass trivially) where ffmpeg isn't installed.</summary>
public sealed class TrimTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();
    private readonly ToolLocator _tools = new();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    private async Task<string> Sample(string name)
    {
        var path = Path.Combine(_dir, name);
        // A keyframe every second, so the fast (copy) cut lands close to the requested start.
        await ProcessRunner.RunAsync(_tools.Ffmpeg!, ["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=6",
            "-f", "lavfi", "-i", "sine=duration=6", "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-c:a", "aac", path], default);
        return path;
    }

    private async Task<string> Trim(string source, string start, string end, bool copy)
    {
        var trim = Catalog.Build().Single(o => o.Id == "video.trim");
        var ctx = new OperationContext
        {
            Tools = _tools,
            OutputDirectory = Path.Combine(_dir, copy ? "copy" : "encode"),
            Parameters = new Dictionary<string, string> { ["start"] = start, ["end"] = end, ["copy"] = copy ? "1" : "" },
        };
        return (await trim.ExecuteAsync([new FileItem(source)], ctx)).Outputs.Single();
    }

    [Fact]
    public async Task Re_encoding_cuts_exactly()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        var output = await Trim(await Sample("clip.mp4"), "1.5", "4", copy: false);
        var info = await FfmpegBackend.ProbeAsync(_tools.Ffmpeg!, output, default);
        Assert.InRange(info!.Duration!.Value, 2.4, 2.6);
        Assert.EndsWith(".mp4", output);
    }

    [Fact]
    public async Task Fast_cut_copies_the_streams_and_keeps_the_container()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        var source = await Sample("clip.mov");
        var output = await Trim(source, "2", "", copy: true);
        var info = await FfmpegBackend.ProbeAsync(_tools.Ffmpeg!, output, default);
        // From the keyframe at 2 s to the end; copying keeps the size and the container.
        Assert.InRange(info!.Duration!.Value, 3.8, 4.2);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.EndsWith(".mov", output);
    }

    [Fact]
    public async Task Reads_a_single_frame_for_previews()
    {
        if (!_tools.Has(ExternalTool.Ffmpeg))
            return;
        var frame = Path.Combine(_dir, "frame.jpg");
        await FfmpegBackend.FrameAsync(_tools.Ffmpeg!, await Sample("clip.mp4"), 3, 72, frame, default);
        using var image = new ImageMagick.MagickImage(frame);
        Assert.Equal(72u, image.Height);
        Assert.Equal(96u, image.Width);
    }
}
