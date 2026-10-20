using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SaymonTools.App.UI;

/// <summary>
/// Colours and fonts shared by the wheel, the job cards and the shelf. Follows the Windows
/// light/dark app setting; <see cref="Refresh"/> re-reads it before a window is built.
/// </summary>
internal static class Theme
{
    private static readonly Palette DarkPalette = new(
        Backdrop: Color.FromArgb(0xF2, 0x0C, 0x0C, 0x0F),
        BackdropEdge: Color.FromArgb(0x38, 0xD8, 0x24, 0x40),
        Slice: Color.FromRgb(0x20, 0x20, 0x25),
        SliceEdge: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Hub: Color.FromRgb(0x10, 0x10, 0x14),
        Panel: Color.FromArgb(0xFC, 0x13, 0x13, 0x17),
        PanelEdge: Color.FromArgb(0x25, 0xEF, 0x70, 0x7B),
        Text: Color.FromRgb(0xFA, 0xF6, 0xF7),
        Muted: Color.FromRgb(0xAA, 0x9D, 0xA3),
        Track: Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF),
        Page: Color.FromRgb(0x0D, 0x0D, 0x11),
        Card: Color.FromRgb(0x19, 0x18, 0x1E),
        Hover: Color.FromArgb(0x1A, 0xFF, 0x30, 0x50),
        Shadow: 0.6);

    private static readonly Palette LightPalette = new(
        Backdrop: Color.FromArgb(0xF5, 0xED, 0xF6, 0xFF),
        BackdropEdge: Color.FromArgb(0x38, 0x2B, 0x78, 0xBC),
        Slice: Color.FromRgb(0xFF, 0xFF, 0xFF),
        SliceEdge: Color.FromArgb(0x26, 0x29, 0x6F, 0xAD),
        Hub: Color.FromRgb(0xF4, 0xFA, 0xFF),
        Panel: Color.FromArgb(0xFC, 0xF8, 0xFC, 0xFF),
        PanelEdge: Color.FromArgb(0x28, 0x29, 0x6F, 0xAD),
        Text: Color.FromRgb(0x15, 0x29, 0x40),
        Muted: Color.FromRgb(0x55, 0x6D, 0x85),
        Track: Color.FromArgb(0x24, 0x23, 0x70, 0xAF),
        Page: Color.FromRgb(0xED, 0xF4, 0xFC),
        Card: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Hover: Color.FromArgb(0x14, 0x28, 0x80, 0xD8),
        Shadow: 0.18);

    private sealed record Palette(
        Color Backdrop,
        Color BackdropEdge,
        Color Slice,
        Color SliceEdge,
        Color Hub,
        Color Panel,
        Color PanelEdge,
        Color Text,
        Color Muted,
        Color Track,
        Color Page,
        Color Card,
        Color Hover,
        double Shadow);

    private static Palette _palette = DarkPalette;

    public static Color AccentColor => IsDark ? Color.FromRgb(0xEB, 0x24, 0x40) : Color.FromRgb(0x17, 0x71, 0xCB);
    public static Color AccentColor2 => IsDark ? Color.FromRgb(0xAC, 0x10, 0x2D) : Color.FromRgb(0x42, 0xA5, 0xEF);

    /// <summary>Red/black or blue/white, used for the selected slice, active tab and progress bars.</summary>
    public static LinearGradientBrush AccentGradient => Freeze(new LinearGradientBrush(AccentColor, AccentColor2, new Point(0, 0), new Point(1, 1)));

    public static SolidColorBrush Accent => Freeze(new SolidColorBrush(AccentColor));
    public static readonly SolidColorBrush TextOnAccent = Freeze(new SolidColorBrush(Colors.White));
    public static readonly SolidColorBrush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)));
    public static readonly SolidColorBrush Success = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0xC3, 0x6E)));

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static bool IsDark { get; private set; } = true;

    public static Brush Backdrop => Solid(_palette.Backdrop);
    public static Brush BackdropEdge => Solid(_palette.BackdropEdge);
    public static Brush Slice => Solid(_palette.Slice);
    public static Brush SliceEdge => Solid(_palette.SliceEdge);
    public static Brush Hub => Solid(_palette.Hub);
    public static Brush Panel => Solid(_palette.Panel);
    public static Brush PanelBorder => Solid(_palette.PanelEdge);
    public static Brush Text => Solid(_palette.Text);
    public static Brush Muted => Solid(_palette.Muted);
    public static Brush Track => Solid(_palette.Track);
    public static Brush Page => Solid(_palette.Page);
    public static Brush Card => Solid(_palette.Card);
    public static Brush Hover => Solid(_palette.Hover);
    public static Brush AccentSoft => Solid(Color.FromArgb(IsDark ? (byte)0x38 : (byte)0x26, AccentColor.R, AccentColor.G, AccentColor.B));

    /// <summary>Soft drop shadow under floating surfaces.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur = 24) =>
        new() { BlurRadius = blur, ShadowDepth = 3, Direction = 270, Opacity = _palette.Shadow, Color = Colors.Black };

    /// <summary>Coloured glow for the highlighted slice.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect Glow() =>
        new() { BlurRadius = 26, ShadowDepth = 0, Opacity = IsDark ? 0.75 : 0.5, Color = AccentColor };

    /// <summary>Forces dark (true) or light (false) instead of following Windows; used by the self-test.</summary>
    public static bool? Override { get; set; }

    /// <summary>Re-reads the Windows "app mode" (light/dark).</summary>
    public static void Refresh() => Use(Override ?? ReadSystemDark());

    private static void Use(bool dark)
    {
        IsDark = dark;
        _palette = dark ? DarkPalette : LightPalette;
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
    }

    private static SolidColorBrush Solid(Color color) => Freeze(new SolidColorBrush(color));

    private static T Freeze<T>(T brush)
        where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
