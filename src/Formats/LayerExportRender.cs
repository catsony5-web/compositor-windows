using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Renders parts of a document in document pixel coordinates for layered exports (.psd and
/// layered PDF/.ai). Ancestor groups keep their transforms and bounds, but not their opacity,
/// blend mode, mask or visibility: the exported folder or PDF layer carries those instead.
/// The live document is never changed; every render works on a small temporary scene.
/// </summary>
internal static class LayerExportRender
{
    // One render call stays below the screen renderer's 16.7 MP surface limit.
    const int Tile = 4096;

    internal static Dictionary<Guid, Layer[]> Children(Document document) => document.Layers.Where(l => l.ParentId != null)
        .GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
    internal static Layer[] Roots(Document document) => document.Layers.Where(l => l.ParentId == null).ToArray();
    internal static Layer[] ChildrenOf(Layer layer, Dictionary<Guid, Layer[]> children) => children.GetValueOrDefault(layer.Id) ?? [];

    /// <summary>A container copy that only positions and clips its children.</summary>
    internal static Layer Neutral(Layer source) => Plain(source);

    /// <summary>A visible, unclipped copy drawn at full strength; the exported record keeps the rest.</summary>
    internal static Layer Plain(Layer source, bool keepMask = false, bool keepOpacity = false)
    {
        var copy = source.Snapshot(); bool container = DrawingLayers.IsContainer(source);
        copy.Visible = true; copy.Blend = BlendMode.Normal; copy.Clipped = false;
        if (!keepOpacity) copy.Opacity = 1;
        if (!keepMask) copy.Mask = null;
        // A masked drawing group clips children to its bounds. Keep that after dropping the mask.
        if (!container && copy.Kind == LayerKind.Group && copy.Category == LayerCategory.Drawing) copy.Category = LayerCategory.Automatic;
        return copy;
    }

    static void AddDescendants(Layer layer, Dictionary<Guid, Layer[]> children, List<Layer> output, int depth)
    {
        if (depth > 17) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
        foreach (var child in ChildrenOf(layer, children)) { output.Add(child); if (child.Kind == LayerKind.Group) AddDescendants(child, children, output, depth + 1); }
    }

    /// <summary>
    /// Renders <paramref name="members"/> (siblings in stack order, with the original subtrees
    /// of group members) inside their neutral ancestors, for the given document rectangle.
    /// </summary>
    internal static Raster Render(Document document, IReadOnlyList<Layer> ancestors, IReadOnlyList<Layer> members,
        Dictionary<Guid, Layer[]> children, Int32Rect region, CancellationToken token)
    {
        var layers = new List<Layer>(ancestors.Count + members.Count);
        foreach (var ancestor in ancestors) layers.Add(Neutral(ancestor));
        foreach (var member in members) { layers.Add(member); if (member.Kind == LayerKind.Group) AddDescendants(member, children, layers, ancestors.Count); }
        var scene = new Document(long.MaxValue)
        {
            Width = document.Width, Height = document.Height, Dpi = document.Dpi, Name = document.Name, Layers = layers,
            Materials = document.Materials, MaterialRegions = document.MaterialRegions
        };
        return Region(scene, region, token);
    }

    /// <summary>
    /// The layer's mask (or full coverage) in document space. The mask value is carried as
    /// grey colour and the drawn area as alpha, so edge antialiasing is not applied twice:
    /// the exported layer pixels already carry it.
    /// </summary>
    internal static Raster Coverage(Document document, Layer layer, IReadOnlyList<Layer> ancestors, Dictionary<Guid, Layer[]> children, Int32Rect region, CancellationToken token)
    {
        var grey = new Raster(layer.Pixels.Width, layer.Pixels.Height); var mask = layer.Mask;
        for (int i = 0, p = 0; p < grey.Data.Length; i++, p += 4)
        {
            if ((i & 1048575) == 0) token.ThrowIfCancellationRequested();
            grey.Data[p] = grey.Data[p + 1] = grey.Data[p + 2] = mask?[i] ?? 255; grey.Data[p + 3] = 255;
        }
        var copy = Plain(layer); copy.Kind = LayerKind.Raster; copy.Pixels = grey;
        copy.Text = null; copy.Shape = null; copy.Vector = null; copy.Material = null; copy.Adjustment = null;
        return Render(document, ancestors, [copy], children, region, token);
    }

    internal static Raster Region(Document scene, Int32Rect region, CancellationToken token)
    {
        if ((long)region.Width * region.Height <= (long)Tile * Tile)
            return DesignRenderer.Render(scene, new Rect(region.X, region.Y, region.Width, region.Height), region.Width, region.Height, token);
        var output = new Raster(region.Width, region.Height);
        for (int y = 0; y < region.Height; y += Tile) for (int x = 0; x < region.Width; x += Tile)
        {
            token.ThrowIfCancellationRequested();
            int w = Math.Min(Tile, region.Width - x), h = Math.Min(Tile, region.Height - y);
            var tile = DesignRenderer.Render(scene, new Rect(region.X + x, region.Y + y, w, h), w, h, token);
            for (int row = 0; row < h; row++) Buffer.BlockCopy(tile.Data, row * w * 4, output.Data, ((y + row) * region.Width + x) * 4, w * 4);
        }
        return output;
    }

    /// <summary>Document-space bounding box of a layer's raster, including perspective and ancestor transforms.</summary>
    internal static Rect DocumentBounds(Layer layer, IReadOnlyList<Layer> ancestors)
    {
        double w = layer.Pixels.Width, h = layer.Pixels.Height;
        var points = new[] { new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h) }.Select(layer.Document).ToArray();
        for (int i = ancestors.Count - 1; i >= 0; i--) { var matrix = ancestors[i].Matrix; for (int p = 0; p < points.Length; p++) points[p] = matrix.Transform(points[p]); }
        double left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        return double.IsFinite(left + top + right + bottom) ? new Rect(left, top, right - left, bottom - top) : Rect.Empty;
    }

    /// <summary>A conservative, whole-pixel document rectangle that contains everything the layer can draw.</summary>
    internal static Int32Rect Footprint(Document document, Layer layer, IReadOnlyList<Layer> ancestors)
    {
        var rect = DocumentBounds(layer, ancestors);
        for (int i = 0; i < ancestors.Count && !rect.IsEmpty; i++)
            if (!DrawingLayers.IsContainer(ancestors[i])) rect.Intersect(DocumentBounds(ancestors[i], ancestors.Take(i).ToArray()));
        return Pixels(document, rect);
    }

    internal static Int32Rect Canvas(Document document) => new(0, 0, document.Width, document.Height);

    /// <summary>The drawing area of a stack: its parent's bounds, or the canvas at the top level.</summary>
    internal static Int32Rect StackArea(Document document, IReadOnlyList<Layer> ancestors) =>
        ancestors.Count == 0 ? Canvas(document) : Footprint(document, ancestors[^1], ancestors.Take(ancestors.Count - 1).ToArray());

    static Int32Rect Pixels(Document document, Rect rect)
    {
        if (rect.IsEmpty) return new Int32Rect();
        rect.Inflate(2, 2); rect.Intersect(new Rect(0, 0, document.Width, document.Height));
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return new Int32Rect();
        int left = (int)Math.Floor(rect.Left), top = (int)Math.Floor(rect.Top), right = (int)Math.Ceiling(rect.Right), bottom = (int)Math.Ceiling(rect.Bottom);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    internal static Int32Rect Union(IEnumerable<Int32Rect> rects)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in rects.Where(r => r.Width > 0 && r.Height > 0))
        { left = Math.Min(left, r.X); top = Math.Min(top, r.Y); right = Math.Max(right, r.X + r.Width); bottom = Math.Max(bottom, r.Y + r.Height); }
        return right <= left ? new Int32Rect() : new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Tight bounds of pixels whose alpha is above zero, relative to the raster.</summary>
    internal static Int32Rect Opaque(Raster raster, CancellationToken token)
    {
        int left = raster.Width, top = raster.Height, right = -1, bottom = -1;
        for (int y = 0; y < raster.Height; y++)
        {
            if ((y & 255) == 0) token.ThrowIfCancellationRequested();
            int row = y * raster.Width * 4;
            for (int x = 0; x < raster.Width; x++)
            {
                if (raster.Data[row + x * 4 + 3] == 0) continue;
                if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; bottom = y;
            }
        }
        return right < left ? new Int32Rect() : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    internal static Raster Crop(Raster source, Int32Rect area)
    {
        if (area.X == 0 && area.Y == 0 && area.Width == source.Width && area.Height == source.Height) return source;
        var output = new Raster(area.Width, area.Height);
        for (int y = 0; y < area.Height; y++) Buffer.BlockCopy(source.Data, ((area.Y + y) * source.Width + area.X) * 4, output.Data, y * area.Width * 4, area.Width * 4);
        return output;
    }

    /// <summary>Multiplies alpha by another raster's alpha (a clipping base).</summary>
    internal static void MultiplyAlpha(Raster target, Raster coverage, CancellationToken token)
    {
        for (int i = 3; i < target.Data.Length; i += 4)
        {
            if ((i & 4194303) == 3) token.ThrowIfCancellationRequested();
            target.Data[i] = (byte)((target.Data[i] * coverage.Data[i] + 127) / 255);
        }
    }
}
