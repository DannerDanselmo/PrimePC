using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PrimePC.App.Controls;

/// <summary>Série imutável de pontos (tempo, valor) para os gráficos.</summary>
public sealed class ChartSeries
{
    public ChartSeries(DateTime[] times, double[] values)
    {
        Times = times;
        Values = values;
    }

    public DateTime[] Times { get; }
    public double[] Values { get; }
    public int Count => Values.Length;

    public static ChartSeries Empty { get; } = new(Array.Empty<DateTime>(), Array.Empty<double>());

    public static ChartSeries From(IReadOnlyList<Sensors.SensorSample> samples, Func<double, double>? transform = null)
    {
        var t = new DateTime[samples.Count];
        var v = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            t[i] = samples[i].Timestamp;
            v[i] = transform is null ? samples[i].Value : transform(samples[i].Value);
        }

        return new ChartSeries(t, v);
    }
}

/// <summary>
/// Gráfico de linha leve, desenhado diretamente (sem dependências). Suporta até duas séries, eixo Y com
/// rótulos, janela de tempo deslizante e modo compacto (minigráfico).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = Reg(nameof(Series), typeof(ChartSeries), null);
    public static readonly DependencyProperty Series2Property = Reg(nameof(Series2), typeof(ChartSeries), null);
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.SteelBlue);
    public static readonly DependencyProperty Stroke2Property = Reg(nameof(Stroke2), typeof(Brush), Brushes.OrangeRed);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), null);
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush), typeof(Brush), Brushes.LightGray);
    public static readonly DependencyProperty TextBrushProperty = Reg(nameof(TextBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty ShowAxisProperty = Reg(nameof(ShowAxis), typeof(bool), true);
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), typeof(double), double.NaN);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), double.NaN);
    public static readonly DependencyProperty WindowSecondsProperty = Reg(nameof(WindowSeconds), typeof(double), 0d);
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), "");
    public static readonly DependencyProperty GlowProperty = Reg(nameof(Glow), typeof(bool), true);

    /// <summary>Desenha um halo translúcido sob a linha (efeito neon).</summary>
    public bool Glow { get => (bool)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(LineChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartSeries? Series { get => (ChartSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public ChartSeries? Series2 { get => (ChartSeries?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public bool ShowAxis { get => (bool)GetValue(ShowAxisProperty); set => SetValue(ShowAxisProperty, value); }

    /// <summary>Mínimo fixo do eixo Y (NaN = automático).</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>Máximo fixo do eixo Y (NaN = automático).</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Janela de tempo exibida em segundos (0 = toda a série).</summary>
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (size.Width < 10 || size.Height < 10)
            return;

        var s1 = Series;
        var s2 = Series2;
        bool any = (s1?.Count ?? 0) > 0 || (s2?.Count ?? 0) > 0;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        double left = ShowAxis ? 46 : 0, bottom = ShowAxis ? 18 : 0, top = ShowAxis ? 6 : 2;
        var plot = new Rect(left, top, Math.Max(1, size.Width - left - 4), Math.Max(1, size.Height - top - bottom));

        if (!any)
        {
            if (ShowAxis)
            {
                var text = new FormattedText("Sem dados", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, TextBrush, dpi);
                dc.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2, plot.Top + (plot.Height - text.Height) / 2));
            }

            return;
        }

        DateTime end = Max(Last(s1), Last(s2));
        DateTime start = WindowSeconds > 0 ? end.AddSeconds(-WindowSeconds) : Min(First(s1), First(s2));
        if (end <= start)
            start = end.AddSeconds(-1);

        double min = Minimum, max = Maximum;
        if (double.IsNaN(min) || double.IsNaN(max))
        {
            double lo = double.MaxValue, hi = double.MinValue;
            Range(s1, start, ref lo, ref hi);
            Range(s2, start, ref lo, ref hi);
            if (lo == double.MaxValue)
            {
                lo = 0;
                hi = 1;
            }

            double pad = Math.Max((hi - lo) * 0.12, Math.Abs(hi) * 0.02 + 0.5);
            if (double.IsNaN(min)) min = Math.Max(lo >= 0 ? 0 : double.MinValue, lo - pad);
            if (double.IsNaN(max)) max = hi + pad;
        }

        if (max - min < 1e-9)
            max = min + 1;

        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();
        if (ShowAxis)
        {
            for (int i = 0; i <= 4; i++)
            {
                double y = Math.Round(plot.Top + plot.Height * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                double value = max - (max - min) * i / 4;
                var label = new FormattedText(FormatAxis(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5,
                    TextBrush, dpi);
                dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
            }

            var span = end - start;
            var startLabel = new FormattedText(span.TotalSeconds >= 120 ? $"-{span.TotalMinutes:0} min" : $"-{span.TotalSeconds:0} s",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, TextBrush, dpi);
            dc.DrawText(startLabel, new Point(plot.Left, plot.Bottom + 2));
            var endLabel = new FormattedText(end.ToString("HH:mm:ss"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5,
                TextBrush, dpi);
            dc.DrawText(endLabel, new Point(plot.Right - endLabel.Width, plot.Bottom + 2));
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 1, plot.Top - 2, plot.Width + 2, plot.Height + 4)));
        if (s1 is not null)
            DrawSeries(dc, s1, plot, start, end, min, max, Stroke, Fill, Glow);
        if (s2 is not null)
            DrawSeries(dc, s2, plot, start, end, min, max, Stroke2, null, Glow);
        dc.Pop();
    }

    private static void DrawSeries(DrawingContext dc, ChartSeries s, Rect plot, DateTime start, DateTime end, double min, double max,
        Brush stroke, Brush? fill, bool glow)
    {
        double span = (end - start).TotalSeconds;
        var points = new List<Point>(s.Count);
        for (int i = 0; i < s.Count; i++)
        {
            if (s.Times[i] < start)
                continue;
            double x = plot.Left + (s.Times[i] - start).TotalSeconds / span * plot.Width;
            double y = plot.Bottom - (s.Values[i] - min) / (max - min) * plot.Height;
            points.Add(new Point(x, y));
        }

        if (points.Count == 0)
            return;

        if (fill is not null && points.Count > 1)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, plot.Bottom), true, true);
                ctx.PolyLineTo(points, false, true);
                ctx.LineTo(new Point(points[^1].X, plot.Bottom), false, false);
            }

            area.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var pen = new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        Brush? halo = null;
        if (glow)
        {
            halo = stroke.CloneCurrentValue();
            halo.Opacity = 0.22;
            halo.Freeze();
        }

        if (points.Count > 1)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points, true, true);
            }

            line.Freeze();
            if (halo is not null)
            {
                var haloPen = new Pen(halo, 7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                haloPen.Freeze();
                dc.DrawGeometry(null, haloPen, line);
            }

            dc.DrawGeometry(null, pen, line);
        }

        if (halo is not null)
            dc.DrawEllipse(halo, null, points[^1], 7, 7);
        dc.DrawEllipse(stroke, null, points[^1], 3, 3);
    }

    private static string FormatAxis(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("0", CultureInfo.CurrentCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.#", CultureInfo.CurrentCulture);

    private static void Range(ChartSeries? s, DateTime start, ref double lo, ref double hi)
    {
        if (s is null)
            return;
        for (int i = 0; i < s.Count; i++)
        {
            if (s.Times[i] < start)
                continue;
            lo = Math.Min(lo, s.Values[i]);
            hi = Math.Max(hi, s.Values[i]);
        }
    }

    private static DateTime Last(ChartSeries? s) => s is { Count: > 0 } ? s.Times[^1] : DateTime.MinValue;
    private static DateTime First(ChartSeries? s) => s is { Count: > 0 } ? s.Times[0] : DateTime.MaxValue;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
