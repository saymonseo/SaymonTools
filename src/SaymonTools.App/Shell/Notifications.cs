namespace SaymonTools.App.Shell;

/// <summary>Short messages through the tray icon's balloon / Windows notification.</summary>
internal static class Notifications
{
    public static Action<string, string, bool>? Sink { get; set; }

    public static void Show(string title, string text, bool error = false) => Sink?.Invoke(title, text, error);
}
