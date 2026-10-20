using System.IO;
using System.Windows.Threading;
using SaymonTools.App.Actions;
using SaymonTools.App.Input;
using SaymonTools.App.Jobs;
using SaymonTools.App.Native;
using SaymonTools.App.Shell;
using SaymonTools.Core;
using SaymonTools.Core.Operations;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Wheel;

/// <summary>Decides when a wheel opens (drag gesture, hotkey, context menu) and with which operations.</summary>
internal sealed class WheelController
{
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly JobRunner _jobs;
    private readonly GlobalInput _input;
    private IReadOnlyList<Operation> _coreOperations;
    private IReadOnlyList<Operation> _actions;
    private readonly List<string> _pendingPaths = [];
    private readonly DispatcherTimer _argsTimer;
    private WheelWindow? _wheel;

    public WheelController(AppSettings settings, ToolLocator tools, JobRunner jobs, GlobalInput input)
    {
        _settings = settings;
        _tools = tools;
        _jobs = jobs;
        _input = input;
        _coreOperations = BuildOperations(settings);
        _actions = ActionCatalog.Build(settings, settings.Save, jobs);

        // Explorer starts one process per selected file; gather paths that arrive close together.
        _argsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _argsTimer.Tick += (_, _) =>
        {
            _argsTimer.Stop();
            var paths = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingPaths.Clear();
            OpenFor(paths, ExplorerSelection.CursorOrCenter());
        };

        input.IsWheelKey = key => !_settings.Paused && _settings.WheelFor(key) is not null;
        input.ModifierTapped += OnModifierTapped;
        input.LeftButtonUp += () => _wheel?.OnDragEnded();
        input.MouseDown += point =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Click } wheel && !wheel.Contains(point))
                wheel.Finish();
        };
        input.EscapePressed += () =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Drag } wheel)
                wheel.Finish();
        };
    }

    /// <summary>
    /// Rebuilds the wheels after the settings changed: the UI language (labels are fixed when an operation
    /// is created) or the user's presets.
    /// </summary>
    public void Reload()
    {
        _coreOperations = BuildOperations(_settings);
        _actions = ActionCatalog.Build(_settings, _settings.Save, _jobs);
    }

    /// <summary>The cross-platform catalog plus tools that need Windows itself (OCR).</summary>
    public static IReadOnlyList<Operation> BuildOperations() => [.. Catalog.Build(), new Ocr.OcrOperation()];

    /// <summary>The built-in operations followed by the user's presets.</summary>
    public static IReadOnlyList<Operation> BuildOperations(AppSettings settings) =>
        [.. BuildOperations(), .. settings.Presets.Select(PresetOperations.Build)];

    /// <summary>
    /// What fits these items. Conversions that need FFmpeg or Pandoc stay on the wheel even before those are
    /// downloaded; choosing one offers the download first.
    /// </summary>
    /// <remarks>
    /// With a mixed selection (say 7 images and 3 PDFs) operations that fit only part of it are offered too,
    /// after the ones that fit everything, and run on that part.
    /// </remarks>
    private IReadOnlyList<Operation> Operations(WheelKind wheel, IReadOnlyList<FileItem> items)
    {
        var whole = new List<Operation>();
        var partial = new List<Operation>();
        foreach (var op in wheel == WheelKind.Actions ? _actions : _coreOperations)
        {
            if (op.Wheel != wheel || Offer(op, items) is not { } offer)
                continue;
            var list = offer is PartialOperation ? partial : whole;
            if (offer.IsAvailable(_tools, items))
                list.Add(offer);
            else if (MissingDownloads(offer, items) is { } missing)
                list.Add(new NeedsToolOperation(offer, missing));
        }
        return WheelLayout.Arrange([.. whole, .. partial], _settings.HiddenOperations, _settings.FavoriteOperations);
    }

    /// <summary><paramref name="op"/> itself, a <see cref="PartialOperation"/> for the part it fits, or null.</summary>
    private static Operation? Offer(Operation op, IReadOnlyList<FileItem> items)
    {
        if (op.AppliesTo(items))
            return op;
        var part = op.Applicable(items);
        return part.Count > 0 ? new PartialOperation(op, part, items.Count) : null;
    }

    /// <summary>The smallest set of downloadable tools that would make <paramref name="op"/> work, or null.</summary>
    private IReadOnlyList<ExternalTool>? MissingDownloads(Operation op, IReadOnlyList<FileItem> items)
    {
        var missing = ToolInstaller.Installable.Where(t => !_tools.Has(t)).ToList();
        if (missing.Count == 0)
            return null;
        foreach (var candidate in missing.Select(t => new[] { t }).Append(missing.ToArray()))
        {
            if (op.IsAvailable(_tools.Assuming(candidate), items))
                return candidate;
        }
        return null;
    }

    /// <summary>Every operation the wheels can show (for the wheel editor).</summary>
    public IReadOnlyList<Operation> AllOperations => [.. _coreOperations, .. _actions];

    private Operation? LastFor(IReadOnlyList<FileItem> items)
    {
        var id = _settings.LastOperationId;
        var op = id is null ? null : AllOperations.FirstOrDefault(o => o.Id == id);
        var offer = op is null ? null : Offer(op, items);
        return offer is not null && offer.IsAvailable(_tools, items) ? offer : null;
    }

    private void OnModifierTapped(ModifierKey key)
    {
        if (_settings.Paused || _settings.WheelFor(key) is not { } wheel)
            return;
        if (_wheel is { IsFinished: false } open)
        {
            // Same key again: toggle "repeat last"; another key: switch wheel.
            if (wheel == open.CurrentWheel)
                open.ToggleRepeat();
            else
                open.SwitchTo(wheel);
            return;
        }
        if (_input.IsDragging)
            Open(WheelMode.Drag, wheel, null, _input.Cursor);
    }

    public void OnHotkey()
    {
        var paths = ExplorerSelection.Get(Win32.GetForegroundWindow());
        if (paths.Count == 0)
        {
            Notifications.Show(T("Nothing selected", "Ничего не выбрано"),
                T("Select files in File Explorer, copy them (Ctrl+C) or take a screenshot (Win+Shift+S), then press the shortcut again.", "Выберите файлы в Проводнике, скопируйте их (Ctrl+C) или сделайте снимок экрана (Win+Shift+S), затем нажмите сочетание ещё раз."));
            return;
        }
        OpenFor(paths, ExplorerSelection.CursorOrCenter());
    }

    /// <summary>Opens the wheel for whatever is on the clipboard (tray menu).</summary>
    public void OpenForClipboard()
    {
        var paths = ExplorerSelection.FromClipboard();
        if (paths.Count == 0)
        {
            Notifications.Show(T("The clipboard is empty", "Буфер обмена пуст"),
                T("Copy files or an image, e.g. a screenshot (Win+Shift+S).", "Скопируйте файлы или изображение, например снимок экрана (Win+Shift+S)."));
            return;
        }
        OpenFor(paths, ExplorerSelection.CursorOrCenter());
    }

    /// <summary>Paths from the context menu (possibly one call per file).</summary>
    public void QueuePaths(IEnumerable<string> paths)
    {
        _pendingPaths.AddRange(paths);
        _argsTimer.Stop();
        _argsTimer.Start();
    }

    public void OpenFor(IReadOnlyList<string> paths, Win32.POINT at)
    {
        var items = paths
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Select(p => new FileItem(p))
            .ToList();
        if (items.Count == 0)
            return;
        Open(WheelMode.Click, WheelKind.Convert, items, at);
    }

    private void Open(WheelMode mode, WheelKind wheel, IReadOnlyList<FileItem>? items, Win32.POINT at)
    {
        _wheel?.Finish();
        UI.Theme.Refresh();
        var window = new WheelWindow(mode, wheel, items, _settings, Operations, _jobs.Start)
        {
            LastOperation = LastFor,
            RunRepeat = targets =>
            {
                if (LastFor(targets) is { } last)
                    _jobs.Start(last, targets, _settings.LastParameters);
            },
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wheel, window))
                _wheel = null;
        };
        _wheel = window;
        window.ShowAt(at);
    }
}
