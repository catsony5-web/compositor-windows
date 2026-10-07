using System.IO;
using System.Windows;

namespace Compositor.Windows;

public enum SketchLineColor { Original, Black, Custom }

// One cleanup's settings. A null threshold or speck size uses the value measured from the photo.
public sealed record SketchOptions
{
    // Flatten the sheet's perspective (from the given or detected corners); false keeps the whole photo.
    public bool Flatten { get; init; } = true;
    // Line threshold in normalized darkness, 0 (paper) … 1 (ink).
    public double? Threshold { get; init; }
    // Marks of fewer pixels than this (at the result's resolution) are removed; 0 keeps every mark.
    public int? SpeckSize { get; init; }
    // 0 … 1: pushes partial coverage (faint lines, soft edges) toward solid.
    public double Boldness { get; init; }
    public SketchLineColor LineColor { get; init; } = SketchLineColor.Original;
    // #AARRGGBB for SketchLineColor.Custom.
    public uint CustomColor { get; init; } = 0xFF1F3A68;
}

// The sheet's corners in photo pixels (top-left, top-right, bottom-right, bottom-left) and how sure the
// detection is (0..1). Not found: the corners are the whole image.
public sealed record SheetDetection(Point[] Corners, double Confidence, bool Found);

// Lines: transparent line art (straight BGRA). Corners: the quadrilateral that was flattened, null when
// the whole photo was used.
public sealed record SketchResult(Raster Lines, Point[]? Corners, double Threshold, double AutomaticThreshold, int SpeckSize, SheetDetection? Detection);

// A sheet with its lighting evened out: what line extraction reads, kept by the dialog so threshold,
// speck, boldness and color changes redo only the extraction.
public sealed class SketchSheet
{
    public int Width { get; }
    public int Height { get; }
    // 0 paper … 1 ink after dividing by the paper's own lighting.
    internal float[] Darkness { get; }
    // The photo divided by the paper's lighting (paper → white), BGR, three bytes per pixel.
    internal byte[] Normalized { get; }
    public double AutomaticThreshold { get; }
    public int AutomaticSpeckSize { get; }

    internal SketchSheet(int width, int height, float[] darkness, byte[] normalized, double threshold, int speck)
    {
        Width = width; Height = height; Darkness = darkness; Normalized = normalized; AutomaticThreshold = threshold; AutomaticSpeckSize = speck;
    }
}

// 스케치 사진 정리: a phone photo of a hand sketch → clean line art. The sheet is found (or given by its four
// corners), its perspective flattened to a rectangle, the paper's uneven lighting divided out, and the
// lines kept as coverage with the shared line-art core (LineArt): paper and faint marks become clear,
// pen and pencil lines keep their color (or take black or a chosen color). Pure functions on rasters;
// every step can be cancelled and runs off the UI thread at full photo resolution.
public static partial class SketchCleanup
{
    // Results are kept within the size automation may create (one side, total pixels).
    public const int MaxSide = 8192;
    public const long MaxPixels = 16_777_216;
    // An A-series sheet (1:√2) is assumed when the measured ratio is this close.
    public const double PaperRatioTolerance = .03;
    // The automatic line threshold stays in this range: faint ruled lines and smudges below, pencil above.
    public const double MinAutoThreshold = .22, MaxAutoThreshold = .6;

    public static SketchResult Clean(Raster photo, IReadOnlyList<Point>? corners, SketchOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(photo); ArgumentNullException.ThrowIfNull(options);
        SheetDetection? detection = null;
        Point[]? quad = null;
        if (options.Flatten)
        {
            if (corners != null) quad = Corners(corners, photo.Width, photo.Height);
            else
            {
                detection = DetectSheet(photo, token);
                if (detection.Found) quad = detection.Corners;
            }
        }
        token.ThrowIfCancellationRequested();
        Raster sheet;
        if (quad != null) { var (width, height) = FlatSize(quad, photo.Width, photo.Height); sheet = Flatten(photo, quad, width, height, token); }
        else { var (width, height) = WholeSize(photo.Width, photo.Height); sheet = Flatten(photo, ImageCorners(photo.Width, photo.Height), width, height, token); }
        var prepared = Prepare(sheet, token);
        var lines = Extract(prepared, options, out double threshold, out int speck, token);
        return new SketchResult(lines, quad, threshold, prepared.AutomaticThreshold, speck, detection);
    }

