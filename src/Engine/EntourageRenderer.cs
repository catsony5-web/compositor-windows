using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>One item of a natural scatter: where its ground point (or plan centre) goes and how it varies.</summary>
public sealed record EntouragePlacement(Point Anchor, double Meters, bool Flip, int Variant, double Rotation);

/// <summary>
/// Turns an EntourageSpec into layer content. Built-in items become retained vector paths (crisp at
/// any zoom and exported as vectors) with a same-size preview; 내 점경 become image layers styled from
/// their original. Every item has an anchor: the ground point under an elevation item (its base) or
/// the centre of a plan symbol. Placing and restyling keep that anchor where it was.
/// </summary>
public static class EntourageRenderer
{
    // Line weight at a drawing scale: a 1.7 m person 68 px tall (40 px/m) gets a pen of about 1 px.
    public static double BasePen(double pixelsPerMeter) => Math.Clamp(.55 + .012 * pixelsPerMeter, .5, 5);

    /// <summary>1:100 at the document's print resolution, kept so a person is 3–25% of the shorter side.</summary>
    public static double DefaultPixelsPerMeter(Document doc)
    {
        double ppm = doc.Dpi / 2.54, shortSide = Math.Max(1, Math.Min(doc.Width, doc.Height));
        ppm = Math.Clamp(ppm, shortSide * .03 / 1.7, Math.Max(shortSide * .03 / 1.7, shortSide * .25 / 1.7));
        return Math.Round(Math.Clamp(ppm, EntourageSpec.MinPixelsPerMeter, EntourageSpec.MaxPixelsPerMeter), 2);
    }

    /// <summary>The scale entourage in this document already uses (the topmost placed item), or null.</summary>
    public static double? DocumentPixelsPerMeter(Document doc) => doc.Layers.LastOrDefault(l => l.Entourage != null)?.Entourage!.PixelsPerMeter;

    public static Color LineColor(EntourageSpec spec) => VectorShapes.Color(spec.LineArgb);
    public static Color FillColor(EntourageSpec spec)
    {
        var line = LineColor(spec);
        return spec.Fill switch
        {
            EntourageFill.Solid => line,
            // A light tint of the line colour: neutral grey for black lines.
            EntourageFill.Gray => Color.FromArgb(line.A, (byte)(255 - (255 - line.R) * .2), (byte)(255 - (255 - line.G) * .2), (byte)(255 - (255 - line.B) * .2)),
            _ => Color.FromArgb(line.A, 255, 255, 255)
        };
    }

    /// <summary>A new spec for an item with its defaults at a scale.</summary>
    public static EntourageSpec Spec(EntourageItem item, double pixelsPerMeter, int variant = 0) => new()
    {
        ItemId = item.Id, View = item.View, Meters = item.DefaultMeters, PixelsPerMeter = pixelsPerMeter, Fill = item.DefaultFill,
        Variant = item.Variants <= 1 ? 0 : ((variant % item.Variants) + item.Variants) % item.Variants
    };

    // ---- Built-in vector items -----------------------------------------------------------------

    sealed record Frame(EntourageArt Art, double Scale, double Pen, double Pad, int Width, int Height, Point Anchor);

    static Frame Measure(EntourageSpec spec)
    {
        var item = EntourageLibrary.Find(spec.ItemId) ?? throw new InvalidDataException($"알 수 없는 점경 항목입니다: {spec.ItemId}");
        var art = EntourageLibrary.Art(item, spec.Variant); var b = art.Bounds;
        // Elevation: height above the ground line; plan: the longer side.
        double size = spec.View == EntourageView.Plan || item.View == EntourageView.Plan ? Math.Max(b.Width, b.Height) : Math.Max(1, -b.Top);
        double scale = spec.SizePixels / size, pen = BasePen(spec.PixelsPerMeter) * spec.LineWeight;
        double pad = Math.Ceiling(pen + 1.5);
        int width = Math.Max(1, (int)Math.Ceiling(b.Width * scale + 2 * pad)), height = Math.Max(1, (int)Math.Ceiling(b.Height * scale + 2 * pad));
        if (Math.Max(width, height) > EntourageSpec.MaxSide + 64) throw new InvalidDataException($"점경은 긴 변 {EntourageSpec.MaxSide:N0}px까지 놓을 수 있습니다. 높이나 축척을 줄이세요.");
        return new(art, scale, pen, pad, width, height, new Point(pad - b.X * scale, pad - b.Y * scale));
    }

