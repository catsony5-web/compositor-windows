using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// The user's line patterns (LinePatterns) drawn like the built-in hatch patterns: the stored
// coverage tile is recolored with the fill's ink, thickened or thinned for the line weight (from a
// periodic signed distance field, so the lines grow evenly and stay seamless) and resampled with
// wrap-around to whole device pixels. Tiles are cached (LRU) by tile, size, weight and ink.
public static class LinePatternRenderer
{
    public const long CacheBudgetBytes = 64L * 1024 * 1024;

    sealed class Mask(int width, int height, float[] coverage, double stroke)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public float[] Coverage { get; } = coverage;
        // Typical stroke width in tile pixels (2 × area / perimeter of the ink).
        public double Stroke { get; } = stroke;
        public Lazy<float[]> Signed { get; } = new(() => SignedDistance(coverage, width, height), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    static readonly ConditionalWeakTable<Raster, Mask> masks = new();

    static Mask Of(MaterialAsset asset)
    {
        if (!LinePatterns.IsCustom(asset)) throw new ArgumentException("선 패턴이 아닙니다.", nameof(asset));
        return masks.GetValue(asset.Pixels, pixels =>
        {
            int w = pixels.Width, h = pixels.Height; var coverage = new float[w * h];
            for (int i = 0; i < coverage.Length; i++) coverage[i] = pixels.Data[i * 4 + 3] / 255f;
            return new Mask(w, h, coverage, EstimateStroke(coverage, w, h));
        });
    }

    // Typical stroke width of the pattern's lines in tile pixels.
    public static double Stroke(MaterialAsset asset) => Of(asset).Stroke;

    // Repeat size relative to the default material size: a tile whose lines are thin compared with its
    // width (a large scan) starts larger, so its lines read at about the weight of the built-in pens.
    public static double RepeatScale(MaterialAsset asset)
    {
        var mask = Of(asset);
        return Math.Clamp(.6 * mask.Width / (mask.Stroke * 64), 1, 4);
    }

    static double EstimateStroke(float[] coverage, int w, int h)
    {
        double area = 0; long edges = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, below = (y + 1) % h * w;
            for (int x = 0; x < w; x++)
            {
                bool ink = coverage[row + x] >= .5f;
                area += coverage[row + x];
                if (ink != coverage[row + (x + 1) % w] >= .5f) edges++;
                if (ink != coverage[below + x] >= .5f) edges++;
            }
        }
        return edges == 0 ? 1 : Math.Clamp(2 * area / edges, 1, 64);
    }

    // ---- Line weight ------------------------------------------------------------------------

    // Signed distance (tile px) to the edge of the ink on the torus: negative inside. Edge pixels use
    // their antialiased coverage, so line weight 100% reproduces the stored tile exactly.
    static float[] SignedDistance(float[] coverage, int w, int h)
    {
        var ink = new bool[coverage.Length];
        for (int i = 0; i < ink.Length; i++) ink[i] = coverage[i] >= .5f;
        var toInk = Distance(ink, w, h, true); var toPaper = Distance(ink, w, h, false);
        var signed = new float[coverage.Length];
        for (int i = 0; i < signed.Length; i++)
        {
            float c = coverage[i];
            signed[i] = c > .02f && c < .98f ? .5f - c : ink[i] ? -(float)(Math.Sqrt(toPaper[i]) - .5) : (float)(Math.Sqrt(toInk[i]) - .5);
        }
        return signed;
    }

    const double Far = 1e20;

