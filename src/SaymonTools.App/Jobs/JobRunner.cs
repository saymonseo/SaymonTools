using SaymonTools.App.Actions;
using SaymonTools.App.Shell;
using SaymonTools.App.UI;
using SaymonTools.Core;
using SaymonTools.Core.Operations;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Jobs;

/// <summary>Runs an operation in the background and shows its progress card.</summary>
internal sealed class JobRunner(AppSettings settings, ToolLocator tools)
{
    private JobsWindow? _window;

    /// <summary>Files a job created (watched folders use it to skip their own results).</summary>
    public event Action<IReadOnlyList<string>>? Produced;

    public void Start(Operation op, IReadOnlyList<FileItem> items) => Start(op, items, null);

    /// <param name="parameters">Values for the operation's questions; when given (repeat last) no dialog is shown.</param>
    /// <param name="automatic">Started by a watched folder: not remembered for "repeat last" and never revealed.</param>
    public async void Start(Operation op, IReadOnlyList<FileItem> items, IReadOnlyDictionary<string, string>? parameters, bool automatic = false)
    {
        if (op is NeedsToolOperation needs)
        {
            if (await DownloadToolsAsync(needs.Missing, ask: true))
                Start(needs.Inner, items, parameters);
            return;
        }

        // Part of a mixed selection: the card, questions and results only concern the files it fits.
        if (op is PartialOperation partial)
        {
            items = partial.Applicable(items);
            op = partial.Inner;
            if (items.Count == 0)
                return;
        }

        if (op is AppAction action)
        {
            Remember(op, null);
            try
            {
                await action.RunAsync(items);
            }
            catch (Exception ex)
            {
                Notifications.Show(op.Label, ex.Message, error: true);
            }
            return;
        }

        parameters ??= new Dictionary<string, string>();
        if (op.Parameters.Count > 0 && parameters.Count == 0)
        {
            var dialog = new ParameterDialog(op, items);
            if (dialog.ShowDialog() != true)
                return;
            parameters = dialog.Values;
        }
        if (!automatic)
            Remember(op, parameters);

        _window ??= new JobsWindow();
        using var cts = new CancellationTokenSource();
        var title = op.Wheel == WheelKind.Convert && op.Label.ToUpperInvariant() == op.Label ? T($"Convert to {op.Label}", $"Конвертация в {op.Label}") : op.Label;
        var detail = items.Count == 1 ? items[0].Name : Items(items.Count);
        var card = _window.Add(title, detail, iconPath: items[0].Path, cancel: () =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });
        var context = new OperationContext
        {
            Tools = tools,
            Progress = new Progress<double>(card.Report),
            CancellationToken = cts.Token,
            OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? null : settings.OutputDirectory,
            Parameters = parameters,
            Settings = settings.Quality,
        };

        try
        {
            var result = await Task.Run(() => op.ExecuteAsync(items, context));
            Produced?.Invoke(result.Outputs);
            card.Complete(result.Outputs, result.Message, result.Note);
            if (!automatic && settings.RevealResult && result.Outputs.Count > 0)
                JobCard.Reveal(result.Outputs[0]);
        }
        catch (OperationCanceledException)
        {
            card.Cancelled();
        }
        catch (Exception ex)
        {
            card.Fail(Friendly(ex));
        }
    }

    /// <summary>A finished card for work done in the app itself (no progress), with one follow-up button.</summary>
    public void ShowDone(string title, string detail, string? iconPath, string status, string actionLabel, string actionGlyph, Action action)
    {
        _window ??= new JobsWindow();
        _window.Add(title, detail, cancel: () => { }, iconPath).Done(status, actionLabel, actionGlyph, action);
    }

    /// <summary>Downloads FFmpeg/Pandoc with a progress card; true when everything is installed.</summary>
    public async Task<bool> DownloadToolsAsync(IReadOnlyList<ExternalTool> missing, bool ask)
    {
        if (ask)
        {
            var names = string.Join(" + ", missing.Select(ToolLocator.DisplayName));
            var size = string.Join(" + ", missing.Select(ToolInstaller.DownloadSize));
            var owner = ForegroundHelper.Host();
            var answer = System.Windows.MessageBox.Show(owner,
                T($"This conversion needs {names} ({size} download). Download it now? It's a one-time step.", $"Для конвертации требуется {names} (загрузка {size}). Скачать сейчас? Это нужно сделать только один раз."),
                "SaymonTools", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            owner.Close();
            if (answer != System.Windows.MessageBoxResult.Yes)
                return false;
        }

        _window ??= new JobsWindow();
        foreach (var tool in missing)
        {
            using var cts = new CancellationTokenSource();
            var card = _window.Add(T($"Downloading {ToolLocator.DisplayName(tool)}", $"Скачивание {ToolLocator.DisplayName(tool)}"), ToolInstaller.DownloadSize(tool), () =>
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            });
            try
            {
                await ToolInstaller.InstallAsync(tool, new Progress<double>(card.Report), cts.Token);
                tools.Refresh();
                card.Complete([], null, T("Ready; it won't be downloaded again.", "Готово. Повторная загрузка не потребуется."));
            }
            catch (OperationCanceledException)
            {
                card.Cancelled();
                return false;
            }
            catch (Exception ex)
            {
                card.Fail(Friendly(ex));
                return false;
            }
        }
        return true;
    }

    private void Remember(Operation op, IReadOnlyDictionary<string, string>? parameters)
    {
        settings.LastOperationId = op.Id;
        settings.LastParameters = parameters is { Count: > 0 } ? new Dictionary<string, string>(parameters) : null;
        settings.Save();
    }

    private static string Friendly(Exception ex) => ex switch
    {
        ConversionException => ex.Message,
        UnauthorizedAccessException => T("Cannot write to the target folder. Choose another output folder in Settings.", "Нет доступа для записи в папку результата. Выберите другую папку в настройках."),
        System.IO.IOException => T($"File error: {ex.Message}", $"Ошибка файла: {ex.Message}"),
        ImageMagick.MagickException => T($"Could not process the image: {ex.Message}", $"Не удалось обработать изображение: {ex.Message}"),
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
