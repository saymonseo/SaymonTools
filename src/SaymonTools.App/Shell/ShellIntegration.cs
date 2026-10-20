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

    public static bool IsContextMenuEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(MenuRoots[0] + @"\command");
            return key?.GetValue(null) is string value && value.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetContextMenu(bool enabled)
    {
        foreach (var root in MenuRoots)
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
                continue;
            }
            using var key = Registry.CurrentUser.CreateSubKey(root);
            key.SetValue(null, "SaymonTools…");
            key.SetValue("Icon", $"\"{Exe}\",0");
            key.SetValue("MultiSelectModel", "Player");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{Exe}\" --wheel \"%1\"");
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
