using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// One mark of a hatch pattern. Anchor is in unit tile coordinates [0,1)². Points are offsets from it:
// in tile-width units for mark shapes (blades, dashes, dots, blobs), so a vertical ratio change keeps
// their shape, or in unit coordinates (StretchLocal) for tile-spanning geometry (lines, joints, cells).
// A dot is a single filled point whose diameter comes from its pen class.
internal readonly record struct HatchMark(Point Anchor, Vector[] Points, byte Pen, bool Closed, bool Filled, bool StretchLocal);
// Marks of one pattern, pen widths per class in document pixels, and an alpha multiplier (0–255) for the ink.
// TileUnits (screentones): pen widths and dot diameters are fractions of the tile width, so a screen keeps
// its ink coverage at any repeat size, and the pen scale a tile is drawn with is the line weight alone.
// Inverted: ink covers the tile except round holes at the dots; line weight shrinks the holes (more ink)
// and a hole never grows past MaxPen (a fraction of the tile width), so holes stay apart.
internal sealed record HatchGeometry(HatchMark[] Marks, double[] PenDoc, uint InkAlphaScale, bool TileUnits = false, bool Inverted = false, double MaxPen = 0);

// Built-in hatch patterns drawn from vector marks at the resolution they are shown. A pattern tile is
// seamless: a mark that crosses an edge is drawn again on the opposite side. Tiles are cached (LRU)
// by pattern, pixel size, pen scale and ink, so zoom steps and slider ticks reuse them.
public static class HatchPatternRenderer
{
    public const int CanonicalSize = 256;
    public const long CacheBudgetBytes = 128L * 1024 * 1024;
    public const int MaxTileSide = 2048, MinTileSide = 16;
    const long MaxTilePixels = 4L * 1024 * 1024;
    const double CanonicalPenScale = 1.6;

    // Pen classes: strokes fine / thin / medium / bold, then dot diameters fine / small / medium (document px).
    internal const byte Fine = 0, Thin = 1, Medium = 2, Bold = 3, DotFine = 4, DotSmall = 5, DotMedium = 6;
    static readonly double[] standardPens = [.45, .7, 1.1, 2.0, .9, 1.5, 2.3];
    static bool IsDot(byte pen) => pen >= DotFine;

    static readonly Lazy<HatchGeometry>[] geometries = HatchPatterns.All.Select(p => new Lazy<HatchGeometry>(() => Build(p), LazyThreadSafetyMode.ExecutionAndPublication)).ToArray();
    static readonly Lazy<MaterialAsset>[] assets = HatchPatterns.All.Select(p => new Lazy<MaterialAsset>(() =>
        new MaterialAsset(HatchPatterns.StableId(p), HatchPatterns.AssetName(p), Canonical(p), HatchPatterns.Source(p), true), LazyThreadSafetyMode.ExecutionAndPublication)).ToArray();

    // The shared library asset of a pattern: reserved ID, Korean name, 256 px fallback pixels for older builds.
    public static MaterialAsset Create(HatchPattern p) => assets[Index(p)].Value;

    internal static HatchGeometry Geometry(HatchPattern p) => geometries[Index(p)].Value;

    static int Index(HatchPattern p) => (int)p >= 0 && (int)p < HatchPatterns.All.Count ? (int)p : throw new ArgumentOutOfRangeException(nameof(p));

    // The frozen fallback tile stored in documents. Once released, a generator version's output must not change.
    internal static Raster Canonical(HatchPattern p) => Raster.FromBitmap(Draw(p, CanonicalSize, CanonicalSize, SwatchPen(p, CanonicalPenScale), HatchPatterns.DefaultInk, default, true));

    // The pen scale for a tile of `tilePixels` across a repeat `tileWidth` document px wide: document px → tile
    // px times the line weight for line hatches; the line weight alone for screentones (marks sized to the tile).
    public static double PenScale(HatchPattern p, double tilePixels, double tileWidth, double lineWeight) =>
        Geometry(p).TileUnits ? lineWeight : tilePixels / tileWidth * lineWeight;

    // The pen scale of a fixed-size preview tile (swatches, thumbnails): `linePen` for line hatches, which keeps
    // their lines legible at that size, and line weight 100% for screentones, which keeps their coverage.
    public static double SwatchPen(HatchPattern p, double linePen, double lineWeight = 1) => Geometry(p).TileUnits ? lineWeight : linePen * lineWeight;

    // Tile pixel size for a repeat of tileWidth×tileHeight document px shown at deviceScale: the nearest
    // whole device pixels, so the brush maps the tile (almost) 1:1 and thin lines are not resampled.
    // Repeats smaller than MinTileSide are drawn at that size; larger than MaxTileSide use Vector.
    public static (int Width, int Height) TileSize(double tileWidth, double tileHeight, double deviceScale)
    {
        if (!double.IsFinite(deviceScale) || deviceScale <= 0) deviceScale = 1;
        double w = Math.Max(1e-6, tileWidth * deviceScale), h = Math.Max(1e-6, tileHeight * deviceScale);
        double clamp = w < MinTileSide ? MinTileSide / w : w > MaxTileSide ? MaxTileSide / w : 1;
        return Fit((int)Math.Max(1, Math.Round(w * clamp)), (int)Math.Max(1, Math.Round(Math.Min(h * clamp, int.MaxValue / 2d))));
    }

    // A repeat this large on the device is drawn from vector marks instead of an upscaled bitmap tile.
    public static bool NeedsVector(double tileWidth, double tileHeight, double deviceScale)
    {
        if (!double.IsFinite(deviceScale) || deviceScale <= 0) deviceScale = 1;
        double w = tileWidth * deviceScale, h = tileHeight * deviceScale;
        return w > MaxTileSide || h > MaxTileSide || w * h > MaxTilePixels;
    }

