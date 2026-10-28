using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SaymonTools.Core;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Shell;

/// <summary>
/// Updates through GitHub Releases of <see cref="Brand.Repository"/>: checks the latest release now and then,
/// and installs its "SaymonTools-Setup-X.Y.Z.exe" after checking its SHA-256. Installed copies (Program Files)
/// update in place; a portable copy only gets the release page.
/// </summary>
internal sealed class Updater
{
    /// <summary>This build's version (from the assembly, set by build.ps1 -Version).</summary>
    public static Version Current { get; } = ReadVersion();

    // After Current: static fields initialise in order and the client's User-Agent uses the version.
    private static readonly HttpClient Http = CreateHttp();
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private DispatcherTimer? _timer;
    private Version? _announced;

    /// <summary>True when running from Program Files, i.e. installed by the setup.</summary>
    public static bool IsInstalled
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return false;
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath)
                .Any(dir => dir.Length > 0 && exe.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    public UpdateInfo? Available { get; private set; }

    public DateTime? LastChecked { get; private set; }

    public string? LastError { get; private set; }

    public bool Installing { get; private set; }

    public event Action? Changed;

    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var latest = await Releases.LatestAsync(Http, Brand.Repository, cts.Token);
            Available = latest is not null && latest.IsNewerThan(Current) ? latest : null;
            LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            LastError = ex is TaskCanceledException ? T("No answer from GitHub.", "GitHub не отвечает.") : ex.Message;
        }
        LastChecked = DateTime.Now;
        Changed?.Invoke();
        return Available;
    }

    /// <summary>Checks a minute after start and then every 12 hours, while the setting is on; reports each new version once.</summary>
    public void StartBackgroundChecks(AppSettings settings, Action<UpdateInfo> found)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (!settings.CheckForUpdates || Installing)
                return;
            if (await CheckAsync() is { } update && update.Version != _announced)
            {
                _announced = update.Version;
                found(update);
            }
        };
        _timer.Start();
    }

    public async Task InstallAsync(UpdateInfo update)
    {
        if (!IsInstalled)
        {
            // A portable copy has no installer to update; the user downloads the new ZIP.
            Open(update.PageUrl);
            return;
        }
        if (Installing)
            return;
        Installing = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            var setup = Path.Combine(Path.GetTempPath(), $"SaymonTools-Setup-{update.Version}.exe");
            await Releases.DownloadInstallerAsync(Http, update, setup, null, CancellationToken.None);
            // Program Files needs administrator rights: Windows asks once (UAC). The setup closes this app,
            // replaces the files and starts the new version with --updated.
            Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1") { UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastError = T("The update was cancelled.", "Обновление отменено.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ConversionException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            LastError = ex.Message;
            Notifications.Show(T("Update failed", "Не удалось обновить"), ex.Message, error: true);
        }
        finally
        {
            Installing = false;
            Changed?.Invoke();
        }
    }

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static Version ReadVersion()
    {
        // "1.2.0+abc123" (the SDK appends the commit) → 1.2.0
        var text = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return UpdateInfo.TryParseVersion(text?.Split('+')[0] ?? "", out var version) ? version : new Version(1, 0, 0);
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SaymonTools/{Current}");
        return client;
    }
}
