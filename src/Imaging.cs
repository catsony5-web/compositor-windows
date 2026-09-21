using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class Imaging
{
    public static byte Byte(double n) => (byte)Math.Clamp((int)Math.Round(n), 0, 255);
    public static double Blend(double b, double s, BlendMode mode) => mode switch
    {
        BlendMode.Multiply => b * s,
        BlendMode.Screen => b + s - b * s,
        BlendMode.Overlay => b <= .5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s),
        BlendMode.SoftLight => s <= .5 ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * ((b <= .25 ? ((16 * b - 12) * b + 4) * b : Math.Sqrt(b)) - b),
        BlendMode.Darken => Math.Min(b, s), BlendMode.Lighten => Math.Max(b, s),
        BlendMode.Difference => Math.Abs(b - s),
        BlendMode.ColorDodge => b == 0 ? 0 : s == 1 ? 1 : Math.Min(1, b / (1 - s)),
        BlendMode.ColorBurn => b == 1 ? 1 : s == 0 ? 0 : 1 - Math.Min(1, (1 - b) / s),
        _ => s
    };
    public static void Over(byte[] dst, int di, double b, double g, double r, double alpha, BlendMode mode = BlendMode.Normal)
    {
        if (alpha <= 0) return;
        double da = dst[di + 3] / 255.0, oa = alpha + da * (1 - alpha);
        double Channel(double source, byte dest)
        {
            double back = dest / 255.0;
            return ((1 - alpha) * da * back + (1 - da) * alpha * source + alpha * da * Blend(back, source, mode)) / oa * 255;
        }
        dst[di] = Byte(Channel(b, dst[di])); dst[di + 1] = Byte(Channel(g, dst[di + 1])); dst[di + 2] = Byte(Channel(r, dst[di + 2])); dst[di + 3] = Byte(oa * 255);
    }
    public static Raster Render(Document doc)
    {
        var output = new Raster(doc.Width, doc.Height);
        foreach (var layer in doc.Layers.Where(l => l.Visible && l.Opacity > 0)) Composite(output, layer);
        return output;
    }
    public static void Composite(Raster output, Layer layer)
    {
        var src = layer.Pixels; var map = layer.Matrix;
        var corners = new[] { new Point(0, 0), new Point(src.Width, 0), new Point(src.Width, src.Height), new Point(0, src.Height) }.Select(map.Transform).ToArray();
        int left = Math.Clamp((int)Math.Floor(corners.Min(p => p.X)), 0, output.Width);
        int top = Math.Clamp((int)Math.Floor(corners.Min(p => p.Y)), 0, output.Height);
        int right = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X)), 0, output.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.Y)), 0, output.Height);
        map.Invert();
        Parallel.For(top, bottom, y =>
        {
            for (int x = left; x < right; x++)
            {
                var p = map.Transform(new Point(x + .5, y + .5));
                if (p.X < 0 || p.Y < 0 || p.X >= src.Width || p.Y >= src.Height) continue;
                // Bilinear sampling in premultiplied space avoids dark fringes at transparent edges.
                double sx = Math.Clamp(p.X - .5, 0, src.Width - 1), sy = Math.Clamp(p.Y - .5, 0, src.Height - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, src.Width - 1), y1 = Math.Min(y0 + 1, src.Height - 1);
                double fx = sx - x0, fy = sy - y0, a = 0, b = 0, g = 0, r = 0;
                void Sample(int xx, int yy, double weight)
                {
                    int index = yy * src.Width + xx, i = index * 4;
                    double aa = src.Data[i + 3] / 255.0 * (layer.Mask == null ? 1 : layer.Mask[index] / 255.0) * weight;
                    a += aa; b += src.Data[i] / 255.0 * aa; g += src.Data[i + 1] / 255.0 * aa; r += src.Data[i + 2] / 255.0 * aa;
                }
                Sample(x0, y0, (1 - fx) * (1 - fy)); Sample(x1, y0, fx * (1 - fy)); Sample(x0, y1, (1 - fx) * fy); Sample(x1, y1, fx * fy);
                if (a > 0) Over(output.Data, (y * output.Width + x) * 4, b / a, g / a, r / a, a * layer.Opacity, layer.Blend);
            }
        });
    }
    // Direct C# translation of LevelRange.normalized/apply in upstream Document/Levels.swift.
    public static double Level(double value, double black, double white, double gamma, double outputBlack = 0, double outputWhite = 255)
    {
        black = double.IsFinite(black) ? Math.Clamp(black, 0, 254) : 0;
        white = double.IsFinite(white) ? Math.Clamp(white, black + 1, 255) : 255;
        gamma = double.IsFinite(gamma) ? Math.Clamp(gamma, .1, 9.99) : 1;
        outputBlack = double.IsFinite(outputBlack) ? Math.Clamp(outputBlack, 0, 255) : 0;
        outputWhite = double.IsFinite(outputWhite) ? Math.Clamp(outputWhite, 0, 255) : 255;
        return (outputBlack + Math.Pow(Math.Clamp((value * 255 - black) / (white - black), 0, 1), 1 / gamma) * (outputWhite - outputBlack)) / 255;
    }
    public static Raster Adjust(Layer layer, Selection? selection, string kind, double a = 0, double b = 255, double c = 1)
    {
        var src = layer.Pixels; var result = src.Clone(); var matrix = layer.Matrix;
        Parallel.For(0, src.Height, y =>
        {
            for (int x = 0; x < src.Width; x++)
            {
                var point = matrix.Transform(new Point(x + .5, y + .5));
                if (selection != null && !selection.Contains(point.X, point.Y)) continue;
                int i = (y * src.Width + x) * 4;
                double blue = src.Data[i] / 255.0, green = src.Data[i + 1] / 255.0, red = src.Data[i + 2] / 255.0;
                if (kind == "levels") { blue = Level(blue, a, b, c); green = Level(green, a, b, c); red = Level(red, a, b, c); }
                else if (kind == "invert") { blue = 1 - blue; green = 1 - green; red = 1 - red; }
                else if (kind == "grayscale") { blue = green = red = .2126 * red + .7152 * green + .0722 * blue; }
                else if (kind == "exposure") { var multiplier = Math.Pow(2, a); blue *= multiplier; green *= multiplier; red *= multiplier; }
                else if (kind == "saturation")
                {
                    double luminance = .2126 * red + .7152 * green + .0722 * blue, amount = a / 100 + 1;
                    blue = luminance + (blue - luminance) * amount; green = luminance + (green - luminance) * amount; red = luminance + (red - luminance) * amount;
                }
                result.Data[i] = Byte(blue * 255); result.Data[i + 1] = Byte(green * 255); result.Data[i + 2] = Byte(red * 255);
            }
        });
        return result;
    }
    public static Raster Blur(Layer layer, int radius, Selection? selection)
    {
        var source = layer.Pixels; int w = source.Width, h = source.Height;
        radius = Math.Clamp(radius, 1, 30);
        double sigma = Math.Max(.5, radius / 2.0);
        var kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(x => Math.Exp(-x * x / (2 * sigma * sigma))).ToArray();
        double total = kernel.Sum(); for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        var temp = new float[source.Data.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++) for (int k = -radius; k <= radius; k++)
            {
                int si = (y * w + Math.Clamp(x + k, 0, w - 1)) * 4, di = (y * w + x) * 4;
                double weight = kernel[k + radius], alpha = source.Data[si + 3] / 255.0;
                for (int ch = 0; ch < 3; ch++) temp[di + ch] += (float)(source.Data[si + ch] * alpha * weight);
                temp[di + 3] += (float)(source.Data[si + 3] * weight);
            }
        });
        var result = source.Clone(); var map = layer.Matrix;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var p = map.Transform(new Point(x + .5, y + .5));
                if (selection != null && !selection.Contains(p.X, p.Y)) continue;
                double b = 0, g = 0, r = 0, a = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int si = (Math.Clamp(y + k, 0, h - 1) * w + x) * 4; double wt = kernel[k + radius];
                    b += temp[si] * wt; g += temp[si + 1] * wt; r += temp[si + 2] * wt; a += temp[si + 3] * wt;
                }
                int di = (y * w + x) * 4;
                result.Data[di] = a > 0 ? Byte(b * 255 / a) : (byte)0;
                result.Data[di + 1] = a > 0 ? Byte(g * 255 / a) : (byte)0;
                result.Data[di + 2] = a > 0 ? Byte(r * 255 / a) : (byte)0; result.Data[di + 3] = Byte(a);
            }
        });
        return result;
    }
    public static Raster Draw(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        return Raster.FromBitmap(bitmap);
    }
}

