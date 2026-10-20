using System.IO;
using System.Windows;
using SaymonTools.App.UI;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using SaymonTools.Core.Upload;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Actions;

/// <summary>An operation that asks the user something once before it runs (on the UI thread).</summary>
internal interface IConfirmBeforeRun
{
    bool Confirm(IReadOnlyList<FileItem> items);
}

/// <summary>
/// "Upload": puts the files on a free host that needs no account and hands back a public link
/// (copied to the clipboard, with a QR code). Several files or a folder go up as one ZIP.
/// </summary>
internal sealed class UploadAction : Operation, IConfirmBeforeRun
{
    private static readonly Lazy<Uploader> SharedUploader = new(() => new Uploader(Uploader.CreateHttp()));

    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Action<UploadResult, string> _uploaded;

    /// <param name="uploaded">Called (on a worker thread) with the result and the uploaded file's name.</param>
    public UploadAction(AppSettings settings, Action save, Action<UploadResult, string> uploaded)
        : base("action.upload", WheelKind.Actions, T("Upload", "В интернет"), Glyphs.Cloud, Describe(settings))
    {
        _settings = settings;
        _save = save;
        _uploaded = uploaded;
    }

    public static FileHost HostFor(AppSettings settings) =>
        Uploader.Hosts.FirstOrDefault(h => h.Id == settings.UploadHost) ?? Uploader.Hosts[0];

    private static string Describe(AppSettings settings)
    {
        var host = HostFor(settings);
        return $"{host.Name} · {host.Retention(settings.UploadExpiry)}";
    }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => items.Count > 0;

    public bool Confirm(IReadOnlyList<FileItem> items)
    {
        if (_settings.UploadConsent)
            return true;
        var host = HostFor(_settings);
        var owner = ForegroundHelper.Host();
        var answer = MessageBox.Show(owner,
            T($"The file will be uploaded to {host.Name} and anyone with the link can download it ({host.Retention(_settings.UploadExpiry)}). Don't upload private documents.\n\nContinue? This question is asked only once.",
              $"Файл будет загружен на {host.Name}, и скачать его сможет любой, у кого есть ссылка (хранится {host.Retention(_settings.UploadExpiry)}). Не загружайте личные документы.\n\nПродолжить? Этот вопрос задаётся один раз."),
            "SaymonTools", MessageBoxButton.YesNo, MessageBoxImage.Information);
        owner.Close();
        if (answer != MessageBoxResult.Yes)
            return false;
        _settings.UploadConsent = true;
        _save();
        return true;
    }

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        string? temp = null;
        try
        {
            var path = items[0].Path;
            var share = 1.0;
            if (items.Count > 1 || items[0].IsDirectory)
            {
                // One link for everything: pack it into a ZIP first.
                temp = OutputPaths.TempDirectory();
                path = Path.Combine(temp, ArchiveBackend.ArchiveNameFor(items) + ".zip");
                await ArchiveBackend.PackAsync([.. items.Select(i => i.Path)], path, "zip", ctx.CancellationToken).ConfigureAwait(false);
                ctx.Progress.Report(0.1);
                share = 0.9;
            }
            var offset = 1 - share;
            var progress = new Relay(v => ctx.Progress.Report(offset + share * v));
            var result = await SharedUploader.Value.UploadAsync(path, _settings.UploadHost, _settings.UploadExpiry, progress, ctx.CancellationToken).ConfigureAwait(false);
            _uploaded(result, Path.GetFileName(path));
            var note = T("Link copied: ", "Ссылка скопирована: ") + result.Link;
            if (result.Host.Id != _settings.UploadHost)
                note += Environment.NewLine + T($"{HostFor(_settings).Name} didn't work, used {result.Host.Name}.", $"{HostFor(_settings).Name} не сработал, использован {result.Host.Name}.");
            return new OperationResult([], Note: note);
        }
        finally
        {
            if (temp is not null)
                OutputPaths.TryDelete(temp);
        }
    }

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
