using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Draws a layer's live effects (레이어 효과) for both renderers, every export and the move preview.
/// The order is the list order, from the bottom: shadows and outer glows (<see cref="LayerEffect.Behind"/>)
/// paint onto what is below the layer before it, with the layer's opacity and their own blend mode;
/// the other effects paint on the layer's own surface before it is merged: an outside stroke under
/// the layer's pixels, a centre stroke over them, and inside strokes, inner glows and overlays only
/// where the layer itself has coverage. Coverage planes come from a cached
/// <see cref="LayerEffectField"/> in the layer's own axes, so moves and rotations reuse them and an
/// effect reaches beyond the layer's bounds by its <see cref="LayerEffect.Reach"/>.
/// </summary>
public static class LayerEffectRenderer
{
    // Largest effect grid; zoomed-in views of large layers use coarser cells instead.
    const long MaxCells = 16_777_216;
    const int CacheEntries = 16;
    const long CacheBytes = 640L * 1024 * 1024;

    // ---- Fields --------------------------------------------------------------------------

    // The content a field was made from is held weakly: a cached field never keeps a replaced
    // (painted over, re-rendered) layer image alive.
    sealed class FieldKey(Layer layer, double cell, int margin, bool retained)
    {
        readonly WeakReference<byte[]> pixels = new(layer.Pixels.Data);
        readonly WeakReference<byte[]>? mask = layer.Mask is { } m ? new(m) : null;
        readonly WeakReference<object>? content = ContentOf(layer) is { } c ? new(c) : null;
        readonly LayerKind kind = layer.Kind;
        readonly (int, int, double, double, double, int, bool) values = (layer.Pixels.Width, layer.Pixels.Height, ScaleX(layer), ScaleY(layer), cell, margin, retained);
        public bool Alive => pixels.TryGetTarget(out _);
        public bool Matches(Layer layer, double cell, int margin, bool retained) =>
            pixels.TryGetTarget(out var p) && ReferenceEquals(p, layer.Pixels.Data) && kind == layer.Kind &&
            (mask == null ? layer.Mask == null : mask.TryGetTarget(out var m) && ReferenceEquals(m, layer.Mask)) &&
            (content == null ? ContentOf(layer) == null : content.TryGetTarget(out var c) && Equals(c, ContentOf(layer))) &&
            values == (layer.Pixels.Width, layer.Pixels.Height, ScaleX(layer), ScaleY(layer), cell, margin, retained);
    }
    static object? ContentOf(Layer layer) => layer.Kind switch { LayerKind.Text => layer.Text, LayerKind.Shape => layer.Shape, LayerKind.Vector => layer.Vector, LayerKind.Material => layer.Material, _ => null };
    static readonly object cacheGate = new();
    static readonly LinkedList<(FieldKey Key, LayerEffectField Field)> cache = new();
    // Fields built (cache misses), for the checks.
    internal static int FieldBuilds;

    internal static void ClearCache() { lock (cacheGate) cache.Clear(); }

    static double ScaleX(Layer layer) => Math.Abs(layer.Scale * layer.ScaleX);
    static double ScaleY(Layer layer) => Math.Abs(layer.Scale * layer.ScaleY);