public sealed class BrushStroke
{
    readonly Layer layer;
    readonly Raster original;
    readonly byte[]? originalMask;
    readonly float[] coverage;
    readonly Selection? selection;
    readonly Color color;
    readonly double radius, hardness, opacity;
    readonly bool erase, mask;
    Point? previous;
    public BrushStroke(Layer layer, Selection? selection, Color color, double diameter, double hardness, double opacity, bool erase, bool mask)
    {
        this.layer = layer; this.selection = selection; this.color = color; this.hardness = hardness; this.opacity = opacity; this.erase = erase;
        this.mask = mask && layer.Mask != null; radius = diameter / 2 / layer.Scale;
        original = layer.Pixels; originalMask = layer.Mask;
        coverage = new float[original.Width * original.Height];
        if (this.mask) layer.Mask = (byte[])layer.Mask!.Clone(); else layer.Pixels = original.Clone();
    }
    // Ported from BrushRaster.falloff. Whole-stroke maximum coverage preserves opacity on overlap.
    public static double Falloff(double u) => Math.Max(0, (Math.Exp(-2.5 * u * u) - Math.Exp(-2.5)) / (1 - Math.Exp(-2.5)));
    public void Point(Point documentPoint)
    {
        var point = layer.Local(documentPoint);
        if (previous is { } start)
        {
            var delta = point - start; int steps = Math.Max(1, (int)Math.Ceiling(delta.Length / Math.Max(.5, radius * .2)));
            for (int s = 1; s <= steps; s++) Dab(start + delta * (s / (double)steps));
        }
        else Dab(point);
        previous = point;
    }
    void Dab(Point point)
    {
        int w = original.Width, h = original.Height;
        int left = Math.Max(0, (int)Math.Floor(point.X - radius)), right = Math.Min(w - 1, (int)Math.Ceiling(point.X + radius));
        int top = Math.Max(0, (int)Math.Floor(point.Y - radius)), bottom = Math.Min(h - 1, (int)Math.Ceiling(point.Y + radius));
        var map = layer.Matrix;
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            var docPoint = map.Transform(new Point(x + .5, y + .5));
            if (selection != null && !selection.Contains(docPoint.X, docPoint.Y)) continue;
            double distance = Math.Sqrt(Math.Pow(x + .5 - point.X, 2) + Math.Pow(y + .5 - point.Y, 2)) / radius;
            if (distance > 1) continue;
            double amount = (distance <= hardness ? 1 : Falloff((distance - hardness) / (1 - hardness))) * opacity;
            int pi = y * w + x, i = pi * 4;
            if (amount <= coverage[pi]) continue;
            coverage[pi] = (float)amount;
            if (mask)
            {
                double value = erase ? 0 : .2126 * color.R + .7152 * color.G + .0722 * color.B;
                layer.Mask![pi] = Imaging.Byte(originalMask![pi] * (1 - amount) + value * amount);
            }
            else if (erase) layer.Pixels.Data[i + 3] = Imaging.Byte(original.Data[i + 3] * (1 - amount));
            else
            {
                Array.Copy(original.Data, i, layer.Pixels.Data, i, 4);
                Imaging.Over(layer.Pixels.Data, i, color.B / 255.0, color.G / 255.0, color.R / 255.0, amount * color.A / 255.0);
            }
        }
    }
}
