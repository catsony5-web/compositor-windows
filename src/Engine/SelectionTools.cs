using System.Windows;

namespace Compositor.Windows;

public enum SelectionCombine { Replace, Add, Subtract, Intersect }

/// <summary>Canvas-space, immutable eight-bit selection coverage operations.</summary>
public static partial class SelectionTools
{
    public static Selection FromMask(byte[] mask, int width, int height)
        => FromMask(mask, width, height, new Rect(0, 0, width, height));

    public static Selection FromMask(byte[] mask, int width, int height, Rect area)
    {
        Raster.ValidateSize(width, height);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.X + area.Y + area.Width + area.Height)) throw new ArgumentException("선택 영역 좌표가 올바르지 않습니다.", nameof(area));
        if (mask.Length != width * height) throw new ArgumentException("선택 마스크 크기가 다릅니다.", nameof(mask));
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) if (mask[y * width + x] != 0)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        var bounds = right < left ? new Rect(0, 0, 0, 0) : new Rect(area.X + left * area.Width / width, area.Y + top * area.Height / height, (right - left + 1) * area.Width / width, (bottom - top + 1) * area.Height / height);
        return new Selection(bounds) { Coverage = (byte[])mask.Clone(), CanvasWidth = width, CanvasHeight = height,
            CoverageBounds = area == new Rect(0, 0, width, height) ? null : area };
    }

    public static byte[] Mask(Selection? selection, int width, int height)
    {
        Raster.ValidateSize(width, height);
        var data = new byte[width * height];
        if (selection == null) return data;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) data[y * width + x] = Imaging.Byte(selection.Weight(x + .5, y + .5) * 255);
        return data;
    }

    public static Selection Polygon(int width, int height, IReadOnlyList<Point> points, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Raster.ValidateSize(width, height);
        var data = new byte[width * height];
        if (points.Count < 3) return FromMask(data, width, height);
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) throw new ArgumentException("선택 좌표가 올바르지 않습니다.");
        int left = (int)Math.Clamp(Math.Floor(points.Min(p => p.X)), 0, width), right = (int)Math.Clamp(Math.Ceiling(points.Max(p => p.X)), 0, width);
        int top = (int)Math.Clamp(Math.Floor(points.Min(p => p.Y)), 0, height), bottom = (int)Math.Clamp(Math.Ceiling(points.Max(p => p.Y)), 0, height);
        // Two subpixel scanlines and two horizontal samples keep diagonal lasso
        // edges antialiased without testing every polygon edge for every pixel.
        // Even-odd intersections also handle self-crossing freehand lassos.
        var crossings = new List<double>(points.Count);
        for (int y = top; y < bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int yy = 0; yy < 2; yy++)
            {
                double sy = y + .25 + .5 * yy; crossings.Clear();
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    var a = points[i]; var b = points[j];
                    if ((a.Y > sy) != (b.Y > sy)) crossings.Add(a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    double a = crossings[k], b = crossings[k + 1];
                    int start = (int)Math.Clamp(Math.Floor(a), left, right), end = (int)Math.Clamp(Math.Ceiling(b), left, right);
                    for (int x = start; x < end; x++)
                    {
                        if (x + .25 >= a && x + .25 < b) data[y * width + x]++;
                        if (x + .75 >= a && x + .75 < b) data[y * width + x]++;
                    }
                }
            }
            for (int x = left; x < right; x++) data[y * width + x] = Imaging.Byte(data[y * width + x] * 255.0 / 4);
        }
        return FromMask(data, width, height);
    }

    public static Selection MagicWand(Raster composite, Point seed, double tolerance = 32, bool contiguous = true, CancellationToken token = default, bool antialias = false)
    {
        token.ThrowIfCancellationRequested();
        int w = composite.Width, h = composite.Height;
        var data = new byte[w * h];
        if (!double.IsFinite(seed.X) || !double.IsFinite(seed.Y) || seed.X < 0 || seed.Y < 0 || seed.X >= w || seed.Y >= h) return FromMask(data, w, h);
        if (!double.IsFinite(tolerance)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        tolerance = Math.Clamp(tolerance, 0, 255);
        int start = (int)seed.Y * w + (int)seed.X;
        bool Match(int index) => ColorDistanceSquared(composite.Data, start * 4, index * 4) <= tolerance * tolerance;
        if (!contiguous)
        {
            for (int i = 0; i < data.Length; i++) { if ((i & 16383) == 0) token.ThrowIfCancellationRequested(); if (Match(i)) data[i] = 255; }
        }
        else
        {
            // Scanline spans avoid a full-size visited array and per-pixel queue.
            // Rejected samples use 1 temporarily; they never propagate a region.
            var queue = new Queue<int>(); queue.Enqueue(start); int checkedCount = 0;
            bool Eligible(int index)
            {
                if (data[index] != 0) return false;
                if (Match(index)) return true;
                data[index] = 1; return false;
            }
            while (queue.TryDequeue(out int index))
            {
                token.ThrowIfCancellationRequested();
                if (!Eligible(index)) continue;
                int x = index % w, y = index / w, left = x;
                while (left > 0 && Eligible(y * w + left - 1)) left--;
                bool above = false, below = false;
                for (x = left; x < w && Eligible(y * w + x); x++)
                {
                    if ((++checkedCount & 8191) == 0) token.ThrowIfCancellationRequested();
                    int p = y * w + x; data[p] = 255;
                    bool nextAbove = y > 0 && Eligible(p - w), nextBelow = y + 1 < h && Eligible(p + w);
                    if (nextAbove && !above) queue.Enqueue(p - w);
                    if (nextBelow && !below) queue.Enqueue(p + w);
                    above = nextAbove; below = nextBelow;
                }
            }
            for (int i = 0; i < data.Length; i++) if (data[i] == 1) data[i] = 0;
        }
        if (antialias) RefineWandEdges(composite, data, start, tolerance, token);
        token.ThrowIfCancellationRequested();
        return FromMask(data, w, h);
    }

    // Compare premultiplied color plus alpha: hidden RGB in transparent pixels
    // cannot split what is visually one transparent area.
    internal static double ColorDistance(byte[] pixels, int a, int b)
        => Math.Sqrt(ColorDistanceSquared(pixels, a, b));
    internal static double ColorDistanceSquared(byte[] pixels, int a, int b)
    {
        double aa = pixels[a + 3] / 255.0, ba = pixels[b + 3] / 255.0, sum = 0;
        for (int c = 0; c < 3; c++) { double d = pixels[a + c] * aa - pixels[b + c] * ba; sum += d * d; }
        double da = pixels[a + 3] - pixels[b + 3]; return sum / 3 + da * da;
    }

    public static Selection Combine(Selection? current, Selection incoming, int width, int height, SelectionCombine mode, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (mode == SelectionCombine.Replace) return incoming;
        if (current?.CoverageBounds != null || incoming.CoverageBounds != null) return CombinePrecise(current, incoming, width, height, mode, token);
        var a = Mask(current, width, height); var b = Mask(incoming, width, height);
        for (int i = 0; i < a.Length; i++) a[i] = mode switch
        {
            SelectionCombine.Replace => b[i], SelectionCombine.Add => Math.Max(a[i], b[i]),
            SelectionCombine.Subtract => Imaging.Byte(a[i] * (1 - b[i] / 255.0)),
            SelectionCombine.Intersect => Math.Min(a[i], b[i]), _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return FromMask(a, width, height);
    }

    public static Selection Invert(Selection? selection, int width, int height)
    {
        if (selection?.CoverageBounds != null) return CombinePrecise(new Selection(new Rect(0, 0, width, height)), selection, width, height, SelectionCombine.Subtract, default);
        var data = Mask(selection, width, height); for (int i = 0; i < data.Length; i++) data[i] = (byte)(255 - data[i]); return FromMask(data, width, height);
    }

    public static Selection Feather(Selection selection, int width, int height, double radius, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(radius) || radius < 0 || radius > 256) throw new ArgumentOutOfRangeException(nameof(radius));
        var input = Mask(selection, width, height);
        if (radius <= 0) return FromMask(input, width, height);
        if (radius > 4) return FromMask(FastFeather(input, width, height, radius / 2, token), width, height);
        int r = Math.Max(1, (int)Math.Ceiling(radius * 2)); double sigma = Math.Max(.35, radius / 2);
        var kernel = Enumerable.Range(-r, 2 * r + 1).Select(v => Math.Exp(-v * v / (2 * sigma * sigma))).ToArray();
        double total = kernel.Sum(); for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        var temp = new float[input.Length]; var output = new byte[input.Length];
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++) { double sum = 0; for (int k = -r; k <= r; k++) if (x + k >= 0 && x + k < width) sum += input[y * width + x + k] * kernel[k + r]; temp[y * width + x] = (float)sum; }
        });
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++) { double sum = 0; for (int k = -r; k <= r; k++) if (y + k >= 0 && y + k < height) sum += temp[(y + k) * width + x] * kernel[k + r]; output[y * width + x] = Imaging.Byte(sum); }
        });
        return FromMask(output, width, height);
    }

    // Three box filters approximate a Gaussian with bounded O(pixels) work for
    // wide feathers. Zero padding treats the outside of the canvas as unselected.
    static byte[] FastFeather(byte[] input, int w, int h, double sigma, CancellationToken token)
    {
        var a = Array.ConvertAll(input, v => (float)v); var b = new float[input.Length];
        double ideal = Math.Sqrt(4 * sigma * sigma + 1); int lower = (int)Math.Floor(ideal); if (lower % 2 == 0) lower--; lower = Math.Max(1, lower);
        int upper = lower + 2, lowCount = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4));
        var options = new ParallelOptions { CancellationToken = token };
        for (int pass = 0; pass < 3; pass++)
        {
            int radius = ((pass < lowCount ? lower : upper) - 1) / 2; double divisor = radius * 2 + 1;
            Parallel.For(0, h, options, y =>
            {
                int row = y * w; double sum = 0; for (int k = 0; k <= radius && k < w; k++) sum += a[row + k];
                for (int x = 0; x < w; x++) { b[row + x] = (float)(sum / divisor); if (x - radius >= 0) sum -= a[row + x - radius]; if (x + radius + 1 < w) sum += a[row + x + radius + 1]; }
            });
            Parallel.For(0, w, options, x =>
            {
                double sum = 0; for (int k = 0; k <= radius && k < h; k++) sum += b[k * w + x];
                for (int y = 0; y < h; y++) { a[y * w + x] = (float)(sum / divisor); if (y - radius >= 0) sum -= b[(y - radius) * w + x]; if (y + radius + 1 < h) sum += b[(y + radius + 1) * w + x]; }
            });
        }
        return Array.ConvertAll(a, v => Imaging.Byte(v));
    }

    public static Selection Expand(Selection selection, int width, int height, int radius, CancellationToken token = default) => Morphology(selection, width, height, radius, true, token);
    public static Selection Contract(Selection selection, int width, int height, int radius, CancellationToken token = default) => Morphology(selection, width, height, radius, false, token);

    static Selection Morphology(Selection selection, int w, int h, int radius, bool expand, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (radius < 0 || radius > 256) throw new ArgumentOutOfRangeException(nameof(radius));
        var input = Mask(selection, w, h); if (radius == 0) return FromMask(input, w, h);
        if (input.All(v => v == 0 || v == 255)) return FromMask(BinaryMorphology(input, w, h, radius, expand, token), w, h);
        // Exact circular footprint (not a square dilation). For each horizontal
        // offset, the vertical sliding extremum is calculated in linear time.
        var output = new byte[input.Length]; if (!expand) Array.Fill(output, (byte)255);
        var deque = new int[h];
        for (int dx = -radius; dx <= radius; dx++)
        {
            token.ThrowIfCancellationRequested(); int ry = (int)Math.Floor(Math.Sqrt(radius * radius - dx * dx));
            for (int x = 0; x < w; x++)
            {
                int sx = x + dx;
                if (sx < 0 || sx >= w) { if (!expand) for (int y = 0; y < h; y++) output[y * w + x] = 0; continue; }
                int head = 0, tail = 0, added = -1;
                for (int y = 0; y < h; y++)
                {
                    int end = Math.Min(h - 1, y + ry);
                    while (added < end)
                    {
                        added++; byte value = input[added * w + sx];
                        while (tail > head && (expand ? input[deque[tail - 1] * w + sx] <= value : input[deque[tail - 1] * w + sx] >= value)) tail--;
                        deque[tail++] = added;
                    }
                    while (tail > head && deque[head] < y - ry) head++;
                    byte v = !expand && (y - ry < 0 || y + ry >= h) ? (byte)0 : input[deque[head] * w + sx];
                    int index = y * w + x; output[index] = expand ? Math.Max(output[index], v) : Math.Min(output[index], v);
                }
            }
        }
        return FromMask(output, w, h);
    }

    static byte[] BinaryMorphology(byte[] input, int w, int h, int radius, bool expand, CancellationToken token)
    {
        // Squared Euclidean distance transform produces an exact circular result
        // in linear time for the common hard-edged selection case.
        var distances = new float[input.Length]; int longest = Math.Max(w, h);
        var f = new double[longest]; var d = new double[longest]; var sites = new int[longest]; var edges = new double[longest + 1];
        const double infinity = 1e9;
        void Transform(int count)
        {
            int k = 0; sites[0] = 0; edges[0] = double.NegativeInfinity; edges[1] = double.PositiveInfinity;
            for (int q = 1; q < count; q++)
            {
                double s;
                do { int v = sites[k]; s = ((f[q] + q * (double)q) - (f[v] + v * (double)v)) / (2.0 * (q - v)); if (s <= edges[k]) k--; else break; } while (k >= 0);
                k++; sites[k] = q; edges[k] = s; edges[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < count; q++) { while (edges[k + 1] < q) k++; double delta = q - sites[k]; d[q] = delta * delta + f[sites[k]]; }
        }
        for (int x = 0; x < w; x++)
        {
            if ((x & 31) == 0) token.ThrowIfCancellationRequested();
            for (int y = 0; y < h; y++) f[y] = (input[y * w + x] != 0) == expand ? 0 : infinity;
            Transform(h); for (int y = 0; y < h; y++) distances[y * w + x] = (float)d[y];
        }
        var output = new byte[input.Length]; double r2 = radius * radius;
        for (int y = 0; y < h; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++) f[x] = distances[y * w + x]; Transform(w);
            for (int x = 0; x < w; x++)
            {
                double distance = d[x];
                if (!expand)
                {
                    int outside = Math.Min(Math.Min(x + 1, w - x), Math.Min(y + 1, h - y)); distance = Math.Min(distance, outside * (double)outside);
                }
                output[y * w + x] = (expand ? distance <= r2 : distance > r2) ? (byte)255 : (byte)0;
            }
        }
        return output;
    }

    public static Selection FromAlpha(Layer layer, int width, int height)
    {
        Raster.ValidateSize(width, height);
        var mask = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var p = layer.Local(new Point(x + .5, y + .5)); int xx = (int)Math.Floor(p.X), yy = (int)Math.Floor(p.Y);
            if (xx < 0 || yy < 0 || xx >= layer.Pixels.Width || yy >= layer.Pixels.Height) continue;
            int i = yy * layer.Pixels.Width + xx;
            mask[y * width + x] = Imaging.Byte(layer.Pixels.Data[i * 4 + 3] * (layer.Mask == null ? 1 : layer.Mask[i] / 255.0));
        }
        return FromMask(mask, width, height);
    }
}
