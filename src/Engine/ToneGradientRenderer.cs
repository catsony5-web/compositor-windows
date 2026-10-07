using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// The gradient screentones (점 그라데이션, 점묘 그라데이션): a density that runs across the region, so the
// fill is evaluated relative to the region instead of repeating a tile. Every device pixel is computed
// at the resolution shown (screen zoom, export scale, layer pixels) and anti-aliased (dot edges by
// supersampling, the smooth grain from its slope), so the marks stay crisp when zoomed; marks smaller
// than a device pixel are drawn as their average, so the tone stays right when zoomed out.
//  • Dot gradient: the round-dot spot function on the dot screens' 45° lattice. Black dots grow round,
//    meet as a checkerboard at 50% and become round holes beyond, without a seam where they turn.
//  • Stipple gradient: thresholded sparse-convolution noise (one bump of random height at a random point
//    per grain cell), read through its own quantiles so the inked share equals the density on average;
//    the seed moves the bumps. Low densities give scattered specks, high ones ink with scattered holes.
// The density runs linearly from Start to End between the region's first and last points along Angle.
// The lattice follows the fill's repeat size, vertical ratio, rotation and offset like a pattern tile.
public static class ToneGradientRenderer
{
    const int TableSize = 4096, CacheEntries = 4;
    const long CacheMaxPixels = 4L * 1024 * 1024, MaxPixels = 16_777_216;
    // Supersampling grids for pixels a dot edge crosses and for grain specks or holes about a pixel in size.
    const int DotSamples = 4, GrainSamples = 4;

