using System.Text.Json;

namespace SaymonTools.Core;

/// <summary>The latest published release, as reported by the GitHub releases API.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerSha256)
{

    /// <summary>Parses a "releases/latest" response; null when it isn't a usable release.</summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
            return null;
        var page = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

        string? installer = null;
        string? sha = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("SaymonTools-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                installer = asset.GetProperty("browser_download_url").GetString();
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d["sha256:".Length..].ToLowerInvariant();
                break;
            }
        }
        return new UpdateInfo(version, tag, page, installer, sha);
    }

    public bool IsNewerThan(Version current) => Version > Normalize(current);

    public static bool TryParseVersion(string text, out Version version)
    {
        var ok = Version.TryParse(text.Trim().TrimStart('v', 'V'), out var parsed);
        version = ok ? Normalize(parsed!) : new Version(0, 0, 0);
        return ok;
    }

    /// <summary>1.2 → 1.2.0 and 1.2.3.0 → 1.2.3, so versions compare the way people read them.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}

/// <summary>Looks up the latest GitHub release and downloads its installer, refusing anything unverified.</summary>
public static class Releases
{
    public static string LatestUrl(string repository) => $"https://api.github.com/repos/{repository}/releases/latest";

    /// <summary>The latest published release, or null when the repository has none yet.</summary>
    public static async Task<UpdateInfo?> LatestAsync(HttpClient http, string repository, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl(repository));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return UpdateInfo.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Downloads the release's installer to <paramref name="target"/>. Only a github.com HTTPS download with a
    /// published SHA-256 that matches is kept; anything else throws and leaves no file behind.
    /// </summary>
    public static async Task DownloadInstallerAsync(HttpClient http, UpdateInfo update, string target, IProgress<double>? progress, CancellationToken ct)
    {
        if (update.InstallerUrl is null || !Uri.TryCreate(update.InstallerUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(Loc.T("The release has no installer from github.com.", "В релизе нет установщика с github.com."));
        if (string.IsNullOrEmpty(update.InstallerSha256))
            throw new ConversionException(Loc.T("The release doesn't publish the installer's checksum, so it isn't installed.", "В релизе не опубликована контрольная сумма установщика, поэтому он не устанавливается."));
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(target);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        progress?.Report((double)done / total);
                }
            }
            await using var file = File.OpenRead(target);
            var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!actual.Equals(update.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                throw new ConversionException(Loc.T("The downloaded installer doesn't match the published checksum.", "Скачанный установщик не совпадает с опубликованной контрольной суммой."));
        }
        catch
        {
            OutputPaths.TryDelete(target);
            throw;
        }
    }
}
