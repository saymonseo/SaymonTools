using System.Net;
using System.Security.Cryptography;
using System.Text;
using SaymonTools.Core;

namespace SaymonTools.Tests;

public sealed class ReleaseTests : IDisposable
{
    private static readonly byte[] Setup = Encoding.UTF8.GetBytes("pretend installer");
    private static readonly string SetupSha = Convert.ToHexString(SHA256.HashData(Setup)).ToLowerInvariant();
    private readonly string _dir = OutputPaths.TempDirectory();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    /// <summary>Answers from a table of URLs; nothing leaves the machine.</summary>
    private sealed class FakeServer(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static UpdateInfo Update(string? url = "https://github.com/saymonseo/SaymonTools/releases/download/v1.2.0/SaymonTools-Setup-1.2.0.exe", string? sha = null) =>
        new(new Version(1, 2, 0), "v1.2.0", "https://github.com/saymonseo/SaymonTools/releases/tag/v1.2.0", url, sha ?? SetupSha);

    private static HttpClient Serving(byte[] body, out FakeServer server)
    {
        server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        return new HttpClient(server);
    }

    [Fact]
    public async Task Latest_release_is_read_from_the_repository()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.3.0","html_url":"https://github.com/x/y/releases/tag/v1.3.0","assets":[]}"""),
        });
        var latest = await Releases.LatestAsync(new HttpClient(server), "saymonseo/SaymonTools", default);
        Assert.Equal(new Version(1, 3, 0), latest!.Version);
        Assert.Equal("https://api.github.com/repos/saymonseo/SaymonTools/releases/latest", server.Requests.Single().ToString());
    }

    [Fact]
    public async Task No_releases_yet_is_not_an_error()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Releases.LatestAsync(new HttpClient(server), "saymonseo/SaymonTools", default));
    }

    [Fact]
    public async Task A_matching_installer_is_kept()
    {
        var target = Path.Combine(_dir, "setup.exe");
        await Releases.DownloadInstallerAsync(Serving(Setup, out _), Update(), target, null, default);
        Assert.Equal(Setup, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task A_tampered_installer_is_deleted()
    {
        var target = Path.Combine(_dir, "setup.exe");
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            Releases.DownloadInstallerAsync(Serving(Encoding.UTF8.GetBytes("something else"), out _), Update(), target, null, default));
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("http://github.com/saymonseo/SaymonTools/releases/download/v1.2.0/s.exe")]
    [InlineData("https://evil.example.com/s.exe")]
    [InlineData(null)]
    public async Task Only_https_downloads_from_github_are_accepted(string? url)
    {
        var client = Serving(Setup, out var server);
        await Assert.ThrowsAsync<ConversionException>(() => Releases.DownloadInstallerAsync(client, Update(url), Path.Combine(_dir, "s.exe"), null, default));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_release_without_checksum_is_refused()
    {
        var client = Serving(Setup, out var server);
        await Assert.ThrowsAsync<ConversionException>(() => Releases.DownloadInstallerAsync(client, Update(sha: ""), Path.Combine(_dir, "s.exe"), null, default));
        Assert.Empty(server.Requests);
    }
}
