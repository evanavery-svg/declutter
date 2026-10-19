using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Declutter.App.Mvvm;

/// <summary>Colour role of a badge or message.</summary>
public enum Tone
{
    Neutral,
    Info,
    Good,
    Caution,
    Danger,
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        };
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

/// <summary>Maps a <see cref="Tone"/> to the theme brush "Tone.{Tone}.{Part}", Part being Background or Foreground.</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public string Part { get; set; } = "Background";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value is Tone t ? t : Tone.Neutral;
        return Application.Current?.TryFindResource($"Tone.{tone}.{Part}") as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