    /// <summary>The vector content, its preview and the anchor in local pixels for a built-in item.</summary>
    public static (VectorContent Vector, Raster Preview, Point Anchor) Draw(EntourageSpec spec)
    {
        spec.Validate();
        var f = Measure(spec);
        var transform = new MatrixTransform(f.Scale, 0, 0, f.Scale, f.Anchor.X, f.Anchor.Y); transform.Freeze();
        Geometry T(Geometry g) => EntourageShapes.Transformed(g, transform);
        var line = LineColor(spec); var primitives = new List<VectorPrimitive>();
        var silhouette = T(f.Art.Silhouette);
        if (spec.Fill != EntourageFill.None) primitives.Add(new VectorPrimitive(silhouette, FillColor(spec), true, 0));
        if (f.Art.OutlineWeight > 0) primitives.Add(new VectorPrimitive(silhouette, line, false, f.Pen * f.Art.OutlineWeight, Round: true));
        if (spec.Fill != EntourageFill.Solid)
            foreach (var stroke in f.Art.Lines)
                primitives.Add(new VectorPrimitive(T(stroke.Geometry), line, false, f.Pen * stroke.Weight, stroke.Clip is { } clip ? T(clip) : null, Round: true));
        var vector = VectorContent.FromPaths(f.Width, f.Height, primitives);
        return (vector, Preview(vector), f.Anchor);
    }

    static Raster Preview(VectorContent vector) => Imaging.Draw(vector.Width, vector.Height, dc => dc.DrawDrawing(vector.Drawing));

    /// <summary>Coverage of a built-in item's whole silhouette in the layer's own pixels (for shadows and picking).</summary>
    public static VectorContent Silhouette(EntourageSpec spec)
    {
        var f = Measure(spec);
        var transform = new MatrixTransform(f.Scale, 0, 0, f.Scale, f.Anchor.X, f.Anchor.Y); transform.Freeze();
        var primitives = new List<VectorPrimitive> { new(EntourageShapes.Transformed(f.Art.Silhouette, transform), Colors.Black, true, 0) };
        if (f.Art.OutlineWeight > 0) primitives.Add(new(EntourageShapes.Transformed(f.Art.Silhouette, transform), Colors.Black, false, f.Pen * f.Art.OutlineWeight, Round: true));
        return VectorContent.FromPaths(f.Width, f.Height, primitives);
    }

    // ---- 내 점경 (image items) ---------------------------------------------------------------

    /// <summary>The styled pixels of a user's item and its anchor in those pixels.</summary>
    public static (Raster Pixels, Point Anchor) Custom(EntourageSpec spec)
    {
        spec.Validate();
        var source = spec.Source ?? throw new InvalidDataException("내 점경의 원본 이미지가 없습니다.");
        return (Style(source, spec), new Point(source.Width / 2.0, spec.View == EntourageView.Plan ? source.Height / 2.0 : source.Height));
    }

    static double CustomScale(EntourageSpec spec) => Math.Clamp(spec.SizePixels / (spec.View == EntourageView.Plan ? Math.Max(spec.Source!.Width, spec.Source.Height) : spec.Source!.Height), .01, 20);

    /// <summary>
    /// Line drawings take the line colour, with white, grey or solid filling the closed areas the lines
    /// enclose. Images keep their colours (none), fade toward white (white), turn grey (gray) or
    /// become a silhouette in the line colour (solid). Unchanged originals are shared, not copied.
    /// </summary>
    public static Raster Style(Raster source, EntourageSpec spec)
    {
        var line = LineColor(spec);
        if (!spec.LineDrawing && spec.Fill == EntourageFill.None) return source;
        var output = new Raster(source.Width, source.Height); var s = source.Data; var o = output.Data;
        if (spec.LineDrawing)
        {
            byte[]? inside = spec.Fill == EntourageFill.None ? null : Enclosed(source);
            var fill = FillColor(spec);
            for (int i = 0, p = 0; i < s.Length / 4; i++, p += 4)
            {
                double ink = s[p + 3] / 255d, back = inside == null ? 0 : inside[i] / 255d;
                // Lines over the fill (straight alpha): a = ink + back·(1 − ink).
                double a = ink + back * (1 - ink);
                if (a <= 0) continue;
                double r = (line.R * ink + fill.R * back * (1 - ink)) / a, g = (line.G * ink + fill.G * back * (1 - ink)) / a, bl = (line.B * ink + fill.B * back * (1 - ink)) / a;
                o[p] = Imaging.Byte(bl); o[p + 1] = Imaging.Byte(g); o[p + 2] = Imaging.Byte(r); o[p + 3] = Imaging.Byte(a * 255 * line.A / 255d);
            }
            return output;
        }
        for (int p = 0; p < s.Length; p += 4)
        {
            double b = s[p], g = s[p + 1], r = s[p + 2];
            switch (spec.Fill)
            {
                case EntourageFill.White: b = 255 - (255 - b) * .45; g = 255 - (255 - g) * .45; r = 255 - (255 - r) * .45; break;
                case EntourageFill.Gray: double y = .0722 * b + .7152 * g + .2126 * r; b = g = r = y; break;
                default: b = line.B; g = line.G; r = line.R; break;
            }
            o[p] = Imaging.Byte(b); o[p + 1] = Imaging.Byte(g); o[p + 2] = Imaging.Byte(r); o[p + 3] = spec.Fill == EntourageFill.Solid ? Imaging.Byte(s[p + 3] * line.A / 255d) : s[p + 3];
        }
        return output;
    }

