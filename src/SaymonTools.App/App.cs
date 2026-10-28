using System.IO;
using System.Windows;
using System.Windows.Threading;
using SaymonTools.App.Actions;
using SaymonTools.App.Input;
using SaymonTools.App.Jobs;
using SaymonTools.App.Shell;
using SaymonTools.App.UI;
using SaymonTools.App.Wheel;
using SaymonTools.Core;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--self-test")
            return SelfTest.Run(args.Length >= 2 ? args[1] : "self-test");
        if (args.Contains("--unregister"))
        {
            // Called by the uninstaller.
            ShellIntegration.SetContextMenu(false);
            ShellIntegration.SetAutostart(false);
            return 0;
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running: hand over the request (or just open the settings).
            SingleInstance.Forward(args.Length > 0 ? args : ["--settings"]);
            return 0;
        }
        var app = new App(instance, args);
        return app.Run();
    }
}

internal sealed class App : Application
{
    private readonly SingleInstance _instance;
    private readonly string[] _args;
    private AppSettings _settings = null!;
    private ToolLocator _tools = null!;
    private GlobalInput? _input;
    private WheelController _controller = null!;
    private TrayIcon? _tray;
    private FolderWatcher? _watcher;
    private HotkeyListener? _hotkey;
    private HotkeyPreset? _activeHotkey;
    private readonly Updater _updater = new();

    public App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _args = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = AppSettings.Load();
        Loc.Current = _settings.Language ?? Loc.Detect();
        Theme.Override = _settings.Appearance switch { AppAppearance.Dark => true, AppAppearance.Light => false, _ => null };
        Theme.Refresh();
        _tools = new ToolLocator();
        var jobs = new JobRunner(_settings, _tools);
        SettingsWindow.DownloadTool = tool => jobs.DownloadToolsAsync([tool], ask: false);

