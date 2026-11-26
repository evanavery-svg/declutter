using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Declutter.App;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Light, dark and Windows High Contrast colours, switchable while the app is open.</summary>
public static class WindowTheme
{
    private static ResourceDictionary? _colors;

    public static ThemeMode Mode { get; private set; }
    public static bool IsDark { get; private set; }
    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>Raised after the colours change, so views can redraw colours picked in code (badges).</summary>
    public static event EventHandler? Changed;

    public static void Load(Application app, ThemeMode mode)
    {
        Mode = mode;
        _colors = BuildColors(out bool dark);
        IsDark = dark;
        app.Resources.MergedDictionaries.Add(_colors);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Styles.xaml", UriKind.Relative) });

        // Follow Windows when it switches light/dark or High Contrast while Declutter is open.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
                app.Dispatcher.BeginInvoke(Refresh);
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) app.Dispatcher.BeginInvoke(Refresh);
        };
    }

    /// <summary>Switches theme immediately (Settings page).</summary>
    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        Refresh();
    }

    /// <summary>
    /// Swaps in the new colour dictionary; everything using DynamicResource updates by itself. (WPF freezes
    /// brushes once they're in use, so recolouring them in place isn't possible.)
    /// </summary>
    private static void Refresh()
    {
        var app = Application.Current;
        if (_colors == null || app == null) return;
        var fresh = BuildColors(out bool dark);
        int index = app.Resources.MergedDictionaries.IndexOf(_colors);
        if (index < 0) return;
        app.Resources.MergedDictionaries[index] = fresh;
        _colors = fresh;
        IsDark = dark;
        foreach (Window w in app.Windows) Apply(w);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static ResourceDictionary BuildColors(out bool dark)
    {
        if (SystemParameters.HighContrast)
        {
            dark = IsDarkColor(SystemColors.WindowColor);
            return HighContrastColors();
        }
        dark = Mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => SystemUsesDarkMode(),
        };
        return new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
    }

    /// <summary>
    /// High Contrast: every surface uses the user's chosen window colours, text uses their text colour, and
    /// highlights use their highlight colours. Meaning is carried by labels, never by colour alone.
    /// </summary>
    private static ResourceDictionary HighContrastColors()
    {
        var d = new ResourceDictionary();
        void Set(string key, Color c) => d[key] = new SolidColorBrush(c);

        Color window = SystemColors.WindowColor, text = SystemColors.WindowTextColor;
        Color highlight = SystemColors.HighlightColor, onHighlight = SystemColors.HighlightTextColor;
        foreach (var k in new[] { "Brush.Window", "Brush.Nav", "Brush.Card", "Brush.CardHover", "Brush.Control", "Brush.ControlHover",
                     "Brush.ControlPressed", "Brush.Input", "Brush.Track", "Brush.NavSelected" })
            Set(k, window);
        foreach (var k in new[] { "Brush.Border", "Brush.ControlBorder", "Brush.Text", "Brush.TextSecondary", "Brush.TextTertiary", "Brush.ScrollThumb" })
            Set(k, text);
        foreach (var k in new[] { "Brush.Accent", "Brush.AccentHover", "Brush.AccentPressed", "Brush.Danger", "Brush.DangerHover" })
            Set(k, highlight);
        Set("Brush.OnAccent", onHighlight);
        Set("Brush.OnDanger", onHighlight);
        Set("Brush.Disabled", SystemColors.GrayTextColor);
        foreach (var tone in new[] { "Neutral", "Info", "Good", "Caution", "Danger" })
        {
            Set($"Tone.{tone}.Background", window);
            Set($"Tone.{tone}.Foreground", text);
        }
        return d;
    }

    /// <summary>Gives a window a dark title bar in dark mode (Windows 10 2004+ / Windows 11), or a light one.</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    private static bool IsDarkColor(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;

    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
