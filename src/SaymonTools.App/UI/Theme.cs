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
    // SaymonTools: deep graphite-violet surfaces with a violet → teal accent.
    private static readonly Palette DarkPalette = new(
        Backdrop: Color.FromArgb(0xF0, 0x12, 0x10, 0x1C),
        BackdropEdge: Color.FromArgb(0x55, 0x8B, 0x6C, 0xFF),
        Slice: Color.FromRgb(0x1F, 0x1B, 0x2E),
        SliceEdge: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
        Hub: Color.FromRgb(0x16, 0x13, 0x23),
        Panel: Color.FromArgb(0xFC, 0x17, 0x14, 0x24),
        PanelEdge: Color.FromArgb(0x33, 0x9B, 0x87, 0xFF),
        Text: Color.FromRgb(0xF4, 0xF2, 0xFF),
        Muted: Color.FromRgb(0xA4, 0x9F, 0xBE),
        Track: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Page: Color.FromRgb(0x0F, 0x0D, 0x18),
        Card: Color.FromRgb(0x1A, 0x17, 0x28),
        Hover: Color.FromArgb(0x1E, 0x8B, 0x6C, 0xFF),
        Shadow: 0.6);

    private static readonly Palette LightPalette = new(
        Backdrop: Color.FromArgb(0xF5, 0xF6, 0xF3, 0xFF),
        BackdropEdge: Color.FromArgb(0x45, 0x6D, 0x4A, 0xFF),
        Slice: Color.FromRgb(0xFF, 0xFF, 0xFF),
        SliceEdge: Color.FromArgb(0x22, 0x5B, 0x3F, 0xD9),
        Hub: Color.FromRgb(0xF8, 0xF6, 0xFF),
        Panel: Color.FromArgb(0xFC, 0xFC, 0xFB, 0xFF),
        PanelEdge: Color.FromArgb(0x2A, 0x6D, 0x4A, 0xFF),
        Text: Color.FromRgb(0x1C, 0x17, 0x33),
        Muted: Color.FromRgb(0x62, 0x5B, 0x80),
        Track: Color.FromArgb(0x1E, 0x6D, 0x4A, 0xFF),
        Page: Color.FromRgb(0xF3, 0xF1, 0xFA),
        Card: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Hover: Color.FromArgb(0x14, 0x6D, 0x4A, 0xFF),
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

    /// <summary>Violet; the gradient runs from it to <see cref="AccentColor2"/> (teal). Dark enough for white text in both themes.</summary>
    public static Color AccentColor => IsDark ? Color.FromRgb(0x7A, 0x5A, 0xFF) : Color.FromRgb(0x6D, 0x4A, 0xFF);
    public static Color AccentColor2 => IsDark ? Color.FromRgb(0x0E, 0x9A, 0xB8) : Color.FromRgb(0x08, 0x91, 0xB2);

    /// <summary>Violet → teal, used for the selected slice, active tab and progress bars.</summary>
    public static LinearGradientBrush AccentGradient => Freeze(new LinearGradientBrush(AccentColor, AccentColor2, new Point(0, 0), new Point(1, 1)));

    /// <summary>A thin violet → teal outline around the wheel and its centre.</summary>
    public static LinearGradientBrush AccentRing => Freeze(new LinearGradientBrush(
        Color.FromArgb(0xB0, AccentColor.R, AccentColor.G, AccentColor.B),
        Color.FromArgb(0xB0, AccentColor2.R, AccentColor2.G, AccentColor2.B), new Point(0, 0), new Point(1, 1)));

    /// <summary>Corner radius of cards and panels.</summary>
    public static readonly CornerRadius CardCorners = new(18);


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
