using System.IO;
using System.Windows.Threading;
using SaymonTools.App.Jobs;
using SaymonTools.Core;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Shell;

/// <summary>
/// Runs the chosen operation on every new file in a watched folder. A file is processed once it stops
/// growing and can be opened (downloads and copies finish first); SaymonTools's own results are skipped,
/// so a rule like "compress" does not loop on its output.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan SettleCheck = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RememberResults = TimeSpan.FromMinutes(30);

    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly JobRunner _jobs;
    private readonly Func<IReadOnlyList<Operation>> _operations;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DateTime> _ownResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    public FolderWatcher(AppSettings settings, ToolLocator tools, JobRunner jobs, Func<IReadOnlyList<Operation>> operations, Dispatcher dispatcher)
    {
        _settings = settings;
        _tools = tools;
        _jobs = jobs;
        _operations = operations;
        _dispatcher = dispatcher;
        jobs.Produced += outputs =>
        {
            lock (_ownResults)
            {
                foreach (var output in outputs)
                    _ownResults[Path.GetFullPath(output)] = DateTime.UtcNow;
            }
        };
    }

    /// <summary>(Re)starts watching the enabled folders from the settings.</summary>
    public void Apply()
    {
        StopAll();
        foreach (var folder in _settings.WatchFolders.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.OperationId)))
        {
            if (!Directory.Exists(folder.Path))
                continue;
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = folder.IncludeSubfolders,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnFile(e.FullPath);
                watcher.Renamed += (_, e) => OnFile(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Warn(folder.Path, T("Cannot watch the folder", "Не удалось отслеживать папку"), $"{folder.Path}: {ex.Message}");
            }
        }
    }

    private void OnFile(string path)
    {
        if (!WatchRules.IsCandidate(path))
            return;
        lock (_waiting)
        {
            if (!_waiting.Add(path))
                return;
        }
        _ = WaitAndRunAsync(path);
    }

    private async Task WaitAndRunAsync(string path)
    {
        try
        {
            if (!await WaitUntilFinishedAsync(path) || IsOwnResult(path))
                return;
        }
        finally
        {
            lock (_waiting)
                _waiting.Remove(path);
        }
        await _dispatcher.InvokeAsync(() => Run(path));
    }

    private void Run(string path)
    {
        var folder = _settings.WatchFolders.FirstOrDefault(f => f.Enabled && WatchRules.IsInside(f, path));
        if (folder is null)
            return;
        var op = _operations().FirstOrDefault(o => o.Id == folder.OperationId);
        if (op is null)
        {
            Warn(folder.Path, T("Watched folder", "Отслеживаемая папка"),
                T($"The action for {folder.Path} no longer exists (deleted preset?). Change it in Settings → Watched folders.", $"Действие для {folder.Path} больше не существует (шаблон удалён?). Измените его в настройках → Отслеживаемые папки."));
            return;
        }
        var items = new[] { new FileItem(path) };
        if (!op.AppliesTo(items))
            return;
        if (!op.IsAvailable(_tools, items))
        {
            var missing = string.Join(", ", op.Requires.Where(t => !_tools.Has(t)).Select(ToolLocator.DisplayName));
            Warn(folder.Path + "|tools", T("Watched folder", "Отслеживаемая папка"),
                T($"“{op.Label}” needs {missing}. Convert anything once through the wheel and SaymonTools will download it.", $"Для «{op.Label}» требуется {missing}. Выполните любую конвертацию через круговое меню, и SaymonTools скачает нужный инструмент."));
            return;
        }
        _jobs.Start(op, items, null, automatic: true);
    }

    /// <summary>Waits until the file stops changing and can be read; false if it vanished or never settled.</summary>
    private static async Task<bool> WaitUntilFinishedAsync(string path)
    {
        long last = -1;
        var deadline = DateTime.UtcNow + GiveUpAfter;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SettleCheck).ConfigureAwait(false);
            long size;
            try
            {
                if (!File.Exists(path))
                    return false;
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                continue;
            }
            if (size > 0 && size == last && CanRead(path))
                return true;
            last = size;
        }
        return false;
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsOwnResult(string path)
    {
        lock (_ownResults)
        {
            foreach (var old in _ownResults.Where(kv => DateTime.UtcNow - kv.Value > RememberResults).Select(kv => kv.Key).ToList())
                _ownResults.Remove(old);
            return _ownResults.ContainsKey(Path.GetFullPath(path));
        }
    }

    /// <summary>Each problem is reported once per run of the app, not for every file.</summary>
    private void Warn(string key, string title, string text)
    {
        if (_warned.Add(key))
            Notifications.Show(title, text, error: true);
    }

    private void StopAll()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    public void Dispose() => StopAll();
}