    public static Point[] ImageCorners(int width, int height) => [new(0, 0), new(width, 0), new(width, height), new(0, height)];

    // Four corners in the order top-left, top-right, bottom-right, bottom-left, checked to form a convex
    // quadrilateral near the image. A mirrored (counter-clockwise) order is turned around so a sheet is
    // never flattened in mirror image; the first corner stays first.
    public static Point[] Corners(IReadOnlyList<Point> corners, int width, int height)
    {
        if (corners == null || corners.Count != 4) throw new InvalidDataException("꼭짓점 네 개를 지정하세요.");
        var points = corners.ToArray();
        double margin = Math.Max(width, height) * .5;
        foreach (var p in points)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < -margin || p.Y < -margin || p.X > width + margin || p.Y > height + margin)
                throw new InvalidDataException("꼭짓점이 사진에서 너무 멀리 있습니다.");
        if (SignedArea(points) < 0) points = [points[0], points[3], points[2], points[1]];
        try { new WarpQuad(points[0], points[1], points[2], points[3]).Validate(); }
        catch (InvalidDataException) { throw new InvalidDataException("네 꼭짓점이 볼록한 사각형이 되도록 맞추세요."); }
        if (Math.Abs(SignedArea(points)) < 64) throw new InvalidDataException("네 꼭짓점이 너무 가깝습니다. 종이의 네 모서리에 맞추세요.");
        return points;
    }

    // Shoelace area with y pointing down: positive for top-left → top-right → bottom-right → bottom-left.
    internal static double SignedArea(IReadOnlyList<Point> p)
    {
        double sum = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; sum += a.X * b.Y - b.X * a.Y; }
        return sum / 2;
    }

    // ---- Flattening --------------------------------------------------------------------------

    // The flattened sheet's size: its aspect (Aspect, an A-series ratio when within PaperRatioTolerance)
    // at about the photo's own pixel density (the quadrilateral's area), within MaxPixels and MaxSide.
    public static (int Width, int Height) FlatSize(IReadOnlyList<Point> corners, int photoWidth, int photoHeight)
    {
        if (corners.Count != 4) throw new ArgumentException("Four corners are required.", nameof(corners));
        double aspect = Aspect(corners, photoWidth, photoHeight), ratio = Math.Max(aspect, 1 / aspect);
        if (Math.Abs(ratio / Math.Sqrt(2) - 1) <= PaperRatioTolerance) aspect = aspect >= 1 ? Math.Sqrt(2) : 1 / Math.Sqrt(2);
        double area = Math.Max(256, Math.Abs(SignedArea(corners)));
        double width = Math.Sqrt(area * aspect), height = width / aspect;
        return Fit(width, height);
    }

    // Width ÷ height of the rectangle the quadrilateral pictures. A photo is a pinhole projection: with the
    // principal point at the image center, the two vanishing directions of the sheet's sides give the
    // focal length and the true proportions (Zhang & He's whiteboard rectification). When the sides do
    // not converge both ways (straight-on or tilted only up or down, the common phone shot) the focal
    // length is that of a phone's main camera (0.6 × the image diagonal); a parallelogram needs none.
    // Implausible configurations fall back to the averaged opposite sides.
    public static double Aspect(IReadOnlyList<Point> q, int photoWidth, int photoHeight)
    {
        static double Distance(Point a, Point b) => (a - b).Length;
        double averaged = Math.Max(1, (Distance(q[0], q[1]) + Distance(q[3], q[2])) / 2) / Math.Max(1, (Distance(q[0], q[3]) + Distance(q[1], q[2])) / 2);
        double u0 = photoWidth / 2.0, v0 = photoHeight / 2.0, diagonal = Math.Sqrt((double)photoWidth * photoWidth + (double)photoHeight * photoHeight);
        // Homogeneous corners: m1 top-left, m2 top-right, m3 bottom-left, m4 bottom-right.
        static (double X, double Y, double Z) H(Point p) => (p.X, p.Y, 1);
        static (double X, double Y, double Z) CrossH((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var m1 = H(q[0]); var m2 = H(q[1]); var m3 = H(q[3]); var m4 = H(q[2]);
        double d2 = Dot(CrossH(m2, m4), m3), d3 = Dot(CrossH(m3, m4), m2);
        if (Math.Abs(d2) < 1e-9 || Math.Abs(d3) < 1e-9) return averaged;
        double k2 = Dot(CrossH(m1, m4), m3) / d2, k3 = Dot(CrossH(m1, m4), m2) / d3;
        var n2 = (X: k2 * m2.X - m1.X, Y: k2 * m2.Y - m1.Y, Z: k2 * m2.Z - m1.Z);
        var n3 = (X: k3 * m3.X - m1.X, Y: k3 * m3.Y - m1.Y, Z: k3 * m3.Z - m1.Z);
        // The third components are each side's convergence relative to its length.
        double scale2 = Math.Sqrt(n2.X * n2.X + n2.Y * n2.Y), scale3 = Math.Sqrt(n3.X * n3.X + n3.Y * n3.Y);
        if (scale2 < 1e-9 || scale3 < 1e-9) return averaged;
        double c2 = Math.Abs(n2.Z) * diagonal / scale2, c3 = Math.Abs(n3.Z) * diagonal / scale3;
        double focal = .6 * diagonal;
        if (c2 > .02 && c3 > .02)
        {
            double f2 = -((n2.X - u0 * n2.Z) * (n3.X - u0 * n3.Z) + (n2.Y - v0 * n2.Z) * (n3.Y - v0 * n3.Z)) / (n2.Z * n3.Z);
            if (f2 > 0 && Math.Sqrt(f2) is var solved && solved >= .25 * diagonal && solved <= 4 * diagonal) focal = solved;
        }
        double Norm2((double X, double Y, double Z) n) => ((n.X - u0 * n.Z) * (n.X - u0 * n.Z) + (n.Y - v0 * n.Z) * (n.Y - v0 * n.Z)) / (focal * focal) + n.Z * n.Z;
        double aspect = Math.Sqrt(Norm2(n2) / Norm2(n3));
        return double.IsFinite(aspect) && aspect > .05 && aspect < 20 && aspect / averaged is > .5 and < 2 ? aspect : averaged;
    }

    // The whole photo's size within the result limits (no aspect change).
    public static (int Width, int Height) WholeSize(int width, int height) => Fit(width, height);

    static (int Width, int Height) Fit(double width, double height)
    {
        double fit = Math.Min(1, Math.Min(Math.Sqrt(MaxPixels / (width * height)), MaxSide / Math.Max(width, height)));
        int w = fit < 1 ? (int)Math.Floor(width * fit) : (int)Math.Round(width), h = fit < 1 ? (int)Math.Floor(height * fit) : (int)Math.Round(height);
        return (Math.Clamp(w, 16, MaxSide), Math.Clamp(h, 16, MaxSide));
    }

    // Resamples the quadrilateral (top-left, top-right, bottom-right, bottom-left) into a width × height
    // rectangle through its homography: bilinear taps, several per pixel where the photo is denser than
    // the result. Transparency and anything outside the photo read as white paper.
    public static Raster Flatten(Raster photo, IReadOnlyList<Point> corners, int width, int height, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        Raster.ValidateSize(width, height);
        if ((long)width * height > MaxPixels) throw new InvalidDataException("결과 이미지가 너무 큽니다.");
        var quad = Corners(corners, photo.Width, photo.Height);
        var map = new WarpQuad(quad[0], quad[1], quad[2], quad[3]).Map();
        double density = Math.Sqrt(Math.Abs(SignedArea(quad)) / ((double)width * height));
        int taps = density <= 1.25 ? 1 : Math.Min(4, (int)Math.Ceiling(density));
        var output = new Raster(width, height);
        byte[] source = photo.Data, target = output.Data; int sw = photo.Width, sh = photo.Height;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double b = 0, g = 0, r = 0;
                for (int sy = 0; sy < taps; sy++)
                    for (int sx = 0; sx < taps; sx++)
                    {
                        var p = map.Transform(new Point((x + (sx + .5) / taps) / width, (y + (sy + .5) / taps) / height));
                        var (bb, gg, rr) = SampleOverWhite(source, sw, sh, p.X, p.Y);
                        b += bb; g += gg; r += rr;
                    }
                int n = taps * taps, d = (y * width + x) * 4;
                target[d] = Imaging.Byte(b / n); target[d + 1] = Imaging.Byte(g / n); target[d + 2] = Imaging.Byte(r / n); target[d + 3] = 255;
            }
        });
        return output;
    }

    // Bilinear BGR at a continuous position (pixel centers at +0.5) over white paper.
    static (double B, double G, double R) SampleOverWhite(byte[] data, int width, int height, double px, double py)
    {
        if (!double.IsFinite(px) || !double.IsFinite(py)) return (255, 255, 255);
        double sx = px - .5, sy = py - .5;
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        double fx = sx - x0, fy = sy - y0, b = 0, g = 0, r = 0;
        void Tap(int x, int y, double weight)
        {
            if (weight <= 0) return;
            if (x < 0 || y < 0 || x >= width || y >= height) { b += 255 * weight; g += 255 * weight; r += 255 * weight; return; }
            int i = (y * width + x) * 4; double a = data[i + 3] / 255d, paper = 255 * (1 - a);
            b += (data[i] * a + paper) * weight; g += (data[i + 1] * a + paper) * weight; r += (data[i + 2] * a + paper) * weight;
        }
        Tap(x0, y0, (1 - fx) * (1 - fy)); Tap(x0 + 1, y0, fx * (1 - fy)); Tap(x0, y0 + 1, (1 - fx) * fy); Tap(x0 + 1, y0 + 1, fx * fy);
        return (b, g, r);
    }

    // ---- Even lighting -----------------------------------------------------------------------

    // Estimates the paper's lighting at large scale and divides it out, so a shadowed corner reads as
    // white paper and a pen line in it as dark as in the light. The estimate is a grid of cells (about
    // 1/240 of the sheet): each cell's bright tone (the mean color of its brightest quarter), closed
    // (local maximum, then minimum, over about 1/12 of the sheet) so lines, text and fills up to that
    // size do not count as paper, and lightly smoothed. Large dark areas (a table at the edge) read as
    // paper too; regions far darker than the sheet's paper are dropped rather than amplified.
    public static SketchSheet Prepare(Raster sheet, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        int w = sheet.Width, h = sheet.Height, longest = Math.Max(w, h);
        int cell = Math.Clamp((int)Math.Round(longest / 240.0), 2, 64);
        int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
        byte[] data = sheet.Data;
        var tone = new float[3][]; for (int c = 0; c < 3; c++) tone[c] = new float[gw * gh];
        var options = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, gh, options, () => new int[256], (gy, _, histogram) =>
        {
            for (int gx = 0; gx < gw; gx++)
            {
                Array.Clear(histogram);
                int x0 = gx * cell, y0 = gy * cell, x1 = Math.Min(w, x0 + cell), y1 = Math.Min(h, y0 + cell), count = 0;
                for (int y = y0; y < y1; y++) for (int x = x0, i = (y * w + x0) * 4; x < x1; x++, i += 4) { histogram[Luma(data, i)]++; count++; }
                // The brightest quarter of the cell: lines rarely cover more than half of a cell.
                int keep = Math.Max(1, count / 4), seen = 0, cut = 255;
                for (; cut > 0; cut--) { seen += histogram[cut]; if (seen >= keep) break; }
                double b = 0, g = 0, r = 0; int n = 0;
                for (int y = y0; y < y1; y++) for (int x = x0, i = (y * w + x0) * 4; x < x1; x++, i += 4)
                    if (Luma(data, i) >= cut) { b += data[i]; g += data[i + 1]; r += data[i + 2]; n++; }
                int k = gy * gw + gx;
                tone[0][k] = (float)(b / n); tone[1][k] = (float)(g / n); tone[2][k] = (float)(r / n);
            }
            return histogram;
        }, _ => { });
        int radius = Math.Max(1, (int)Math.Round(longest / 24.0 / cell));
        for (int c = 0; c < 3; c++)
        {
            token.ThrowIfCancellationRequested();
            var closed = Morphology(Morphology(tone[c], gw, gh, radius, true), gw, gh, radius, false);
            var smooth = Box(Box(closed, gw, gh, 1), gw, gh, 1);
            // Smoothing may only lower the estimate: across a shadow's edge it then errs toward white paper.
            for (int i = 0; i < closed.Length; i++) tone[c][i] = Math.Min(closed[i], smooth[i]);
        }
        // Regions far darker than the sheet's bright paper are not paper (a table beyond the sheet, a deep
        // shadow): they are left clear instead of being amplified into noise.
        var luma = new float[gw * gh];
        for (int i = 0; i < luma.Length; i++) luma[i] = (float)(.0722 * tone[0][i] + .7152 * tone[1][i] + .2126 * tone[2][i]);
        var sorted = (float[])luma.Clone(); Array.Sort(sorted);
        float floor = Math.Max(8, .2f * sorted[(int)((sorted.Length - 1) * .9)]);

        var darkness = new float[w * h]; var normalized = new byte[w * h * 3];
        Parallel.For(0, h, options, y =>
        {
            double gyf = Math.Clamp((y + .5) / cell - .5, 0, gh - 1); int ya = (int)gyf, yb = Math.Min(gh - 1, ya + 1); double ty = gyf - ya;
            for (int x = 0; x < w; x++)
            {
                double gxf = Math.Clamp((x + .5) / cell - .5, 0, gw - 1); int xa = (int)gxf, xb = Math.Min(gw - 1, xa + 1); double tx = gxf - xa;
                int i00 = ya * gw + xa, i01 = ya * gw + xb, i10 = yb * gw + xa, i11 = yb * gw + xb;
                double Lerp(float[] g) => (g[i00] * (1 - tx) + g[i01] * tx) * (1 - ty) + (g[i10] * (1 - tx) + g[i11] * tx) * ty;
                double pb = Lerp(tone[0]), pg = Lerp(tone[1]), pr = Lerp(tone[2]);
                int p = y * w + x, i = p * 4, n = p * 3;
                if (.0722 * pb + .7152 * pg + .2126 * pr < floor)
                {
                    normalized[n] = normalized[n + 1] = normalized[n + 2] = 255; continue;
                }
                double nb = Math.Min(1, data[i] / Math.Max(1, pb)), ng = Math.Min(1, data[i + 1] / Math.Max(1, pg)), nr = Math.Min(1, data[i + 2] / Math.Max(1, pr));
                normalized[n] = Imaging.Byte(nb * 255); normalized[n + 1] = Imaging.Byte(ng * 255); normalized[n + 2] = Imaging.Byte(nr * 255);
                darkness[p] = (float)Math.Clamp(1 - (.0722 * nb + .7152 * ng + .2126 * nr), 0, 1);
            }
        });
        double automatic = Math.Clamp(LineArt.Otsu(darkness), MinAutoThreshold, MaxAutoThreshold);
        return new SketchSheet(w, h, darkness, normalized, automatic, AutomaticSpeckSize(w, h));
    }

    // Dust-sized marks, up to about 0.7 mm across on a photographed page: 16 px at a 2,000 px sheet,
    // growing with the square of the size.
    public static int AutomaticSpeckSize(int width, int height)
    {
        double scale = Math.Max(width, height) / 2000.0;
        return Math.Clamp((int)Math.Round(16 * scale * scale), 3, 600);
    }

    static int Luma(byte[] data, int i) => (int)((.0722 * data[i] + .7152 * data[i + 1] + .2126 * data[i + 2]) + .5);

    // Separable square maximum (or minimum) of a float grid.
    internal static float[] Morphology(float[] source, int w, int h, int radius, bool maximum)
    {
        var across = new float[source.Length]; var result = new float[source.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = source[y * w + x];
                for (int k = Math.Max(0, x - radius), end = Math.Min(w - 1, x + radius); k <= end; k++) { float s = source[y * w + k]; if (maximum ? s > v : s < v) v = s; }
                across[y * w + x] = v;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = across[y * w + x];
                for (int k = Math.Max(0, y - radius), end = Math.Min(h - 1, y + radius); k <= end; k++) { float s = across[k * w + x]; if (maximum ? s > v : s < v) v = s; }
                result[y * w + x] = v;
            }
        return result;
    }

    // Separable box mean with clamped edges.
    internal static float[] Box(float[] source, int w, int h, int radius)
    {
        var across = new float[source.Length]; var result = new float[source.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, x - radius), end = Math.Min(w - 1, x + radius); k <= end; k++) { sum += source[y * w + k]; n++; }
                across[y * w + x] = (float)(sum / n);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, y - radius), end = Math.Min(h - 1, y + radius); k <= end; k++) { sum += across[k * w + x]; n++; }
                result[y * w + x] = (float)(sum / n);
            }
        return result;
    }

    // ---- Lines -------------------------------------------------------------------------------

    // The transparent line layer. Pixels at least as dark as the threshold are line cores; connected
    // cores smaller than the speck size are dropped; coverage comes from the shared band (LineArt) only
    // next to the remaining cores, so faint ruled lines and smudges below the threshold vanish while
    // the strokes keep antialiased edges. Boldness pushes partial coverage toward solid. Original color
    // recovers each pixel's ink from the paper-normalized photo (unmixed from white by its coverage,
    // smoothed among neighbouring line pixels), so a blue pen stays blue.
    public static Raster Extract(SketchSheet sheet, SketchOptions options, out double threshold, out int speckSize, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sheet); ArgumentNullException.ThrowIfNull(options);
        threshold = Math.Clamp(options.Threshold is { } t && double.IsFinite(t) ? t : sheet.AutomaticThreshold, .02, .98);
        speckSize = Math.Clamp(options.SpeckSize ?? sheet.AutomaticSpeckSize, 0, 1_000_000);
        double boldness = double.IsFinite(options.Boldness) ? Math.Clamp(options.Boldness, 0, 1) : 0;
        int w = sheet.Width, h = sheet.Height, longest = Math.Max(w, h);
        var darkness = sheet.Darkness; var normalized = sheet.Normalized;
        var band = LineArt.Measure(darkness, threshold);
        double cut = threshold;
        var core = new byte[w * h];
        var parallel = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, h, parallel, y => { for (int i = y * w, end = i + w; i < end; i++) if (darkness[i] >= cut) core[i] = 1; });
        // The sheet's own antialiased border (paper mixed with the table) adds to faint marks that reach it,
        // so the outermost ring (about 0.4 mm on a page) never holds a line core; strips go further.
        ClearRing(core, w, h, Math.Max(2, (int)Math.Round(longest / 800.0)));
        ClearEdgeStrips(core, w, h, Math.Max(3, (int)Math.Round(longest * .02)));
        if (speckSize > 1) RemoveSpecks(core, w, h, speckSize, token);
        if (!core.Contains((byte)1)) throw new InvalidDataException("이미지에서 선을 찾지 못했습니다. 선 인식 기준을 낮추거나 얼룩 정리를 줄여 보세요.");
        int halo = Math.Clamp((int)Math.Round(longest / 2000.0), 1, 3);
        var near = Dilate(core, w, h, halo, token);

        // Coverage and unmixed ink: observed = (1 − α)·white + α·ink, so ink = 1 − (1 − observed) / α.
        // Bytes keep a 16-megapixel sheet's working memory modest.
        bool original = options.LineColor == SketchLineColor.Original;
        var alpha = new byte[w * h]; var ink = original ? new byte[w * h * 3] : [];
        Parallel.For(0, h, parallel, y =>
        {
            for (int p = y * w, end = p + w; p < end; p++)
            {
                if (near[p] == 0) continue;
                double a = band.Coverage(darkness[p]);
                byte stored = Imaging.Byte(a * 255);
                if (stored == 0) continue;
                alpha[p] = stored;
                if (!original) continue;
                int n = p * 3; double used = stored / 255.0;
                for (int c = 0; c < 3; c++) ink[n + c] = Imaging.Byte(Math.Clamp(1 - (1 - normalized[n + c] / 255.0) / used, 0, 1) * 255);
            }
        });
        near = []; core = [];
        double exponent = 1 + 3 * boldness;
        var shownAlpha = new byte[256];
        for (int v = 0; v < 256; v++) shownAlpha[v] = Imaging.Byte((1 - Math.Pow(1 - v / 255.0, exponent)) * 255);
        uint custom = options.CustomColor;
        double customAlpha = (custom >> 24) / 255.0;
        var output = new Raster(w, h); var target = output.Data;
        Parallel.For(0, h, parallel, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x; int a = alpha[p];
                if (a == 0) continue;
                int d = p * 4;
                if (original)
                {
                    // Neighbours weighted by α²: solid pixels set the color, faint edges follow them.
                    double b = 0, g = 0, r = 0, weight = 0;
                    for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                        {
                            int q = yy * w + xx; int aq = alpha[q];
                            if (aq == 0) continue;
                            double wq = aq * aq; int n = q * 3;
                            b += ink[n] * wq; g += ink[n + 1] * wq; r += ink[n + 2] * wq; weight += wq;
                        }
                    target[d] = Imaging.Byte(b / weight); target[d + 1] = Imaging.Byte(g / weight); target[d + 2] = Imaging.Byte(r / weight);
                    target[d + 3] = shownAlpha[a];
                }
                else if (options.LineColor == SketchLineColor.Black) target[d + 3] = shownAlpha[a];
                else
                {
                    target[d] = (byte)custom; target[d + 1] = (byte)(custom >> 8); target[d + 2] = (byte)(custom >> 16);
                    target[d + 3] = Imaging.Byte(shownAlpha[a] * customAlpha);
                }
            }
        });
        return output;
    }

    public static Raster Extract(SketchSheet sheet, SketchOptions options, CancellationToken token = default) => Extract(sheet, options, out _, out _, token);

    internal static void ClearRing(byte[] core, int w, int h, int width)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            if (y < width || y >= h - width) Array.Clear(core, row, w);
            else { Array.Clear(core, row, Math.Min(width, w)); Array.Clear(core, row + Math.Max(0, w - width), Math.Min(width, w)); }
        }
    }

    // A strip of table or of the paper's own edge shadow along a side (corners placed a little outside
    // the sheet, a soft sheet border) is not a line: dark pixels within three pixels of a side that run
    // along it for a long stretch (short gaps bridged) are cleared, inward up to `band` pixels. A line
    // that runs off the sheet touches the side only for its own width and stays.
    internal static void ClearEdgeStrips(byte[] core, int w, int h, int band)
    {
        // Every side reads the mask as it was, so where two strips meet in a corner neither hides the other.
        var original = (byte[])core.Clone();
        void Side(int length, int depth, Func<int, int, int> index)
        {
            int minimum = Math.Max(4 * band, (int)(length * .03)), gap = Math.Max(2, band / 2), reach = Math.Min(3, depth);
            // Where along the side a dark run begins (depth from the side), or -1.
            var begins = new int[length];
            for (int t = 0; t < length; t++)
            {
                begins[t] = -1;
                for (int d = 0; d < reach; d++) if (original[index(t, d)] != 0) { begins[t] = d; break; }
            }
            // Inward from where the run begins, over single-pixel gaps (the strip's soft inner edge).
            void Clear(int from, int to)
            {
                if (to - from + 1 < minimum) return;
                for (int s = from; s <= to; s++)
                {
                    if (begins[s] < 0) continue;
                    for (int d = begins[s], misses = 0; d < depth; d++)
                    {
                        int i = index(s, d);
                        if (original[i] != 0) { core[i] = 0; misses = 0; }
                        else if (++misses > 1) break;
                    }
                }
            }
            int start = -1, last = -1;
            for (int t = 0; t < length; t++)
            {
                if (begins[t] < 0) continue;
                if (start >= 0 && t - last - 1 > gap) { Clear(start, last); start = -1; }
                if (start < 0) start = t;
                last = t;
            }
            if (start >= 0) Clear(start, last);
        }
        int across = Math.Min(band, w), down = Math.Min(band, h);
        Side(h, across, (t, d) => t * w + d);
        Side(h, across, (t, d) => t * w + w - 1 - d);
        Side(w, down, (t, d) => d * w + t);
        Side(w, down, (t, d) => (h - 1 - d) * w + t);
    }

    // Clears 8-connected groups of core pixels smaller than `minimum`. Returns how many groups went.
    internal static int RemoveSpecks(byte[] core, int w, int h, int minimum, CancellationToken token = default)
    {
        var stack = new int[4096]; var members = new int[minimum]; int removed = 0;
        for (int start = 0; start < core.Length; start++)
        {
            if ((start & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            if (core[start] != 1) continue;
            int count = 0, top = 0; stack[top++] = start; core[start] = 2;
            while (top > 0)
            {
                int p = stack[--top];
                if (count < minimum) members[count] = p;
                count++;
                int x = p % w, y = p / w;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                    {
                        int q = yy * w + xx;
                        if (core[q] != 1) continue;
                        core[q] = 2;
                        if (top == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                        stack[top++] = q;
                    }
            }
            if (count < minimum) { for (int k = 0; k < count; k++) core[members[k]] = 0; removed++; }
        }
        for (int i = 0; i < core.Length; i++) if (core[i] == 2) core[i] = 1;
        return removed;
    }

    // Square dilation of a 0/1 mask.
    internal static byte[] Dilate(byte[] mask, int w, int h, int radius, CancellationToken token = default)
    {
        var across = new byte[mask.Length]; var result = new byte[mask.Length];
        var options = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, h, options, y =>
        {
            int row = y * w, last = -radius - 1;
            for (int x = 0; x < w; x++) { if (mask[row + x] != 0) last = x; if (x - last <= radius) across[row + x] = 1; }
            last = w + radius + 1;
            for (int x = w - 1; x >= 0; x--) { if (mask[row + x] != 0) last = x; if (last - x <= radius) across[row + x] = 1; }
        });
        Parallel.For(0, w, options, x =>
        {
            int last = -radius - 1;
            for (int y = 0; y < h; y++) { if (across[y * w + x] != 0) last = y; if (y - last <= radius) result[y * w + x] = 1; }
            last = h + radius + 1;
            for (int y = h - 1; y >= 0; y--) { if (across[y * w + x] != 0) last = y; if (last - y <= radius) result[y * w + x] = 1; }
        });
        return result;
    }
}
