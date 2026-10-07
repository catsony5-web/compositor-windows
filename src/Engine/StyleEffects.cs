using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Pixel work of the design-style adjustment layers: 한계값 (threshold), 망점 (halftone screen),
/// 종이·인쇄 질감 (paper, photocopy and edge wear) and 빛 번짐 (glow).
/// Every effect is evaluated in the adjustment layer's own coordinates through the view transform,
/// so the canvas at any zoom, a scaled export and the document-size image agree: screens and paper
/// grain stay fixed to the document, lengths are document pixels, and detail smaller than a device
/// pixel fades to its average instead of aliasing. Noise comes from hashed integer lattices and the
/// layer's seed: no repeating tile, and the same settings always give the same pixels.
/// </summary>
public static class StyleEffects
{
    public static bool Handles(AdjustmentKind kind) => kind is AdjustmentKind.Threshold or AdjustmentKind.Halftone or AdjustmentKind.PaperTexture or AdjustmentKind.Glow;

    /// <summary>The effect writes alpha as well as color (한계값 without the original transparency).</summary>
    public static bool ChangesAlpha(AdjustmentSpec spec) => spec.Kind == AdjustmentKind.Threshold && !spec.Threshold.KeepAlpha;

    /// <summary>These settings leave every pixel unchanged.</summary>
    public static bool IsIdentity(AdjustmentSpec spec) => spec.Kind switch
    {
        AdjustmentKind.Glow => spec.Glow.Intensity == 0,
        AdjustmentKind.PaperTexture => spec.Paper.IsNeutral,
        _ => false
    };

    /// <summary>How far (layer pixels) a pixel's result depends on its surroundings: light spreading in from outside a rendered area.</summary>
    public static double Reach(AdjustmentSpec spec) => spec.Kind == AdjustmentKind.Glow && spec.Glow.Intensity > 0 ? spec.Glow.Radius : 0;

    /// <summary>Device pixels a render must add around its area so spreading light from outside is included.</summary>
    internal static int ReachPixels(AdjustmentSpec spec, double deviceScale)
    {
        double reach = Reach(spec) * deviceScale;
        if (reach <= 0) return 0;
        // The widest blur level spans 3σ = reach; averaging into cells and resampling back add a few cells.
        return (int)Math.Ceiling(reach + 4 * Cell(reach / 3) + 4);
    }

    /// <summary>A visible glow layer makes every pixel depend on its neighbors (partial redraws are not exact).</summary>
    public static bool Spreads(Document document) =>
        document.Layers.Any(layer => layer.Kind == LayerKind.Adjustment && layer.Visible && layer.Opacity > 0 && layer.Adjustment is { } spec && Reach(spec) > 0);

    /// <summary>
    /// Applies the effect to <paramref name="source"/> without changing it. <paramref name="layerToTarget"/> maps the
    /// adjustment layer's pixels to the raster's pixels; the page is the layer's pixel size (where rough edges sit).
    /// </summary>
    public static Raster Apply(Raster source, AdjustmentSpec spec, Matrix layerToTarget, double pageWidth, double pageHeight, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(spec);
        spec.Validate(); token.ThrowIfCancellationRequested();
        if (!Handles(spec.Kind)) throw new ArgumentException("스타일 효과 조정이 아닙니다.", nameof(spec));
        if (IsIdentity(spec)) return source.Clone();
        var space = new Space(layerToTarget, pageWidth, pageHeight);
        return spec.Kind switch
        {
            AdjustmentKind.Threshold => Threshold(source, spec.Threshold, token),
            AdjustmentKind.Halftone => Halftone(source, spec.Halftone, space, token),
            AdjustmentKind.PaperTexture => Paper(source, spec.Paper, space, token),
            _ => Glow(source, spec.Glow, space, token)
        };
    }

