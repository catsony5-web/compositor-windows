using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class AdvancedFilters
{
    public static Raster MotionBlur(Layer layer, double angle, int distance, Selection? selection = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(angle) || distance < 0 || distance > 512) throw new ArgumentOutOfRangeException(nameof(distance));
        var source = layer.Pixels; var result = source.Clone(); if (distance == 0) return result;
        double dx = Math.Cos(angle * Math.PI / 180), dy = Math.Sin(angle * Math.PI / 180);
        int samples = Math.Max(2, distance + 1);
        Parallel.For(0, source.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                var p = layer.Document(new Point(x + .5, y + .5)); double weight = selection?.Weight(p.X, p.Y) ?? 1; if (weight <= 0) continue;
                double a = 0, b = 0, g = 0, r = 0;
                for (int s = 0; s < samples; s++)
                {
                    double offset = (s / (samples - 1.0) - .5) * distance;
                    var sample = RetouchTools.Sample(source, x + dx * offset, y + dy * offset, true);
                    double alpha = sample.A / 255; a += alpha; b += sample.B * alpha; g += sample.G * alpha; r += sample.R * alpha;
                }
                var blurred = a <= 0 ? default : new RetouchTools.Pixel(b / a, g / a, r / a, a / samples * 255);
                RetouchTools.BlendPixel(source, result, (y * source.Width + x) * 4, blurred, weight);
            }
        });
        return result;
    }

    public static Raster AddNoise(Layer layer, double amount, int seed = 1, bool monochrome = true, Selection? selection = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(amount) || amount < 0 || amount > 100) throw new ArgumentOutOfRangeException(nameof(amount));
        var source = layer.Pixels; var result = source.Clone();
        Parallel.For(0, source.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                var p = layer.Document(new Point(x + .5, y + .5)); double weight = selection?.Weight(p.X, p.Y) ?? 1;
                int i = (y * source.Width + x) * 4; if (weight <= 0 || source.Data[i + 3] == 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    // Counter-based noise is reproducible regardless of thread
                    // scheduling. Triangular grain avoids a systematic color bias.
                    uint index = unchecked((uint)(i + (monochrome ? 0 : c) + seed * 7919));
                    double noise = (Random01(index) + Random01(index ^ 0x9e3779b9) - 1) * amount * 2.55;
                    double changed = Math.Clamp(source.Data[i + c] + noise, 0, 255);
                    result.Data[i + c] = Imaging.Byte(source.Data[i + c] * (1 - weight) + changed * weight);
                }
            }
        });
        return result;
    }

    static double Random01(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; value ^= value >> 16; return value / (double)uint.MaxValue;
    }

    /// <param name="distortion">Radial barrel/pincushion coefficient, -1 to 1.</param>
    /// <param name="zoom">Image zoom, 0.1 to 10; 1 leaves framing unchanged.</param>
    public static Raster LensCorrection(Layer layer, double distortion, double zoom = 1, Selection? selection = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(distortion) || distortion < -1 || distortion > 1 || !double.IsFinite(zoom) || zoom < .1 || zoom > 10) throw new ArgumentOutOfRangeException(nameof(distortion));
        var source = layer.Pixels; var result = source.Clone();
        double cx = (source.Width - 1) / 2.0, cy = (source.Height - 1) / 2.0, scale = Math.Max(1, Math.Max(source.Width, source.Height) / 2.0);
        Parallel.For(0, source.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                var p = layer.Document(new Point(x + .5, y + .5)); double weight = selection?.Weight(p.X, p.Y) ?? 1; if (weight <= 0) continue;
                double nx = (x - cx) / scale / zoom, ny = (y - cy) / scale / zoom, factor = 1 + distortion * (nx * nx + ny * ny);
                var sample = RetouchTools.Sample(source, cx + nx * factor * scale, cy + ny * factor * scale);
                RetouchTools.BlendPixel(source, result, (y * source.Width + x) * 4, sample, weight);
            }
        });
        return result;
    }

    public static Raster HueSaturation(Raster source, double hue, double saturation, double lightness = 0)
    {
        if (!double.IsFinite(hue) || !double.IsFinite(saturation) || !double.IsFinite(lightness)) throw new ArgumentOutOfRangeException(nameof(hue));
        saturation = Math.Clamp(saturation, -100, 100) / 100; lightness = Math.Clamp(lightness, -100, 100) / 100;
        double hueShift = (hue % 360) / 360;
        return Map(source, (b, g, r) =>
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
            double l = (max + min) / 2, s = chroma == 0 ? 0 : chroma / (1 - Math.Abs(2 * l - 1)), h = 0;
            if (chroma > 0) h = max == r ? ((g - b) / chroma) / 6 : max == g ? ((b - r) / chroma + 2) / 6 : ((r - g) / chroma + 4) / 6;
            h = (h + hueShift + 2) % 1; s = Math.Clamp(s * (1 + saturation), 0, 1);
            l = lightness >= 0 ? l + (1 - l) * lightness : l * (1 + lightness);
            double c = (1 - Math.Abs(2 * l - 1)) * s, sector = h * 6, xx = c * (1 - Math.Abs(sector % 2 - 1)), m = l - c / 2;
            (double rr, double gg, double bb) = sector switch { < 1 => (c, xx, 0.0), < 2 => (xx, c, 0.0), < 3 => (0.0, c, xx), < 4 => (0.0, xx, c), < 5 => (xx, 0.0, c), _ => (c, 0.0, xx) };
            return (bb + m, gg + m, rr + m);
        });
    }

    /// <summary>Piecewise-linear RGB curve. Control points use 0..255 axes.</summary>
    public static Raster Curves(Raster source, IReadOnlyList<Point> controlPoints)
    {
        if (controlPoints.Count < 2 || controlPoints.Count > 256 || controlPoints.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 255 || p.Y < 0 || p.Y > 255))
            throw new ArgumentException("곡선은 0~255 범위의 두 개 이상 점이 필요합니다.");
        var points = controlPoints.OrderBy(p => p.X).ToArray();
        for (int i = 1; i < points.Length; i++) if (points[i].X <= points[i - 1].X) throw new ArgumentException("곡선 입력 좌표는 중복될 수 없습니다.");
        var lookup = new byte[256]; int segment = 0;
        for (int v = 0; v < 256; v++)
        {
            if (v <= points[0].X) lookup[v] = Imaging.Byte(points[0].Y);
            else if (v >= points[^1].X) lookup[v] = Imaging.Byte(points[^1].Y);
            else
            {
                while (segment + 1 < points.Length - 1 && v > points[segment + 1].X) segment++;
                var a = points[segment]; var b = points[segment + 1]; lookup[v] = Imaging.Byte(a.Y + (b.Y - a.Y) * (v - a.X) / (b.X - a.X));
            }
        }
        var result = source.Clone();
        for (int i = 0; i < result.Data.Length; i += 4) for (int c = 0; c < 3; c++) result.Data[i + c] = lookup[source.Data[i + c]];
        return result;
    }

    public static Raster GradientMap(Raster source, Color shadows, Color highlights)
    {
        return Map(source, (b, g, r) =>
        {
            double l = .2126 * r + .7152 * g + .0722 * b;
            return ((shadows.B * (1 - l) + highlights.B * l) / 255, (shadows.G * (1 - l) + highlights.G * l) / 255, (shadows.R * (1 - l) + highlights.R * l) / 255);
        });
    }

    public static Raster ApplySelection(Layer layer, Raster filtered, Selection? selection)
    {
        if (filtered.Width != layer.Pixels.Width || filtered.Height != layer.Pixels.Height) throw new ArgumentException("필터 결과 크기가 다릅니다.");
        var source = layer.Pixels; var result = source.Clone();
        for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
        {
            var p = layer.Document(new Point(x + .5, y + .5)); int i = (y * source.Width + x) * 4;
            RetouchTools.BlendPixel(source, result, i, new RetouchTools.Pixel(filtered.Data[i], filtered.Data[i + 1], filtered.Data[i + 2], filtered.Data[i + 3]), selection?.Weight(p.X, p.Y) ?? 1);
        }
        return result;
    }

    static Raster Map(Raster source, Func<double, double, double, (double B, double G, double R)> operation)
    {
        var result = source.Clone();
        for (int i = 0; i < source.Data.Length; i += 4)
        {
            var color = operation(source.Data[i] / 255.0, source.Data[i + 1] / 255.0, source.Data[i + 2] / 255.0);
            result.Data[i] = Imaging.Byte(color.B * 255); result.Data[i + 1] = Imaging.Byte(color.G * 255); result.Data[i + 2] = Imaging.Byte(color.R * 255);
        }
        return result;
    }
}