    /// <summary>Output pixels per parent pixel for a layer drawn with <paramref name="world"/> (local → output).</summary>
    internal static double OutputScale(Layer layer, Matrix world)
    {
        double local = ScaleX(layer) * ScaleY(layer);
        double scale = Math.Sqrt(Math.Abs(world.M11 * world.M22 - world.M12 * world.M21) / Math.Max(1e-12, local));
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    // How far the coverage planes reach outward and inward (parent px), which sets the grid margin.
    static double GridReach(LayerEffect[] effects)
    {
        double reach = 0;
        foreach (var effect in effects)
        {
            if (!effect.UsesCoverage) continue;
            reach = Math.Max(reach, effect.Kind switch
            {
                LayerEffectKind.DropShadow => effect.Reach - effect.Distance,
                LayerEffectKind.InnerGlow => effect.Hard + 1.4 * effect.Soft,
                _ => effect.Reach
            });
        }
        return reach;
    }

    // Cells of 1/q parent px, q the output scale rounded up to a power of two (1 at actual size): as sharp
    // as the output, and one grid for every scale within a factor of two. Too large a grid coarsens.
    static (double Cell, double Kx, double Ky, int Margin, int Width, int Height) Grid(Layer layer, double outputScale, LayerEffect[] effects)
    {
        double q = Math.Clamp(Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(outputScale, 1e-6)) - 1e-9)), 1 / 16.0, 8);
        double cell = 1 / q, sx = ScaleX(layer), sy = ScaleY(layer), reach = GridReach(effects);
        while (true)
        {
            double kx = sx / cell, ky = sy / cell;
            // The margin grows in steps, so small size changes keep the same grid.
            int margin = ((int)Math.Ceiling(reach / cell) + 3 + 15) / 16 * 16;
            int width = (int)Math.Min(int.MaxValue / 2, Math.Ceiling(layer.Pixels.Width * kx - 1e-9) + 2.0 * margin);
            int height = (int)Math.Min(int.MaxValue / 2, Math.Ceiling(layer.Pixels.Height * ky - 1e-9) + 2.0 * margin);
            if ((long)width * height <= MaxCells || cell >= 256) return (cell, kx, ky, margin, Math.Max(1, width), Math.Max(1, height));
            cell *= 2;
        }
    }

    // Kinds the design renderer draws from their retained content; their coverage is drawn the same way.
    static bool DrawsRetained(Layer layer) => layer.Warp == null &&
        (layer.Kind is LayerKind.Vector or LayerKind.Text or LayerKind.Material || layer.Shape is { } shape && ShapeGeometry.UsesDrawing(shape));

    internal static LayerEffectField Field(Layer layer, double outputScale, bool retained, CancellationToken token)
    {
        var effects = LayerEffects.Active(layer);
        var grid = Grid(layer, outputScale, effects);
        retained &= DrawsRetained(layer);
        lock (cacheGate)
        {
            for (var node = cache.First; node != null; node = node.Next)
                if (node.Value.Key.Matches(layer, grid.Cell, grid.Margin, retained)) { cache.Remove(node); cache.AddFirst(node); return node.Value.Field; }
        }
        var field = new LayerEffectField(grid.Width, grid.Height, grid.Margin, grid.Cell, grid.Kx, grid.Ky, Coverage(layer, grid.Width, grid.Height, new Matrix(grid.Kx, 0, 0, grid.Ky, grid.Margin, grid.Margin), retained, token));
        lock (cacheGate)
        {
            Interlocked.Increment(ref FieldBuilds);
            cache.AddFirst((new FieldKey(layer, grid.Cell, grid.Margin, retained), field));
            long bytes = 0; int count = 0;
            for (var node = cache.First; node != null;)
            {
                var next = node.Next;
                if (!node.Value.Key.Alive) { cache.Remove(node); node = next; continue; }
                bytes += node.Value.Field.Bytes; count++;
                if (count > CacheEntries || bytes > CacheBytes && count > 1) cache.Remove(node);
                node = next;
            }
        }
        return field;
    }

    // The content's coverage on the grid: drawn like the renderer draws it (retained content crisp,
    // pixels resampled), with the layer mask, at full opacity and without its warp.
    static byte[] Coverage(Layer layer, int width, int height, Matrix localToGrid, bool retained, CancellationToken token)
    {
        var copy = Plain(layer); copy.Warp = null;
        Raster surface;
        if (retained)
        {
            surface = DesignRenderer.RenderRetained(copy, localToGrid, width, height, token);
            if (layer.Mask is { } mask)
            {
                var back = localToGrid; back.Invert(); int mw = layer.Pixels.Width, mh = layer.Pixels.Height;
                Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4; if (surface.Data[i + 3] == 0) continue;
                        var p = back.Transform(new Point(x + .5, y + .5));
                        int lx = (int)Math.Floor(p.X), ly = (int)Math.Floor(p.Y);
                        surface.Data[i + 3] = lx < 0 || ly < 0 || lx >= mw || ly >= mh ? (byte)0 : (byte)((surface.Data[i + 3] * mask[ly * mw + lx] + 127) / 255);
                    }
                });
            }
        }
        else { surface = new Raster(width, height); Imaging.Composite(surface, copy, token, localToGrid); }
        var alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = surface.Data[i * 4 + 3];
        return alpha;
    }

    // The layer as content only: full strength, normal blend, no effects.
    static Layer Plain(Layer layer)
    {
        var copy = layer.Snapshot(); copy.Opacity = 1; copy.Blend = BlendMode.Normal; copy.Effects = null; copy.Clipped = false; copy.Visible = true;
        return copy;
    }

    // ---- Bounds ----------------------------------------------------------------------------

    /// <summary>Output pixels the layer's effects can reach beyond the layer's own bounds.</summary>
    public static double OutputReach(Layer layer, Matrix world) => LayerEffects.Any(layer) ? LayerEffects.Reach(layer) * OutputScale(layer, world) + 2 : 0;

    /// <summary>The layer's rectangle in its local pixels, grown by its effects' reach.</summary>
    public static Rect LocalBounds(Layer layer)
    {
        var rect = new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height);
        if (!LayerEffects.Any(layer)) return rect;
        double reach = LayerEffects.Reach(layer) + 2;
        rect.Inflate(reach / Math.Max(1e-6, ScaleX(layer)), reach / Math.Max(1e-6, ScaleY(layer)));
        return rect;
    }

    /// <summary>True when a shadow or glow of the layer reads the pixels below with a blend mode other than normal.</summary>
    public static bool ReadsBackdrop(Layer layer) => LayerEffects.Any(layer) && Array.Exists(layer.Effects!, e => e.Enabled && e.Behind && e.Blend != BlendMode.Normal);

    public static bool HasBehind(Layer layer) => LayerEffects.Any(layer) && Array.Exists(layer.Effects!, e => e.Enabled && e.Behind);

    // ---- Painting --------------------------------------------------------------------------

    enum Mode { Over, AtopContent, AtopTarget }

    // Output → grid for a layer drawn with `world` (warp included), shifted by a plane offset in cells.
    readonly struct Mapping
    {
        readonly Matrix inverse, localToGrid; readonly ProjectiveMap? warp; readonly int width, height;
        public Mapping(Layer layer, Matrix world, Matrix localToGrid)
        {
            inverse = world; inverse.Invert(); this.localToGrid = localToGrid;
            warp = layer.Warp?.Map().Inverse(); width = layer.Pixels.Width; height = layer.Pixels.Height;
        }
        public Point Local(Point output)
        {
            var p = inverse.Transform(output);
            if (warp is { } w) { p = w.Transform(p); p = new Point(p.X * width, p.Y * height); }
            return p;
        }
        public Point Grid(Point output) => localToGrid.Transform(Local(output));
    }

    // Output rectangle covered by a local rectangle, clamped to the target.
    static Int32Rect OutputArea(Layer layer, Matrix world, Rect local, int width, int height)
    {
        var corners = new[] { local.TopLeft, local.TopRight, local.BottomRight, local.BottomLeft };
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var corner in corners)
        {
            var p = world.Transform(layer.Warp?.Forward(corner, layer.Pixels.Width, layer.Pixels.Height) ?? corner);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return new Int32Rect(0, 0, width, height);
            left = Math.Min(left, p.X); top = Math.Min(top, p.Y); right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
        }
        int l = (int)Math.Clamp(Math.Floor(left) - 1, 0, width), t = (int)Math.Clamp(Math.Floor(top) - 1, 0, height);
        int r = (int)Math.Clamp(Math.Ceiling(right) + 1, 0, width), b = (int)Math.Clamp(Math.Ceiling(bottom) + 1, 0, height);
        return r <= l || b <= t ? new Int32Rect() : new Int32Rect(l, t, r - l, b - t);
    }

    /// <summary>
    /// Paints the shadows and outer glows of <paramref name="layer"/> onto <paramref name="target"/> (what is
    /// below the layer), drawn with <paramref name="world"/> (local → target pixels). A clipped layer's
    /// effects stay within the target's coverage.
    /// </summary>
    public static void PaintBehind(Raster target, Layer layer, Matrix world, bool retained, bool clipped, double opacity, CancellationToken token = default)
    {
        var effects = LayerEffects.Active(layer);
        if (!Array.Exists(effects, e => e.Behind) || opacity <= 0) return;
        var field = Field(layer, OutputScale(layer, world), retained, token);
        foreach (var effect in effects)
        {
            if (!effect.Behind) continue;
            token.ThrowIfCancellationRequested();
            PaintPlane(target, layer, world, field, effect, field.Plane(effect, token), opacity, clipped ? Mode.AtopTarget : Mode.Over, null, token);
        }
    }

    /// <summary>
    /// Paints the effects that belong to the layer itself onto <paramref name="surface"/>, which holds the
    /// layer's own pixels drawn with <paramref name="world"/> at full opacity.
    /// </summary>
    public static void PaintFront(Raster surface, Layer layer, Matrix world, bool retained, CancellationToken token = default)
    {
        var effects = LayerEffects.Active(layer);
        if (!Array.Exists(effects, e => !e.Behind)) return;
        // The layer's coverage before any effect: inside strokes, inner glows and overlays stay within it.
        var area = OutputArea(layer, world, new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height), surface.Width, surface.Height);
        var content = new ContentCoverage(area, new byte[area.Width * area.Height]);
        for (int y = 0; y < area.Height; y++)
        {
            int source = ((area.Y + y) * surface.Width + area.X) * 4 + 3, row = y * area.Width;
            for (int x = 0; x < area.Width; x++) content.Alpha[row + x] = surface.Data[source + x * 4];
        }
        LayerEffectField? field = null;
        static bool Outside(LayerEffect e) => e.Kind == LayerEffectKind.Stroke && e.Position == StrokePosition.Outside;
        foreach (var effect in effects)
        {
            if (effect.Behind || Outside(effect)) continue;
            token.ThrowIfCancellationRequested();
            if (effect.UsesCoverage)
            {
                field ??= Field(layer, OutputScale(layer, world), retained, token);
                var mode = effect.Kind == LayerEffectKind.InnerGlow || effect.Position == StrokePosition.Inside ? Mode.AtopContent : Mode.Over;
                PaintPlane(surface, layer, world, field, effect, field.Plane(effect, token), 1, mode, content, token);
            }
            else Overlay(surface, layer, world, effect, content, token);
        }
        // Outside strokes stack among themselves in list order and go under the layer as one: the layer's
        // anti-aliased edge then blends into the nearest stroke without a seam.
        if (!Array.Exists(effects, Outside)) return;
        field ??= Field(layer, OutputScale(layer, world), retained, token);
        var reach = OutputArea(layer, world, Rect.Transform(new Rect(0, 0, field.Width, field.Height), field.GridToLocal), surface.Width, surface.Height);
        if (reach.Width <= 0 || reach.Height <= 0) return;
        var stack = new Raster(reach.Width, reach.Height); var shifted = world; shifted.Translate(-reach.X, -reach.Y);
        foreach (var effect in effects)
            if (Outside(effect)) PaintPlane(stack, layer, shifted, field, effect, field.Plane(effect, token), 1, Mode.Over, null, token);
        Parallel.For(0, reach.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < reach.Width; x++)
            {
                int s = (y * reach.Width + x) * 4; if (stack.Data[s + 3] == 0) continue;
                Under(surface.Data, ((reach.Y + y) * surface.Width + reach.X + x) * 4, stack.Data[s] / 255.0, stack.Data[s + 1] / 255.0, stack.Data[s + 2] / 255.0, stack.Data[s + 3] / 255.0);
            }
        });
    }

    sealed record ContentCoverage(Int32Rect Area, byte[] Alpha)
    {
        public double At(int x, int y) => x < Area.X || y < Area.Y || x >= Area.X + Area.Width || y >= Area.Y + Area.Height ? 0 : Alpha[(y - Area.Y) * Area.Width + x - Area.X] / 255.0;
    }

    static void PaintPlane(Raster target, Layer layer, Matrix world, LayerEffectField field, LayerEffect effect, byte[] plane, double opacity, Mode mode, ContentCoverage? content, CancellationToken token)
    {
        var color = DocumentFeatures.Color(effect.ColorArgb);
        double strength = effect.Opacity * opacity * color.A / 255;
        if (strength <= 0) return;
        var toLocal = field.GridToLocal; var toGrid = field.LocalToGrid;
        if (effect.Kind == LayerEffectKind.DropShadow && effect.Distance > 0)
        {
            // The shadow falls along its angle in the parent space; in the grid that direction turns with
            // the layer (and flips with it), and its length is in cells.
            double angle = effect.Angle * Math.PI / 180;
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle)) * effect.Distance;
            var linear = layer.Matrix; linear.OffsetX = linear.OffsetY = 0; linear.Invert();
            var local = linear.Transform(direction);
            var shift = new Vector(local.X * field.Kx, local.Y * field.Ky);
            toGrid.Translate(-shift.X, -shift.Y); toLocal = toGrid; toLocal.Invert();
        }
        var gridToOutput = toLocal; gridToOutput.Append(world);
        var area = OutputArea(layer, world, Rect.Transform(new Rect(0, 0, field.Width, field.Height), toLocal), target.Width, target.Height);
        if (mode == Mode.AtopContent && content != null) area = Intersect(area, content.Area);
        if (area.Width <= 0 || area.Height <= 0) return;
        var map = new Mapping(layer, world, toGrid);
        bool affine = layer.Warp == null; var inverse = gridToOutput; inverse.Invert();
        int gw = field.Width, gh = field.Height;
        double b = color.B / 255.0, g = color.G / 255.0, r = color.R / 255.0;
        Parallel.For(area.Y, area.Y + area.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                var p = affine ? inverse.Transform(new Point(x + .5, y + .5)) : map.Grid(new Point(x + .5, y + .5));
                double coverage = Sample(plane, gw, gh, p.X - .5, p.Y - .5);
                if (coverage <= 0) continue;
                double a = coverage * strength; int i = (y * target.Width + x) * 4;
                switch (mode)
                {
                    case Mode.Over: Imaging.Over(target.Data, i, b, g, r, a, effect.Blend); break;
                    case Mode.AtopTarget: Atop(target.Data, i, b, g, r, a, effect.Blend); break;
                    default:
                        double own = content!.At(x, y), all = target.Data[i + 3] / 255.0;
                        if (own > 0 && all > 0) Atop(target.Data, i, b, g, r, Math.Min(1, a * own / all), effect.Blend);
                        break;
                }
            }
        });
    }

    static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y), r = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r <= l || bottom <= t ? new Int32Rect() : new Int32Rect(l, t, r - l, bottom - t);
    }

    // Bilinear coverage of a plane; outside the plane is empty.
    static double Sample(byte[] plane, int w, int h, double sx, double sy)
    {
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        if (x0 < -1 || y0 < -1 || x0 >= w || y0 >= h) return 0;
        double fx = sx - x0, fy = sy - y0;
        double V(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : plane[y * w + x];
        return ((V(x0, y0) * (1 - fx) + V(x0 + 1, y0) * fx) * (1 - fy) + (V(x0, y0 + 1) * (1 - fx) + V(x0 + 1, y0 + 1) * fx) * fy) / 255;
    }

    // Destination-over: the effect goes under what the pixel already holds.
    static void Under(byte[] dst, int i, double b, double g, double r, double a)
    {
        double da = dst[i + 3] / 255.0, oa = da + a * (1 - da);
        if (oa <= 0) return;
        double under = a * (1 - da);
        dst[i] = Imaging.Byte((dst[i] * da + b * 255 * under) / oa); dst[i + 1] = Imaging.Byte((dst[i + 1] * da + g * 255 * under) / oa);
        dst[i + 2] = Imaging.Byte((dst[i + 2] * da + r * 255 * under) / oa); dst[i + 3] = Imaging.Byte(oa * 255);
    }

    // Source-atop: a share of the pixel takes the (blended) effect color; its coverage stays.
    static void Atop(byte[] dst, int i, double b, double g, double r, double a, BlendMode blend)
    {
        if (dst[i + 3] == 0 || a <= 0) return;
        var color = Imaging.BlendRgb(dst[i + 2] / 255.0, dst[i + 1] / 255.0, dst[i] / 255.0, r, g, b, blend);
        dst[i] = Imaging.Byte(dst[i] * (1 - a) + color.B * a * 255); dst[i + 1] = Imaging.Byte(dst[i + 1] * (1 - a) + color.G * a * 255);
        dst[i + 2] = Imaging.Byte(dst[i + 2] * (1 - a) + color.R * a * 255);
    }

    // Color and gradient overlays recolor the layer's own pixels and keep its coverage. A gradient runs
    // across the layer's rectangle along its angle, in the layer's own axes, so it turns with the layer.
    static void Overlay(Raster target, Layer layer, Matrix world, LayerEffect effect, ContentCoverage content, CancellationToken token)
    {
        var area = content.Area;
        if (area.Width <= 0 || area.Height <= 0 || effect.Opacity <= 0) return;
        var start = DocumentFeatures.Color(effect.ColorArgb); var end = DocumentFeatures.Color(effect.EndColorArgb);
        bool gradient = effect.Kind == LayerEffectKind.GradientOverlay;
        double angle = effect.Angle * Math.PI / 180, ux = Math.Cos(angle), uy = Math.Sin(angle);
        double w = layer.Pixels.Width, h = layer.Pixels.Height, extent = Math.Max(1e-9, Math.Abs(ux) * w / 2 + Math.Abs(uy) * h / 2);
        var map = new Mapping(layer, world, Matrix.Identity);
        Parallel.For(area.Y, area.Y + area.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                double own = content.At(x, y); int i = (y * target.Width + x) * 4; double all = target.Data[i + 3] / 255.0;
                if (own <= 0 || all <= 0) continue;
                double r = start.R, g = start.G, b = start.B, alpha = start.A;
                if (gradient)
                {
                    var p = map.Local(new Point(x + .5, y + .5));
                    double t = Math.Clamp(.5 + ((p.X - w / 2) * ux + (p.Y - h / 2) * uy) / (2 * extent), 0, 1);
                    // Interpolate premultiplied so a transparent end fades without a dark fringe.
                    double a0 = start.A / 255.0 * (1 - t), a1 = end.A / 255.0 * t; alpha = (a0 + a1) * 255;
                    if (alpha <= 0) continue;
                    r = (start.R * a0 + end.R * a1) / (a0 + a1); g = (start.G * a0 + end.G * a1) / (a0 + a1); b = (start.B * a0 + end.B * a1) / (a0 + a1);
                }
                double a = effect.Opacity * alpha / 255 * Math.Min(1, own / all);
                Atop(target.Data, i, b / 255, g / 255, r / 255, a, effect.Blend);
            }
        });
    }

    // ---- Whole layer -----------------------------------------------------------------------

    /// <summary>
    /// The layer with its effects as one image on its effect grid, and the grid's mapping to the layer's
    /// local pixels (the move preview draws it at that mapping followed by the layer's transform).
    /// Behind effects keep their own opacity; the caller applies the layer's opacity to the whole image.
    /// </summary>
    public static (Raster Pixels, Matrix ToLocal) Flatten(Layer layer, double outputScale = 1, CancellationToken token = default)
    {
        // Drawn like the pixel compositor draws it (Imaging.Render), on the layer's own axes.
        var flat = layer.Snapshot(); flat.Warp = null;
        var field = Field(flat, outputScale, false, token);
        var toGrid = field.LocalToGrid;
        var image = new Raster(field.Width, field.Height);
        PaintBehind(image, flat, toGrid, false, false, 1, token);
        var own = new Raster(field.Width, field.Height);
        Imaging.Composite(own, Plain(flat), token, toGrid);
        PaintFront(own, flat, toGrid, false, token);
        Imaging.Merge(image, own, 1, BlendMode.Normal, false, token);
        return (image, field.GridToLocal);
    }

    // Layer rows are rebuilt on every edit; a thumbnail is made again only when its layer's look changed.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Tuple<object?, byte[]?, LayerEffect[], double, double, System.Windows.Media.Imaging.BitmapSource>> thumbnails = new();

    /// <summary>A layer-list thumbnail that shows the layer's effects.</summary>
    public static System.Windows.Media.Imaging.BitmapSource Thumbnail(Layer layer, int maxSide = 68)
    {
        var effects = layer.Effects ?? [];
        if (thumbnails.TryGetValue(layer.Pixels.Data, out var known) && Equals(known.Item1, ContentOf(layer)) && ReferenceEquals(known.Item2, layer.Mask) &&
            LayerEffects.Same(known.Item3, effects) && known.Item4 == ScaleX(layer) && known.Item5 == ScaleY(layer) && known.Item6.PixelWidth <= maxSide && known.Item6.PixelHeight <= maxSide)
            return known.Item6;
        double size = Math.Max(1, Math.Max(layer.Pixels.Width * ScaleX(layer), layer.Pixels.Height * ScaleY(layer)) + 2 * LayerEffects.Reach(layer));
        var (pixels, _) = Flatten(layer, Math.Min(1, maxSide * 2 / size));
        var thumbnail = pixels.Thumbnail(maxSide);
        thumbnails.AddOrUpdate(layer.Pixels.Data, Tuple.Create(ContentOf(layer), layer.Mask, effects, ScaleX(layer), ScaleY(layer), thumbnail));
        return thumbnail;
    }
}