    static readonly Lazy<double[]> dotThresholds = new(DotThresholds, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<double[]> grainQuantiles = new(GrainQuantiles, LazyThreadSafetyMode.ExecutionAndPublication);

    // ---- Shared geometry ------------------------------------------------------------------------

    // The region's extent along the gradient direction: the smallest and largest p·(cos θ, sin θ).
    public static (double Min, double Max) Extent(Geometry boundary, double angle)
    {
        var turned = new GeometryGroup(); turned.Children.Add(boundary); turned.Transform = new RotateTransform(-angle);
        var bounds = turned.Bounds;
        return bounds.IsEmpty ? (0, 0) : (bounds.Left, bounds.Right);
    }

    // Requested ink coverage at a layer point: Start at the region's first point along the direction, End at its last.
    public static double CoverageAt(MaterialFill fill, Point point)
    {
        var gradient = ToneGradient.Of(fill); var (min, max) = Extent(fill.Boundary.Geometry, gradient.Angle);
        double radians = gradient.Angle * Math.PI / 180, s = point.X * Math.Cos(radians) + point.Y * Math.Sin(radians);
        double t = max - min > 1e-9 ? Math.Clamp((s - min) / (max - min), 0, 1) : .5;
        return gradient.Start + (gradient.End - gradient.Start) * t;
    }

    // ---- Dot gradient: round-dot spot function ----------------------------------------------------

    // Spot value at lattice coordinates (A sites on integers): 1 at a dot centre, 0 on the 50% diamond,
    // −1 at a hole centre. Its slope is at most 4 per lattice unit.
    internal static double Spot(double x, double y)
    {
        double ex = 2 * (x - Math.Floor(x + .5)), ey = 2 * (y - Math.Floor(y + .5)), ax = Math.Abs(ex), ay = Math.Abs(ey);
        return ax + ay <= 1 ? 1 - (ex * ex + ey * ey) : (ax - 1) * (ax - 1) + (ay - 1) * (ay - 1) - 1;
    }

    // Area share where Spot ≥ threshold: round dots below 39%, dots clipped to the diamond up to 50%, and
    // the mirror image (round holes) beyond.
    internal static double SpotCoverage(double threshold)
    {
        if (threshold < 0) return 1 - SpotCoverage(-threshold);
        if (threshold >= 1) return 0;
        if (threshold >= .5) return Math.PI * (1 - threshold) / 4;
        double rho2 = 1 - threshold, rho = Math.Sqrt(rho2), h = Math.Sqrt(.5);
        double segment = rho2 * Math.Acos(h / rho) - h * Math.Sqrt(rho2 - h * h);
        return (Math.PI * rho2 - 4 * segment) / 4;
    }

    static double[] DotThresholds()
    {
        var table = new double[TableSize + 1];
        for (int i = 0; i <= TableSize; i++)
        {
            double coverage = i / (double)TableSize, low = -1, high = 1;
            for (int step = 0; step < 64; step++) { double middle = (low + high) / 2; if (SpotCoverage(middle) > coverage) low = middle; else high = middle; }
            table[i] = (low + high) / 2;
        }
        // Nothing at 0%, everything at 100%.
        table[0] = 1 + 1e-9; table[TableSize] = -1 - 1e-9;
        return table;
    }

    // Spot threshold that inks `coverage` of a lattice cell.
    internal static double DotThreshold(double coverage) => Lookup(dotThresholds.Value, coverage);

    // ---- Stipple gradient: thresholded grain noise ----------------------------------------------

    static int GrainSeed(int seed) => unchecked(0x5717 + seed * 7919);

    // Grain value at grain coordinates: one bump (1 − d²)², radius one cell, per cell at a random point with
    // a random height in [−1, 1] (one hash per cell). The 3×3 cells around the last cell asked for are kept,
    // so neighbouring pixels reuse them; one instance serves one thread.
    internal sealed class GrainField(int seed)
    {
        readonly double[] cells = new double[27];
        int ix = int.MinValue, iy = int.MinValue;

        public double Value(double x, double y) => Value(x, y, out _);

        // Value and slope (per grain unit).
        public double Value(double x, double y, out double slope)
        {
            int cx0 = (int)Math.Floor(x), cy0 = (int)Math.Floor(y);
            if (cx0 != ix || cy0 != iy) Load(cx0, cy0);
            double sum = 0, gx = 0, gy = 0;
            for (int i = 0; i < 27; i += 3)
            {
                double dx = x - cells[i], dy = y - cells[i + 1], s = dx * dx + dy * dy;
                if (s >= 1) continue;
                double k = 1 - s, height = cells[i + 2];
                sum += height * k * k; gx -= 4 * height * k * dx; gy -= 4 * height * k * dy;
            }
            slope = Math.Sqrt(gx * gx + gy * gy);
            return sum;
        }

        void Load(int x, int y)
        {
            ix = x; iy = y; int i = 0;
            for (int cy = y - 1; cy <= y + 1; cy++)
                for (int cx = x - 1; cx <= x + 1; cx++, i += 3)
                {
                    ulong h = Hash(seed, cx, cy);
                    cells[i] = cx + (h & 0x1FFFFF) * (1.0 / 0x200000); cells[i + 1] = cy + (h >> 21 & 0x1FFFFF) * (1.0 / 0x200000);
                    cells[i + 2] = (h >> 42) * (2.0 / 0x400000) - 1;
                }
        }
    }

    internal static double Grain(int seed, double x, double y) => new GrainField(seed).Value(x, y);

    static ulong Hash(int seed, int x, int y)
    {
        ulong v = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)x * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)y * 0x94D049BB133111EBUL);
        v = unchecked(v + 0x9E3779B97F4A7C15UL);
        v = unchecked((v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL);
        v = unchecked((v ^ (v >> 27)) * 0x94D049BB133111EBUL);
        return v ^ (v >> 31);
    }

    // Quantiles of the grain value, measured once on a fixed sample: inking where the grain is at least the
    // (1 − c) quantile inks a share c of the area. The distribution does not depend on the seed.
    static double[] GrainQuantiles()
    {
        const int cells = 160, per = 4;
        var samples = new double[cells * cells * per * per]; int n = 0; var field = new GrainField(GrainSeed(-1));
        for (int j = 0; j < cells * per; j++)
            for (int i = 0; i < cells * per; i++)
                samples[n++] = field.Value((i + .37) / per, (j + .61) / per);
        Array.Sort(samples);
        var table = new double[TableSize + 1];
        for (int q = 0; q <= TableSize; q++) table[q] = samples[(int)Math.Round(q / (double)TableSize * (samples.Length - 1))];
        return table;
    }

    // Grain level above which a share `coverage` of the area is inked.
    internal static double GrainThreshold(double coverage) =>
        coverage <= 0 ? double.PositiveInfinity : coverage >= 1 ? double.NegativeInfinity : Lookup(grainQuantiles.Value, 1 - coverage);

    static double Lookup(double[] table, double value)
    {
        double x = Math.Clamp(value, 0, 1) * TableSize; int i = (int)x;
        return i >= TableSize ? table[TableSize] : table[i] + (table[i + 1] - table[i]) * (x - i);
    }

    // ---- Drawing --------------------------------------------------------------------------------

    sealed record Cached(MaterialFill Fill, Matrix Device, Rect? Visible, DrawingGroup Drawing);
    static readonly LinkedList<Cached> cache = new();

    // The fill drawn for `device` (layer px → device px): the part of the region inside `visible` (layer px,
    // the whole region when null) as one image of device pixels, placed back in layer space and clipped to
    // the boundary, over the background color when the fill has one. The last few results are reused.
    public static DrawingGroup Drawing(MaterialFill fill, HatchPattern pattern, Matrix device, Rect? visible, CancellationToken token = default)
    {
        lock (cache)
            for (var node = cache.First; node != null; node = node.Next)
                if (ReferenceEquals(node.Value.Fill, fill) && node.Value.Device == device && node.Value.Visible == visible)
                { cache.Remove(node); cache.AddFirst(node); return node.Value.Drawing; }
        var boundary = fill.Boundary.Geometry;
        var group = new DrawingGroup { ClipGeometry = boundary };
        if (MaterialRenderer.Background(fill) is { } paper) group.Children.Add(new GeometryDrawing(paper, null, boundary));
        var area = boundary.Bounds;
        if (visible is { } shown) area.Intersect(shown);
        long pixels = 0;
        if (!area.IsEmpty && area.Width > 0 && area.Height > 0 && device.HasInverse && Render(fill, pattern, device, area, token) is { } image)
        {
            pixels = (long)image.Bitmap.PixelWidth * image.Bitmap.PixelHeight;
            var placed = new DrawingGroup { Transform = new MatrixTransform(image.ToLayer) };
            placed.Children.Add(new ImageDrawing(image.Bitmap, image.Rect));
            RenderOptions.SetBitmapScalingMode(placed, BitmapScalingMode.HighQuality);
            group.Children.Add(placed);
        }
        group.Freeze();
        if (pixels <= CacheMaxPixels)
            lock (cache)
            {
                cache.AddFirst(new Cached(fill, device, visible, group));
                while (cache.Count > CacheEntries) cache.RemoveLast();
            }
        return group;
    }

    internal static void ClearCache() { lock (cache) cache.Clear(); lock (swatches) swatches.Clear(); }

    static readonly Dictionary<(HatchPattern, int, int, uint, ToneGradient), BitmapSource> swatches = [];
    // A preview of a gradient screentone on a clear width×height px area: by default its density runs from
    // light at the left to dark at the right; `gradient` and `ink` show a fill's own settings.
    public static BitmapSource Swatch(HatchPattern pattern, int width, int height, uint ink = 0, ToneGradient? gradient = null)
    {
        if (!HatchPatterns.IsGradient(pattern)) throw new ArgumentOutOfRangeException(nameof(pattern));
        width = Math.Clamp(width, 8, 1024); height = Math.Clamp(height, 8, 1024); gradient ??= new ToneGradient(0, .05, .95);
        var key = (pattern, width, height, ink, gradient);
        lock (swatches) if (swatches.TryGetValue(key, out var known)) return known;
        // Two-thirds of the height per repeat keeps the dots and grain legible at swatch size.
        double tile = Math.Max(24, height * 2 / 3d);
        var fill = new MaterialFill(HatchPatternRenderer.Create(pattern), Guid.NewGuid(), "Swatch", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"),
            width, height, tile, tile, Ink: ink, Gradient: gradient);
        var bitmap = Render(fill, pattern, Matrix.Identity, new Rect(0, 0, width, height), default)!.Value.Bitmap;
        lock (swatches) { if (swatches.Count > 64) swatches.Clear(); swatches[key] = bitmap; }
        return bitmap;
    }

    readonly record struct Image(BitmapSource Bitmap, Rect Rect, Matrix ToLayer);

    static Image? Render(MaterialFill fill, HatchPattern pattern, Matrix device, Rect area, CancellationToken token)
    {
        var bounds = new MatrixTransform(device).TransformBounds(area);
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width + bounds.Height + bounds.X + bounds.Y)) return null;
        // A request larger than one bitmap is drawn coarser and stretched (exports draw in chunks first).
        double fit = Math.Min(1, Math.Sqrt(MaxPixels / Math.Max(1, bounds.Width * bounds.Height)));
        if (fit < 1) { device.Append(new Matrix(fit, 0, 0, fit, 0, 0)); bounds = new MatrixTransform(device).TransformBounds(area); }
        int x0 = (int)Math.Floor(bounds.X), y0 = (int)Math.Floor(bounds.Y);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Right) - x0), height = Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - y0);
        if ((long)width * height > MaxPixels) return null;
        var toLayer = device; toLayer.Invert();

        var gradient = ToneGradient.Of(fill);
        var (min, max) = Extent(fill.Boundary.Geometry, gradient.Angle);
        double radians = gradient.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians), span = max - min;
        // t (0 at the first point along the direction, 1 at the last) as an affine function of device px.
        double tx = 0, ty = 0, t0 = .5;
        if (span > 1e-9)
        {
            tx = (toLayer.M11 * cos + toLayer.M12 * sin) / span; ty = (toLayer.M21 * cos + toLayer.M22 * sin) / span;
            t0 = (toLayer.OffsetX * cos + toLayer.OffsetY * sin - min) / span;
        }
        double start = gradient.Start, range = gradient.End - gradient.Start;
        // Pattern space: device px → layer px → unrotated brush space → tile units.
        var toTile = toLayer; var turn = Matrix.Identity; turn.Rotate(-fill.Angle); toTile.Append(turn);
        toTile.Append(new Matrix(1 / fill.TileWidth, 0, 0, 1 / fill.TileHeight, -fill.OffsetX / fill.TileWidth, -fill.OffsetY / fill.TileHeight));
        bool dots = pattern == HatchPattern.DotGradient;
        double cells = dots ? HatchPatternRenderer.ScreenCells : HatchPatternRenderer.StippleCells;
        // Lattice coordinates: dot sites on integers (X = cx + cy − ½, Y = cx − cy over cell coordinates) or grain cells.
        Matrix lattice = dots ? new Matrix(cells, cells, cells, -cells, -.5, 0) : new Matrix(cells, 0, 0, cells, 0, 0);
        var map = toTile; map.Append(lattice);
        // Half a pixel diagonal in lattice units, and the smallest lattice step per device px.
        double radius = Enumerable.Max(new[] { new Vector(.5, .5), new Vector(.5, -.5) }, v => map.Transform(v).Length);
        double across = map.Transform(new Vector(1, 0)).Length, down = map.Transform(new Vector(0, 1)).Length;
        double unit = Math.Min(across, down), footprint = (across + down) / 2;
        double coverageStep = Math.Abs(range) * (Math.Abs(tx) + Math.Abs(ty)) / 2;
        uint ink = fill.Ink == 0 ? HatchPatterns.DefaultInk : fill.Ink;
        double inkAlpha = (ink >> 24 & 0xFF) / 255d;
        byte inkR = (byte)(ink >> 16), inkG = (byte)(ink >> 8), inkB = (byte)ink;
        int seed = GrainSeed(gradient.Seed);
        var thresholds = dotThresholds.Value; _ = grainQuantiles.Value;
        // Marks smaller than about a device pixel are not resolvable: their average, the requested coverage, is drawn.
        bool flat = dots ? unit > 1 / 1.5 : unit > 1 / .75;
        var pixels = new byte[checked(width * height * 4)];
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, row =>
        {
            double y = y0 + row + .5; int offset = row * width * 4;
            var grain = dots ? null : new GrainField(seed);
            for (int column = 0; column < width; column++)
            {
                double x = x0 + column + .5;
                double coverage = start + range * Math.Clamp(x * tx + y * ty + t0, 0, 1), ink01;
                if (flat || coverage <= 0 || coverage >= 1) ink01 = Math.Clamp(coverage, 0, 1);
                else
                {
                    double lx = x * map.M11 + y * map.M21 + map.OffsetX, ly = x * map.M12 + y * map.M22 + map.OffsetY;
                    if (dots)
                    {
                        // The spot value changes at most 4 per lattice unit, except across the 50% diamond, where it
                        // jumps: pixels near a dot edge or that diamond are supersampled, the rest are solid or clear.
                        double low = Math.Max(0, coverage - coverageStep), high = Math.Min(1, coverage + coverageStep);
                        double value = Spot(lx, ly), margin = 4 * radius;
                        double ex = 2 * (lx - Math.Floor(lx + .5)), ey = 2 * (ly - Math.Floor(ly + .5)), diamond = Math.Abs(Math.Abs(ex) + Math.Abs(ey) - 1) / (2 * Math.Sqrt(2));
                        ink01 = diamond > radius && value - margin >= DotThreshold(low) ? 1 : diamond > radius && value + margin < DotThreshold(high) ? 0 : Supersample(x, y);
                    }
                    else
                    {
                        // The grain is smooth: its edge is anti-aliased from the value and slope at the pixel centre.
                        // Near a peak or a pit the slope says little (a speck smaller than the pixel would fill it):
                        // where the bend over the pixel outweighs the slope and the level is within reach, sample.
                        double value = grain!.Value(lx, ly, out double slope), level = GrainThreshold(coverage), bend = 12 * radius * radius;
                        ink01 = bend > slope * radius && Math.Abs(value - level) < bend + slope * radius ? SampleGrain(grain, x, y)
                            : Math.Clamp(.5 + (value - level) / Math.Max(slope * footprint, 1e-9), 0, 1);
                    }
                }
                if (ink01 <= 0) continue;
                double a = ink01 * inkAlpha;
                int i = offset + column * 4;
                pixels[i] = (byte)Math.Round(inkB * a); pixels[i + 1] = (byte)Math.Round(inkG * a); pixels[i + 2] = (byte)Math.Round(inkR * a); pixels[i + 3] = (byte)Math.Round(255 * a);
            }
        });
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); bitmap.Freeze();
        return new Image(bitmap, new Rect(x0, y0, width, height), toLayer);

        // Share of the dot samples in a pixel that are inked, each with its own density.
        double Supersample(double x, double y)
        {
            const int n = DotSamples; int inked = 0;
            for (int sy = 0; sy < n; sy++)
                for (int sx = 0; sx < n; sx++)
                {
                    double px = x + (sx + .5) / n - .5, py = y + (sy + .5) / n - .5;
                    double coverage = start + range * Math.Clamp(px * tx + py * ty + t0, 0, 1);
                    if (Spot(px * map.M11 + py * map.M21 + map.OffsetX, px * map.M12 + py * map.M22 + map.OffsetY) >= Lookup(thresholds, coverage)) inked++;
                }
            return inked / (double)(n * n);
        }

        // Share of the grain samples in a pixel that are inked.
        double SampleGrain(GrainField grain, double x, double y)
        {
            const int n = GrainSamples; int inked = 0;
            for (int sy = 0; sy < n; sy++)
                for (int sx = 0; sx < n; sx++)
                {
                    double px = x + (sx + .5) / n - .5, py = y + (sy + .5) / n - .5;
                    double coverage = start + range * Math.Clamp(px * tx + py * ty + t0, 0, 1);
                    if (grain.Value(px * map.M11 + py * map.M21 + map.OffsetX, px * map.M12 + py * map.M22 + map.OffsetY) >= GrainThreshold(coverage)) inked++;
                }
            return inked / (double)(n * n);
        }
    }
}