    static (int Width, int Height) Fit(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        double fit = Math.Min(1, Math.Min(MaxTileSide / (double)Math.Max(width, height), Math.Sqrt(MaxTilePixels / ((double)width * height))));
        if (fit < 1) { width = Math.Max(1, (int)Math.Floor(width * fit)); height = Math.Max(1, (int)Math.Floor(height * fit)); }
        return (width, height);
    }

    readonly record struct Key(HatchPattern Pattern, int Width, int Height, int Pen, uint Ink);
    sealed class Entry(Key key, Lazy<BitmapSource> tile)
    {
        public Key Key { get; } = key;
        public Lazy<BitmapSource> Tile { get; } = tile;
        public long Bytes;
        public bool Done;
    }
    static readonly object gate = new();
    static readonly Dictionary<Key, LinkedListNode<Entry>> entries = [];
    static readonly LinkedList<Entry> recent = new();
    static long cachedBytes;
    static int builds;

    internal static (int Builds, long Bytes, int Entries) CacheStats { get { lock (gate) return (builds, cachedBytes, entries.Count); } }
    internal static int LargestCachedSide { get { lock (gate) return entries.Values.Where(n => n.Value.Done).Select(n => Math.Max(n.Value.Key.Width, n.Value.Key.Height)).DefaultIfEmpty(0).Max(); } }
    internal static void ClearCache() { lock (gate) { entries.Clear(); recent.Clear(); cachedBytes = 0; } lock (swatches) swatches.Clear(); }