        _input = new GlobalInput(Dispatcher);
        try
        {
            _input.Start();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "SaymonTools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _controller = new WheelController(_settings, _tools, jobs, _input);
        jobs.PresetsChanged = _controller.Reload;
        jobs.FindOperation = id => _controller.AllOperations.FirstOrDefault(o => o.Id == id);
        Ocr.OcrOperation.CopyToClipboard = text => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        });
        _tray = CreateTray();
        _watcher = new FolderWatcher(_settings, _tools, jobs, () => _controller.AllOperations, Dispatcher);
        _watcher.Apply();
        SettingsWindow.Updater = _updater;
        SettingsWindow.Operations = () => _controller.AllOperations;
        _updater.StartBackgroundChecks(_settings, update => _tray.OfferUpdate(update.Version, () => _ = _updater.InstallAsync(update)));
        _hotkey = new HotkeyListener(_controller.OnHotkey);
        ApplyHotkey();
        _instance.Listen(Dispatcher, HandleArgs);

        ShellIntegration.Refresh();
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            TryIntegrateOnFirstRun();
            OpenSettings();
        }
        else if (_args.Contains("--updated"))
        {
            _tray.Show(T("SaymonTools was updated", "SaymonTools обновлён"), T($"Version {Updater.Current}", $"Версия {Updater.Current}"), false);
        }
        else if (!_args.Contains("--background"))
        {
            _tray.Show(T("SaymonTools is running", "SaymonTools запущен"), T("Grab a file, tap Shift while dragging and drop it on a format.", "Перетащите файл, нажмите Shift и отпустите его над нужным форматом."), false);
        }
        HandleArgs(_args);
    }

    private static void TryIntegrateOnFirstRun()
    {
        try
        {
            ShellIntegration.SetContextMenu(true);
            ShellIntegration.SetAutostart(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    private TrayIcon CreateTray()
    {
        var tray = new TrayIcon(OpenSettings, () => _controller.OpenForClipboard(), () => ShelfWindow.Open([]), ShowHelp, SetPaused, _settings.Paused, () => Shutdown());
        if (_updater.Available is { } update)
            tray.OfferUpdate(update.Version, () => _ = _updater.InstallAsync(update));
        return tray;
    }

    private void HandleArgs(string[] args)
    {
        if (args.Contains("--settings"))
            OpenSettings();
        var wheelIndex = Array.IndexOf(args, "--wheel");
        var menuIndex = Array.IndexOf(args, "--menu");
        var wheel = menuIndex >= 0 && menuIndex + 1 < args.Length ? ShellIntegration.WheelFromArg(args[menuIndex + 1]) : null;
        if (wheelIndex >= 0)
            _controller.QueuePaths(args.Skip(wheelIndex + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)), wheel ?? WheelKind.Convert);
    }

    private void OpenSettings() => SettingsWindow.ShowSingle(_settings, _tools, OnSettingsChanged);

    private void OnSettingsChanged()
    {
        Theme.Override = _settings.Appearance switch { AppAppearance.Dark => true, AppAppearance.Light => false, _ => null };
        Theme.Refresh();
        var language = _settings.Language ?? Loc.Detect();
        var relocalized = language != Loc.Current;
        Loc.Current = language;
        _controller.Reload();
        _watcher?.Apply();
        if (relocalized)
        {
            // The Explorer submenu labels are stored in the registry in the old language.
            ShellIntegration.Refresh();
            _tray?.Dispose();
            _tray = CreateTray();
        }
        ApplyHotkey();
        if (_tray is not null)
            _tray.Paused = _settings.Paused;
    }

    /// <summary>
    /// Registers the chosen shortcut. If another app already owns it, switches to the first free preset
    /// and says so once, instead of leaving the user without a shortcut.
    /// </summary>
    private void ApplyHotkey()
    {
        var wanted = _settings.Hotkey;
        if (_hotkey is null || _activeHotkey == wanted)
            return;
        if (_hotkey.Register(wanted))
        {
            _activeHotkey = wanted;
            return;
        }

        var taken = AppSettings.HotkeyName(wanted);
        foreach (var preset in Enum.GetValues<HotkeyPreset>().Where(p => p != HotkeyPreset.None && p != wanted))
        {
            if (!_hotkey.Register(preset))
                continue;
            _activeHotkey = preset;
            _settings.Hotkey = preset;
            _settings.Save();
            SettingsWindow.RefreshIfOpen();
            var now = AppSettings.HotkeyName(preset);
            Notifications.Show(T("Shortcut changed", "Сочетание клавиш изменено"),
                T($"{taken} is used by another app, so SaymonTools now uses {now}. You can change it in Settings.", $"{taken} используется другим приложением, поэтому SaymonTools теперь использует {now}. Это можно изменить в настройках."));
            return;
        }

        _activeHotkey = HotkeyPreset.None;
        Notifications.Show(T("Shortcut is taken", "Сочетание клавиш занято"),
            T("All offered shortcuts are used by other apps. Open the wheel by dragging or from the tray menu.", "Все предложенные сочетания заняты другими приложениями. Открывайте круговое меню перетаскиванием или через значок в трее."), error: true);
    }

    private void SetPaused(bool paused)
    {
        _settings.Paused = paused;
        _settings.Save();
    }

    private void ShowHelp()
    {
        var convert = AppSettings.KeyName(_settings.ConvertKey);
        var tools = AppSettings.KeyName(_settings.ToolsKey);
        var actions = AppSettings.KeyName(_settings.ActionsKey);
        var hotkey = AppSettings.HotkeyName(_settings.Hotkey);
        var text = T(
            $"""
            1. Grab a file with the mouse (in File Explorer, on the desktop, an image in your browser, an Outlook attachment…).
            2. While dragging, tap a key:
                 {convert} – convert to another format
                 {tools} – tools (compress, shrink, trim, merge…)
                 {actions} – actions (copy, move, path, terminal, floating shelf, share)
            3. Drop the file on a slice of the wheel. Drop it in the middle to cancel.

            The wheel stays open until you drop; you can release the key and tap another one to switch wheels.
            Tap the same key again to "repeat the last action".

            Without the mouse: select files in File Explorer (or copy a file or a screenshot) and press {hotkey}.
            Arrows pick a slice, Enter runs it, Tab switches wheels, Esc closes.
            Also via right-click → “SaymonTools…”.

            The result is saved next to the original file; the original stays untouched.
            """,
            $"""
            1. Возьмите файл мышью: в Проводнике, на рабочем столе, изображение в браузере или вложение Outlook.
            2. При перетаскивании нажмите клавишу:
                 {convert} — конвертация в другой формат
                 {tools} — инструменты: сжатие, уменьшение, обрезка, объединение
                 {actions} — действия: копирование, перемещение, путь, терминал, плавающая полка, отправка
            3. Отпустите файл над сектором меню. В центре — отмена.

            Меню остаётся открытым, пока вы не отпустите файл. Можно отпустить клавишу и нажать другую для смены меню.
            Повторное нажатие той же клавиши повторяет последнее действие.

            Без мыши: выберите файлы в Проводнике (или скопируйте файл либо снимок экрана) и нажмите {hotkey}.
            Стрелки — выбор сектора, Enter — запуск, Tab — смена меню, Esc — закрыть.
            Также можно нажать правой кнопкой → «SaymonTools…».

            Результат сохраняется рядом с исходным файлом. Исходник не изменяется.
            """);
        MessageBox.Show(text, T("How SaymonTools works", "Как работает SaymonTools"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        Notifications.Show(T("SaymonTools – error", "SaymonTools — ошибка"), e.Exception.Message, error: true);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "log.txt"), $"[{DateTime.Now:s}] {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _input?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
