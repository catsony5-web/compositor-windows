using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class HistogramView : FrameworkElement
{
    public double[][] Bins { get; private set; } = [new double[256], new double[256], new double[256]];
    public HistogramView() { Height = 90; MinWidth = 180; ToolTip = "현재 합성 이미지의 RGB 분포 · 투명 픽셀 제외"; }
    public void Update(Raster raster)
    {
        var bins = new[] { new double[256], new double[256], new double[256] };
        int pixels = raster.Width * raster.Height, step = Math.Max(1, (int)Math.Ceiling(pixels / 65536.0));
        for (int p = 0; p < pixels; p += step)
        {
            int i = p * 4; double a = raster.Data[i + 3] / 255.0;
            bins[0][raster.Data[i + 2]] += a; bins[1][raster.Data[i + 1]] += a; bins[2][raster.Data[i]] += a;
        }
        Bins = bins; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 1 || h < 1) return;
        dc.DrawRoundedRectangle(Theme.Brush("#14171C"), null, new Rect(0, 0, w, h), 7, 7);
        for (int i = 1; i < 4; i++) dc.DrawLine(new Pen(Theme.Brush("#30353F"), .5), new Point(w * i / 4, 0), new Point(w * i / 4, h));
        double max = Math.Max(1, Bins.SelectMany(b => b).Max());
        Color[] colors = [Color.FromArgb(115, 248, 134, 145), Color.FromArgb(100, 110, 226, 173), Color.FromArgb(130, 120, 178, 255)];
        for (int c = 0; c < 3; c++)
        {
            var path = new StreamGeometry(); using (var g = path.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                for (int x = 0; x < 256; x++) g.LineTo(new Point(x * w / 255, h - Math.Sqrt(Bins[c][x] / max) * (h - 5)), true, false);
                g.LineTo(new Point(w, h), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(colors[c]), new Pen(new SolidColorBrush(colors[c] with { A = 200 }), .7), path);
        }
    }
}
