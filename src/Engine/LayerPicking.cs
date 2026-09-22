using System.Windows;

namespace Compositor.Windows;

public static class LayerPicking
{
    const double MinimumEffectiveAlpha = 1.0 / 255;
    static readonly Vector[][] NearbyRings = CreateNearbyRings();

    public static Layer? Pick(Document doc, Point documentPoint)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!Inside(doc, documentPoint)) return null;
        return new Picker(doc).At(documentPoint);
    }

    // The radius is measured on screen, so thin paths are equally easy to acquire
    // at different zoom levels. No document pixels or selection state are changed.
    public static Layer? PickNear(Document doc, Point documentPoint, double zoom, double radiusDip = 4)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!Inside(doc, documentPoint) || !double.IsFinite(zoom) || zoom <= 0 || !double.IsFinite(radiusDip) || radiusDip < 0) return null;
        var picker = new Picker(doc);
        var exact = picker.At(documentPoint);
        if (radiusDip == 0) return exact;
        int floor = exact == null ? -1 : picker.Order(exact);
        if (floor == picker.LastOrder) return exact;
        double radius = radiusDip / zoom;
        foreach (var ring in NearbyRings)
        {
            Layer? nearest = null;
            int nearestOrder = floor;
            foreach (var offset in ring)
            {
                var candidate = picker.At(documentPoint + offset * radius, floor);
                if (candidate != null && picker.Order(candidate) > nearestOrder)
                {
                    nearest = candidate;
                    nearestOrder = picker.Order(candidate);
                }
            }
            if (nearest != null) return nearest;
        }
        return exact;
    }

    static bool Inside(Document doc, Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && point.X >= 0 && point.Y >= 0 && point.X < doc.Width && point.Y < doc.Height;

    static Vector[][] CreateNearbyRings()
    {
        int[] counts = [8, 12, 16, 24];
        return counts.Select((count, ring) => Enumerable.Range(0, count)
            .Select(i => new Vector(Math.Cos(i * Math.Tau / count), Math.Sin(i * Math.Tau / count)) * ((ring + 1) / 4.0))
            .ToArray()).ToArray();
    }

    sealed class Picker
    {
        readonly Document document;
        readonly Layer[] roots;
        readonly Dictionary<Guid, Layer[]> children;
        readonly Dictionary<Layer, int> order = [];
        readonly Dictionary<Layer, int> lastDescendantOrder = [];
        readonly Dictionary<Layer, System.Windows.Media.Matrix> inverse = [];
        readonly Dictionary<Layer, Rect> footprints = [];
        public int LastOrder => order.Count - 1;

        public Picker(Document document)
        {
            this.document = document;
            roots = document.Layers.Where(l => l.ParentId == null).ToArray();
            children = document.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
            Index(roots, 0);
        }

        // A child can appear anywhere in Document.Layers. Its group's place in
        // the paint stack, rather than its flat list index, determines occlusion.
        void Index(Layer[] stack, int depth)
        {
            if (depth > 16) return;
            foreach (var layer in stack)
            {
                if (!order.TryAdd(layer, order.Count)) continue;
                if (layer.Kind == LayerKind.Group) Index(Children(layer), depth + 1);
                lastDescendantOrder[layer] = order.Count - 1;
                // Conservative bounds prune whole CAD group runs before the
                // nearby-hit probes inspect individual raster samples. A full
                // canvas group has only its children's visible footprint here.
                if (layer.Warp == null)
                {
                    var local = new Rect(-1, -1, layer.Pixels.Width + 2d, layer.Pixels.Height + 2d);
                    if (layer.Kind == LayerKind.Group)
                    {
                        Rect occupied = Rect.Empty;
                        foreach (var child in Children(layer))
                        {
                            if (!child.Visible || child.Opacity <= 0 || child.Kind == LayerKind.Adjustment) continue;
                            if (!footprints.TryGetValue(child, out var childBounds)) { occupied = local; break; }
                            occupied.Union(childBounds);
                        }
                        if (DrawingLayers.IsContainer(layer)) local = occupied; else local.Intersect(occupied);
                    }
                    footprints[layer] = local.IsEmpty ? Rect.Empty : new System.Windows.Media.MatrixTransform(layer.Matrix).TransformBounds(local);
                }
            }
        }

        public int Order(Layer layer) => order[layer];
        public Layer? At(Point documentPoint, int minimumOrder = -1) => Inside(document, documentPoint)
            ? PickStack(roots, documentPoint, 1, 0, minimumOrder) : null;
        Layer[] Children(Layer layer) => children.GetValueOrDefault(layer.Id) ?? [];
        Point Local(Layer layer, Point parentPoint)
        {
            if (!inverse.TryGetValue(layer, out var matrix))
            {
                matrix = layer.Matrix;
                matrix.Invert();
                inverse.Add(layer, matrix);
            }
            var local = matrix.Transform(parentPoint);
            return layer.Warp == null ? local : layer.Warp.Inverse(local, layer.Pixels.Width, layer.Pixels.Height);
        }

        double LayerAlpha(Layer layer, Point parentPoint, int depth)
        {
            if (depth > 16 || !layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) return 0;
            if (footprints.TryGetValue(layer, out var bounds) && !bounds.Contains(parentPoint)) return 0;
            var local = Local(layer, parentPoint);
            if (!double.IsFinite(local.X) || !double.IsFinite(local.Y)) return 0;
            if (layer.Kind != LayerKind.Group) return Sample(layer, local, false) * layer.Opacity;
            double alpha = 0;
            foreach (var child in Children(layer))
            {
                if (child.Clipped || child.Kind == LayerKind.Adjustment) continue;
                double a = LayerAlpha(child, local, depth + 1); alpha = a + alpha * (1 - a);
            }
            return alpha * Sample(layer, local, true) * layer.Opacity;
        }
        Layer? PickStack(Layer[] stack, Point parentPoint, double inheritedAlpha, int depth, int minimumOrder)
        {
            if (depth > 16) return null;
            for (int i = stack.Length - 1; i >= 0; i--)
            {
                var layer = stack[i]; if (!layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) continue;
                if (footprints.TryGetValue(layer, out var bounds) && !bounds.Contains(parentPoint)) continue;
                // The exact center hit is an occlusion floor. Searching nearby
                // must never reach through that object to a layer beneath it.
                if (minimumOrder >= 0 && (layer.Kind == LayerKind.Group && !layer.Locked
                    ? lastDescendantOrder[layer] <= minimumOrder : order[layer] <= minimumOrder)) continue;
                double clip = 1;
                if (layer.Clipped)
                {
                    int baseIndex = i - 1; while (baseIndex >= 0 && stack[baseIndex].Clipped) baseIndex--;
                    if (baseIndex < 0) continue;
                    clip = LayerAlpha(stack[baseIndex], parentPoint, depth);
                }
                if (layer.Kind == LayerKind.Group && !layer.Locked)
                {
                    var local = Local(layer, parentPoint);
                    double coverage = inheritedAlpha * clip * layer.Opacity * Sample(layer, local, true);
                    if (coverage <= MinimumEffectiveAlpha) continue;
                    // A visible child establishes group coverage already. Avoid
                    // scanning thousands of siblings first just to pick it again.
                    var picked = PickStack(Children(layer), local, coverage, depth + 1, minimumOrder);
                    if (picked != null) return picked;
                }
                double effective = LayerAlpha(layer, parentPoint, depth) * inheritedAlpha * clip;
                if (effective <= MinimumEffectiveAlpha) continue;
                if (order[layer] > minimumOrder) return layer;
            }
            return null;
        }
    }
    static double Sample(Layer layer, Point local, bool maskOnly)
    {
        if (maskOnly && DrawingLayers.IsContainer(layer)) return 1;
        var raster = layer.Pixels;
        double sx = local.X - .5, sy = local.Y - .5;
        if (sx < -1 || sy < -1 || sx > raster.Width || sy > raster.Height) return 0;
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy); double fx = sx - x0, fy = sy - y0;
        double At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= raster.Width || y >= raster.Height) return 0;
            int index = y * raster.Width + x;
            return (maskOnly ? 1 : raster.Data[index * 4 + 3] / 255.0) * (layer.Mask == null ? 1 : layer.Mask[index] / 255.0);
        }
        return At(x0, y0) * (1 - fx) * (1 - fy) + At(x0 + 1, y0) * fx * (1 - fy) + At(x0, y0 + 1) * (1 - fx) * fy + At(x0 + 1, y0 + 1) * fx * fy;
    }
}