    /// <summary>Where raster pixel centers lie in layer coordinates, and how many device pixels one layer pixel covers.</summary>
    readonly struct Space
    {
        // layer point of raster pixel center (x, y): (A·x + C·y + E, B·x + D·y + F)
        public readonly double A, B, C, D, E, F, Scale, Width, Height, OriginX, OriginY;
        public Space(Matrix layerToTarget, double width, double height)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            double det = layerToTarget.M11 * layerToTarget.M22 - layerToTarget.M12 * layerToTarget.M21;
            if (!double.IsFinite(det) || Math.Abs(det) < 1e-12) throw new ArgumentException("조정 레이어 변환을 역산할 수 없습니다.", nameof(layerToTarget));
            var inverse = layerToTarget; inverse.Invert();
            A = inverse.M11; B = inverse.M12; C = inverse.M21; D = inverse.M22;
            E = inverse.OffsetX + .5 * (A + C); F = inverse.OffsetY + .5 * (B + D);
            Scale = Math.Sqrt(Math.Abs(det)); Width = width; Height = height;
            OriginX = layerToTarget.OffsetX; OriginY = layerToTarget.OffsetY;
        }
    }

    static ParallelOptions Options(CancellationToken token) => new() { CancellationToken = token };
    static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    // Rec. 709 luminance of 8-bit display values in 1/10000 of a level: exact for grays.
    static int Luma10k(byte[] data, int i) => 2126 * data[i + 2] + 7152 * data[i + 1] + 722 * data[i];

    // ---------------------------------------------------------------- 한계값

    static Raster Threshold(Raster source, ThresholdSpec spec, CancellationToken token)
    {
        var result = new Raster(source.Width, source.Height);
        byte[] src = source.Data, dst = result.Data;
        // Luminance at or above the level is white, so a gray of exactly the level is white. A soft edge
        // ramps up over the levels just below it and reaches white at the level, whatever its width.
        double level = spec.Level * 10000, soft = spec.Smoothness * 10000;
        bool keepAlpha = spec.KeepAlpha;
        Parallel.For(0, source.Height, Options(token), y =>
        {
            for (int i = y * source.Width * 4, end = i + source.Width * 4; i < end; i += 4)
            {
                double luma = Luma10k(src, i);
                byte value = luma >= level ? (byte)255 : soft <= 0 ? (byte)0 : Imaging.Byte(255 * Smooth((luma - level + soft) / soft));
                dst[i] = dst[i + 1] = dst[i + 2] = value;
                byte alpha = src[i + 3];
                // Without the original transparency, coverage of 50% or more becomes opaque and the rest disappears.
                dst[i + 3] = keepAlpha || alpha >= 128 ? keepAlpha ? alpha : (byte)255 : soft <= 0 ? (byte)0 : Imaging.Byte(255 * Smooth((alpha - 128 + spec.Smoothness) / spec.Smoothness));
            }
        });
        return result;
    }

    // ---------------------------------------------------------------- 망점

    const int PhaseSteps = 4096;
    // cos(2πu) and sin(2πu) for u = -0.5 … 0.5 in PhaseSteps steps (cell-relative position).
    static readonly double[] cosTable = Enumerable.Range(0, PhaseSteps + 2).Select(i => Math.Cos(2 * Math.PI * (i / (double)PhaseSteps - .5))).ToArray();
    static readonly double[] sinTable = Enumerable.Range(0, PhaseSteps + 2).Select(i => Math.Sin(2 * Math.PI * (i / (double)PhaseSteps - .5))).ToArray();
    // Round dots grow from the cell center as the level set of s = (cos 2πu + cos 2πv) / 2. The table gives the
    // level t(d) whose region s > t covers exactly the fraction d of the cell, so ink coverage equals darkness.
    const int CoverageSteps = 1024;
    static readonly Lazy<double[]> roundLevels = new(BuildRoundLevels);

    static double[] BuildRoundLevels()
    {
        const int n = 512;
        var c = new double[n];
        for (int i = 0; i < n; i++) c[i] = Math.Cos(2 * Math.PI * ((i + .5) / n - .5));
        var samples = new double[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) samples[y * n + x] = (c[x] + c[y]) / 2;
        Array.Sort(samples); Array.Reverse(samples);
        var levels = new double[CoverageSteps + 1];
        levels[0] = 1; levels[CoverageSteps] = -1;
        for (int k = 1; k < CoverageSteps; k++)
        {
            double rank = k / (double)CoverageSteps * samples.Length - .5;
            int low = Math.Clamp((int)Math.Floor(rank), 0, samples.Length - 1), high = Math.Min(samples.Length - 1, low + 1);
            levels[k] = Lerp(samples[low], samples[high], rank - low);
        }
        return levels;
    }

    static double Interpolate(double[] table, double position)
    {
        position = Math.Clamp(position, 0, table.Length - 1.000001);
        int i = (int)position; return Lerp(table[i], table[i + 1], position - i);
    }

    /// <summary>Ink coverage of one pixel. u, v: position in the cell (-0.5…0.5); d: darkness; w: one device pixel in cell units.</summary>
    internal static double DotCoverage(HalftoneShape shape, double u, double v, double d, double w)
    {
        if (d <= 0) return 0;
        if (d >= 1) return 1;
        switch (shape)
        {
            case HalftoneShape.Line:
                // A band |v| < d/2 in every period, box-filtered over the pixel's width along v.
                return Periodic(Math.Abs(v), d / 2, w);
            case HalftoneShape.Square:
                // Squares |u|, |v| < √d / 2 grow until the cells close: ink is a band along u times a band along v.
                return Periodic(Math.Abs(u), Math.Sqrt(d) / 2, w) * Periodic(Math.Abs(v), Math.Sqrt(d) / 2, w);
            default:
                {
                    double pu = (u + .5) * PhaseSteps, pv = (v + .5) * PhaseSteps;
                    double s = (Interpolate(cosTable, pu) + Interpolate(cosTable, pv)) / 2;
                    double su = Interpolate(sinTable, pu), sv = Interpolate(sinTable, pv);
                    double gradient = Math.PI * Math.Sqrt(su * su + sv * sv);
                    double level = Interpolate(roundLevels.Value, d * CoverageSteps);
                    return Math.Clamp(.5 + (s - level) / Math.Max(gradient, 1e-9) / w, 0, 1);
                }
        }
    }

    // Coverage of a pixel of width w centered at distance x from the middle of a band of half-width h.
    static double Band(double x, double h, double w) =>
        Math.Max(0, Math.Clamp(.5 + (h - x) / w, 0, 1) + Math.Clamp(.5 + (h + x) / w, 0, 1) - 1);
    // The same for bands repeating every 1 (x = 0…0.5 from the nearest middle): the next band counts too.
    static double Periodic(double x, double h, double w) => Math.Min(1, Band(x, h, w) + Band(1 - x, h, w));

    static Raster Halftone(Raster source, HalftoneSpec spec, Space space, CancellationToken token)
    {
        var result = source.Clone();
        byte[] src = source.Data, dst = result.Data;
        double radians = spec.Angle * Math.PI / 180, cos = Math.Cos(radians) / spec.CellSize, sin = Math.Sin(radians) / spec.CellSize;
        double cellPixels = spec.CellSize * space.Scale, pixel = 1 / cellPixels;
        // Cells only a couple of device pixels wide cannot be drawn: they fade into their mean tone (no moiré).
        double detail = Math.Clamp((cellPixels - 2) / 2, 0, 1);
        if (spec.Shape == HalftoneShape.Round) _ = roundLevels.Value;
        var ink = DocumentFeatures.Color(spec.InkArgb); var paper = DocumentFeatures.Color(spec.PaperArgb);
        double inkAlpha = ink.A / 255.0, paperAlpha = paper.A / 255.0;
        Parallel.For(0, source.Height, Options(token), y =>
        {
            double layerX = space.C * y + space.E, layerY = space.D * y + space.F;
            for (int x = 0, i = y * source.Width * 4; x < source.Width; x++, i += 4, layerX += space.A, layerY += space.B)
            {
                double u = layerX * cos + layerY * sin, v = layerY * cos - layerX * sin;
                u -= Math.Floor(u) + .5; v -= Math.Floor(v) + .5;
                double darkness = 1 - Luma10k(src, i) / 2_550_000.0;
                double coverage = DotCoverage(spec.Shape, u, v, darkness, pixel);
                coverage = darkness + (coverage - darkness) * detail;
                for (int c = 0; c < 3; c++)
                {
                    byte channel = src[i + c], inkChannel = c == 0 ? ink.B : c == 1 ? ink.G : ink.R, paperChannel = c == 0 ? paper.B : c == 1 ? paper.G : paper.R;
                    double inkValue = channel + (inkChannel - channel) * inkAlpha, paperValue = channel + (paperChannel - channel) * paperAlpha;
                    dst[i + c] = Imaging.Byte(paperValue + (inkValue - paperValue) * coverage);
                }
            }
        });
        return result;
    }

    // ---------------------------------------------------------------- noise (hashed lattices, no tile)

    static readonly float[] gradientX = new float[256], gradientY = new float[256];
    static StyleEffects()
    {
        for (int i = 0; i < 256; i++) { double angle = 2 * Math.PI * (i + .5) / 256; gradientX[i] = (float)Math.Cos(angle); gradientY[i] = (float)Math.Sin(angle); }
    }

    internal static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed * 0x9E3779B9u ^ (uint)x * 0x85EBCA6Bu;
            h = BitOperations.RotateLeft(h, 13) * 0xC2B2AE35u ^ (uint)y * 0x27D4EB2Fu;
            h = BitOperations.RotateLeft(h, 15) * 0x165667B1u;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }
    }
    static double Unit(uint hash) => (hash >> 8) * (1.0 / 16777216);

    // Gradient noise (about -1…1) with one lattice point per unit. The lattice hash is a cheap multiply-mix:
    // only its top eight bits pick a gradient, and integer lattice coordinates never repeat as a tile.
    internal static double Noise(double x, double y, uint seed)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y);
        int ix = (int)fx, iy = (int)fy; double dx = x - fx, dy = y - fy;
        double u = dx * dx * dx * (dx * (dx * 6 - 15) + 10), v = dy * dy * dy * (dy * (dy * 6 - 15) + 10);
        uint column0 = unchecked((uint)ix * 0x8DA6B343u), column1 = unchecked(column0 + 0x8DA6B343u);
        uint row0 = unchecked((uint)iy * 0xD8163841u ^ seed), row1 = unchecked(((uint)iy + 1) * 0xD8163841u ^ seed);
        uint h00 = Lattice(column0 ^ row0), h10 = Lattice(column1 ^ row0), h01 = Lattice(column0 ^ row1), h11 = Lattice(column1 ^ row1);
        double n00 = gradientX[h00] * dx + gradientY[h00] * dy, n10 = gradientX[h10] * (dx - 1) + gradientY[h10] * dy;
        double n01 = gradientX[h01] * dx + gradientY[h01] * (dy - 1), n11 = gradientX[h11] * (dx - 1) + gradientY[h11] * (dy - 1);
        return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v) * 1.42;
    }
    static uint Lattice(uint h) { unchecked { h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; return h >> 24; } }

    // How much of a feature with this period (device pixels) can be shown: below about one pixel it averages away.
    static double Visible(double devicePeriod) => Math.Clamp(devicePeriod - 1, 0, 1);

    // Octaves from `period` (layer pixels) down by halves; octaves finer than a device pixel fade out,
    // keeping the normalization so a zoomed-out view is the low-passed full-size texture.
    static double Fractal(double x, double y, double period, int octaves, uint seed, double scale)
    {
        double sum = 0, norm = 0, amplitude = 1;
        for (int octave = 0; octave < octaves; octave++, period /= 2, amplitude *= .5)
        {
            norm += amplitude;
            double visible = Visible(period * scale);
            if (visible > 0) sum += amplitude * visible * Noise(x / period, y / period, seed + (uint)octave * 0x632BE5ABu);
        }
        return sum / norm;
    }

    // ---------------------------------------------------------------- 종이·인쇄 질감

    const int BandRows = 32, FiberStride = 8, SpeckStride = 11;

    // Strands or specks of the hashed cells around one band of rows, built once per band instead of
    // hashing every cell again for every pixel. Items of cell (qx, qy) are Items[Start[i]..Start[i + 1]).
    sealed class Scatter
    {
        public int MinX, MinY, Columns, Rows;
        public int[] Start = [];
        public double[] Items = [];
        public int Index(int qx, int qy) => (Math.Clamp(qy, MinY, MinY + Rows - 1) - MinY) * Columns + Math.Clamp(qx, MinX, MinX + Columns - 1) - MinX;

        public static Scatter Build(double minX, double minY, double maxX, double maxY, double cell, int stride, Func<int, int, List<double>, int> fill)
        {
            var scatter = new Scatter { MinX = (int)Math.Floor(minX / cell) - 2, MinY = (int)Math.Floor(minY / cell) - 2 };
            scatter.Columns = (int)Math.Floor(maxX / cell) + 3 - scatter.MinX; scatter.Rows = (int)Math.Floor(maxY / cell) + 3 - scatter.MinY;
            var start = new int[scatter.Columns * scatter.Rows + 1]; var items = new List<double>();
            for (int qy = 0; qy < scatter.Rows; qy++) for (int qx = 0; qx < scatter.Columns; qx++)
            {
                start[qy * scatter.Columns + qx] = items.Count / stride;
                fill(scatter.MinX + qx, scatter.MinY + qy, items);
            }
            start[^1] = items.Count / stride; scatter.Start = start; scatter.Items = items.ToArray();
            return scatter;
        }
    }

    // Seeded paper fibres: zero to two short, gently bent strands per cell, mostly darker, some lighter.
    static int Fibers(int qx, int qy, List<double> items, double cell, double s, uint seed)
    {
        double chance = Unit(Hash(qx, qy, seed + 101));
        int count = chance < .45 ? 1 : chance < .7 ? 2 : 0;
        for (int f = 0; f < count; f++)
        {
            int key = qx * 2 + f;
            uint place = Hash(key, qy, seed + 107), shape = Hash(key, qy, seed + 113);
            items.Add((qx + (place & 0xFFFF) / 65536.0) * cell); items.Add((qy + (place >> 16) / 65536.0) * cell);
            items.Add(gradientX[shape & 255]); items.Add(gradientY[shape & 255]);
            items.Add(cell * (.25 + .45 * (shape >> 8 & 255) / 255.0)); items.Add(s * (.16 + .3 * (shape >> 16 & 255) / 255.0));
            items.Add((shape >> 24) < 184 ? 1 : -.65); items.Add(.5 * (Unit(Hash(key, qy, seed + 127)) - .5));
        }
        return count;
    }

    // Photocopy toner: an irregular speck in some cells (more where the copy is dirtier), dark on
    // paper or a light dropout in solid ink. Most specks are tiny; a few are large.
    static int Specks(int qx, int qy, List<double> items, double cell, double s, uint seed, double chance)
    {
        uint h = Hash(qx, qy, seed + 601);
        double dirt = Math.Clamp(.15 + 1.5 * (Noise(qx * .13, qy * .13, seed + 677) + .35), .1, 2.5);
        if (Unit(h) >= chance * dirt) return 0;
        uint place = Hash(qx, qy, seed + 607), look = Hash(qx, qy, seed + 613);
        double size = (h & 255) / 255.0;
        items.Add((qx + (place & 0xFFFF) / 65536.0) * cell); items.Add((qy + (place >> 16) / 65536.0) * cell);
        items.Add(s * (.25 + 1.5 * size * size * size)); items.Add(.55 + .45 * (look & 255) / 255.0); items.Add((look >> 8 & 255) < 77 ? 1 : 0);
        // Outline wobble: two lobes and three lobes at seeded angles.
        items.Add(gradientX[look >> 16 & 255]); items.Add(gradientY[look >> 16 & 255]); items.Add(.1 + .25 * (look >> 24) / 255.0);
        uint lobes = Hash(qx, qy, seed + 619);
        items.Add(gradientX[lobes & 255]); items.Add(gradientY[lobes & 255]); items.Add(.2 * (lobes >> 8 & 255) / 255.0);
        return 1;
    }

    static Raster Paper(Raster source, PaperTextureSpec spec, Space space, CancellationToken token)
    {
        var result = source.Clone();
        byte[] src = source.Data, dst = result.Data;
        int width = source.Width, height = source.Height;
        uint seed = unchecked((uint)spec.Seed * 0x2545F491u + 0x6A09E667u);
        double s = spec.Scale, k = space.Scale, pixel = 1 / k;
        var tint = DocumentFeatures.Color(spec.TintArgb); double tintAmount = spec.Tint * tint.A / 255;
        double tintB = 1 + (tint.B / 255.0 - 1) * tintAmount, tintG = 1 + (tint.G / 255.0 - 1) * tintAmount, tintR = 1 + (tint.R / 255.0 - 1) * tintAmount;
        var edgeColor = DocumentFeatures.Color(spec.EdgeArgb); double edgeAlpha = edgeColor.A / 255.0;
        double reach = Math.Max(1, spec.EdgeWidth * Math.Min(space.Width, space.Height));
        double fiberCell = 18 * s, speckCell = 7 * s, streakBin = 48 * s, toothVisible = Visible(s * k), bandVisible = Visible(220 * s * k);
        // Strands and specks far below a device pixel are invisible at this zoom; their average is negligible.
        bool fibers = spec.Fibers > 0 && s * k >= .2, specks = spec.Toner > 0 && s * k >= .15;
        double speckChance = spec.Toner * .45, streakChance = .2 + .55 * spec.Streaks;
        Parallel.For(0, (height + BandRows - 1) / BandRows, Options(token), band =>
        {
            int firstRow = band * BandRows, lastRow = Math.Min(height, firstRow + BandRows) - 1;
            // Layer-space bounds of this band, from its corner pixel centers.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (int cy in new[] { firstRow, lastRow }) foreach (int cx in new[] { 0, width - 1 })
            {
                double lx = space.A * cx + space.C * cy + space.E, ly = space.B * cx + space.D * cy + space.F;
                minX = Math.Min(minX, lx); maxX = Math.Max(maxX, lx); minY = Math.Min(minY, ly); maxY = Math.Max(maxY, ly);
            }
            var strands = fibers ? Scatter.Build(minX, minY, maxX, maxY, fiberCell, FiberStride, (qx, qy, list) => Fibers(qx, qy, list, fiberCell, s, seed)) : null;
            var dots = specks ? Scatter.Build(minX, minY, maxX, maxY, speckCell, SpeckStride, (qx, qy, list) => Specks(qx, qy, list, speckCell, s, seed, speckChance)) : null;
            for (int y = firstRow; y <= lastRow; y++)
            {
                double layerX = space.C * y + space.E, layerY = space.D * y + space.F;
                for (int x = 0, i = y * width * 4; x < width; x++, i += 4, layerX += space.A, layerY += space.B)
                {
                    double px = layerX, py = layerY;
                    double b = src[i] / 255.0, g = src[i + 1] / 255.0, r = src[i + 2] / 255.0;
                    // Paper shows on light areas; ink covers it.
                    double paperShows = Smooth((Luma10k(src, i) / 2_550_000.0 - .08) / .6);
                    // Tinted paper: white takes the paper color, black ink stays black.
                    b *= tintB; g *= tintG; r *= tintR;
                    double shade = 0, lift = 0;
                    if (spec.Grain > 0)
                    {
                        double mottle = Fractal(px, py, 48 * s, 3, seed + 11, k);
                        double tooth = toothVisible * Noise(px / s, py / s, seed + 23);
                        shade += spec.Grain * (.1 * mottle + .09 * tooth) * paperShows;
                        // Dense ink is never perfectly even either.
                        lift += spec.Grain * .05 * Math.Max(0, mottle + .5 * tooth) * (1 - paperShows);
                    }
                    if (strands != null && paperShows > 0) shade -= spec.Fibers * .2 * Fiber(strands, px, py, k, pixel, fiberCell) * paperShows;
                    if (spec.Streaks > 0)
                    {
                        double streak = 0; int bin = (int)Math.Floor(px / streakBin);
                        for (int j = bin - 1; j <= bin + 1; j++)
                        {
                            if (Unit(Hash(j, 3, seed + 307)) >= streakChance) continue;
                            uint h = Hash(j, 5, seed + 307);
                            double center = (j + (h & 0xFFFF) / 65536.0) * streakBin, size = (h >> 16 & 255) / 255.0, half = s * (.25 + 1.4 * size * size);
                            double distance = Math.Abs(px - center);
                            if (distance > half + pixel) continue;
                            double profile = Smooth((Noise(j * 1.618 + .5, py / (160 * s), seed + 401) + .1) * 1.8);
                            streak = Math.Max(streak, Band(distance * k, half * k, 1) * profile * (.45 + .55 * (h >> 24) / 255.0));
                        }
                        double banding = bandVisible * Noise(px / (220 * s), .37, seed + 503);
                        shade -= spec.Streaks * (.5 * streak + .05 * banding) * paperShows;
                    }
                    double speck = 0, dropout = 0;
                    if (dots != null)
                    {
                        int cx = (int)Math.Floor(px / speckCell), cy = (int)Math.Floor(py / speckCell);
                        for (int qy = cy - 1; qy <= cy + 1; qy++) for (int qx = cx - 1; qx <= cx + 1; qx++)
                        {
                            int cellIndex = dots.Index(qx, qy);
                            for (int item = dots.Start[cellIndex], end = dots.Start[cellIndex + 1]; item < end; item++)
                            {
                                int o = item * SpeckStride; var d = dots.Items; double radius = d[o + 2];
                                double dx = px - d[o], dy = py - d[o + 1];
                                // Specks smaller than half a device pixel keep their area as a fainter dot.
                                double shown = Math.Max(radius, .5 * pixel), limit = shown * 1.6 + pixel, squared = dx * dx + dy * dy;
                                if (squared > limit * limit) continue;
                                double distance = Math.Sqrt(squared), outline = shown;
                                if (distance > 1e-9)
                                {
                                    double ux = dx / distance, uy = dy / distance;
                                    double two = (ux * ux - uy * uy) * d[o + 5] + 2 * ux * uy * d[o + 6], three = (4 * ux * ux * ux - 3 * ux) * d[o + 8] + (3 * uy - 4 * uy * uy * uy) * d[o + 9];
                                    outline = shown * (1 + d[o + 7] * two + d[o + 10] * three);
                                }
                                double cover = Math.Clamp(.5 + (outline - distance) * k, 0, 1) * (radius / shown) * (radius / shown) * d[o + 3];
                                if (d[o + 4] > 0) dropout = Math.Max(dropout, cover); else speck = Math.Max(speck, cover);
                            }
                        }
                    }
                    if (spec.Toner > 0)
                        // Copied solids are not quite solid.
                        lift += spec.Toner * .08 * Math.Max(0, toothVisible * Noise(px / (1.5 * s), py / (1.5 * s), seed + 653)) * (1 - paperShows);
                    double factor = 1 + shade;
                    b = b * factor + lift; g = g * factor + lift; r = r * factor + lift;
                    if (speck > 0) { double t = speck * paperShows; b += (.07 - b) * t; g += (.06 - g) * t; r += (.06 - r) * t; }
                    if (dropout > 0) { double t = dropout * (1 - paperShows) * .85; b += (tintB - b) * t; g += (tintG - g) * t; r += (tintR - r) * t; }
                    double inside = Math.Min(Math.Min(px, space.Width - px), Math.Min(py, space.Height - py));
                    if (spec.Edges > 0 && edgeAlpha > 0 && inside < reach * 1.7)
                    {
                        double rough = reach * (.55 * Fractal(px, py, reach * 1.3, 4, seed + 701, k) + .12 * Fractal(px, py, 3 * s, 2, seed + 709, k));
                        double t = (inside + rough) / reach;
                        double scorch = Math.Pow(1 - Smooth(t), 1.6), rim = 1 - Smooth(t / .18);
                        double amount = spec.Edges * Math.Clamp(.8 * scorch + .7 * rim, 0, 1) * edgeAlpha;
                        b += (edgeColor.B / 255.0 - b) * amount; g += (edgeColor.G / 255.0 - g) * amount; r += (edgeColor.R / 255.0 - r) * amount;
                    }
                    dst[i] = Imaging.Byte(b * 255); dst[i + 1] = Imaging.Byte(g * 255); dst[i + 2] = Imaging.Byte(r * 255);
                }
            }
        });
        return result;
    }

    // Darkening by the strands near a point (positive darker, negative lighter), box-filtered over one device pixel.
    static double Fiber(Scatter strands, double px, double py, double k, double pixel, double cell)
    {
        int cx = (int)Math.Floor(px / cell), cy = (int)Math.Floor(py / cell);
        double total = 0; var items = strands.Items;
        for (int qy = cy - 1; qy <= cy + 1; qy++) for (int qx = cx - 1; qx <= cx + 1; qx++)
        {
            int cellIndex = strands.Index(qx, qy);
            for (int item = strands.Start[cellIndex], end = strands.Start[cellIndex + 1]; item < end; item++)
            {
                int o = item * FiberStride;
                double dx = px - items[o], dy = py - items[o + 1], half = items[o + 4], width = items[o + 5];
                double limit = half * 1.15 + width + pixel;
                if (dx * dx + dy * dy > limit * limit) continue;
                double ca = items[o + 2], sa = items[o + 3];
                double along = dx * ca + dy * sa, across = dy * ca - dx * sa, t = Math.Clamp(along, -half, half), u = t / half;
                // A gentle bend across the strand's length.
                double ex = along - t, ey = across - items[o + 7] * half * (1 - u * u);
                double distance = Math.Sqrt(ex * ex + ey * ey);
                if (distance > width + pixel) continue;
                // Strands thinner than a device pixel keep their area as a fainter line.
                double shown = Math.Max(width, .5 * pixel), cover = Band(distance * k, shown * k, 1) * width / shown;
                total += items[o + 6] * cover * Smooth((half - Math.Abs(t)) / (half * .35 + 1e-9));
            }
        }
        return Math.Clamp(total, -1, 1);
    }

    // ---------------------------------------------------------------- 빛 번짐

    static readonly float[] toLinear = Enumerable.Range(0, 256).Select(i => (float)Linear(i / 255.0)).ToArray();
    static readonly Lazy<byte[]> toEncoded = new(() => Enumerable.Range(0, 65536).Select(i => Imaging.Byte(Encoded(i / 65535.0) * 255)).ToArray());
    static double Linear(double encoded) => encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
    static double Encoded(double linear) => linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;

    // Averaging cell for a blur of σ device pixels: the largest power of two up to σ, so σ is 1–2 cells.
    static int Cell(double sigma)
    {
        int cell = 1;
        while (cell < 256 && cell * 2 <= sigma) cell *= 2;
        return cell;
    }

    // Light spreads as a sum of three blurs (σ = reach/12, /6, /3): a bright core and a wide halo.
    static readonly (double Divisor, float Weight)[] glowLevels = [(12, .45f), (6, .33f), (3, .22f)];

    // Cells of cell × cell device pixels aligned to the layer's origin, so the tiles and viewports of one
    // image average the same pixels together; Start is where the first (possibly partial) cell begins.
    readonly record struct Grid(int Cell, int StartX, int StartY, int Width, int Height)
    {
        public static Grid For(int cell, int width, int height, double originX, double originY)
        {
            int phaseX = Mod((int)Math.Round(originX), cell), phaseY = Mod((int)Math.Round(originY), cell);
            int startX = phaseX == 0 ? 0 : phaseX - cell, startY = phaseY == 0 ? 0 : phaseY - cell;
            return new(cell, startX, startY, (width - startX + cell - 1) / cell, (height - startY + cell - 1) / cell);
        }
    }

    static Raster Glow(Raster source, GlowSpec spec, Space space, CancellationToken token)
    {
        int width = source.Width, height = source.Height;
        byte[] src = source.Data;
        double threshold = Linear(spec.Threshold), span = Math.Max(1e-4, 1 - threshold);
        var tint = DocumentFeatures.Color(spec.TintArgb); double tintAmount = tint.A / 255.0;
        double tintB = Linear(tint.B / 255.0), tintG = Linear(tint.G / 255.0), tintR = Linear(tint.R / 255.0), tintPeak = Math.Max(tintR, Math.Max(tintG, tintB));
        double reach = spec.Radius * space.Scale;
        var grids = glowLevels.Select(level => Grid.For(Cell(reach / level.Divisor), width, height, space.OriginX, space.OriginY)).ToArray();
        // Bright pass straight into the finest grid: linear light above the threshold (by its strongest
        // channel, so colored lamps glow too), weighted by coverage.
        var first = grids[0]; var bright = new float[first.Width * first.Height * 3]; int lit = 0;
        float area = 1f / (first.Cell * first.Cell);
        Parallel.For(0, first.Height, Options(token), () => 0, (gy, _, count) =>
        {
            int y0 = Math.Max(0, first.StartY + gy * first.Cell), y1 = Math.Min(height, first.StartY + (gy + 1) * first.Cell);
            for (int gx = 0; gx < first.Width; gx++)
            {
                int x0 = Math.Max(0, first.StartX + gx * first.Cell), x1 = Math.Min(width, first.StartX + (gx + 1) * first.Cell);
                double sb = 0, sg = 0, sr = 0;
                for (int y = y0; y < y1; y++) for (int i = (y * width + x0) * 4, end = (y * width + x1) * 4; i < end; i += 4)
                {
                    if (src[i + 3] == 0) continue;
                    double b = toLinear[src[i]], g = toLinear[src[i + 1]], r = toLinear[src[i + 2]], peak = Math.Max(r, Math.Max(g, b));
                    double weight = (peak - threshold) / span;
                    if (weight <= 0) continue;
                    weight = Smooth(weight) * src[i + 3] / 255.0; count++;
                    if (tintAmount > 0 && tintPeak > 0)
                    {
                        double scale = peak / tintPeak;
                        b += (tintB * scale - b) * tintAmount; g += (tintG * scale - g) * tintAmount; r += (tintR * scale - r) * tintAmount;
                    }
                    sb += b * weight; sg += g * weight; sr += r * weight;
                }
                int d = (gy * first.Width + gx) * 3; bright[d] = (float)sb * area; bright[d + 1] = (float)sg * area; bright[d + 2] = (float)sr * area;
            }
            return count;
        }, count => Interlocked.Add(ref lit, count));
        if (lit == 0) return source.Clone();
        // Coarser levels average the finer light (aligned power-of-two cells nest), each is blurred,
        // and the sum collapses from the widest level back onto the finest grid.
        var light = new float[glowLevels.Length][]; light[0] = bright;
        for (int j = 1; j < light.Length; j++) light[j] = grids[j].Cell == grids[j - 1].Cell ? light[j - 1] : Reduce(light[j - 1], grids[j - 1], grids[j], token);
        float[]? sum = null;
        for (int j = light.Length - 1; j >= 0; j--)
        {
            var level = Blur(light[j], grids[j], reach / glowLevels[j].Divisor / grids[j].Cell, glowLevels[j].Weight * (float)spec.Intensity, token);
            if (sum != null) AddResampled(sum, grids[j + 1], level, grids[j], token);
            sum = level;
        }
        var glow = sum!; var grid = first; var result = source.Clone(); byte[] dst = result.Data; var encode = toEncoded.Value;
        Parallel.For(0, height, Options(token), y =>
        {
            double fy = (y + .5 - grid.StartY) / grid.Cell - .5; int y0 = (int)Math.Floor(fy); float ty = (float)(fy - y0);
            int ya = Math.Clamp(y0, 0, grid.Height - 1) * grid.Width, yb = Math.Clamp(y0 + 1, 0, grid.Height - 1) * grid.Width;
            for (int x = 0, i = y * width * 4; x < width; x++, i += 4)
            {
                double fx = (x + .5 - grid.StartX) / grid.Cell - .5; int x0 = (int)Math.Floor(fx); float tx = (float)(fx - x0);
                int xa = Math.Clamp(x0, 0, grid.Width - 1), xb = Math.Clamp(x0 + 1, 0, grid.Width - 1);
                int p00 = (ya + xa) * 3, p10 = (ya + xb) * 3, p01 = (yb + xa) * 3, p11 = (yb + xb) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float top = glow[p00 + c] + (glow[p10 + c] - glow[p00 + c]) * tx, bottom = glow[p01 + c] + (glow[p11 + c] - glow[p01 + c]) * tx;
                    float add = top + (bottom - top) * ty;
                    if (add <= 1e-6f) continue;
                    // Screen in linear light: never past white, and pixels the light does not reach stay exactly as they were.
                    double value = 1 - (1 - toLinear[src[i + c]]) * (1 - Math.Min(1, add));
                    dst[i + c] = Math.Max(src[i + c], encode[(int)Math.Round(value * 65535)]);
                }
            }
        });
        return result;
    }

    // Box average of a finer grid into a coarser one; light outside the raster is zero.
    static float[] Reduce(float[] fine, Grid from, Grid to, CancellationToken token)
    {
        var coarse = new float[to.Width * to.Height * 3]; int factor = to.Cell / from.Cell; float area = 1f / (factor * factor);
        int offsetX = (to.StartX - from.StartX) / from.Cell, offsetY = (to.StartY - from.StartY) / from.Cell;
        Parallel.For(0, to.Height, Options(token), cy =>
        {
            for (int cx = 0; cx < to.Width; cx++)
            {
                float b = 0, g = 0, r = 0;
                for (int fy = Math.Max(0, offsetY + cy * factor), y1 = Math.Min(from.Height, offsetY + (cy + 1) * factor); fy < y1; fy++)
                    for (int fx = Math.Max(0, offsetX + cx * factor), x1 = Math.Min(from.Width, offsetX + (cx + 1) * factor); fx < x1; fx++)
                    { int q = (fy * from.Width + fx) * 3; b += fine[q]; g += fine[q + 1]; r += fine[q + 2]; }
                int d = (cy * to.Width + cx) * 3; coarse[d] = b * area; coarse[d + 1] = g * area; coarse[d + 2] = r * area;
            }
        });
        return coarse;
    }

    // Separable Gaussian of σ cells (zero beyond the grid), scaled by weight; returns a new grid.
    static float[] Blur(float[] input, Grid grid, double sigma, float weight, CancellationToken token)
    {
        int w = grid.Width, h = grid.Height;
        var output = new float[input.Length];
        if (sigma < .35) { for (int i = 0; i < input.Length; i++) output[i] = input[i] * weight; return output; }
        int radius = (int)Math.Ceiling(3 * sigma); var kernel = new float[radius * 2 + 1]; double total = 0;
        for (int t = -radius; t <= radius; t++) total += kernel[t + radius] = (float)Math.Exp(-t * t / (2 * sigma * sigma));
        for (int t = 0; t < kernel.Length; t++) kernel[t] = (float)(kernel[t] / total);
        var temp = new float[input.Length];
        Parallel.For(0, h, Options(token), y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int t = Math.Max(-radius, -x), last = Math.Min(radius, w - 1 - x); t <= last; t++)
                { float kw = kernel[t + radius]; int q = (row + x + t) * 3; b += input[q] * kw; g += input[q + 1] * kw; r += input[q + 2] * kw; }
                int d = (row + x) * 3; temp[d] = b; temp[d + 1] = g; temp[d + 2] = r;
            }
        });
        Parallel.For(0, h, Options(token), y =>
        {
            for (int x = 0; x < w; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int t = Math.Max(-radius, -y), last = Math.Min(radius, h - 1 - y); t <= last; t++)
                { float kw = kernel[t + radius]; int q = ((y + t) * w + x) * 3; b += temp[q] * kw; g += temp[q + 1] * kw; r += temp[q + 2] * kw; }
                int d = (y * w + x) * 3; output[d] = b * weight; output[d + 1] = g * weight; output[d + 2] = r * weight;
            }
        });
        return output;
    }

    // Adds a coarser grid onto a finer one by bilinear sampling at the finer cells' centers.
    static void AddResampled(float[] coarse, Grid from, float[] fine, Grid to, CancellationToken token)
    {
        Parallel.For(0, to.Height, Options(token), y =>
        {
            double fy = (to.StartY + (y + .5) * to.Cell - from.StartY) / from.Cell - .5; int y0 = (int)Math.Floor(fy); float ty = (float)(fy - y0);
            int ya = Math.Clamp(y0, 0, from.Height - 1) * from.Width, yb = Math.Clamp(y0 + 1, 0, from.Height - 1) * from.Width;
            for (int x = 0; x < to.Width; x++)
            {
                double fx = (to.StartX + (x + .5) * to.Cell - from.StartX) / from.Cell - .5; int x0 = (int)Math.Floor(fx); float tx = (float)(fx - x0);
                int xa = Math.Clamp(x0, 0, from.Width - 1), xb = Math.Clamp(x0 + 1, 0, from.Width - 1);
                int p00 = (ya + xa) * 3, p10 = (ya + xb) * 3, p01 = (yb + xa) * 3, p11 = (yb + xb) * 3, d = (y * to.Width + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float top = coarse[p00 + c] + (coarse[p10 + c] - coarse[p00 + c]) * tx, bottom = coarse[p01 + c] + (coarse[p11 + c] - coarse[p01 + c]) * tx;
                    fine[d + c] += top + (bottom - top) * ty;
                }
            }
        });
    }

    static int Mod(int value, int divisor) => (value % divisor + divisor) % divisor;
}
