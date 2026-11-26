using System.Windows;
using System.Windows.Media;

namespace Declutter.App.Controls;

/// <summary>A tiny line chart of a series of numbers (oldest first), scaled to fit.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When true the axis starts at zero (sizes); otherwise it zooms in on the range (timings).</summary>
    public static readonly DependencyProperty FromZeroProperty = DependencyProperty.Register(nameof(FromZero), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool FromZero
    {
        get => (bool)GetValue(FromZeroProperty);
        set => SetValue(FromZeroProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values == null || values.Count == 0 || w < 4 || h < 4) return;

        double min = FromZero ? 0 : values.Min(), max = values.Max();
        if (max - min < 1e-9) { min -= 1; max += 1; }
        const double pad = 3;
        Point At(int i) => new(
            values.Count == 1 ? w / 2 : pad + i * (w - 2 * pad) / (values.Count - 1),
            pad + (1 - (values[i] - min) / (max - min)) * (h - 2 * pad));

        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (int i = 1; i < values.Count; i++) ctx.LineTo(At(i), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawEllipse(Stroke, null, At(values.Count - 1), 3.5, 3.5); // highlight the latest value
    }
}
