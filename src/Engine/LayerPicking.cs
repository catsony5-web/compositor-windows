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
        if (!ValidProbe(doc, documentPoint, zoom, radiusDip)) return null;
        return Near(new Picker(doc), documentPoint, zoom, radiusDip);
    }
    // Reference path for checks: the same probes as a plain scan of every stack.
    internal static Layer? PickNearByScan(Document doc, Point documentPoint, double zoom, double radiusDip = 4)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!ValidProbe(doc, documentPoint, zoom, radiusDip)) return null;
        return Near(new Picker(doc, false), documentPoint, zoom, radiusDip);
    }

    // Pointer hover probes on every mouse move. Building the paint-order index
    // walks every object, so a cache keeps it while the scene is unchanged and
    // rebuilds it after any layer, order or visibility change, including
    // in-place gesture edits that have no history revision yet.
    public sealed class Cache
    {
        readonly SceneState state = new();
        Picker? picker;
        long version = -1;
        public int Builds { get; private set; }
        public Layer? PickNear(Document doc, Point documentPoint, double zoom, double radiusDip = 4)
        {
            ArgumentNullException.ThrowIfNull(doc);
            if (!ValidProbe(doc, documentPoint, zoom, radiusDip)) return null;
            long current = state.Update(doc);
            if (picker == null || current != version) { picker = new Picker(doc); version = current; Builds++; }
            return Near(picker, documentPoint, zoom, radiusDip);
        }
        public void Clear() { picker = null; version = -1; state.Reset(); }
        internal bool Holds(Document doc) => state.Holds(doc);
    }

    static bool ValidProbe(Document doc, Point point, double zoom, double radiusDip) =>
        Inside(doc, point) && double.IsFinite(zoom) && zoom > 0 && double.IsFinite(radiusDip) && radiusDip >= 0;

    static Layer? Near(Picker picker, Point documentPoint, double zoom, double radiusDip)
    {
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
        readonly Dictionary<Layer[], StackIndex?> stackIndexes = new(ReferenceEqualityComparer.Instance);
        public int LastOrder => order.Count - 1;

        readonly bool indexStacks;
        public Picker(Document document, bool indexStacks = true)
        {
            this.document = document; this.indexStacks = indexStacks;
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
                    if (!local.IsEmpty) local.Transform(layer.Matrix);
                    footprints[layer] = local;
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
            double alpha = 0; var stack = Children(layer);
            // Children whose footprint misses the point contribute zero alpha, so
            // visiting only a large stack's indexed candidates, bottom to top, is exact.
            var index = IndexOf(stack); var cell = index?.At(local) ?? []; var always = index?.Always ?? [];
            int count = index == null ? stack.Length : cell.Length + always.Length;
            for (int n = 0, x = 0, y = 0; n < count; n++)
            {
                var child = index == null ? stack[n] : stack[y >= always.Length || x < cell.Length && cell[x] < always[y] ? cell[x++] : always[y++]];
                if (child.Clipped || child.Kind == LayerKind.Adjustment) continue;
                double a = LayerAlpha(child, local, depth + 1); alpha = a + alpha * (1 - a);
            }
            return alpha * Sample(layer, local, true) * layer.Opacity;
        }
        StackIndex? IndexOf(Layer[] stack)
        {
            if (!indexStacks || stack.Length < StackIndex.MinimumLayers) return null;
            if (!stackIndexes.TryGetValue(stack, out var index)) stackIndexes.Add(stack, index = StackIndex.Create(stack, footprints));
            return index;
        }
        Layer? PickStack(Layer[] stack, Point parentPoint, double inheritedAlpha, int depth, int minimumOrder)
        {
            if (depth > 16) return null;
            if (IndexOf(stack) is not { } index)
            {
                for (int i = stack.Length - 1; i >= 0; i--)
                    if (PickAt(stack, i, parentPoint, inheritedAlpha, depth, minimumOrder) is { } hit) return hit;
                return null;
            }
            // Large CAD source layers hold hundreds of objects. Probe only those
            // whose footprint can contain the point, top to bottom as a full scan would.
            var cell = index.At(parentPoint); var always = index.Always;
            for (int a = cell.Length - 1, b = always.Length - 1; a >= 0 || b >= 0;)
            {
                int i = b < 0 || a >= 0 && cell[a] > always[b] ? cell[a--] : always[b--];
                if (PickAt(stack, i, parentPoint, inheritedAlpha, depth, minimumOrder) is { } hit) return hit;
            }
            return null;
        }
        Layer? PickAt(Layer[] stack, int i, Point parentPoint, double inheritedAlpha, int depth, int minimumOrder)
        {
            var layer = stack[i]; if (!layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) return null;
            if (footprints.TryGetValue(layer, out var bounds) && !bounds.Contains(parentPoint)) return null;
            // The exact center hit is an occlusion floor. Searching nearby
            // must never reach through that object to a layer beneath it.
            if (minimumOrder >= 0 && (layer.Kind == LayerKind.Group && !layer.Locked
                ? lastDescendantOrder[layer] <= minimumOrder : order[layer] <= minimumOrder)) return null;
            double clip = 1;
            if (layer.Clipped)
            {
                int baseIndex = i - 1; while (baseIndex >= 0 && stack[baseIndex].Clipped) baseIndex--;
                if (baseIndex < 0) return null;
                clip = LayerAlpha(stack[baseIndex], parentPoint, depth);
            }
            if (layer.Kind == LayerKind.Group && !layer.Locked)
            {
                var local = Local(layer, parentPoint);
                double coverage = inheritedAlpha * clip * layer.Opacity * Sample(layer, local, true);
                if (coverage <= MinimumEffectiveAlpha) return null;
                // A visible child establishes group coverage already. Avoid
                // scanning thousands of siblings first just to pick it again.
                var picked = PickStack(Children(layer), local, coverage, depth + 1, minimumOrder);
                if (picked != null) return picked;
            }
            double effective = LayerAlpha(layer, parentPoint, depth) * inheritedAlpha * clip;
            if (effective <= MinimumEffectiveAlpha) return null;
            return order[layer] > minimumOrder ? layer : null;
        }
    }

    // Uniform grid over one large stack's child footprints, in that stack's parent
    // space. A probe visits only children whose footprint can contain the point;
    // a child without a footprint (perspective) is always visited, one with an
    // empty footprint never, exactly as the linear footprint test decides.
    sealed class StackIndex
    {
        public const int MinimumLayers = 32;
        static readonly int[] None = [];
        readonly Rect area;
        readonly int columns, rows;
        int[]?[] cells = [];
        public int[] Always { get; private set; } = [];
        StackIndex(Rect area, int columns, int rows) { this.area = area; this.columns = columns; this.rows = rows; }
        public static StackIndex Create(Layer[] stack, Dictionary<Layer, Rect> footprints)
        {
            var area = Rect.Empty;
            foreach (var layer in stack) if (footprints.TryGetValue(layer, out var bounds) && !bounds.IsEmpty) area.Union(bounds);
            bool usable = !area.IsEmpty && area.Width > 0 && area.Height > 0 && double.IsFinite(area.Width + area.Height);
            double aspect = usable ? Math.Clamp(area.Width / area.Height, 1 / 16d, 16) : 1;
            int columns = usable ? Math.Clamp((int)Math.Round(Math.Sqrt(stack.Length * aspect)), 1, 128) : 1;
            int rows = usable ? Math.Clamp((int)Math.Round(Math.Sqrt(stack.Length / aspect)), 1, 128) : 1;
            var index = new StackIndex(area, columns, rows);
            var lists = new List<int>?[columns * rows]; var always = new List<int>();
            for (int i = 0; i < stack.Length; i++)
            {
                if (!footprints.TryGetValue(stack[i], out var bounds)) { always.Add(i); continue; }
                if (bounds.IsEmpty) continue;
                if (!usable) { always.Add(i); continue; }
                var (c0, r0) = index.Cell(bounds.TopLeft); var (c1, r1) = index.Cell(bounds.BottomRight);
                // Page-sized items (paper, full-canvas folders) would fill every cell.
                if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > Math.Max(16, lists.Length / 4)) { always.Add(i); continue; }
                for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) (lists[r * columns + c] ??= []).Add(i);
            }
            index.cells = usable ? lists.Select(list => list?.ToArray()).ToArray() : [];
            index.Always = always.ToArray();
            return index;
        }
        (int Column, int Row) Cell(Point point) =>
            (Math.Clamp((int)Math.Floor((point.X - area.X) / area.Width * columns), 0, columns - 1),
             Math.Clamp((int)Math.Floor((point.Y - area.Y) / area.Height * rows), 0, rows - 1));
        public int[] At(Point point)
        {
            if (cells.Length == 0 || !area.Contains(point)) return None;
            var (column, row) = Cell(point);
            return cells[row * columns + column] ?? None;
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