    // Squared Euclidean distance to the nearest pixel whose ink flag equals `target`, on the torus
    // (separable lower-envelope transform over three periods per row and column).
    static double[] Distance(bool[] ink, int w, int h, bool target)
    {
        var columns = new double[w * h];
        int longest = 3 * Math.Max(w, h);
        var f = new double[longest]; var d = new double[longest]; var v = new int[longest]; var z = new double[longest + 1];
        for (int x = 0; x < w; x++)
        {
            for (int k = 0; k < 3 * h; k++) f[k] = ink[k % h * w + x] == target ? 0 : Far;
            Envelope(f, 3 * h, d, v, z);
            for (int y = 0; y < h; y++) columns[y * w + x] = d[h + y];
        }
        var result = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int k = 0; k < 3 * w; k++) f[k] = columns[y * w + k % w];
            Envelope(f, 3 * w, d, v, z);
            for (int x = 0; x < w; x++) result[y * w + x] = Math.Min(Far, d[w + x]);
        }
        return result;
    }

    static void Envelope(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            // z[0] is -∞, so k never drops below 0.
            while (s <= z[k]) { k--; s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]); }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k]; d[q] = dq * dq + f[v[k]];
        }
    }

    // Coverage at the stored resolution for a line weight: the edge moves out (or in) by half the
    // stroke change, so 200% draws lines about twice as wide and 50% about half.
    static float[] Weighted(Mask mask, double weight)
    {
        if (Math.Abs(weight - 1) < .005) return mask.Coverage;
        var signed = mask.Signed.Value; float delta = (float)((weight - 1) * mask.Stroke / 2);
        var result = new float[signed.Length];
        for (int i = 0; i < result.Length; i++) result[i] = Math.Clamp(.5f - signed[i] + delta, 0f, 1f);
        return result;
    }

    // ---- Resampling with wrap-around ---------------------------------------------------------

    readonly record struct Tap(int Index, float Weight);

    // Output sample i of a periodic signal of length `from` resampled to `to`: area average when
    // reducing, linear between neighbours when enlarging. Indices wrap, so the result tiles seamlessly.
    static Tap[][] Kernel(int from, int to)
    {
        var taps = new Tap[to][]; double step = from / (double)to;
        for (int i = 0; i < to; i++)
        {
            if (step >= 1)
            {
                double a = i * step, b = (i + 1) * step; var list = new List<Tap>();
                for (int k = (int)Math.Floor(a); k < (int)Math.Ceiling(b); k++)
                {
                    double overlap = Math.Min(b, k + 1) - Math.Max(a, k);
                    if (overlap > 1e-9) list.Add(new Tap(Wrap(k, from), (float)(overlap / step)));
                }
                taps[i] = list.ToArray();
            }
            else
            {
                double x = (i + .5) * step - .5; int k = (int)Math.Floor(x); float t = (float)(x - k);
                taps[i] = [new Tap(Wrap(k, from), 1 - t), new Tap(Wrap(k + 1, from), t)];
            }
        }
        return taps;
    }

    static int Wrap(int value, int m) => ((value % m) + m) % m;

    internal static float[] Resample(float[] source, int width, int height, int toWidth, int toHeight, CancellationToken token = default)
    {
        if (width == toWidth && height == toHeight) return source;
        var columns = Kernel(width, toWidth); var rows = Kernel(height, toHeight);
        var across = new float[toWidth * height];
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            int row = y * width, output = y * toWidth;
            for (int x = 0; x < toWidth; x++) { float sum = 0; foreach (var tap in columns[x]) sum += source[row + tap.Index] * tap.Weight; across[output + x] = sum; }
        }
        var result = new float[toWidth * toHeight];
        for (int y = 0; y < toHeight; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            var taps = rows[y]; int output = y * toWidth;
            for (int x = 0; x < toWidth; x++) { float sum = 0; foreach (var tap in taps) sum += across[tap.Index * toWidth + x] * tap.Weight; result[output + x] = sum; }
        }
        return result;
    }

    // ---- Tiles ------------------------------------------------------------------------------

    readonly record struct Key(Raster Pixels, int Width, int Height, int Weight, uint Ink);
    static readonly object gate = new();
    static readonly Dictionary<Key, LinkedListNode<(Key Key, BitmapSource Tile)>> entries = [];
    static readonly LinkedList<(Key Key, BitmapSource Tile)> recent = new();
    static long cachedBytes;
    static int builds;

    internal static (int Builds, long Bytes, int Entries) CacheStats { get { lock (gate) return (builds, cachedBytes, entries.Count); } }
    internal static void ClearCache() { lock (gate) { entries.Clear(); recent.Clear(); cachedBytes = 0; } }

    // A frozen tile of width×height with the ink drawn in and transparent paper.
    public static BitmapSource Tile(MaterialAsset asset, int width, int height, double lineWeight, uint ink, CancellationToken token = default)
    {
        var mask = Of(asset);
        if (!double.IsFinite(lineWeight) || lineWeight <= 0) throw new ArgumentOutOfRangeException(nameof(lineWeight));
        width = Math.Clamp(width, 1, HatchPatternRenderer.MaxTileSide); height = Math.Clamp(height, 1, HatchPatternRenderer.MaxTileSide);
        var key = new Key(asset.Pixels, width, height, (int)Math.Round(lineWeight * 200), ink == 0 ? HatchPatterns.DefaultInk : ink);
        lock (gate)
            if (entries.TryGetValue(key, out var found)) { recent.Remove(found); recent.AddFirst(found); return found.Value.Tile; }
        var coverage = Resample(Weighted(mask, key.Weight / 200d), mask.Width, mask.Height, width, height, token);
        token.ThrowIfCancellationRequested();
        var data = new byte[width * height * 4]; double alpha = key.Ink >> 24 & 0xFF;
        byte b = (byte)key.Ink, g = (byte)(key.Ink >> 8), r = (byte)(key.Ink >> 16);
        for (int i = 0, p = 0; i < coverage.Length; i++, p += 4)
        {
            data[p] = b; data[p + 1] = g; data[p + 2] = r;
            data[p + 3] = (byte)Math.Clamp(Math.Round(coverage[i] * alpha), 0, 255);
        }
        var tile = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4); tile.Freeze();
        lock (gate)
        {
            if (entries.TryGetValue(key, out var raced)) return raced.Value.Tile;
            builds++;
            entries.Add(key, recent.AddFirst((key, tile))); cachedBytes += data.LongLength;
            while (cachedBytes > CacheBudgetBytes && recent.Last is { } last && !ReferenceEquals(last.Value.Tile, tile))
            { recent.RemoveLast(); entries.Remove(last.Value.Key); cachedBytes -= (long)last.Value.Key.Width * last.Value.Key.Height * 4; }
        }
        return tile;
    }

    static readonly ConditionalWeakTable<Raster, Dictionary<int, BitmapSource>> swatches = new();

    // A palette swatch: one repeat on white paper, devicePx wide, at the tile's own proportions.
    public static BitmapSource Swatch(MaterialAsset asset, int devicePx)
    {
        devicePx = Math.Clamp(devicePx, HatchPatternRenderer.MinTileSide, 512);
        var known = swatches.GetValue(asset.Pixels, _ => []);
        lock (known) if (known.TryGetValue(devicePx, out var hit)) return hit;
        int height = Math.Clamp((int)Math.Round(devicePx * MaterialEditing.Aspect(asset)), 1, 1024);
        var tile = Tile(asset, devicePx, height, 1, HatchPatterns.DefaultInk);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, devicePx, height)); dc.DrawImage(tile, new Rect(0, 0, devicePx, height)); }
        var bitmap = new RenderTargetBitmap(devicePx, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        lock (known) known[devicePx] = bitmap;
        return bitmap;
    }
}

