using System.Windows;
using System.Windows.Media;

namespace PrimePC.App.Controls;

/// <summary>Gráfico compacto do tempo de cada quadro (picos = travadas), usado na sobreposição.</summary>
public sealed class FrameTimeGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(FrameTimeGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4A, 0x2E)), 1.4) { LineJoin = PenLineJoin.Round });
    private static readonly Pen Guide = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xA3, 0xAC, 0xC2)), 1) { DashStyle = DashStyles.Dash });

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        // Linha de referência em 16,7 ms (60 FPS) quando cabe na escala.
        var v = Values;
        double max = Math.Max(20, v is { Length: > 0 } ? v.Max() * 1.15 : 20);
        double guideY = h - 16.7 / max * h;
        dc.DrawLine(Guide, new Point(0, guideY), new Point(w, guideY));
        if (v is null || v.Length < 2)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double step = w / (v.Length - 1);
            ctx.BeginFigure(new Point(0, h - v[0] / max * h), false, false);
            for (int i = 1; i < v.Length; i++)
                ctx.LineTo(new Point(i * step, h - v[i] / max * h), true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, Line, geometry);
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
