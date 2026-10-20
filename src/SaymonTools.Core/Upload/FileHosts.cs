using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Upload;

/// <summary>How long Litterbox keeps a file.</summary>
public enum UploadExpiry
{
    Hour,
    HalfDay,
    Day,
    ThreeDays,
}

/// <summary>A free file host that takes anonymous uploads (no account, no API key) and returns a public link.</summary>
public abstract class FileHost
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>Largest file the host accepts.</summary>
    public abstract long MaxBytes { get; }

    /// <summary>How long the link works, for the user ("3 days").</summary>
    public abstract string Retention(UploadExpiry expiry);

    /// <summary>Uploads one file and returns its public link.</summary>
    public abstract Task<string> UploadAsync(HttpClient http, string path, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct);

    /// <summary>
    /// A form like a browser sends it: quoted field names and a plain quoted file name, instead of .NET's
    /// defaults (unquoted names, an extra filename*) that simple upload scripts may not expect.
    /// </summary>
    protected static MultipartFormDataContent Form(string path, string field, IProgress<double> progress, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            var text = new StringContent(value);
            text.Headers.ContentType = null;
            text.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
            form.Add(text);
        }
        var file = new ProgressFileContent(path, progress);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(field), FileName = Quote(Path.GetFileName(path)) };
        form.Add(file);
        return form;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "'") + "\"";

    protected static async Task<string> PostAsync(HttpClient http, string url, HttpContent content, CancellationToken ct)
    {
        using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(body)}", null, response.StatusCode);
        return body;
    }

    /// <summary>The answer must be a web link; hosts report errors as plain text with status 200.</summary>
    protected static string RequireLink(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.ToString()
            : throw new HttpRequestException(T("Unexpected answer: ", "Неожиданный ответ: ") + Shorten(text));

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>litterbox.catbox.moe: up to 1 GB, kept 1 hour to 3 days.</summary>
public sealed class LitterboxHost : FileHost
{
    public override string Id => "litterbox";

    public override string Name => "Litterbox";

    public override long MaxBytes => 1L << 30;

    public override string Retention(UploadExpiry expiry) => expiry switch
    {
        UploadExpiry.Hour => T("1 hour", "1 час"),
        UploadExpiry.HalfDay => T("12 hours", "12 часов"),
        UploadExpiry.Day => T("1 day", "1 день"),
        _ => T("3 days", "3 дня"),
    };

    public override async Task<string> UploadAsync(HttpClient http, string path, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct)
    {
        var time = expiry switch { UploadExpiry.Hour => "1h", UploadExpiry.HalfDay => "12h", UploadExpiry.Day => "24h", _ => "72h" };
        using var form = Form(path, "fileToUpload", progress, ("reqtype", "fileupload"), ("time", time));
        return RequireLink(await PostAsync(http, "https://litterbox.catbox.moe/resources/internals/api.php", form, ct).ConfigureAwait(false));
    }
}

/// <summary>temp.sh: up to 4 GB, kept 3 days.</summary>
public sealed class TempShHost : FileHost
{
    public override string Id => "tempsh";

    public override string Name => "temp.sh";

    public override long MaxBytes => 4L << 30;

    public override string Retention(UploadExpiry expiry) => T("3 days", "3 дня");

    public override async Task<string> UploadAsync(HttpClient http, string path, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct)
    {
        using var form = Form(path, "file", progress);
        return RequireLink(await PostAsync(http, "https://temp.sh/upload", form, ct).ConfigureAwait(false));
    }
}

/// <summary>gofile.io as a guest: no stated size limit, removed after a while without downloads.</summary>
public sealed class GofileHost : FileHost
{
    public override string Id => "gofile";

    public override string Name => "Gofile";

    public override long MaxBytes => long.MaxValue;

    public override string Retention(UploadExpiry expiry) => T("until unused for a while", "пока файл скачивают");

    public override async Task<string> UploadAsync(HttpClient http, string path, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct)
    {
        using var form = Form(path, "file", progress);
        var body = await PostAsync(http, "https://upload.gofile.io/uploadfile", form, ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "ok"
                && root.TryGetProperty("data", out var data) && data.TryGetProperty("downloadPage", out var page))
                return RequireLink(page.GetString() ?? "");
        }
        catch (JsonException)
        {
        }
        throw new HttpRequestException(T("Unexpected answer: ", "Неожиданный ответ: ") + (body.Length <= 200 ? body : body[..200]));
    }
}

/// <summary>A file's bytes as an upload body, reporting how much has been sent.</summary>
internal sealed class ProgressFileContent(string path, IProgress<double> progress) : HttpContent
{
    private readonly long _length = new FileInfo(path).Length;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buffer = new byte[81920];
        long sent = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sent += read;
            progress.Report(_length == 0 ? 1 : (double)sent / _length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}

/// <param name="Link">The public link.</param>
/// <param name="Host">The host that took the file (may differ from the chosen one after a failure).</param>
/// <param name="Retention">How long the link works.</param>
public sealed record UploadResult(string Link, FileHost Host, string Retention);

/// <summary>Uploads to the chosen host and, if it is down, blocked or the file is too big for it, to the next one.</summary>
public sealed class Uploader(HttpClient http, IReadOnlyList<FileHost>? hosts = null)
{
    /// <remarks>temp.sh first: Litterbox sits behind a bot filter that refuses uploads from some networks.</remarks>
    public static IReadOnlyList<FileHost> Hosts { get; } = [new TempShHost(), new GofileHost(), new LitterboxHost()];

    private readonly IReadOnlyList<FileHost> _hosts = hosts ?? Hosts;

    public static HttpClient CreateHttp()
    {
        // Big files over slow connections: no overall timeout; the user can cancel from the card.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaymonTools/1.0");
        return client;
    }

    /// <summary>The order hosts are tried in: the preferred one first, then the others.</summary>
    public IReadOnlyList<FileHost> Order(string? preferred) =>
        [.. _hosts.Where(h => h.Id == preferred), .. _hosts.Where(h => h.Id != preferred)];

    public async Task<UploadResult> UploadAsync(string path, string? preferred, UploadExpiry expiry, IProgress<double> progress, CancellationToken ct)
    {
        var size = new FileInfo(path).Length;
        var errors = new List<string>();
        foreach (var host in Order(preferred))
        {
            if (size > host.MaxBytes)
            {
                errors.Add($"{host.Name}: " + T("file too big", "файл слишком большой"));
                continue;
            }
            try
            {
                progress.Report(0);
                var link = await host.UploadAsync(http, path, expiry, progress, ct).ConfigureAwait(false);
                return new UploadResult(link, host, host.Retention(expiry));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                errors.Add($"{host.Name}: {Describe(ex)}");
            }
        }
        throw new ConversionException(T("Couldn't upload to any service:", "Не удалось загрузить ни на один сервис:") + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge } => T("file too big", "файл слишком большой"),
        HttpRequestException { StatusCode: null, InnerException: not null } => T("no connection", "нет соединения"),
        TaskCanceledException => T("timed out", "превышено время ожидания"),
        _ => ex.Message,
    };
}