// Turns a picked image (a scanned or drawn hatch tile) into a line pattern tile: dark strokes become
// ink coverage, light paper becomes transparent. The image is reduced to LinePatterns.MaxSide with
// area averaging first; an automatic threshold (Otsu) separates paper from ink, and the band between
// the two keeps the antialiased edges.
public sealed class LinePatternSource
{
    public const long MaxFileBytes = 96L * 1024 * 1024;
    // Decoded at most this large before the area-averaged reduction to LinePatterns.MaxSide.
    const int DecodeSide = 2048;

    public int Width { get; }
    public int Height { get; }
    // 0 paper … 1 ink, from luminance and the image's own transparency.
    internal float[] Darkness { get; }
    // Suggested threshold in 0..1.
    public double AutoThreshold { get; }

    LinePatternSource(int width, int height, float[] darkness)
    {
        Width = width; Height = height; Darkness = darkness; AutoThreshold = Otsu(darkness);
    }

    public static LinePatternSource Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new InvalidDataException("이미지 파일을 찾을 수 없습니다.");
        if (file.Length > MaxFileBytes) throw new InvalidDataException("패턴 이미지는 96MiB 이하로 사용하세요.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("패턴 이미지가 비어 있습니다.");
        var frame = decoder.Frames[0]; MaterialEditing.ValidateSize(frame.PixelWidth, frame.PixelHeight);
        double scale = Math.Min(1, DecodeSide / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        Raster pixels;
        if (scale < 1)
        {
            stream.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale)); bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            pixels = Raster.FromBitmap(bitmap);
        }
        else pixels = Raster.FromBitmap(frame);
        return From(pixels);
    }

    public static LinePatternSource From(Raster image)
    {
        if (image.Width < LinePatterns.MinSide || image.Height < LinePatterns.MinSide) throw new InvalidDataException($"패턴 이미지는 한 변 {LinePatterns.MinSide}px 이상이어야 합니다.");
        var darkness = new float[image.Width * image.Height];
        for (int i = 0, p = 0; i < darkness.Length; i++, p += 4)
        {
            double luminance = (.0722 * image.Data[p] + .7152 * image.Data[p + 1] + .2126 * image.Data[p + 2]) / 255;
            darkness[i] = (float)(image.Data[p + 3] / 255d * (1 - luminance));
        }
        double fit = Math.Min(1, LinePatterns.MaxSide / (double)Math.Max(image.Width, image.Height));
        int width = Math.Max(LinePatterns.MinSide, (int)Math.Round(image.Width * fit)), height = Math.Max(LinePatterns.MinSide, (int)Math.Round(image.Height * fit));
        return new(width, height, LinePatternRenderer.Resample(darkness, image.Width, image.Height, width, height));
    }

    static double Otsu(float[] values)
    {
        var histogram = new long[256];
        foreach (var v in values) histogram[Math.Clamp((int)(v * 255 + .5), 0, 255)]++;
        double total = values.Length, sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)histogram[i];
        var between = new double[255]; double below = 0, belowSum = 0, best = -1;
        for (int t = 0; t < 255; t++)
        {
            below += histogram[t]; belowSum += t * (double)histogram[t];
            double above = total - below;
            between[t] = -1;
            if (below == 0 || above == 0) continue;
            double meanBelow = belowSum / below, meanAbove = (sum - belowSum) / above;
            between[t] = below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            best = Math.Max(best, between[t]);
        }
        if (best <= 0) return .5;
        // Empty bins between paper and ink give equal splits: take the middle of that plateau, so the
        // threshold sits between the tones rather than at the edge of the paper's grain.
        int first = Array.FindIndex(between, v => v >= best * (1 - 1e-9)), last = Array.FindLastIndex(between, v => v >= best * (1 - 1e-9));
        // The class boundary lies between bins t and t+1.
        return Math.Clamp(((first + last) / 2d + .5) / 255, .02, .98);
    }

    // The coverage tile (straight alpha in the alpha channel) for `threshold` (0..1, darkness). Paper
    // below the band, halfway from the paper tone to the threshold, becomes clear and ink above the
    // band halfway to the ink tone becomes solid, so scan grain and grey ink are cleaned up.
    // trim crops blank margins around the drawing.
    public Raster Convert(double threshold, bool trim = false)
    {
        if (!double.IsFinite(threshold)) threshold = AutoThreshold;
        threshold = Math.Clamp(threshold, .01, .99);
        double paper = 0, ink = 0; long papers = 0, inks = 0;
        foreach (var d in Darkness) if (d < threshold) { paper += d; papers++; } else { ink += d; inks++; }
        if (inks == 0) throw new InvalidDataException("이미지에서 선을 찾지 못했습니다. 선 인식 기준을 낮춰 보세요.");
        paper = papers > 0 ? paper / papers : 0; ink /= inks;
        if (ink - paper < .06) throw new InvalidDataException("선과 바탕의 밝기 차이가 너무 작습니다. 밝은 바탕에 어두운 선이 있는 이미지를 사용하세요.");
        double low = (paper + threshold) / 2, high = Math.Max(low + .002, (threshold + ink) / 2);
        var coverage = new byte[Darkness.Length]; long solid = 0;
        for (int i = 0; i < coverage.Length; i++)
        {
            double c = Math.Clamp((Darkness[i] - low) / (high - low), 0, 1);
            coverage[i] = (byte)Math.Round(c * 255);
            if (coverage[i] >= 128) solid++;
        }
        if (solid == 0) throw new InvalidDataException("이미지에서 선을 찾지 못했습니다. 선 인식 기준을 낮춰 보세요.");
        if (solid > coverage.Length * .9) throw new InvalidDataException("이미지의 거의 전부가 선으로 인식됩니다. 밝은 바탕에 어두운 선이 있는 이미지를 쓰거나 선 인식 기준을 높여 보세요.");
        int left = 0, top = 0, right = Width - 1, bottom = Height - 1;
        if (trim)
        {
            left = Width; top = Height; right = -1; bottom = -1;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (coverage[y * Width + x] > 4) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            // Keep at least the minimum tile around the drawing's centre.
            void Grow(ref int a, ref int b, int length)
            {
                while (b - a + 1 < LinePatterns.MinSide) { if (a > 0) a--; if (b - a + 1 < LinePatterns.MinSide && b < length - 1) b++; }
            }
            Grow(ref left, ref right, Width); Grow(ref top, ref bottom, Height);
        }
        int w = right - left + 1, h = bottom - top + 1;
        var tile = new Raster(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) tile.Data[(y * w + x) * 4 + 3] = coverage[(y + top) * Width + x + left];
        return tile;
    }
}
