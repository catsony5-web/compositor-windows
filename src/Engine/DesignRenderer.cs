using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Render only the visible document rectangle at physical screen resolution.
// Affine groups keep their children as paths instead of scaling a group bitmap.
public static partial class DesignRenderer
{
    static readonly ConditionalWeakTable<TextSpec, DrawingGroup> textDrawings = new();
    public static bool HasRetainedContent(Document doc) => doc.Layers.Any(l => l.Kind is LayerKind.Vector or LayerKind.Text or LayerKind.Shape);
    public static Raster Render(Document doc, Rect area, int width, int height, CancellationToken token = default)
        => RenderCore(doc, area, width, height, true, token);
    public static Raster RenderOutput(Document doc, CancellationToken token = default)
        => HasRetainedContent(doc) ? RenderCore(doc, new Rect(0, 0, doc.Width, doc.Height), doc.Width, doc.Height, false, token) : Imaging.Render(doc, token);
    static Raster RenderCore(Document doc, Rect area, int width, int height, bool screen, CancellationToken token)
    {
        Raster.ValidateSize(width, height);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.X + area.Y + area.Width + area.Height)) throw new ArgumentException("표시 영역이 올바르지 않습니다.");
        if (screen && (long)width * height > 16_777_216) throw new ArgumentException("한 번에 그릴 화면 영역이 너무 큽니다.");
        var children = doc.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var map = new Matrix(width / area.Width, 0, 0, height / area.Height, -area.X * width / area.Width, -area.Y * height / area.Height);
        Matrix World(Layer layer, Matrix parent) { var result = layer.Matrix; result.Append(parent); return result; }
        Raster Image(Layer layer, Matrix parent, int depth)
        {
            token.ThrowIfCancellationRequested(); if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
            var world = World(layer, parent);
            if (layer.Kind == LayerKind.Group && layer.Warp == null)
            {
                var group = Stack(children.GetValueOrDefault(layer.Id) ?? [], world, depth + 1); ApplyMask(group, layer, world, true, token); return group;
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
            if (layer.Warp == null && layer.Kind is LayerKind.Vector or LayerKind.Text)
            {
                var rendered = RenderRetained(copy, world, width, height, token); ApplyMask(rendered, layer, world, false, token); return rendered;
            }
            var output = new Raster(width, height); Imaging.Composite(output, copy, token, world); return output;
        }
        Raster Stack(Layer[] stack, Matrix parent, int depth)
        {
            var output = new Raster(width, height);
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
                    var batch = DrawPathRun(stack, index, end, children, parent, width, height, token);
                    Imaging.Merge(output, batch, 1, BlendMode.Normal, false, token);
                }
                else if (layer.Kind == LayerKind.Adjustment) Imaging.ApplyAdjustment(output, layer, token, World(layer, parent));
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
            return output;
        }
        return Stack(doc.Layers.Where(l => l.ParentId == null).ToArray(), map, 0);
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
        return Imaging.Draw(width, height, dc =>
        {
            dc.PushTransform(new MatrixTransform(map)); dc.PushClip(new RectangleGeometry(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height)));
            if (pdf != null) dc.DrawImage(pdf.Bitmap(), visible);
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