    /// <summary>Coverage (0..255) of the areas a line drawing encloses: everything the border cannot reach without crossing ink.</summary>
    public static byte[] Enclosed(Raster lines)
    {
        int w = lines.Width, h = lines.Height; var outside = new bool[w * h]; var queue = new Queue<int>();
        bool Open(int i) => lines.Data[i * 4 + 3] < 96;
        void Seed(int x, int y) { int i = y * w + x; if (!outside[i] && Open(i)) { outside[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % w, y = i / w;
            if (x > 0) Seed(x - 1, y); if (x < w - 1) Seed(x + 1, y); if (y > 0) Seed(x, y - 1); if (y < h - 1) Seed(x, y + 1);
        }
        var result = new byte[w * h];
        for (int i = 0; i < result.Length; i++) result[i] = outside[i] ? (byte)0 : (byte)255;
        return result;
    }

    // ---- Layers ---------------------------------------------------------------------------------

    /// <summary>A new layer for the spec with its anchor at `anchor` (parent pixels).</summary>
    public static Layer Create(EntourageSpec spec, Point anchor, string name, bool flip = false, double rotation = 0)
    {
        var layer = new Layer { Name = name, Entourage = spec, FlipX = flip, Rotation = rotation, Category = LayerCategory.Automatic };
        Point local;
        if (spec.IsCustom)
        {
            var (pixels, a) = Custom(spec);
            layer.Kind = LayerKind.Raster; layer.Pixels = pixels; layer.Scale = CustomScale(spec); local = a;
        }
        else
        {
            var (vector, preview, a) = Draw(spec);
            layer.Kind = LayerKind.Vector; layer.Vector = vector; layer.Pixels = preview; local = a;
        }
        var at = layer.Document(local);
        layer.X = anchor.X - at.X; layer.Y = anchor.Y - at.Y;
        return layer;
    }

    /// <summary>Where the layer's anchor is in its parent's pixels.</summary>
    public static Point Anchor(Layer layer) => layer.Document(LocalAnchor(layer));

    // The anchor in the layer's own pixels, from its current spec and pixel size.
    static Point LocalAnchor(Layer layer)
    {
        var spec = layer.Entourage ?? throw new InvalidOperationException("점경 레이어가 아닙니다.");
        bool plan = spec.View == EntourageView.Plan;
        if (spec.IsCustom || EntourageLibrary.Find(spec.ItemId) == null)
            return new Point(layer.Pixels.Width / 2.0, plan ? layer.Pixels.Height / 2.0 : layer.Pixels.Height);
        try
        {
            var f = Measure(spec);
            if (f.Width == layer.Pixels.Width && f.Height == layer.Pixels.Height) return f.Anchor;
        }
        catch (InvalidDataException) { }
        return new Point(layer.Pixels.Width / 2.0, plan ? layer.Pixels.Height / 2.0 : layer.Pixels.Height - 1);
    }

    /// <summary>
    /// Redraws the layer for a changed spec, keeping its anchor, transform, flip, mask (resampled to the
    /// new size) and perspective corners (scaled about the anchor). resetSize drops a size set with the
    /// transform handles, so the item is exactly its Meters at its scale again.
    /// </summary>
    public static void Update(Layer layer, EntourageSpec spec, bool resetSize = false)
    {
        if (layer.Entourage is not { } before) throw new InvalidOperationException("점경 레이어가 아닙니다.");
        if (before == spec && !resetSize) return;
        if (spec.IsCustom != before.IsCustom) throw new InvalidDataException("내장 점경과 내 점경은 서로 바꿀 수 없습니다.");
        var anchor = Anchor(layer); var oldLocal = LocalAnchor(layer);
        int oldWidth = layer.Pixels.Width, oldHeight = layer.Pixels.Height;
        Raster pixels; VectorContent? vector = null; Point local; double scale = layer.Scale;
        // A user's image is sized by its layer scale: only a new size replaces a scale set by hand.
        if (spec.IsCustom) { (pixels, local) = Custom(spec); if (spec.Meters != before.Meters || spec.PixelsPerMeter != before.PixelsPerMeter) scale = CustomScale(spec); }
        else (vector, pixels, local) = Draw(spec);
        if (layer.Mask is { } mask && (oldWidth != pixels.Width || oldHeight != pixels.Height))
        {
            var resized = new byte[pixels.Width * pixels.Height];
            for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
                resized[y * pixels.Width + x] = mask[Math.Min(oldHeight - 1, y * oldHeight / pixels.Height) * oldWidth + Math.Min(oldWidth - 1, x * oldWidth / pixels.Width)];
            layer.Mask = resized;
        }
        if (layer.Warp is { } warp && !spec.IsCustom)
        {
            double ratio = spec.SizePixels / Math.Max(1e-9, before.SizePixels);
            Point Map(Point p) => local + (p - oldLocal) * ratio;
            layer.Warp = new WarpQuad(Map(warp.TopLeft), Map(warp.TopRight), Map(warp.BottomRight), Map(warp.BottomLeft));
        }
        layer.Pixels = pixels; layer.Vector = vector; layer.Entourage = spec; layer.Scale = scale;
        if (resetSize) { layer.ScaleX = 1; layer.ScaleY = 1; layer.Scale = spec.IsCustom ? CustomScale(spec) : 1; }
        layer.X = 0; layer.Y = 0;
        var at = layer.Document(local);
        layer.X = anchor.X - at.X; layer.Y = anchor.Y - at.Y;
    }

    /// <summary>A copy of the layer that covers the item's whole silhouette, as a shadow source.</summary>
    internal static void ShadowSource(Layer copy)
    {
        if (copy.Entourage is not { } spec || copy.Warp != null) return;
        try
        {
            if (spec.IsCustom)
            {
                if (spec.LineDrawing && copy.Pixels.Width == spec.Source?.Width && copy.Pixels.Height == spec.Source.Height)
                    copy.Pixels = Style(spec.Source, spec with { Fill = EntourageFill.Solid });
                return;
            }
            if (copy.Kind != LayerKind.Vector || EntourageLibrary.Find(spec.ItemId) == null) return;
            var silhouette = Silhouette(spec);
            if (silhouette.Width != copy.Pixels.Width || silhouette.Height != copy.Pixels.Height) return;
            copy.Vector = silhouette;
        }
        catch (InvalidDataException) { }
    }

    // ---- Natural scatter ---------------------------------------------------------------------

    /// <summary>
    /// Positions for several copies of one item: along the ground line of `area` (or around `center`)
    /// in elevation, spread over `area` (or a disc around `center`) in plan, each with a slight size
    /// change, a random flip and shape variant, and (plan) a random turn. The same seed gives the same
    /// arrangement. `inside` limits plan positions to a selection.
    /// </summary>
    public static IReadOnlyList<EntouragePlacement> Scatter(EntourageCategory category, EntourageView view, double meters, double pixelsPerMeter, double footprint,
        int variants, int count, Point center, Rect? area, Func<Point, bool>? inside, int seed)
    {
        count = Math.Clamp(count, 1, 64);
        var random = new EntourageRandom((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 17);
        double spread = category switch { EntourageCategory.People => .06, EntourageCategory.Plants => .16, _ => .08 };
        double size = meters * pixelsPerMeter, width = Math.Max(1, footprint);
        var result = new List<EntouragePlacement>();
        EntouragePlacement Make(Point anchor) => new(anchor, Math.Clamp(meters * (1 + random.Range(-spread, spread)), EntourageSpec.MinMeters, EntourageSpec.MaxMeters),
            random.Chance(.5), variants <= 1 ? 0 : random.Below(variants), view == EntourageView.Plan ? Math.Round(random.Range(0, 360), 1) : 0);
        if (view == EntourageView.Elevation)
        {
            double span = area is { } a ? a.Width : Math.Max(width * count * 1.15, width * 1.5);
            double left = area is { } r ? r.Left : center.X - span / 2, ground = area is { } g ? g.Bottom : center.Y;
            double slot = span / count;
            for (int i = 0; i < count; i++)
            {
                double x = left + slot * (i + .5) + random.Range(-.32, .32) * slot;
                result.Add(Make(new Point(x, ground)));
            }
            return result;
        }
        double radius = Math.Max(width, size) * (.55 + .5 * Math.Sqrt(count));
        Rect bounds = area ?? new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        double minimum = Math.Max(width, size) * .72;
        bool Allowed(Point p) => area != null ? inside?.Invoke(p) ?? bounds.Contains(p) : (p - center).Length <= radius;
        for (int i = 0; i < count; i++)
        {
            Point? best = null; double bestDistance = -1;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var p = new Point(bounds.Left + random.Unit() * bounds.Width, bounds.Top + random.Unit() * bounds.Height);
                if (!Allowed(p)) continue;
                double nearest = result.Count == 0 ? double.MaxValue : result.Min(q => (q.Anchor - p).Length);
                if (nearest >= minimum) { best = p; break; }
                if (nearest > bestDistance) { bestDistance = nearest; best = p; }
            }
            if (best is { } chosen) result.Add(Make(chosen));
        }
        // Back to front by height on the sheet, so lower items overlap the ones above them.
        return result.OrderBy(p => p.Anchor.Y).ToList();
    }
}
