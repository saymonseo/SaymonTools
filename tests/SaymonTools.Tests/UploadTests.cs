using System.Net;
using SaymonTools.Core;
using SaymonTools.Core.Upload;

namespace SaymonTools.Tests;

public sealed class UploadTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    /// <summary>Answers each request from a table keyed by URL and remembers what was sent; nothing leaves the machine.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class Last : IProgress<double>
    {
        public double Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    private string File(string name, string text = "hello")
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Litterbox_sends_the_form_it_expects_and_returns_the_link()
    {
        var server = new FakeServer((_, _) => Ok("https://litter.catbox.moe/abc123.txt\n"));
        var progress = new Last();
        var result = await new Uploader(new HttpClient(server)).UploadAsync(File("note.txt"), "litterbox", UploadExpiry.Day, progress, default);

        Assert.Equal("https://litter.catbox.moe/abc123.txt", result.Link);
        Assert.Equal("litterbox", result.Host.Id);
        Assert.Equal("1 day", result.Retention);
        var (url, body) = Assert.Single(server.Requests);
        Assert.Equal("https://litterbox.catbox.moe/resources/internals/api.php", url);
        Assert.Contains("name=\"reqtype\"", body);
        Assert.Contains("fileupload", body);
        Assert.Contains("name=\"time\"", body);
        Assert.Contains("24h", body);
        // Quoted like a browser sends it.
        Assert.Contains("name=\"fileToUpload\"; filename=\"note.txt\"", body);
        Assert.DoesNotContain("filename*", body);
        Assert.Contains("hello", body);
        Assert.Equal(1, progress.Value, 3);
    }

    [Fact]
    public async Task Gofile_link_comes_from_the_json_answer()
    {
        var server = new FakeServer((_, _) => Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/Xy12","code":"Xy12"}}"""));
        var result = await new Uploader(new HttpClient(server)).UploadAsync(File("a.bin"), "gofile", UploadExpiry.ThreeDays, new Last(), default);
        Assert.Equal("https://gofile.io/d/Xy12", result.Link);
        Assert.Equal("https://upload.gofile.io/uploadfile", server.Requests[0].Url);
        Assert.Contains("name=\"file\"; filename=\"a.bin\"", server.Requests[0].Body);
    }

    [Fact]
    public async Task A_failing_host_hands_over_to_the_next_one()
    {
        var server = new FakeServer((request, _) => request.RequestUri!.Host switch
        {
            "litterbox.catbox.moe" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") },
            "temp.sh" => Ok("https://temp.sh/AbCdE/a.txt"),
            _ => throw new InvalidOperationException("should not get this far"),
        });
        var result = await new Uploader(new HttpClient(server)).UploadAsync(File("a.txt"), "litterbox", UploadExpiry.ThreeDays, new Last(), default);
        Assert.Equal("tempsh", result.Host.Id);
        Assert.Equal("https://temp.sh/AbCdE/a.txt", result.Link);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_link_counts_as_a_failure()
    {
        var server = new FakeServer((_, _) => Ok("File type not allowed"));
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            new Uploader(new HttpClient(server)).UploadAsync(File("a.txt"), "litterbox", UploadExpiry.ThreeDays, new Last(), default));
        Assert.Contains("Litterbox: Unexpected answer: File type not allowed", ex.Message);
        Assert.Contains("temp.sh:", ex.Message);
        Assert.Contains("Gofile:", ex.Message);
    }

    [Fact]
    public async Task Hosts_too_small_for_the_file_are_skipped()
    {
        var tiny = new TinyHost();
        var server = new FakeServer((_, _) => Ok("https://temp.sh/x/big.bin"));
        var uploader = new Uploader(new HttpClient(server), [tiny, new TempShHost()]);
        var result = await uploader.UploadAsync(File("big.bin", "more than ten bytes"), "tiny", UploadExpiry.ThreeDays, new Last(), default);
        Assert.Equal("tempsh", result.Host.Id);
        Assert.False(tiny.Called);
    }

    [Fact]
    public async Task Cancelling_stops_without_trying_other_hosts()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeServer((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Uploader(new HttpClient(server)).UploadAsync(File("a.txt"), "litterbox", UploadExpiry.ThreeDays, new Last(), cts.Token));
        Assert.Single(server.Requests);
    }

    [Fact]
    public void Preferred_host_goes_first() =>
        Assert.Equal(["gofile", "tempsh", "litterbox"], new Uploader(new HttpClient()).Order("gofile").Select(h => h.Id));

    private sealed class TinyHost : FileHost
    {
        public bool Called { get; private set; }

        public override string Id => "tiny";

        public override string Name => "Tiny";

        public override long MaxBytes => 10;

        public override string Retention(UploadExpiry expiry) => "-";

        public override Task<string> UploadAsync(HttpClient http, string path, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult("https://tiny/x");
        }
    }
}
