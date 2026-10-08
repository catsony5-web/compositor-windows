using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class HistogramView : FrameworkElement
{
    // Red, green, blue and (Rec. 709) luminance bins.
    public double[][] Bins { get; private set; } = [new double[256], new double[256], new double[256], new double[256]];
    /// <summary>Draws the luminance distribution as a gray area under the RGB curves (간결한 화면 histogram panel).</summary>
    public bool ShowLuminance { get; set; }
    public HistogramView() { Height = 90; MinWidth = 180; ToolTip = "현재 합성 이미지의 RGB 분포 · 투명 픽셀 제외"; }
    public void Update(Raster raster) => Show(Compute(raster));
    /// <summary>Shows bins computed earlier (for example off the UI thread by <see cref="Compute"/>).</summary>
    public void Show(double[][] bins) { Bins = bins; InvalidateVisual(); }
    /// <summary>Alpha-weighted RGB and luminance bins from about 65,536 evenly spaced pixels. Pure, so it can run on a worker thread.</summary>
    public static double[][] Compute(Raster raster)
    {
        var bins = new[] { new double[256], new double[256], new double[256], new double[256] };
        int pixels = raster.Width * raster.Height, step = Math.Max(1, (int)Math.Ceiling(pixels / 65536.0));
        for (int p = 0; p < pixels; p += step)
        {
            int i = p * 4; double a = raster.Data[i + 3] / 255.0;
            byte r = raster.Data[i + 2], g = raster.Data[i + 1], b = raster.Data[i];
            bins[0][r] += a; bins[1][g] += a; bins[2][b] += a;
            bins[3][Luminance(r, g, b)] += a;
        }
        return bins;
    }

    /// <summary>Rec. 709 luminance of an 8-bit color, rounded to a bin.</summary>
    public static int Luminance(byte r, byte g, byte b) => Math.Clamp((int)Math.Round(.2126 * r + .7152 * g + .0722 * b), 0, 255);

    /// <summary>Weighted mean luminance (0–255) and the sampled weight of the luminance bins; NaN mean when nothing is opaque.</summary>
    public static (double Mean, double Weight) LuminanceStats(double[][] bins)
    {
        if (bins.Length < 4) return (double.NaN, 0);
        double weight = 0, sum = 0;
        for (int v = 0; v < 256; v++) { weight += bins[3][v]; sum += v * bins[3][v]; }
        return (weight > 0 ? sum / weight : double.NaN, weight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 1 || h < 1) return;
        double radius = Theme.Compact ? 2 : 7;
        dc.DrawRoundedRectangle(Theme.Input, null, new Rect(0, 0, w, h), radius, radius);
        for (int i = 1; i < 4; i++) dc.DrawLine(new Pen(Theme.Line, .5), new Point(w * i / 4, 0), new Point(w * i / 4, h));
        int channels = ShowLuminance && Bins.Length > 3 ? 4 : 3;
        double max = Math.Max(1, Bins.Take(channels).SelectMany(b => b).Max());
        StreamGeometry Curve(double[] bins)
        {
            var path = new StreamGeometry(); using (var g = path.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                for (int x = 0; x < 256; x++) g.LineTo(new Point(x * w / 255, h - Math.Sqrt(bins[x] / max) * (h - 5)), true, false);
                g.LineTo(new Point(w, h), true, false);
            }
            return path;
        }
        if (channels == 4) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)), null, Curve(Bins[3]));
        Color[] colors = [Color.FromArgb(115, 248, 134, 145), Color.FromArgb(100, 110, 226, 173), Color.FromArgb(130, 120, 178, 255)];
        for (int c = 0; c < 3; c++)
        {
            // Over the luminance area the channels are outlines only, so the gray stays readable.
            var fill = channels == 4 ? null : new SolidColorBrush(colors[c]);
            dc.DrawGeometry(fill, new Pen(new SolidColorBrush(colors[c] with { A = 200 }), channels == 4 ? 1 : .7), Curve(Bins[c]));
        }
    }
}
