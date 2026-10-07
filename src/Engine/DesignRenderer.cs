using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Render only the visible document rectangle at physical screen resolution.
// Affine groups keep their children as paths instead of scaling a group bitmap.
public static partial class DesignRenderer
{
    static readonly ConditionalWeakTable<TextSpec, DrawingGroup> textDrawings = new();
    public static bool HasRetainedContent(Document doc) => doc.Layers.Any(l => l.Kind is LayerKind.Vector or LayerKind.Text or LayerKind.Shape or LayerKind.Material);
    public static Raster Render(Document doc, Rect area, int width, int height, CancellationToken token = default)
        => RenderCore(doc, area, width, height, true, token, default);
    // Checks vary the path pass size and observe each finished pass.
    internal static Raster Render(Document doc, Rect area, int width, int height, CancellationToken token, int passSize, Action? passRendered = null)
        => RenderCore(doc, area, width, height, true, token, new(passSize, passRendered));
    public static Raster RenderOutput(Document doc, CancellationToken token = default)
        => HasRetainedContent(doc) || doc.Layers.Any(l => DrawingLayers.IsContainer(l) || IsPassThrough(l)) ? RenderCore(doc, new Rect(0, 0, doc.Width, doc.Height), doc.Width, doc.Height, false, token, default) : Imaging.Render(doc, token);
    // The document area drawn at width×height (any scale) in chunks of at most 4096 px: exports at 2x and
    // more draw vectors, text and hatch patterns at the output resolution instead of resampling 1x pixels.
    public static Raster RenderScaled(Document doc, Rect area, int width, int height, CancellationToken token = default)
    {
        const int chunk = 4096;
        if ((long)width * height <= (long)chunk * chunk) return Render(doc, area, width, height, token);
        Raster.ValidateSize(width, height);
        var output = new Raster(width, height); double sx = area.Width / width, sy = area.Height / height;
        for (int y = 0; y < height; y += chunk) for (int x = 0; x < width; x += chunk)
        {
            token.ThrowIfCancellationRequested();
            int w = Math.Min(chunk, width - x), h = Math.Min(chunk, height - y);
            var part = Render(doc, new Rect(area.X + x * sx, area.Y + y * sy, w * sx, h * sy), w, h, token);
            for (int row = 0; row < h; row++) Buffer.BlockCopy(part.Data, row * w * 4, output.Data, ((y + row) * width + x) * 4, w * 4);
        }
        return output;
    }
    // Cull: device pixels the caller will read. Layers that cannot touch them are
    // skipped; everything else is drawn exactly as in the full render.
    readonly record struct PathPasses(int Size, Action? Rendered, Rect? Cull = null);
    // Render(doc, area, width, height) on the full surface, but only correct inside `dirty`:
    // the same transforms and surfaces as a fresh render, without the layers that cannot
    // reach those pixels. Inside `dirty` the pixels equal the fresh render's.
    internal static Raster RenderDirty(Document doc, Rect area, int width, int height, Int32Rect dirty, CancellationToken token = default)
    {
        var cull = new Rect(dirty.X - 2, dirty.Y - 2, dirty.Width + 4, dirty.Height + 4);
        return RenderCore(doc, area, width, height, true, token, new(0, null, cull));
    }
    static Matrix ViewMap(Rect area, int width, int height) =>
        new(width / area.Width, 0, 0, height / area.Height, -area.X * width / area.Width, -area.Y * height / area.Height);
    // The pixels `region` of Render(doc, area, width, height), drawn on a surface of that size only.
    internal static Raster RenderRegion(Document doc, Rect area, int width, int height, Int32Rect region, CancellationToken token = default)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.X + area.Y + area.Width + area.Height)) throw new ArgumentException("표시 영역이 올바르지 않습니다.");
        var map = ViewMap(area, width, height); map.OffsetX -= region.X; map.OffsetY -= region.Y;
        return RenderCore(doc, map, region.Width, region.Height, true, token, default);
    }
    static Raster RenderCore(Document doc, Rect area, int width, int height, bool screen, CancellationToken token, PathPasses passes)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.X + area.Y + area.Width + area.Height)) throw new ArgumentException("표시 영역이 올바르지 않습니다.");
        return RenderCore(doc, ViewMap(area, width, height), width, height, screen, token, passes);
    }
    static Raster RenderCore(Document doc, Matrix map, int width, int height, bool screen, CancellationToken token, PathPasses passes)
    {
        Raster.ValidateSize(width, height);
        if (screen && (long)width * height > 16_777_216) throw new ArgumentException("한 번에 그릴 화면 영역이 너무 큽니다.");
        // Light that spreads (빛 번짐) reaches the requested pixels from around them: draw the document's
        // border around the area too, so a viewport, a tile or a chunk shows what the whole image shows.
        if (passes.Cull == null && SpreadMargin(doc, map) is > 0 and var margin)
        {
            var page = new Rect(0, 0, doc.Width, doc.Height); page.Transform(map);
            int left = Math.Min(0, Math.Max(-margin, (int)Math.Floor(page.Left))), top = Math.Min(0, Math.Max(-margin, (int)Math.Floor(page.Top)));
            int right = Math.Max(width, Math.Min(width + margin, (int)Math.Ceiling(page.Right))), bottom = Math.Max(height, Math.Min(height + margin, (int)Math.Ceiling(page.Bottom)));
            if ((left < 0 || top < 0 || right > width || bottom > height) && (long)(right - left) * (bottom - top) <= 4L * Math.Max(16_777_216L, (long)width * height))
            {
                var inner = map; inner.OffsetX -= left; inner.OffsetY -= top;
                var wide = RenderLayers(doc, inner, right - left, bottom - top, token, passes);
                var output = new Raster(width, height);
                for (int row = 0; row < height; row++) Buffer.BlockCopy(wide.Data, ((row - top) * (right - left) - left) * 4, output.Data, row * width * 4, width * 4);
                return output;
            }
        }
        return RenderLayers(doc, map, width, height, token, passes);
    }
    // Device pixels a render must add on each side for the widest visible spreading effect.
    static int SpreadMargin(Document doc, Matrix map)
    {
        if (!doc.Layers.Any(l => l.Kind == LayerKind.Adjustment && l.Visible && l.Adjustment is { } spec && StyleEffects.Reach(spec) > 0)) return 0;
        var lookup = new Dictionary<Guid, Layer>(doc.Layers.Count); int margin = 0;
        foreach (var layer in doc.Layers) lookup.TryAdd(layer.Id, layer);
        foreach (var layer in doc.Layers)
        {
            if (layer.Kind != LayerKind.Adjustment || !layer.Visible || layer.Adjustment is not { } spec || StyleEffects.Reach(spec) <= 0) continue;
            var world = layer.Matrix;
            int depth = 0;
            for (var parent = layer.ParentId; parent is { } id && depth++ < 17 && lookup.TryGetValue(id, out var group); parent = group.ParentId) world.Append(group.Matrix);
            world.Append(map);
            margin = Math.Max(margin, StyleEffects.ReachPixels(spec, Math.Sqrt(Math.Abs(world.M11 * world.M22 - world.M12 * world.M21))));
        }
        return margin;
    }
    static Raster RenderLayers(Document doc, Matrix map, int width, int height, CancellationToken token, PathPasses passes)
    {
        var children = doc.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        Matrix World(Layer layer, Matrix parent) { var result = layer.Matrix; result.Append(parent); return result; }
        Raster Image(Layer layer, Matrix parent, int depth)
        {
            token.ThrowIfCancellationRequested(); if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
            var world = World(layer, parent);
            if (layer.Kind == LayerKind.Group && layer.Warp == null)
            {
                var group = Stack(children.GetValueOrDefault(layer.Id) ?? [], world, depth + 1); ApplyMask(group, layer, world, !DrawingLayers.IsContainer(layer), token); return group;
            }
            var copy = layer.Snapshot(); copy.Opacity = 1; copy.Blend = BlendMode.Normal;
            if (layer.Kind == LayerKind.Group)
            {
                // Perspective groups remain a pixel effect; ordinary affine groups stay vector.
                var subtree = new Document { Width = layer.Pixels.Width, Height = layer.Pixels.Height };
                var ids = new HashSet<Guid> { layer.Id };
                for (int i = 0; i < 16; i++) foreach (var child in doc.Layers.Where(l => l.ParentId.HasValue && ids.Contains(l.ParentId.Value))) ids.Add(child.Id);
                subtree.Layers = doc.Layers.Where(l => l.Id != layer.Id && ids.Contains(l.Id)).Select(l => { var c = l.Snapshot(); if (c.ParentId == layer.Id) c.ParentId = null; return c; }).ToList();
                copy.Pixels = Imaging.Render(subtree, token); copy.Kind = LayerKind.Raster;
            }
            if (layer.Warp == null && layer.Kind is LayerKind.Vector or LayerKind.Text or LayerKind.Material)
            {
                var rendered = RenderRetained(copy, world, width, height, token); ApplyMask(rendered, layer, world, false, token); return rendered;
            }
            var output = new Raster(width, height); Imaging.Composite(output, copy, token, world); return output;
        }
        Raster Stack(Layer[] stack, Matrix parent, int depth)
        {
            var output = new Raster(width, height);
            Paint(stack, parent, depth, output);
            return output;
        }
        // A pass-through folder (a design style) paints its children onto this surface, so their
        // adjustments and blend modes read the layers below the folder. Its opacity and mask blend
        // the result with what was there before; it has no blend mode or clip rectangle of its own.
        void PassThrough(Layer group, Matrix parent, int depth, Raster output)
        {
            if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
            var world = World(group, parent); var members = children.GetValueOrDefault(group.Id) ?? [];
            if (group.Opacity >= 1 && group.Mask == null) { Paint(members, world, depth + 1, output); return; }
            var before = output.Clone(); Paint(members, world, depth + 1, output);
            var inverse = world; inverse.Invert(); var mask = group.Mask; int maskWidth = group.Pixels.Width, maskHeight = group.Pixels.Height;
            Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    double amount = group.Opacity;
                    if (mask != null)
                    {
                        var p = inverse.Transform(new Point(x + .5, y + .5));
                        amount *= p.X < 0 || p.Y < 0 || p.X >= maskWidth || p.Y >= maskHeight ? 0 : mask[(int)p.Y * maskWidth + (int)p.X] / 255d;
                    }
                    if (amount >= 1) continue;
                    int i = (y * width + x) * 4;
                    // Mix in premultiplied space so the alpha of either state never tints the other.
                    double a0 = before.Data[i + 3] / 255d, a1 = output.Data[i + 3] / 255d, a = a0 + (a1 - a0) * amount;
                    for (int c = 0; c < 3; c++)
                    {
                        double v = before.Data[i + c] * a0 + (output.Data[i + c] * a1 - before.Data[i + c] * a0) * amount;
                        output.Data[i + c] = a > 0 ? Imaging.Byte(v / a) : (byte)0;
                    }
                    output.Data[i + 3] = Imaging.Byte(a * 255);
                }
            });
        }
        void Paint(Layer[] stack, Matrix parent, int depth, Raster output)
        {
            for (int index = 0; index < stack.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var layer = stack[index]; if (layer.Clipped) continue;
                int end = index + 1; while (end < stack.Length && stack[end].Clipped) end++;
                if (!layer.Visible || layer.Opacity <= 0) { index = end - 1; continue; }
                if (end == index + 1 && CanBatchPaths(layer, children, depth, token))
                {
                    // CAD object imports can contain thousands of paths. Draw a
                    // compatible run in one surface instead of one per object.
                    while (end < stack.Length && !stack[end].Clipped &&
                        (end + 1 == stack.Length || !stack[end + 1].Clipped) && CanBatchPaths(stack[end], children, depth, token)) end++;
                    // A run that drew nothing leaves the output unchanged.
                    if (DrawPathRun(stack, index, end, children, parent, width, height, passes, token) is { } batch)
                        Imaging.Merge(output, batch, 1, BlendMode.Normal, false, token);
                }
                else if (layer.Kind == LayerKind.Adjustment) Imaging.ApplyAdjustment(output, layer, token, World(layer, parent));
                else if (end == index + 1 && IsPassThrough(layer)) PassThrough(layer, parent, depth, output);
                else if (passes.Cull is { } cull && layer.Kind != LayerKind.Group && layer.Warp == null && !Reaches(layer, World(layer, parent), cull)) { }
                else
                {
                    var image = Image(layer, parent, depth);
                    for (int j = index + 1; j < end; j++)
                    {
                        var clip = stack[j]; if (!clip.Visible || clip.Opacity <= 0) continue;
                        if (clip.Kind == LayerKind.Adjustment) Imaging.ApplyAdjustment(image, clip, token, World(clip, parent));
                        else Imaging.Merge(image, Image(clip, parent, depth), clip.Opacity, clip.Blend, true, token);
                    }
                    Imaging.Merge(output, image, layer.Opacity, layer.Blend, false, token);
                }
                index = end - 1;
            }
        }
        return Stack(doc.Layers.Where(l => l.ParentId == null).ToArray(), map, 0);
    }
    /// <summary>
    /// A folder whose children paint onto the surface below it (see <see cref="Layer.PassThrough"/>).
    /// Giving the folder its own blend mode or a perspective makes it an isolated group again.
    /// </summary>
    public static bool IsPassThrough(Layer layer) => layer.Kind == LayerKind.Group && layer.PassThrough && layer.Warp == null && layer.Blend == BlendMode.Normal;
    // Every leaf paints only inside its pixel rectangle (shapes and resampling add a pixel).
    static bool Reaches(Layer layer, Matrix world, Rect cull)
    {
        var bounds = new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height); bounds.Transform(world);
        return bounds.IntersectsWith(cull);
    }
    internal static DrawingGroup TextDrawing(TextSpec text) => textDrawings.GetValue(text, spec => DocumentFeatures.TextDrawing(spec).Drawing);
    internal static Raster RenderRetained(Layer layer, Matrix map, int width, int height, CancellationToken token)
    {
        if ((long)width * height > 16_777_216 || width > 8192 || height > 8192)
        {
            var output = new Raster(width, height);
            for (int y = 0; y < height; y += 1536) for (int x = 0; x < width; x += 1536)
            {
                token.ThrowIfCancellationRequested(); var tileMap = map; tileMap.OffsetX -= x; tileMap.OffsetY -= y;
                int w = Math.Min(1536, width - x), h = Math.Min(1536, height - y);
                var tile = RenderRetained(layer, tileMap, w, h, token);
                for (int row = 0; row < h; row++) Buffer.BlockCopy(tile.Data, row * w * 4, output.Data, ((row + y) * width + x) * 4, w * 4);
            }
            return output;
        }
        var inverse = map; inverse.Invert();
        var visible = new MatrixTransform(inverse).TransformBounds(new Rect(0, 0, width, height));
        visible.Intersect(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return new Raster(width, height);
        Raster? pdf = null;
        if (layer.Vector is { Format: VectorFormat.Pdf } source)
        {
            double sx = Math.Sqrt(map.M11 * map.M11 + map.M12 * map.M12), sy = Math.Sqrt(map.M21 * map.M21 + map.M22 * map.M22);
            int w = Math.Max(1, (int)Math.Ceiling(visible.Width * sx)), h = Math.Max(1, (int)Math.Ceiling(visible.Height * sy));
            double fit = Math.Min(1, Math.Sqrt(16_777_216d / ((double)w * h)));
            w = Math.Clamp((int)(w * fit), 1, 8192); h = Math.Clamp((int)(h * fit), 1, 8192);
            pdf = PdfCompatibility.RenderRegionAsync(source, visible, w, h, token).GetAwaiter().GetResult();
        }
        token.ThrowIfCancellationRequested();
        // A hatch pattern is redrawn for the device scale; chunked output reuses the cached tile, and a
        // repeat too large for a tile is drawn as vector marks over the visible part only. Gradient
        // screentones are evaluated on exactly these device pixels (map).
        var material = layer.Material is { } fill && pdf == null
            ? MaterialRenderer.Drawing(fill, Math.Max(Math.Sqrt(map.M11 * map.M11 + map.M12 * map.M12), Math.Sqrt(map.M21 * map.M21 + map.M22 * map.M22)), token, visible, map) : null;
        return Imaging.Draw(width, height, dc =>
        {
            dc.PushTransform(new MatrixTransform(map)); dc.PushClip(new RectangleGeometry(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height)));
            if (pdf != null) dc.DrawImage(pdf.Bitmap(), visible);
            else if (material != null) dc.DrawDrawing(material);
            else if (layer.Vector != null) dc.DrawDrawing(layer.Vector.Drawing);
            else dc.DrawDrawing(TextDrawing(layer.Text!));
            dc.Pop(); dc.Pop();
        });
    }
    static void ApplyMask(Raster image, Layer layer, Matrix map, bool clipBounds, CancellationToken token)
    {
        if (layer.Mask == null && !clipBounds) return;
        map.Invert();
        Parallel.For(0, image.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4; if (image.Data[i + 3] == 0) continue;
                var p = map.Transform(new Point(x + .5, y + .5));
                if (p.X < 0 || p.Y < 0 || p.X >= layer.Pixels.Width || p.Y >= layer.Pixels.Height) { image.Data[i + 3] = 0; continue; }
                if (layer.Mask != null) image.Data[i + 3] = Imaging.Byte(image.Data[i + 3] * layer.Mask[(int)p.Y * layer.Pixels.Width + (int)p.X] / 255d);
            }
        });
    }
}
