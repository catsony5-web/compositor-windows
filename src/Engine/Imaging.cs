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
        var blended = BlendRgb(dst[di + 2] / 255.0, dst[di + 1] / 255.0, dst[di] / 255.0, r, g, b, mode);
        double Channel(double source, byte dest, double blend)
        {
            double back = dest / 255.0;
            return ((1 - alpha) * da * back + (1 - da) * alpha * source + alpha * da * blend) / oa * 255;
        }
        dst[di] = Byte(Channel(b, dst[di], blended.B)); dst[di + 1] = Byte(Channel(g, dst[di + 1], blended.G)); dst[di + 2] = Byte(Channel(r, dst[di + 2], blended.R)); dst[di + 3] = Byte(oa * 255);
    }
    public static (double R, double G, double B) BlendRgb(double br, double bg, double bb, double sr, double sg, double sb, BlendMode mode)
    {
        if (mode < BlendMode.Hue) return (Blend(br, sr, mode), Blend(bg, sg, mode), Blend(bb, sb, mode));
        // W3C nonseparable blend operations preserve luminance instead of HSL lightness.
        static double Lum((double R, double G, double B) c) => .3 * c.R + .59 * c.G + .11 * c.B;
        static double Sat((double R, double G, double B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        static (double R, double G, double B) SetLum((double R, double G, double B) c, double l)
        {
            double d = l - Lum(c); c = (c.R + d, c.G + d, c.B + d);
            double n = Math.Min(c.R, Math.Min(c.G, c.B)), x = Math.Max(c.R, Math.Max(c.G, c.B));
            if (n < 0) c = (l + (c.R - l) * l / (l - n), l + (c.G - l) * l / (l - n), l + (c.B - l) * l / (l - n));
            if (x > 1) c = (l + (c.R - l) * (1 - l) / (x - l), l + (c.G - l) * (1 - l) / (x - l), l + (c.B - l) * (1 - l) / (x - l));
            return c;
        }
        static (double R, double G, double B) SetSat((double R, double G, double B) c, double s)
        {
            double min = Math.Min(c.R, Math.Min(c.G, c.B)), max = Math.Max(c.R, Math.Max(c.G, c.B));
            return max > min ? ((c.R - min) * s / (max - min), (c.G - min) * s / (max - min), (c.B - min) * s / (max - min)) : (0, 0, 0);
        }
        var back = (br, bg, bb); var source = (sr, sg, sb);
        return mode switch { BlendMode.Hue => SetLum(SetSat(source, Sat(back)), Lum(back)), BlendMode.Saturation => SetLum(SetSat(back, Sat(source)), Lum(back)), BlendMode.Color => SetLum(source, Lum(back)), _ => SetLum(back, Lum(source)) };
    }
    public static Raster Render(Document doc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (doc.Layers.Any(l => DrawingLayers.IsContainer(l) || l.Kind == LayerKind.Material)) return DesignRenderer.RenderOutput(doc, cancellationToken);
        var root = doc.Layers.Where(l => l.ParentId == null).ToArray();
        var children = doc.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var directGroups = new Dictionary<(Guid Id, int Width, int Height), bool>();
        bool CanCompositeGroupDirectly(Layer group, int width, int height, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
            cancellationToken.ThrowIfCancellationRequested();
            var key = (group.Id, width, height);
            if (directGroups.TryGetValue(key, out var cached)) return cached;
            bool direct = group.Opacity == 1 && group.Blend == BlendMode.Normal && !group.Clipped &&
                group.Mask == null && group.Warp == null && group.Matrix.IsIdentity &&
                group.Pixels.Width == width && group.Pixels.Height == height;
            if (direct)
            {
                foreach (var child in children.GetValueOrDefault(group.Id) ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Normal source-over is associative. Any operation that
                    // reads the group's isolated backdrop must keep that surface.
                    if (child.Clipped || child.Blend != BlendMode.Normal || child.Kind == LayerKind.Adjustment ||
                        child.Kind == LayerKind.Group && !CanCompositeGroupDirectly(child, width, height, depth + 1))
                    { direct = false; break; }
                }
            }
            directGroups[key] = direct;
            return direct;
        }
        Raster LayerImage(Layer layer, int width, int height, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
            var copy = layer.Snapshot(); copy.Opacity = 1; copy.Blend = BlendMode.Normal;
            if (layer.Kind == LayerKind.Group) copy.Pixels = Stack(children.GetValueOrDefault(layer.Id) ?? [], layer.Pixels.Width, layer.Pixels.Height, depth + 1);
            var rendered = new Raster(width, height); Composite(rendered, copy, cancellationToken); return rendered;
        }
        Raster Stack(Layer[] stack, int width, int height, int depth)
        {
            var output = new Raster(width, height);
            CompositeStack(stack, output, depth);
            return output;
        }
        void CompositeStack(Layer[] stack, Raster output, int depth)
        {
            int width = output.Width, height = output.Height;
            for (int index = 0; index < stack.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var layer = stack[index];
                if (layer.Clipped) continue; // No underlying base in this stack.
                int end = index + 1; while (end < stack.Length && stack[end].Clipped) end++;
                if (!layer.Visible || layer.Opacity <= 0) { index = end - 1; continue; }
                if (layer.Kind == LayerKind.Adjustment) ApplyAdjustment(output, layer, cancellationToken);
                else if (end == index + 1 && layer.Kind != LayerKind.Group) Composite(output, layer, cancellationToken);
                else if (end == index + 1 && CanCompositeGroupDirectly(layer, width, height, depth))
                {
                    // Imported CAD groups are identity containers for tight
                    // object rasters. Reuse the destination rather than create
                    // two full-canvas intermediates for every source-layer run.
                    CompositeStack(children.GetValueOrDefault(layer.Id) ?? [], output, depth + 1);
                }
                else
                {
                    // An un-clipped layer and its following clipped layers form an alpha-preserving stack.
                    var image = LayerImage(layer, width, height, depth);
                    for (int j = index + 1; j < end; j++)
                    {
                        var clip = stack[j]; if (!clip.Visible || clip.Opacity <= 0) continue;
                        if (clip.Kind == LayerKind.Adjustment) ApplyAdjustment(image, clip, cancellationToken);
                        else Merge(image, LayerImage(clip, width, height, depth), clip.Opacity, clip.Blend, true, cancellationToken);
                    }
                    Merge(output, image, layer.Opacity, layer.Blend, false, cancellationToken);
                }
                index = end - 1;
            }
        }
        return Stack(root, doc.Width, doc.Height, 0);
    }
    internal static void Merge(Raster target, Raster source, double opacity, BlendMode blend, bool clipped, CancellationToken token)
    {
        Parallel.For(0, target.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < target.Width; x++)
            {
                int i = (y * target.Width + x) * 4; double a = source.Data[i + 3] / 255.0 * opacity;
                if (!clipped) Over(target.Data, i, source.Data[i] / 255.0, source.Data[i + 1] / 255.0, source.Data[i + 2] / 255.0, a, blend);
                else if (target.Data[i + 3] > 0 && a > 0)
                {
                    var color = BlendRgb(target.Data[i + 2] / 255.0, target.Data[i + 1] / 255.0, target.Data[i] / 255.0, source.Data[i + 2] / 255.0, source.Data[i + 1] / 255.0, source.Data[i] / 255.0, blend);
                    target.Data[i] = Byte(target.Data[i] * (1 - a) + color.B * a * 255); target.Data[i + 1] = Byte(target.Data[i + 1] * (1 - a) + color.G * a * 255); target.Data[i + 2] = Byte(target.Data[i + 2] * (1 - a) + color.R * a * 255);
                }
            }
        });
    }
    internal static void ApplyAdjustment(Raster target, Layer layer, CancellationToken token, Matrix? transform = null)
    {
        var adjusted = DocumentFeatures.ApplyAdjustment(target, layer.Adjustment!, token);
        var coverageLayer = layer.Snapshot(); coverageLayer.Pixels = Raster.Solid(layer.Pixels.Width, layer.Pixels.Height, Colors.White); coverageLayer.Blend = BlendMode.Normal; coverageLayer.Opacity = 1;
        var coverage = new Raster(target.Width, target.Height); Composite(coverage, coverageLayer, token, transform);
        Parallel.For(0, target.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < target.Width; x++)
            {
                int i = (y * target.Width + x) * 4; double a = coverage.Data[i + 3] / 255.0 * layer.Opacity;
                var rgb = BlendRgb(target.Data[i + 2] / 255.0, target.Data[i + 1] / 255.0, target.Data[i] / 255.0, adjusted.Data[i + 2] / 255.0, adjusted.Data[i + 1] / 255.0, adjusted.Data[i] / 255.0, layer.Blend);
                target.Data[i] = Byte(target.Data[i] * (1 - a) + rgb.B * a * 255); target.Data[i + 1] = Byte(target.Data[i + 1] * (1 - a) + rgb.G * a * 255); target.Data[i + 2] = Byte(target.Data[i + 2] * (1 - a) + rgb.R * a * 255);
            }
        });
    }
    public static void Composite(Raster output, Layer layer, CancellationToken cancellationToken = default, Matrix? transform = null)
    {
        if (layer.Kind == LayerKind.Shape)
        {
            var shape = layer.Shape ?? throw new System.IO.InvalidDataException("도형 정보가 없습니다.");
            shape.Validate();
            if (shape.Width != layer.Pixels.Width || shape.Height != layer.Pixels.Height)
                throw new System.IO.InvalidDataException("도형의 크기와 레이어 이미지 크기가 다릅니다.");
            VectorShapes.Composite(output, layer, cancellationToken, transform); return;
        }
        var src = layer.Pixels; var mask = layer.Mask; var map = transform ?? layer.Matrix;
        var forward = map;
        var corners = new[] { new Point(0, 0), new Point(src.Width, 0), new Point(src.Width, src.Height), new Point(0, src.Height) }.Select(p => forward.Transform(layer.Warp?.Forward(p, src.Width, src.Height) ?? p)).ToArray();
        int sampleFactorX = 1, sampleFactorY = 1;
        // Pre-filter minification in premultiplied space. Bilinear alone aliases fine details.
        if (layer.Warp == null)
        {
            double scaleX = Math.Sqrt(map.M11 * map.M11 + map.M12 * map.M12), scaleY = Math.Sqrt(map.M21 * map.M21 + map.M22 * map.M22);
            while (sampleFactorX < 64 && sampleFactorX * 2 * scaleX <= 1) sampleFactorX *= 2;
            while (sampleFactorY < 64 && sampleFactorY * 2 * scaleY <= 1) sampleFactorY *= 2;
            if (sampleFactorX > 1 || sampleFactorY > 1) { src = DownsampleForRender(src, mask, sampleFactorX, sampleFactorY, cancellationToken); mask = null; }
        }
        int left = (int)Math.Clamp(Math.Floor(corners.Min(p => p.X)) - 1, 0, output.Width), top = (int)Math.Clamp(Math.Floor(corners.Min(p => p.Y)) - 1, 0, output.Height);
        int right = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.X)) + 1, 0, output.Width), bottom = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.Y)) + 1, 0, output.Height);
        map.Invert(); var inverseWarp = layer.Warp?.Map().Inverse();
        Parallel.For(top, bottom, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = left; x < right; x++)
            {
                var p = map.Transform(new Point(x + .5, y + .5));
                if (inverseWarp is { } warp) { p = warp.Transform(p); p = new Point(p.X * src.Width, p.Y * src.Height); }
                else if (sampleFactorX > 1 || sampleFactorY > 1) p = new Point(p.X / sampleFactorX, p.Y / sampleFactorY);
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < -.5 || p.Y < -.5 || p.X > src.Width + .5 || p.Y > src.Height + .5) continue;
                // Transparent extension, rather than a hard geometric cutoff, gives subpixel edge coverage.
                double sx = p.X - .5, sy = p.Y - .5; int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0, a = 0, b = 0, g = 0, r = 0;
                void Sample(int xx, int yy, double weight)
                {
                    if (xx < 0 || yy < 0 || xx >= src.Width || yy >= src.Height || weight <= 0) return;
                    int index = yy * src.Width + xx, i = index * 4;
                    double aa = src.Data[i + 3] / 255.0 * (mask == null ? 1 : mask[index] / 255.0) * weight;
                    a += aa; b += src.Data[i] / 255.0 * aa; g += src.Data[i + 1] / 255.0 * aa; r += src.Data[i + 2] / 255.0 * aa;
                }
                Sample(x0, y0, (1 - fx) * (1 - fy)); Sample(x0 + 1, y0, fx * (1 - fy)); Sample(x0, y0 + 1, (1 - fx) * fy); Sample(x0 + 1, y0 + 1, fx * fy);
                if (a > 0) Over(output.Data, (y * output.Width + x) * 4, b / a, g / a, r / a, a * layer.Opacity, layer.Blend);
            }
        });
    }
    static Raster DownsampleForRender(Raster source, byte[]? mask, int factorX, int factorY, CancellationToken token)
    {
        var result = new Raster((source.Width + factorX - 1) / factorX, (source.Height + factorY - 1) / factorY);
        Parallel.For(0, result.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < result.Width; x++)
            {
                double a = 0, b = 0, g = 0, r = 0;
                for (int yy = y * factorY; yy < Math.Min(source.Height, (y + 1) * factorY); yy++)
                for (int xx = x * factorX; xx < Math.Min(source.Width, (x + 1) * factorX); xx++)
                {
                    int pixel = yy * source.Width + xx, i = pixel * 4; double alpha = source.Data[i + 3] / 255.0 * (mask == null ? 1 : mask[pixel] / 255.0);
                    a += alpha; b += source.Data[i] * alpha; g += source.Data[i + 1] * alpha; r += source.Data[i + 2] * alpha;
                }
                int di = (y * result.Width + x) * 4;
                if (a > 0) { result.Data[di] = Byte(b / a); result.Data[di + 1] = Byte(g / a); result.Data[di + 2] = Byte(r / a); }
                result.Data[di + 3] = Byte(a * 255 / (factorX * factorY));
            }
        }); return result;
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
        var src = layer.Pixels; var result = src.Clone();
        Parallel.For(0, src.Height, y =>
        {
            for (int x = 0; x < src.Width; x++)
            {
                var point = layer.Document(new Point(x + .5, y + .5));
                double coverage = selection?.Weight(point.X, point.Y) ?? 1; if (coverage <= 0) continue;
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
                result.Data[i] = Byte(src.Data[i] * (1 - coverage) + Math.Clamp(blue, 0, 1) * coverage * 255); result.Data[i + 1] = Byte(src.Data[i + 1] * (1 - coverage) + Math.Clamp(green, 0, 1) * coverage * 255); result.Data[i + 2] = Byte(src.Data[i + 2] * (1 - coverage) + Math.Clamp(red, 0, 1) * coverage * 255);
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
        var result = source.Clone();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var p = layer.Document(new Point(x + .5, y + .5));
                double coverage = selection?.Weight(p.X, p.Y) ?? 1; if (coverage <= 0) continue;
                double b = 0, g = 0, r = 0, a = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int si = (Math.Clamp(y + k, 0, h - 1) * w + x) * 4; double wt = kernel[k + radius];
                    b += temp[si] * wt; g += temp[si + 1] * wt; r += temp[si + 2] * wt; a += temp[si + 3] * wt;
                }
                int di = (y * w + x) * 4;
                // Interpolate the selected effect in premultiplied space as its alpha changes.
                double originalAlpha = source.Data[di + 3], outAlpha = originalAlpha * (1 - coverage) + a * coverage;
                result.Data[di] = outAlpha > 0 ? Byte((source.Data[di] * originalAlpha * (1 - coverage) + b * 255 * coverage) / outAlpha) : (byte)0;
                result.Data[di + 1] = outAlpha > 0 ? Byte((source.Data[di + 1] * originalAlpha * (1 - coverage) + g * 255 * coverage) / outAlpha) : (byte)0;
                result.Data[di + 2] = outAlpha > 0 ? Byte((source.Data[di + 2] * originalAlpha * (1 - coverage) + r * 255 * coverage) / outAlpha) : (byte)0; result.Data[di + 3] = Byte(outAlpha);
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
    readonly BrushTip tip;
    readonly double spacing, cos, sin;
    readonly bool legacyRound;
    double nextDabDistance;
    Point? previous;
    public BrushStroke(Layer layer, Selection? selection, Color color, double diameter, double hardness, double opacity, bool erase, bool mask,
        BrushTip? tip = null, double spacing = .10, double angleDeg = 0)
    {
        this.layer = layer; this.selection = selection; this.color = color;
        this.hardness = double.IsFinite(hardness) ? Math.Clamp(hardness, 0, 1) : 1;
        this.opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1; this.erase = erase;
        this.mask = mask && layer.Mask != null; radius = double.IsFinite(diameter) ? Math.Clamp(diameter, 1, 8192) / 2 : .5;
        this.tip = tip ?? BrushTip.Round;
        this.spacing = Math.Max(.5, radius * 2 * (double.IsFinite(spacing) ? Math.Clamp(spacing, .01, 1.5) : .10));
        legacyRound = ReferenceEquals(this.tip, BrushTip.Round) && spacing == .10;
        double angle = (double.IsFinite(angleDeg) ? angleDeg % 360 : 0) * Math.PI / 180;
        cos = Math.Cos(angle); sin = Math.Sin(angle); nextDabDistance = this.spacing;
        original = layer.Pixels; originalMask = layer.Mask;
        coverage = new float[original.Width * original.Height];
        if (this.mask) layer.Mask = (byte[])layer.Mask!.Clone(); else layer.Pixels = original.Clone();
    }
    // Ported from BrushRaster.falloff. Whole-stroke maximum coverage preserves opacity on overlap.
    public static double Falloff(double u) => Math.Max(0, (Math.Exp(-2.5 * u * u) - Math.Exp(-2.5)) / (1 - Math.Exp(-2.5)));
    public void Point(Point documentPoint)
    {
        if (!double.IsFinite(documentPoint.X) || !double.IsFinite(documentPoint.Y)) return;
        var point = documentPoint;
        if (previous is { } start)
        {
            var delta = point - start;
            if (legacyRound)
            {
                int steps = Math.Max(1, (int)Math.Ceiling(delta.Length / Math.Max(.5, radius * .2)));
                for (int s = 1; s <= steps; s++) Dab(start + delta * (s / (double)steps));
            }
            else if (delta.Length > 0)
            {
                // Carry unused distance through pointer events so stamp positions do not depend on event frequency.
                double length = delta.Length, distance = nextDabDistance;
                while (distance <= length + 1e-9) { Dab(start + delta * (Math.Min(distance, length) / length)); distance += spacing; }
                nextDabDistance = distance - length;
            }
        }
        else Dab(point);
        previous = point;
    }
    void Dab(Point point)
    {
        int w = original.Width, h = original.Height;
        double extent = ReferenceEquals(tip, BrushTip.Round) ? radius : radius * (Math.Abs(cos) + Math.Abs(sin));
        var bounds = new[] { new Point(point.X - extent, point.Y - extent), new Point(point.X + extent, point.Y - extent), new Point(point.X + extent, point.Y + extent), new Point(point.X - extent, point.Y + extent) }.Select(layer.Local).ToArray();
        if (bounds.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return;
        int left = (int)Math.Clamp(Math.Floor(bounds.Min(p => p.X)), 0, w), right = (int)Math.Clamp(Math.Ceiling(bounds.Max(p => p.X)), -1, w - 1);
        int top = (int)Math.Clamp(Math.Floor(bounds.Min(p => p.Y)), 0, h), bottom = (int)Math.Clamp(Math.Ceiling(bounds.Max(p => p.Y)), -1, h - 1);
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            var docPoint = layer.Document(new Point(x + .5, y + .5));
            double selected = selection?.Weight(docPoint.X, docPoint.Y) ?? 1; if (selected <= 0) continue;
            var offset = docPoint - point;
            double shapeCoverage;
            if (ReferenceEquals(tip, BrushTip.Round))
            {
                // Keep the original round-brush arithmetic and overlap behavior exactly.
                double distance = offset.Length / radius;
                if (distance > 1) continue;
                shapeCoverage = distance <= hardness ? 1 : Falloff((distance - hardness) / (1 - hardness));
            }
            else shapeCoverage = tip.Sample((offset.X * cos + offset.Y * sin) / radius, (-offset.X * sin + offset.Y * cos) / radius, hardness);
            double amount = shapeCoverage * opacity * selected;
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
