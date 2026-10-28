using Microsoft.Win32;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Shell;

/// <summary>Autostart and the "SaymonTools…" entry in Explorer's context menu (per user, no admin needed).</summary>
internal static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SaymonTools";
    private static readonly string[] MenuRoots = [@"Software\Classes\*\shell\SaymonTools", @"Software\Classes\Directory\shell\SaymonTools"];

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException(T("Unknown application path.", "Неизвестный путь приложения."));

    public static bool IsAutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Exe}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>The submenu entries, one per wheel; the key names keep them in this order.</summary>
    private static readonly (string Key, Core.WheelKind Wheel, string Arg)[] MenuEntries =
    [
        ("1convert", Core.WheelKind.Convert, "convert"),
        ("2tools", Core.WheelKind.Tools, "tools"),
        ("3actions", Core.WheelKind.Actions, "actions"),
    ];

    /// <summary>The wheel named by <c>--menu convert|tools|actions</c>.</summary>
    public static Core.WheelKind? WheelFromArg(string? arg) =>
        MenuEntries.FirstOrDefault(e => e.Arg == arg) is { Arg: not null } entry ? entry.Wheel : null;

    public static bool IsContextMenuEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{MenuRoots[0]}\shell\{MenuEntries[0].Key}\command");
            return key?.GetValue(null) is string value && value.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// "SaymonTools ▸ Convert / Tools / Actions" on files and folders, each opening that wheel.
    /// Rewritten from scratch, so older single-entry versions and stale labels go away.
    /// </summary>
    public static void SetContextMenu(bool enabled)
    {
        foreach (var root in MenuRoots)
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            if (!enabled)
                continue;
            using var key = Registry.CurrentUser.CreateSubKey(root);
            key.SetValue("MUIVerb", "SaymonTools");
            key.SetValue("Icon", $"\"{Exe}\",0");
            // An empty SubCommands makes Explorer show the "shell" subkeys as a submenu.
            key.SetValue("SubCommands", "");
            foreach (var (name, wheel, arg) in MenuEntries)
            {
                using var entry = key.CreateSubKey($@"shell\{name}");
                entry.SetValue("MUIVerb", AppSettings.WheelName(wheel) + "…");
                entry.SetValue("Icon", $"\"{Exe}\",0");
                entry.SetValue("MultiSelectModel", "Player");
                using var command = entry.CreateSubKey("command");
                command.SetValue(null, $"\"{Exe}\" --menu {arg} --wheel \"%1\"");
            }
        }
    }

    /// <summary>Re-points registered entries at the current exe after the app was moved or updated.</summary>
    public static void Refresh()
    {
        try
        {
            if (Registry.CurrentUser.OpenSubKey(MenuRoots[0]) is { } existing)
            {
                existing.Dispose();
                SetContextMenu(true);
            }
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not null)
                SetAutostart(true);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
