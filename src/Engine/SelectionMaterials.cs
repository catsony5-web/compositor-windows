using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Which kind of surface a selected region most likely is.
public enum SurfaceHint { General, Wall, Floor, Ground }

public sealed record MaterialSuggestion(SurfaceHint Surface, string? LayerName, IReadOnlyList<MaterialKind> Order);

// Recommended material swatches for a selected region. The drawing layer that best
// matches the selection's bounds names the surface (A-WALL, A-FLOR-PATT, L-SITE); the
// region's shape decides between a wall band and a floor area when names are silent.
public static class SelectionMaterials
{
    // Built-in swatches offered for a selection. Plain gray fill is left out.
    public static readonly MaterialKind[] Presets =
        [MaterialKind.Concrete, MaterialKind.Brick, MaterialKind.Wood, MaterialKind.Tile, MaterialKind.Stone, MaterialKind.Insulation, MaterialKind.Gravel, MaterialKind.Diagonal];

    static readonly Dictionary<SurfaceHint, MaterialKind[]> orders = new()
    {
        [SurfaceHint.Wall] = [MaterialKind.Concrete, MaterialKind.Brick, MaterialKind.Stone, MaterialKind.Insulation, MaterialKind.Tile, MaterialKind.Wood, MaterialKind.Diagonal, MaterialKind.Gravel],
        [SurfaceHint.Floor] = [MaterialKind.Wood, MaterialKind.Tile, MaterialKind.Stone, MaterialKind.Concrete, MaterialKind.Brick, MaterialKind.Gravel, MaterialKind.Insulation, MaterialKind.Diagonal],
        [SurfaceHint.Ground] = [MaterialKind.Gravel, MaterialKind.Stone, MaterialKind.Brick, MaterialKind.Concrete, MaterialKind.Wood, MaterialKind.Tile, MaterialKind.Diagonal, MaterialKind.Insulation],
        [SurfaceHint.General] = [MaterialKind.Concrete, MaterialKind.Wood, MaterialKind.Tile, MaterialKind.Brick, MaterialKind.Stone, MaterialKind.Insulation, MaterialKind.Gravel, MaterialKind.Diagonal]
    };

    static readonly string[] groundKeys = ["LAND", "SITE", "EARTH", "GRAVEL", "SAND", "PAVE", "PAVING", "GRND", "조경", "대지", "외부", "실외", "포장", "토사", "자갈", "잔디", "마당"];
    static readonly string[] wallKeys = ["WALL", "MASN", "CONC", "COLS", "COLUMN", "CORE", "벽", "조적", "콘크리트", "기둥", "옹벽"];
    static readonly string[] floorKeys = ["FLOR", "FLOOR", "FLR", "ROOM", "SPACE", "FNSH", "FINISH", "바닥", "마루", "거실", "침실", "실내", "마감"];