    // A frozen, transparent tile with the ink drawn in. penScale converts document px to tile px.
    public static BitmapSource Tile(HatchPattern p, int width, int height, double penScale, uint ink, CancellationToken token = default)
    {
        Index(p);
        if (!double.IsFinite(penScale) || penScale < 0) throw new ArgumentOutOfRangeException(nameof(penScale));
        (width, height) = Fit(width, height);
        var key = new Key(p, width, height, (int)Math.Min(int.MaxValue, Math.Round(penScale * 20)), ink == 0 ? HatchPatterns.DefaultInk : ink);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            LinkedListNode<Entry> node;
            lock (gate)
            {
                if (entries.TryGetValue(key, out var found)) { node = found; recent.Remove(node); recent.AddFirst(node); }
                else
                {
                    var build = token;
                    var entry = new Entry(key, new Lazy<BitmapSource>(() =>
                    {
                        Interlocked.Increment(ref builds);
                        return Draw(key.Pattern, key.Width, key.Height, key.Pen / 20d, key.Ink, build, false);
                    }, LazyThreadSafetyMode.ExecutionAndPublication));
                    node = recent.AddFirst(entry); entries.Add(key, node);
                }
            }
            try
            {
                var tile = node.Value.Tile.Value;
                lock (gate)
                {
                    if (!node.Value.Done && entries.TryGetValue(key, out var live) && ReferenceEquals(live, node))
                    {
                        node.Value.Done = true; node.Value.Bytes = (long)tile.PixelWidth * tile.PixelHeight * 4; cachedBytes += node.Value.Bytes;
                        for (var last = recent.Last; cachedBytes > CacheBudgetBytes && last != null;)
                        {
                            var previous = last.Previous;
                            if (!ReferenceEquals(last, node) && last.Value.Done) { recent.Remove(last); entries.Remove(last.Value.Key); cachedBytes -= last.Value.Bytes; }
                            last = previous;
                        }
                    }
                }
                return tile;
            }
            catch (Exception error)
            {
                // A cancelled or failed build is never cached; another caller's cancellation does not fail this one.
                lock (gate)
                    if (entries.TryGetValue(key, out var live) && ReferenceEquals(live, node)) { recent.Remove(node); entries.Remove(key); }
                token.ThrowIfCancellationRequested();
                if (error is OperationCanceledException) continue;
                throw;
            }
        }
    }

    static readonly Dictionary<(HatchPattern, int), BitmapSource> swatches = [];
    // A palette swatch: one seamless tile on white paper, drawn at the button's device pixels.
    public static BitmapSource Swatch(HatchPattern p, int devicePx)
    {
        devicePx = Math.Clamp(devicePx, MinTileSide, 512);
        lock (swatches) if (swatches.TryGetValue((p, devicePx), out var known)) return known;
        var tile = Draw(p, devicePx, devicePx, SwatchPen(p, CanonicalPenScale * devicePx / 96d), HatchPatterns.DefaultInk, default, false);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, devicePx, devicePx)); dc.DrawImage(tile, new Rect(0, 0, devicePx, devicePx)); }
        var bitmap = new RenderTargetBitmap(devicePx, devicePx, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        lock (swatches) swatches[(p, devicePx)] = bitmap;
        return bitmap;
    }

    // Marks thinner or smaller than this in device px are drawn at the minimum with their ink thinned
    // in proportion, so a zoomed-out view keeps the same tone as 100% and the exported pixels.
    const double MinStroke = .35, MinDot = .8;
    // Fill slots: dots by pen class (4–6), filled blobs in slot 7.
    const int BlobSlot = 7;

    static SolidColorBrush Ink(uint ink, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(Math.Round(alpha), 0, 255), (byte)(ink >> 16), (byte)(ink >> 8), (byte)ink)); brush.Freeze();
        return brush;
    }

    // A thin horizontal or vertical line is centred on a pixel row (odd widths) or edge (even widths),
    // so at 1:1 it is one crisp row of ink rather than two half-grey rows.
    static void Snap(List<Point> points, double pen)
    {
        if (points.Count != 2) return;
        bool centre = pen <= 1.5 || (int)Math.Round(pen) % 2 == 1;
        double Snapped(double c) => centre ? Math.Floor(c) + .5 : Math.Round(c);
        var a = points[0]; var b = points[1];
        if (Math.Abs(a.Y - b.Y) < 1e-9) { double y = Snapped(a.Y); points[0] = new Point(a.X, y); points[1] = new Point(b.X, y); }
        else if (Math.Abs(a.X - b.X) < 1e-9) { double x = Snapped(a.X); points[0] = new Point(x, a.Y); points[1] = new Point(x, b.Y); }
    }

    // Draws the marks on a transparent width×height tile. Every mark whose pixel bounds (with the pen)
    // cross an edge is drawn again shifted by one tile, so the tile repeats without seams.
    static BitmapSource Draw(HatchPattern p, int width, int height, double penScale, uint ink, CancellationToken token, bool reference)
    {
        var geometry = Geometry(p);
        double alpha = (ink >> 24 & 0xFF) * geometry.InkAlphaScale / 255d;
        var widths = Widths(geometry, width, penScale);
        if (geometry.Inverted) return DrawHoles(geometry, width, height, widths, ink, alpha, token, reference);
        var strokes = new StreamGeometry?[4]; var strokeContexts = new StreamGeometryContext?[4];
        var fills = new StreamGeometry?[8]; var fillContexts = new StreamGeometryContext?[8];
        var points = new List<Point>(64);
        var xs = new double[3]; var ys = new double[3];
        try
        {
            for (int index = 0; index < geometry.Marks.Length; index++)
            {
                if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                var mark = geometry.Marks[index];
                points.Clear();
                foreach (var offset in mark.Points)
                    points.Add(new Point((mark.Anchor.X + offset.X) * width, mark.Anchor.Y * height + offset.Y * (mark.StretchLocal ? height : width)));
                bool dot = mark.Filled && points.Count == 1 && IsDot(mark.Pen);
                double radius = dot ? Math.Max(MinDot, widths[mark.Pen]) / 2 : 0;
                if (!mark.Filled && mark.StretchLocal) Snap(points, Math.Max(MinStroke, widths[mark.Pen]));
                double pad = dot ? radius : mark.Filled ? .75 : Math.Max(MinStroke, widths[mark.Pen]) / 2 + .75;
                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                foreach (var point in points) { left = Math.Min(left, point.X); right = Math.Max(right, point.X); top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y); }
                left -= pad; right += pad; top -= pad; bottom += pad;
                int nx = 0, ny = 0;
                xs[nx++] = 0; ys[ny++] = 0;
                if (reference) { xs[nx++] = width; xs[nx++] = -width; ys[ny++] = height; ys[ny++] = -height; }
                else
                {
                    if (left < 0) xs[nx++] = width;
                    if (right > width) xs[nx++] = -width;
                    if (top < 0) ys[ny++] = height;
                    if (bottom > height) ys[ny++] = -height;
                }
                var context = dot ? Open(fills, fillContexts, mark.Pen) : mark.Filled ? Open(fills, fillContexts, BlobSlot) : Open(strokes, strokeContexts, mark.Pen);
                for (int ix = 0; ix < nx; ix++) for (int iy = 0; iy < ny; iy++)
                {
                    var shift = new Vector(xs[ix], ys[iy]);
                    if (dot) { if (geometry.TileUnits) Disc(context, points[0] + shift, radius); else Circle(context, points[0] + shift, radius); continue; }
                    context.BeginFigure(points[0] + shift, mark.Filled, mark.Closed);
                    for (int k = 1; k < points.Count; k++) context.LineTo(points[k] + shift, !mark.Filled, true);
                }
            }
        }
        finally { Close(fillContexts); Close(strokeContexts); }
        token.ThrowIfCancellationRequested();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (byte pen = 0; pen < strokes.Length; pen++)
            {
                if (strokes[pen] is not { } stroke) continue;
                stroke.Freeze(); double drawn = Math.Max(MinStroke, widths[pen]);
                var line = new Pen(Ink(ink, alpha * widths[pen] / drawn), drawn) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; line.Freeze();
                dc.DrawGeometry(null, line, stroke);
            }
            for (int slot = 0; slot < fills.Length; slot++)
            {
                if (fills[slot] is not { } filled) continue;
                filled.Freeze();
                double tone = slot == BlobSlot ? 1 : Math.Pow(Math.Min(1, widths[slot] / MinDot), 2);
                dc.DrawGeometry(Ink(ink, alpha * tone), null, filled);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    // Pen widths and dot diameters in tile px: document px times the pen scale for line hatches; for
    // screentones a fraction of the tile width times the line weight (holes: divided by it, kept apart).
    static double[] Widths(HatchGeometry geometry, double tileWidth, double penScale) => geometry.PenDoc.Select(d =>
        !geometry.TileUnits ? d * penScale
        : geometry.Inverted ? Math.Min(d / Math.Max(penScale, .01), geometry.MaxPen) * tileWidth
        : d * penScale * tileWidth).ToArray();

    // An inverted screen: ink over the whole tile with round holes cut out (even-odd), each hole that
    // crosses an edge cut again on the opposite side. Holes too small to draw are averaged into a flat
    // tone of the same coverage, which is what a zoomed-out view of them shows.
    static BitmapSource DrawHoles(HatchGeometry geometry, int width, int height, double[] widths, uint ink, double alpha, CancellationToken token, bool reference)
    {
        var tile = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double holes = geometry.Marks.Sum(mark => Math.PI * widths[mark.Pen] * widths[mark.Pen] / 4);
            if (geometry.Marks.Any(mark => widths[mark.Pen] < MinDot))
                dc.DrawRectangle(Ink(ink, alpha * Math.Clamp(1 - holes / (width * (double)height), 0, 1)), null, tile);
            else
            {
                var cut = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var context = cut.Open())
                {
                    context.BeginFigure(tile.TopLeft, true, true); context.PolyLineTo([tile.TopRight, tile.BottomRight, tile.BottomLeft], false, false);
                    for (int index = 0; index < geometry.Marks.Length; index++)
                    {
                        if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                        var mark = geometry.Marks[index];
                        var center = new Point(mark.Anchor.X * width, mark.Anchor.Y * height); double radius = widths[mark.Pen] / 2;
                        foreach (double dx in new double[] { 0, width, -width })
                            foreach (double dy in new double[] { 0, height, -height })
                            {
                                bool crossesX = dx == 0 || dx > 0 && center.X - radius < 0 || dx < 0 && center.X + radius > width;
                                bool crossesY = dy == 0 || dy > 0 && center.Y - radius < 0 || dy < 0 && center.Y + radius > height;
                                if (reference || crossesX && crossesY) Disc(context, center + new Vector(dx, dy), radius);
                            }
                    }
                }
                cut.Freeze(); dc.DrawGeometry(Ink(ink, alpha), null, cut);
            }
        }
        token.ThrowIfCancellationRequested();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    static StreamGeometryContext Open(StreamGeometry?[] set, StreamGeometryContext?[] contexts, int slot)
    {
        if (contexts[slot] is { } open) return open;
        set[slot] = new StreamGeometry { FillRule = FillRule.Nonzero }; return contexts[slot] = set[slot]!.Open();
    }
    static void Close(StreamGeometryContext?[] contexts) { foreach (var context in contexts) context?.Close(); }

    static void Circle(StreamGeometryContext context, Point center, double radius)
    {
        context.BeginFigure(new Point(center.X + radius, center.Y), true, true);
        context.ArcTo(new Point(center.X - radius, center.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
        context.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
    }

    // A round dot of exactly the circle's area. WPF flattens a small arc into a few chords inside the circle
    // (a 2 px dot loses about a tenth of its ink), so screentones draw their dots as polygons whose radius
    // makes up for the chords, fine enough that the facets stay under 1/20 px.
    static void Disc(StreamGeometryContext context, Point center, double radius)
    {
        int sides = Math.Clamp((int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - .05 / Math.Max(radius, .05)))), 12, 256);
        double scaled = radius * Math.Sqrt(Math.PI / (sides / 2d * Math.Sin(2 * Math.PI / sides)));
        context.BeginFigure(new Point(center.X + scaled, center.Y), true, true);
        var points = new Point[sides - 1];
        for (int i = 1; i < sides; i++) points[i - 1] = new Point(center.X + scaled * Math.Cos(2 * Math.PI * i / sides), center.Y + scaled * Math.Sin(2 * Math.PI * i / sides));
        context.PolyLineTo(points, false, false);
    }

    public const int MaxVectorTiles = 256;

    // The marks of every repeat that meets `region` (layer px, in the unrotated brush space; tile is the
    // brush viewport) as vector geometry, for repeats too large for a tile bitmap. WPF rasterises it at
    // device resolution, so lines stay sharp at any size or zoom. Null when the region spans too many repeats.
    internal static Drawing? VectorMarks(HatchPattern p, Rect tile, double lineWeight, uint ink, Rect region, CancellationToken token = default)
    {
        var geometry = Geometry(p);
        if (tile.Width <= 0 || tile.Height <= 0 || region.IsEmpty || !double.IsFinite(region.Width + region.Height)) return null;
        double fi0 = Math.Floor((region.Left - tile.X) / tile.Width) - 1, fi1 = Math.Floor((region.Right - tile.X) / tile.Width) + 1;
        double fj0 = Math.Floor((region.Top - tile.Y) / tile.Height) - 1, fj1 = Math.Floor((region.Bottom - tile.Y) / tile.Height) + 1;
        if ((fi1 - fi0 + 1) * (fj1 - fj0 + 1) > MaxVectorTiles) return null;
        long i0 = (long)fi0, i1 = (long)fi1, j0 = (long)fj0, j1 = (long)fj1;
        if (ink == 0) ink = HatchPatterns.DefaultInk;
        double alpha = (ink >> 24 & 0xFF) * geometry.InkAlphaScale / 255d;
        // Pen widths in layer px (screentones: a fraction of the repeat width).
        var widths = Widths(geometry, tile.Width, lineWeight);
        var strokes = new StreamGeometry?[4]; var strokeContexts = new StreamGeometryContext?[4];
        var fills = new StreamGeometry?[8]; var fillContexts = new StreamGeometryContext?[8];
        // An inverted screen is one even-odd figure set: ink over every repeat in range, holes cut out.
        StreamGeometry? cut = null; StreamGeometryContext? cutContext = null;
        if (geometry.Inverted)
        {
            cut = new StreamGeometry { FillRule = FillRule.EvenOdd }; cutContext = cut.Open();
            var area = new Rect(tile.X + i0 * tile.Width, tile.Y + j0 * tile.Height, (i1 - i0 + 1) * tile.Width, (j1 - j0 + 1) * tile.Height);
            cutContext.BeginFigure(area.TopLeft, true, true); cutContext.PolyLineTo([area.TopRight, area.BottomRight, area.BottomLeft], false, false);
        }
        try
        {
            for (long j = j0; j <= j1; j++) for (long i = i0; i <= i1; i++)
            {
                token.ThrowIfCancellationRequested();
                double ox = tile.X + i * tile.Width, oy = tile.Y + j * tile.Height;
                foreach (var mark in geometry.Marks)
                {
                    Point At(Vector offset) => new(ox + (mark.Anchor.X + offset.X) * tile.Width, oy + mark.Anchor.Y * tile.Height + offset.Y * (mark.StretchLocal ? tile.Height : tile.Width));
                    if (mark.Filled && mark.Points.Length == 1 && IsDot(mark.Pen))
                    {
                        var dots = cutContext ?? Open(fills, fillContexts, mark.Pen);
                        if (geometry.TileUnits) Disc(dots, At(mark.Points[0]), widths[mark.Pen] / 2); else Circle(dots, At(mark.Points[0]), widths[mark.Pen] / 2);
                        continue;
                    }
                    var context = mark.Filled ? Open(fills, fillContexts, BlobSlot) : Open(strokes, strokeContexts, mark.Pen);
                    context.BeginFigure(At(mark.Points[0]), mark.Filled, mark.Closed);
                    for (int k = 1; k < mark.Points.Length; k++) context.LineTo(At(mark.Points[k]), !mark.Filled, true);
                }
            }
        }
        finally { Close(fillContexts); Close(strokeContexts); cutContext?.Close(); }
        var brush = Ink(ink, alpha); var group = new DrawingGroup();
        if (cut != null) { cut.Freeze(); group.Children.Add(new GeometryDrawing(brush, null, cut)); }
        for (int pen = 0; pen < strokes.Length; pen++)
        {
            if (strokes[pen] is not { } stroke) continue;
            stroke.Freeze();
            var line = new Pen(brush, widths[pen]) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; line.Freeze();
            group.Children.Add(new GeometryDrawing(null, line, stroke));
        }
        foreach (var filled in fills) if (filled != null) { filled.Freeze(); group.Children.Add(new GeometryDrawing(brush, null, filled)); }
        group.Freeze(); return group;
    }

    // Same tile with every mark repeated at all nine neighbouring positions: what a seamless tile must equal.
    internal static BitmapSource ReferenceTile(HatchPattern p, int width, int height, double penScale) =>
        Draw(p, width, height, penScale, HatchPatterns.DefaultInk, default, true);

    // ---- Pattern geometry --------------------------------------------------------------------

    // Stateless SplitMix64 hash to [0,1). Patterns never use System.Random, so tiles are reproducible.
    internal static double U(int seed, int i, int j = 0, int k = 0)
    {
        ulong x = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)i * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)j * 0x94D049BB133111EBUL ^ (ulong)(uint)k * 0xD6E8FEB86659FD93UL);
        x = unchecked(x + 0x9E3779B97F4A7C15UL);
        x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
        x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
        x ^= x >> 31;
        return (x >> 11) * (1.0 / 9007199254740992.0);
    }

    static int Mod(int value, int m) => ((value % m) + m) % m;
    static double Wrap(double value) { double w = value - Math.Floor(value); return w >= 1 ? 0 : w; }

    // Periodic value noise on an m×m lattice (wraps with the tile), 0..1.
    static double Noise(int seed, double x, double y, int m)
    {
        double fx = Wrap(x) * m, fy = Wrap(y) * m; int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
        double tx = fx - ix, ty = fy - iy; tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        double V(int a, int b) => U(seed, Mod(a, m), Mod(b, m), 911);
        double top = V(ix, iy) + (V(ix + 1, iy) - V(ix, iy)) * tx, low = V(ix, iy + 1) + (V(ix + 1, iy + 1) - V(ix, iy + 1)) * tx;
        return top + (low - top) * ty;
    }

    sealed class Marks(int seed)
    {
        public readonly List<HatchMark> List = [];
        public int Seed { get; } = seed;
        public void Dot(Point at, byte pen) => List.Add(new(Wrapped(at), [new Vector(0, 0)], pen, false, true, false));
        public void Stroke(Point at, byte pen, params Vector[] points) => List.Add(new(Wrapped(at), points, pen, false, false, false));
        public void Outline(Point at, byte pen, params Vector[] points) => List.Add(new(Wrapped(at), points, pen, true, false, false));
        public void Blob(Point at, params Vector[] points) => List.Add(new(Wrapped(at), points, 0, true, true, false));
        // Tile-spanning geometry in unit coordinates.
        public void Line(Point from, byte pen, params Vector[] points) => List.Add(new(Wrapped(from), points, pen, false, false, true));
        public void Area(Point from, params Vector[] points) => List.Add(new(Wrapped(from), points, 0, true, true, true));
        static Point Wrapped(Point p) => new(Wrap(p.X), Wrap(p.Y));
        // Jittered kx×ky grid: one candidate position per cell, wrapped into the tile.
        public void Scatter(int kx, int ky, double jitter, int layer, Action<Point, int, int> place)
        {
            for (int j = 0; j < ky; j++) for (int i = 0; i < kx; i++)
            {
                double x = (i + .5 + (U(Seed, i, j, layer * 4 + 1) - .5) * jitter) / kx, y = (j + .5 + (U(Seed, i, j, layer * 4 + 2) - .5) * jitter) / ky;
                place(new Point(Wrap(x), Wrap(y)), i, j);
            }
        }
    }

    static Vector Polar(double length, double degrees) => new(length * Math.Cos(degrees * Math.PI / 180), -length * Math.Sin(degrees * Math.PI / 180));

    // Screentone layout: dot screens have ScreenCells × ScreenCells cells per repeat with two dots each on a
    // 45° lattice (A sites at ¼,¼ and ¾,¾ of a cell, the hole sites of inverted screens at ¾,¼ and ¼,¾), line
    // screens LineScreenLines horizontal lines, grid screens GridScreenLines lines each way, the stipple
    // StippleCells grain cells. The gradient screentones use the same lattice (ToneGradientRenderer).
    internal const int ScreenCells = 8, LineScreenLines = 16, GridScreenLines = 12, StippleCells = 40;

    // Dot diameter (tile-width units) for a coverage on the dot lattice: one dot per half cell.
    internal static double ScreenDot(double coverage) => Math.Sqrt(2 * coverage / Math.PI) / ScreenCells;

    // Tone screens, sized to the tile so the ink coverage holds at any repeat size: round black dots up to
    // 45%, round holes in ink from 60% (as a round-dot screen inverts), horizontal lines, a line grid, a
    // full-tile area for black poché, and uniform stand-ins for the gradients (their fallback tile).
    static HatchGeometry Screen(HatchPattern p)
    {
        var m = new Marks(0x5A17 + (int)p);
        var pens = new double[standardPens.Length];
        const int k = ScreenCells;
        void Lattice(bool holes)
        {
            for (int j = 0; j < k; j++) for (int i = 0; i < k; i++)
            {
                m.Dot(new Point((i + (holes ? .75 : .25)) / k, (j + .25) / k), DotFine);
                m.Dot(new Point((i + (holes ? .25 : .75)) / k, (j + .75) / k), DotFine);
            }
        }
        double coverage = HatchPatterns.Coverage(p) ?? 0;
        switch (p)
        {
            case HatchPattern.DotScreen60 or HatchPattern.DotScreen75:
                Lattice(true); pens[DotFine] = ScreenDot(1 - coverage);
                // Neighbouring hole sites are √½ cell apart.
                return new HatchGeometry(m.List.ToArray(), pens, 255, TileUnits: true, Inverted: true, MaxPen: .999 * Math.Sqrt(.5) / k);
            case HatchPattern.DotScreen10 or HatchPattern.DotScreen20 or HatchPattern.DotScreen30 or HatchPattern.DotScreen45:
                Lattice(false); pens[DotFine] = ScreenDot(coverage); break;
            case HatchPattern.LineScreen20 or HatchPattern.LineScreen35 or HatchPattern.LineScreen50:
                for (int i = 0; i < LineScreenLines; i++) m.Line(new Point(0, (i + .5) / LineScreenLines), Thin, new Vector(0, 0), new Vector(1, 0));
                pens[Thin] = coverage / LineScreenLines; break;
            case HatchPattern.GridScreen30:
                // Two crossing families of coverage c together cover 1 − (1 − c)², so each has c = 1 − √(1 − coverage).
                for (int i = 0; i < GridScreenLines; i++)
                {
                    m.Line(new Point(0, (i + .5) / GridScreenLines), Thin, new Vector(0, 0), new Vector(1, 0));
                    m.Line(new Point((i + .5) / GridScreenLines, 0), Thin, new Vector(0, 0), new Vector(0, 1));
                }
                pens[Thin] = (1 - Math.Sqrt(1 - coverage)) / GridScreenLines; break;
            case HatchPattern.SolidBlack:
                m.Area(new Point(0, 0), new Vector(0, 0), new Vector(1, 0), new Vector(1, 1), new Vector(0, 1)); break;
            case HatchPattern.DotGradient:
                Lattice(false); pens[DotFine] = ScreenDot(.4); break;
            case HatchPattern.StippleGradient:
                m.Scatter(StippleCells, StippleCells, .95, 0, (at, i, j) => { if (U(m.Seed, i, j, 10) < .5) m.Dot(at, DotFine); });
                pens[DotFine] = .8 / StippleCells; break;
            default: throw new ArgumentOutOfRangeException(nameof(p));
        }
        return new HatchGeometry(m.List.ToArray(), pens, 255, TileUnits: true);
    }

    internal static HatchGeometry Build(HatchPattern p)
    {
        if (HatchPatterns.IsScreentone(p)) return Screen(p);
        var m = new Marks(0x5A17 + (int)p);
        double R(int i, int j, int k) => U(m.Seed, i, j, k);
        switch (p)
        {
            case HatchPattern.GrassSparse:
                // Small tufts of two or three short blades, and a few specks.
                m.Scatter(6, 6, .85, 0, (at, i, j) =>
                {
                    if (R(i, j, 10) < .22) return;
                    int blades = R(i, j, 11) < .3 ? 1 : R(i, j, 11) < .75 ? 2 : 3; double height = .045 + R(i, j, 12) * .035;
                    for (int b = 0; b < blades; b++)
                    {
                        double angle = 90 - (b - (blades - 1) / 2d) * 24 + (R(i, j, 20 + b) - .5) * 12;
                        var foot = new Vector((b - (blades - 1) / 2d) * .011, 0);
                        m.Stroke(at, Fine, foot, foot + Polar(height * (.7 + R(i, j, 30 + b) * .45), angle));
                    }
                });
                m.Scatter(5, 5, 1, 1, (at, i, j) => { if (R(i, j, 40) < .45) m.Dot(at, DotFine); });
                break;
            case HatchPattern.Meadow:
                // Dense short upright strokes in loose rows, thicker and thinner in patches.
                m.Scatter(20, 13, .95, 0, (at, i, j) =>
                {
                    double density = .62 + .38 * Noise(m.Seed, at.X, at.Y, 4);
                    if (R(i, j, 10) > density) return;
                    double length = .05 + R(i, j, 11) * .035, angle = 90 + (R(i, j, 12) - .5) * 18;
                    m.Stroke(at, Fine, new Vector(0, 0), Polar(length, angle));
                });
                break;
            case HatchPattern.Sand:
                m.Scatter(24, 24, .95, 0, (at, i, j) => { if (R(i, j, 10) < .9) m.Dot(at, DotFine); });
                break;
            case HatchPattern.DotsSparse:
                m.Scatter(7, 7, .9, 0, (at, i, j) =>
                {
                    double pick = R(i, j, 10);
                    if (pick < .22) return;
                    if (pick < .32) m.Stroke(at, Thin, new Vector(0, 0), Polar(.012 + R(i, j, 11) * .01, 70 + R(i, j, 12) * 40));
                    else m.Dot(at, pick < .85 ? DotFine : DotSmall);
                });
                break;
            case HatchPattern.DashDiagonal:
                m.Scatter(5, 5, .9, 0, (at, i, j) =>
                {
                    if (R(i, j, 10) < .12) return;
                    double angle = 52 + (R(i, j, 11) - .5) * 14, length = .06 + R(i, j, 12) * .09;
                    var half = Polar(length / 2, angle);
                    m.Stroke(at, Thin, -half, half);
                    if (R(i, j, 13) < .12)
                    {
                        var side = Polar(.022, angle - 90); var short_ = half * (.45 + R(i, j, 14) * .3);
                        m.Stroke(at, Thin, side - short_ + half * .3, side + short_ + half * .3);
                    }
                });
                break;
            case HatchPattern.DashHorizontal:
                m.Scatter(5, 7, .9, 0, (at, i, j) =>
                {
                    if (R(i, j, 10) < .12) return;
                    double length = .06 + R(i, j, 11) * .08, angle = (R(i, j, 12) - .5) * 5;
                    var half = Polar(length / 2, angle);
                    m.Stroke(at, Medium, -half, half);
                });
                break;
            case HatchPattern.Dots:
                m.Scatter(8, 8, .95, 0, (at, _, _) => m.Dot(at, DotSmall));
                break;
            case HatchPattern.PavingSmall:
                Voronoi(m, 8, 0, 2, Thin);
                break;
            case HatchPattern.Lines:
                for (int i = 0; i < 10; i++) m.Line(new Point(0, (i + .5) / 10), Thin, new Vector(0, 0), new Vector(1, 0));
                break;
            case HatchPattern.Stipple:
                m.Scatter(30, 30, .95, 0, (at, i, j) =>
                {
                    if (R(i, j, 10) < .4 + .6 * Noise(m.Seed, at.X, at.Y, 8)) m.Dot(at, DotFine);
                });
                break;
            case HatchPattern.Flagstone:
                // Drawn at twice the base repeat (HatchPatterns.RepeatScale) with four times the cells, so
                // the large stones keep their size and the repeat does not read as a lattice.
                Voronoi(m, 8, 20, 3, Bold);
                break;
            case HatchPattern.Cobble:
                Voronoi(m, 12, 24, 3, Medium);
                break;
            case HatchPattern.Gravel:
                m.Scatter(13, 13, .95, 0, (at, i, j) =>
                {
                    double n = Noise(m.Seed, at.X, at.Y, 4);
                    if (R(i, j, 10) > .4 + .6 * n) return;
                    double kind = R(i, j, 11);
                    if (kind < .45) m.Dot(at, DotSmall);
                    else if (kind < .7) m.Dot(at, DotMedium);
                    else
                    {
                        int corners = 5 + (int)(R(i, j, 12) * 2); double size = .006 + R(i, j, 13) * .007, turn = R(i, j, 14) * 360;
                        m.Blob(at, Enumerable.Range(0, corners).Select(c => Polar(size * (.7 + R(i, j, 20 + c) * .5), turn + c * 360d / corners)).ToArray());
                    }
                });
                m.Scatter(17, 17, 1, 1, (at, i, j) => { if (R(i, j, 50) < .25 + .5 * Noise(m.Seed, at.X, at.Y, 3)) m.Dot(at, DotFine); });
                break;
            case HatchPattern.Concrete:
                m.Scatter(11, 11, .95, 0, (at, i, j) => { double pick = R(i, j, 10); if (pick < .72) m.Dot(at, pick < .5 ? DotFine : DotSmall); });
                m.Scatter(4, 4, .8, 1, (at, i, j) =>
                {
                    double size = .025 + R(i, j, 20) * .02, turn = R(i, j, 21) * 360;
                    m.Outline(at, Thin, Polar(size, turn), Polar(size * (.8 + R(i, j, 22) * .3), turn + 125 + R(i, j, 23) * 20), Polar(size * (.7 + R(i, j, 24) * .3), turn + 240 + R(i, j, 25) * 20));
                });
                break;
            case HatchPattern.Brick:
                // Four courses; head joints offset by half a brick in every other course.
                for (int course = 0; course < 4; course++)
                {
                    double y = (course + .5) / 4;
                    m.Line(new Point(0, y), Thin, new Vector(0, 0), new Vector(1, 0));
                    for (int b = 0; b < 2; b++)
                        m.Line(new Point(Wrap(.25 + b * .5 + (course % 2) * .25), y), Thin, new Vector(0, 0), new Vector(0, .25));
                }
                break;
            case HatchPattern.Diagonal:
                for (int i = 0; i < 6; i++) m.Line(new Point((i + .5) / 6, 0), Thin, new Vector(0, 0), new Vector(-1, 1));
                break;
            case HatchPattern.Crosshatch:
                for (int i = 0; i < 4; i++)
                {
                    m.Line(new Point((i + .5) / 4, 0), Thin, new Vector(0, 0), new Vector(-1, 1));
                    m.Line(new Point((i + .5) / 4, 0), Thin, new Vector(0, 0), new Vector(1, 1));
                }
                break;
            case HatchPattern.Grid:
                for (int i = 0; i < 4; i++)
                {
                    m.Line(new Point((i + .5) / 4, 0), Thin, new Vector(0, 0), new Vector(0, 1));
                    m.Line(new Point(0, (i + .5) / 4), Thin, new Vector(0, 0), new Vector(1, 0));
                }
                break;
            case HatchPattern.Insulation:
                // Two bands of alternating half loops, touching the band edges.
                for (int row = 0; row < 2; row++)
                {
                    double center = .25 + row * .5, half = .2; var loop = new List<Vector>();
                    for (int arc = 0; arc < 4; arc++)
                        for (int s = arc == 0 ? 0 : 1; s <= 24; s++)
                        {
                            double t = s / 24d;
                            loop.Add(new Vector(arc / 4d + (1 - Math.Cos(Math.PI * t)) / 8, (arc % 2 == 0 ? -1 : 1) * half * Math.Sin(Math.PI * t)));
                        }
                    m.Line(new Point(0, center), Thin, loop.ToArray());
                }
                break;
        }
        // Dense textures read as a grey tone, like pencil stipple, rather than a black mass.
        uint alpha = p switch { HatchPattern.Meadow => 200, HatchPattern.Sand or HatchPattern.Stipple => 215, _ => 255 };
        return new HatchGeometry(m.List.ToArray(), standardPens.ToArray(), alpha);
    }

    // Voronoi cells of a jittered k×k seed grid (minus `remove` seeds) on the torus, relaxed by Lloyd
    // steps. Each shared edge is emitted once.
    static void Voronoi(Marks m, int k, int remove, int relax, byte pen)
    {
        var seeds = new List<Point>();
        var order = Enumerable.Range(0, k * k).OrderBy(i => U(m.Seed, i, 0, 71)).ToArray();
        var dropped = order.Take(remove).ToHashSet();
        for (int j = 0; j < k; j++) for (int i = 0; i < k; i++)
        {
            if (dropped.Contains(j * k + i)) continue;
            seeds.Add(new Point(Wrap((i + .5 + (U(m.Seed, i, j, 72) - .5) * .8) / k), Wrap((j + .5 + (U(m.Seed, i, j, 73) - .5) * .8) / k)));
        }
        for (int round = 0; round <= relax; round++)
        {
            var cells = seeds.Select((_, i) => Cell(seeds, i)).ToArray();
            if (round < relax) { seeds = cells.Select(c => Centroid(c.Select(v => v.At).ToList())).Select(c => new Point(Wrap(c.X), Wrap(c.Y))).ToList(); continue; }
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                for (int e = 0; e < cell.Count; e++)
                {
                    var (neighbor, dx, dy) = cell[e].Edge;
                    if (neighbor < 0) continue;
                    if (neighbor < i || neighbor == i && (dx < 0 || dx == 0 && dy <= 0)) continue;
                    var a = cell[e].At; var b = cell[(e + 1) % cell.Count].At;
                    if ((b - a).LengthSquared < 1e-12) continue;
                    var from = new Point(Wrap(a.X), Wrap(a.Y));
                    m.Line(from, pen, new Vector(0, 0), b - a);
                }
            }
        }
    }

    readonly record struct Vertex(Point At, (int Neighbor, int Dx, int Dy) Edge);

    // Cell of seed i: a box around it clipped by the bisectors to every other seed and every copy
    // in the neighbouring tiles. Each vertex carries the neighbour whose bisector its outgoing edge lies on.
    static List<Vertex> Cell(List<Point> seeds, int i)
    {
        var s = seeds[i];
        var polygon = new List<Vertex>
        {
            new(new Point(s.X - .5, s.Y - .5), (-1, 0, 0)), new(new Point(s.X + .5, s.Y - .5), (-1, 0, 0)),
            new(new Point(s.X + .5, s.Y + .5), (-1, 0, 0)), new(new Point(s.X - .5, s.Y + .5), (-1, 0, 0))
        };
        for (int j = 0; j < seeds.Count; j++) for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
        {
            if (j == i && dx == 0 && dy == 0) continue;
            var q = new Point(seeds[j].X + dx, seeds[j].Y + dy); var normal = q - s;
            if (normal.LengthSquared > 2.1) continue;
            var middle = s + normal / 2; double limit = Vector.Multiply(middle - new Point(0, 0), normal);
            double Side(Point p) => Vector.Multiply(p - new Point(0, 0), normal) - limit;
            var next = new List<Vertex>(polygon.Count + 2);
            for (int v = 0; v < polygon.Count; v++)
            {
                var a = polygon[v]; var b = polygon[(v + 1) % polygon.Count];
                double sa = Side(a.At), sb = Side(b.At); bool ina = sa <= 0, inb = sb <= 0;
                Point Cross() => a.At + (b.At - a.At) * (sa / (sa - sb));
                if (ina) next.Add(a);
                if (ina && !inb) next.Add(new(Cross(), (j, dx, dy)));
                else if (!ina && inb) next.Add(new(Cross(), a.Edge));
            }
            polygon = next;
            if (polygon.Count < 3) return polygon;
        }
        return polygon;
    }

    static Point Centroid(List<Point> polygon)
    {
        double area = 0, cx = 0, cy = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; double cross = a.X * b.Y - b.X * a.Y;
            area += cross; cx += (a.X + b.X) * cross; cy += (a.Y + b.Y) * cross;
        }
        if (Math.Abs(area) < 1e-12) return polygon.Count > 0 ? polygon[0] : new Point();
        return new Point(cx / (3 * area), cy / (3 * area));
    }
}
