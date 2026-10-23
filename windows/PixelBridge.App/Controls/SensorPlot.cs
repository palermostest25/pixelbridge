using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PixelBridge.App.Controls;

/// <summary>Lightweight 3-axis scrolling plot (ring buffer, redrawn on demand).</summary>
public sealed class SensorPlot : FrameworkElement
{
    private const int Capacity = 400;
    private readonly float[][] _data = [new float[Capacity], new float[Capacity], new float[Capacity]];
    private int _head, _count;
    private static readonly Pen[] Pens =
    [
        Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), 1.5)),
        Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0xA4, 0x6C)), 1.5)),
        Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3E, 0x7B, 0xFA)), 1.5)),
    ];
    private static readonly Pen Grid = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1));

    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    public double MinRange { get; set; } = 1;

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    public void Add(float[] v)
    {
        for (int a = 0; a < 3; a++) _data[a][_head] = a < v.Length ? v[a] : 0;
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    public void Clear() { _count = 0; _head = 0; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double max = MinRange;
        for (int a = 0; a < 3; a++) for (int i = 0; i < _count; i++) max = Math.Max(max, Math.Abs(_data[a][i]));
        const double top = 18; // title band; plot below it
        double mid = top + (h - top) / 2, half = (h - top) / 2 - 2;
        dc.DrawLine(Grid, new Point(0, mid), new Point(w, mid));
        var fg = (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;
        var text = new FormattedText($"{Title}  ±{max:0.##} {Unit}   x/y/z", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(4, 2));
        if (_count < 2) return;
        for (int a = 0; a < 3; a++)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + Capacity) % Capacity;
                    var p = new Point(i * w / (Capacity - 1), mid - _data[a][idx] / max * half);
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, Pens[a], g);
        }
    }
}