    // Surface named by a drawing layer. A generic hatch layer (A-HATCH) names no surface.
    public static SurfaceHint Surface(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer)) return SurfaceHint.General;
        string name = layer.ToUpperInvariant();
        int bar = name.LastIndexOf('|'); if (bar >= 0) name = name[(bar + 1)..];
        if (Has(name, groundKeys)) return SurfaceHint.Ground;
        if (DrawingCleanup.Classify(name) == DrawingRole.Structure || Has(name, wallKeys)) return SurfaceHint.Wall;
        if (Has(name, floorKeys)) return SurfaceHint.Floor;
        return SurfaceHint.General;
    }

    // A material named outright by the layer (…-WOOD, 타일, BRICK), if any.
    public static MaterialKind? Named(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer)) return null;
        var kind = DrawingCleanup.Suggest("", layer, DrawingRole.Other);
        return kind is MaterialKind.Diagonal or MaterialKind.Solid ? null : kind;
    }

    public static MaterialKind[] Order(SurfaceHint surface, MaterialKind? first = null)
    {
        var order = orders[surface];
        return first is { } lead && Presets.Contains(lead) ? [lead, .. order.Where(k => k != lead)] : order.ToArray();
    }

    // Order for a layer name alone: its surface's order, with a material the name spells out first.
    public static MaterialKind[] Recommend(string? layer) => Order(Surface(layer), Named(layer));

    // A drawing has vector linework or imported drawing layer groups.
    public static bool IsDrawing(Document doc) => doc.Layers.Any(l => l.Kind == LayerKind.Vector || DrawingLayers.IsContainer(l));

    public static MaterialSuggestion Suggest(Document doc, Selection selection, CancellationToken token = default)
    {
        var canvas = new Rect(0, 0, doc.Width, doc.Height);
        var bounds = selection.Bounds; bounds.Intersect(canvas);
        if (!IsDrawing(doc) || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return new(SurfaceHint.General, null, Order(SurfaceHint.General));
        string? layer = MatchingLayer(doc, bounds);
        bool thin = IsThin(selection, Math.Max(doc.Width, doc.Height), token);
        var named = Surface(layer);
        // Walls usually bound a picked room: a wide region inside wall lines is a floor.
        var surface = named switch
        {
            SurfaceHint.Ground or SurfaceHint.Floor => named,
            _ => thin ? SurfaceHint.Wall : SurfaceHint.Floor
        };
        // A wall that only bounds the region names neither the surface nor its material.
        if (named == SurfaceHint.Wall && surface != SurfaceHint.Wall) layer = null;
        return new(surface, layer, Order(surface, Named(layer)));
    }

    // The shape test depends only on the selection, so a selection's outline is traced once,
    // not again after every edit of the document (a full-canvas inverted mask is costly).
    static readonly ConditionalWeakTable<Selection, Tuple<double, bool>> thinness = new();
    internal static int ContourTraces;
    static bool IsThin(Selection selection, double documentSide, CancellationToken token)
    {
        if (thinness.TryGetValue(selection, out var known) && known.Item1 == documentSide) return known.Item2;
        Interlocked.Increment(ref ContourTraces);
        bool thin = Thin(SelectionContours.Create(selection, token), documentSide);
        thinness.AddOrUpdate(selection, Tuple.Create(documentSide, thin));
        return thin;
    }

    // Band-shaped regions (wall poché, a partition strip) have a small mean width 2A/P
    // relative to their size; rooms and yards do not.
    public static bool Thin(Geometry region, double documentSide)
    {
        double area = MaterialEditing.Area(region);
        if (!double.IsFinite(area) || area < 1) return false;
        double perimeter = 0;
        foreach (var figure in region.GetFlattenedPathGeometry().Figures)
        {
            var start = figure.StartPoint; var last = start;
            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch { PolyLineSegment poly => poly.Points, LineSegment line => [line.Point], _ => [] };
                foreach (var point in points) { perimeter += (point - last).Length; last = point; }
            }
            if (figure.IsClosed) perimeter += (start - last).Length;
        }
        if (!double.IsFinite(perimeter) || perimeter <= 0) return false;
        double width = 2 * area / perimeter, ratio = width / Math.Sqrt(area);
        return ratio < .15 || ratio < .3 && width <= documentSide * .03;
    }

    // The drawing object whose bounds overlap the selection bounds the most (IoU ≥ 0.5),
    // named by its source CAD layer. Paper, fills, text and hidden objects are ignored.
    static string? MatchingLayer(Document doc, Rect bounds)
    {
        var categories = DrawingLayers.Categories(doc);
        var byId = doc.Layers.ToDictionary(l => l.Id);
        var world = new Dictionary<Guid, Matrix>(); var shown = new Dictionary<Guid, bool>();
        Matrix World(Layer layer)
        {
            if (world.TryGetValue(layer.Id, out var known)) return known;
            var matrix = layer.Matrix;
            if (layer.ParentId is { } parent && byId.TryGetValue(parent, out var group)) matrix = Matrix.Multiply(matrix, World(group));
            return world[layer.Id] = matrix;
        }
        bool Shown(Layer layer)
        {
            if (shown.TryGetValue(layer.Id, out var known)) return known;
            bool visible = layer.Visible && layer.Opacity > 0 && (layer.ParentId is not { } parent || !byId.TryGetValue(parent, out var group) || Shown(group));
            return shown[layer.Id] = visible;
        }
        string Source(Layer layer)
        {
            for (Layer? item = layer; item != null; item = item.ParentId is { } parent ? byId.GetValueOrDefault(parent) : null)
                if (item.SourceLayerName != null) return item.SourceLayerName;
            return layer.Name;
        }
        double canvasArea = (double)doc.Width * doc.Height, best = .5; string? name = null;
        foreach (var layer in doc.Layers)
        {
            if (layer.Kind is LayerKind.Group or LayerKind.Adjustment or LayerKind.Material or LayerKind.Text || layer.Warp != null) continue;
            if (categories.GetValueOrDefault(layer.Id) != LayerCategory.Drawing && layer.Kind != LayerKind.Vector) continue;
            if (!Shown(layer)) continue;
            var box = new MatrixTransform(World(layer)).TransformBounds(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
            if (box.Width * box.Height >= canvasArea * .95 && layer.Kind != LayerKind.Vector) continue;
            var overlap = Rect.Intersect(box, bounds);
            if (overlap.IsEmpty) continue;
            double shared = overlap.Width * overlap.Height, union = box.Width * box.Height + bounds.Width * bounds.Height - shared;
            double score = union <= 0 ? 0 : shared / union;
            if (score > best) { best = score; name = Source(layer); }
        }
        return name;
    }

    // Where a new material layer goes: directly below the lowest drawing linework, so it sits
    // above the paper and earlier fills. A folder that wraps a whole imported drawing (no
    // CAD source layer name) is entered; CAD layer groups and photo groups are not. With
    // several drawings side by side, the one under <paramref name="bounds"/> (the selection,
    // in document pixels) is used. Null: on top of the document.
    public static (Guid? Parent, int Index)? Placement(Document doc, Rect bounds = default)
    {
        if (!IsDrawing(doc)) return null;
        var categories = DrawingLayers.Categories(doc);
        var parents = doc.Layers.Select(l => l.ParentId).OfType<Guid>().ToHashSet();
        bool Linework(Layer l) => l.Kind is LayerKind.Vector or LayerKind.Group && categories.GetValueOrDefault(l.Id) == LayerCategory.Drawing;
        bool Folder(Layer l) => DrawingLayers.IsContainer(l) && l.SourceLayerName == null && parents.Contains(l.Id);
        Guid? parent = null;
        for (int depth = 0; depth < 8; depth++)
        {
            var siblings = doc.Layers.Where(l => l.ParentId == parent && Linework(l)).ToArray();
            if (siblings.Length == 0) return (parent, doc.Layers.Count);
            var first = siblings[0];
            if (Folder(first))
            {
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                {
                    double best = 0;
                    foreach (var folder in siblings.Where(Folder))
                    {
                        var overlap = Rect.Intersect(new MatrixTransform(World(doc, folder)).TransformBounds(new Rect(0, 0, folder.Pixels.Width, folder.Pixels.Height)), bounds);
                        double shared = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                        if (shared > best) { best = shared; first = folder; }
                    }
                }
                if (first.Locked) return null;
                parent = first.Id; continue;
            }
            return (parent, doc.Layers.IndexOf(first));
        }
        return null;
    }

    // Puts a material layer made in document pixels (X/Y, scale 1) at a placement from
    // Placement. Parent group matrices compose when drawn, so inside a moved or scaled drawing
    // folder the layer is expressed in that folder's space to stay over the selection. A folder
    // that is rotated, flipped or scaled out of range cannot hold it upright: the layer then goes
    // at the top level directly above that drawing, where multiply looks the same over white paper.
    public static (Guid? Parent, int Index) Localize(Document doc, Layer layer, (Guid? Parent, int Index) place)
    {
        if (place.Parent is not { } id || doc.Layers.FirstOrDefault(l => l.Id == id) is not { } parent) return place;
        var m = World(doc, parent);
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));
        if (Near(m.M11, 1) && Near(m.M22, 1) && Near(m.M12, 0) && Near(m.M21, 0) && Near(m.OffsetX, 0) && Near(m.OffsetY, 0)) return place;
        if (Near(m.M12, 0) && Near(m.M21, 0) && m.M11 > 0 && m.M22 > 0 && 1 / m.M11 is >= .01 and <= 20 && 1 / m.M22 is >= .01 and <= 20)
        {
            layer.X = (layer.X - m.OffsetX) / m.M11; layer.Y = (layer.Y - m.OffsetY) / m.M22;
            if (Near(m.M11, m.M22)) layer.Scale = 1 / m.M11;
            else { layer.ScaleX = 1 / m.M11; layer.ScaleY = 1 / m.M22; }
            return place;
        }
        var byId = doc.Layers.ToDictionary(l => l.Id);
        var top = parent; while (top.ParentId is { } up && byId.TryGetValue(up, out var next)) top = next;
        bool Inside(Layer l) { for (var p = l.ParentId; p is { } pid && byId.TryGetValue(pid, out var g); p = g.ParentId) if (pid == top.Id) return true; return false; }
        int end = doc.Layers.IndexOf(top);
        for (int i = end + 1; i < doc.Layers.Count; i++) if (Inside(doc.Layers[i])) end = i;
        return (null, end + 1);
    }

    // Where a layer's own pixels land: its matrix composed with every parent group's.
    static Matrix World(Document doc, Layer layer)
    {
        var matrix = layer.Matrix; var seen = new HashSet<Guid> { layer.Id };
        for (var parent = layer.ParentId; parent is { } id && seen.Add(id) && doc.Layers.FirstOrDefault(l => l.Id == id) is { } group; parent = group.ParentId)
            matrix.Append(group.Matrix);
        return matrix;
    }

    // The selection outline as a stored region: clipped to the canvas and, when a detailed
    // outline exceeds the region path budget, simplified in steps of at most a few pixels.
    public static Geometry Boundary(Selection selection, int width, int height, CancellationToken token = default)
    {
        Geometry geometry = SelectionContours.Create(selection, token);
        var canvas = new Rect(0, 0, width, height);
        if (geometry.Bounds.IsEmpty || !canvas.Contains(geometry.Bounds))
            geometry = Geometry.Combine(geometry, new RectangleGeometry(canvas), GeometryCombineMode.Intersect, null);
        if (geometry.Bounds.IsEmpty || MaterialEditing.Area(geometry) <= .000001) throw new InvalidDataException("선택 영역이 비어 있습니다. 채울 영역을 다시 선택하세요.");
        foreach (double tolerance in new[] { 0, .35, .75, 1.5, 3 })
        {
            token.ThrowIfCancellationRequested();
            var candidate = tolerance == 0 ? geometry : Simplify(geometry, tolerance);
            if (!candidate.Bounds.IsEmpty && System.Text.Encoding.UTF8.GetByteCount(RegionPath.From(candidate).Data) <= MaterialEditing.MaxPathBytes) return candidate;
        }
        throw new InvalidDataException("선택 영역의 경계가 너무 복잡합니다. 영역을 나누어 선택한 뒤 다시 적용하세요.");
    }

    static Geometry Simplify(Geometry geometry, double tolerance)
    {
        var flat = geometry.GetFlattenedPathGeometry(.1, ToleranceType.Absolute);
        var result = new StreamGeometry { FillRule = flat.FillRule };
        using (var context = result.Open())
            foreach (var figure in flat.Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (var segment in figure.Segments)
                    if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                    else if (segment is LineSegment line) points.Add(line.Point);
                var kept = Reduce(points, tolerance);
                if (kept.Count < 3) continue;
                context.BeginFigure(kept[0], true, true); context.PolyLineTo(kept.Skip(1).ToArray(), true, false);
            }
        result.Freeze(); return result;
    }

    // Douglas–Peucker on an outline; the first point stays as the anchor of the closed figure.
    static List<Point> Reduce(List<Point> points, double tolerance)
    {
        if (points.Count < 4) return points;
        var keep = new bool[points.Count]; keep[0] = keep[^1] = true;
        var spans = new Stack<(int From, int To)>(); spans.Push((0, points.Count - 1));
        while (spans.Count > 0)
        {
            var (from, to) = spans.Pop();
            if (to - from < 2) continue;
            var a = points[from]; var b = points[to]; var line = b - a; double length = line.Length;
            int farthest = -1; double distance = tolerance;
            for (int i = from + 1; i < to; i++)
            {
                var offset = points[i] - a;
                double d = length < 1e-9 ? offset.Length : Math.Abs(Vector.CrossProduct(line, offset)) / length;
                if (d > distance) { distance = d; farthest = i; }
            }
            if (farthest < 0) continue;
            keep[farthest] = true; spans.Push((from, farthest)); spans.Push((farthest, to));
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    static bool Has(string name, string[] keys)
    {
        var tokens = name.Split(['-', '_', ' ', '.', '$', '|', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var key in keys)
        {
            if (key.Any(c => c >= '가' && c <= '힣')) { if (name.Contains(key, StringComparison.Ordinal)) return true; continue; }
            if (tokens.Any(token => token.StartsWith(key, StringComparison.Ordinal) && (token.Length == key.Length || key.Length >= 4))) return true;
        }
        return false;
    }
}
