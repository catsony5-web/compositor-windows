using System.Windows;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace Compositor.Windows;

// Select editable objects, independently of the pixel selection used by photo tools.
public static class ObjectSelection
{
    static readonly ConditionalWeakTable<VectorContent, Geometry> vectorInk = new();
    public static Guid[] Find(Document document, Rect rectangle, bool crossing, CancellationToken token = default)
    {
        if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) return [];
        var area = new RectangleGeometry(rectangle);
        var index = document.Layers.ToDictionary(l => l.Id);
        var children = document.Layers.ToLookup(l => l.ParentId);
        var bases = new Dictionary<Guid, Layer?>();
        foreach (var siblings in children)
        {
            Layer? basis = null;
            foreach (var item in siblings) { if (item.Clipped) bases[item.Id] = basis; else if (item.Kind != LayerKind.Adjustment) basis = item; }
        }
        var masks = new Dictionary<byte[], Geometry>(ReferenceEqualityComparer.Instance);
        var clipCache = new Dictionary<Guid, Geometry>();
        Geometry Mask(Layer layer)
        {
            if (masks.TryGetValue(layer.Mask!, out var cached)) return cached;
            var path = new StreamGeometry();
            using (var context = path.Open())
                for (int y = 0; y < layer.Pixels.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < layer.Pixels.Width;)
                    {
                        if (layer.Mask![y * layer.Pixels.Width + x] == 0) { x++; continue; }
                        int left = x++; while (x < layer.Pixels.Width && layer.Mask[y * layer.Pixels.Width + x] != 0) x++;
                        context.BeginFigure(new Point(left, y), true, true);
                        context.PolyLineTo([new Point(x, y), new Point(x, y + 1), new Point(left, y + 1)], true, false);
                    }
                }
            path.Freeze(); return masks[layer.Mask!] = path;
        }
        Geometry Clip(Geometry ink, Geometry boundary)
        {
            if (ink.Bounds.IsEmpty || boundary.Bounds.IsEmpty || !ink.Bounds.IntersectsWith(boundary.Bounds)) return Geometry.Empty;
            if (boundary is RectangleGeometry box && (box.Transform == null || box.Transform.Value.IsIdentity) && box.Rect.Contains(ink.Bounds)) return ink;
            return Geometry.Combine(ink, boundary, GeometryCombineMode.Intersect, null, .01, ToleranceType.Absolute);
        }
        Geometry InParent(Layer layer, bool alpha = false)
        {
            token.ThrowIfCancellationRequested();
            if (!layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) return Geometry.Empty;
            Geometry ink;
            if (layer.Kind == LayerKind.Group)
            {
                var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (var child in children[layer.Id]) group.Children.Add(InParent(child, alpha));
                ink = group;
            }
            else ink = LocalInk(layer, alpha, token);
            if (!DrawingLayers.IsContainer(layer)) ink = Clip(ink, new RectangleGeometry(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height)));
            if (layer.Mask != null) ink = Clip(ink, Mask(layer));
            ink = Transform(ink, layer);
            if (layer.Clipped)
            {
                if (bases.GetValueOrDefault(layer.Id) is not { } basis) return Geometry.Empty;
                if (!clipCache.TryGetValue(basis.Id, out var clip)) clipCache[basis.Id] = clip = InParent(basis, true);
                ink = Clip(ink, clip);
            }
            return ink;
        }
        var selected = new List<Guid>();
        foreach (var layer in document.Layers)
        {
            token.ThrowIfCancellationRequested();
            if (layer.Kind is LayerKind.Group or LayerKind.Adjustment) continue;
            var chain = new List<Layer> { layer };
            var parent = layer.ParentId;
            while (parent is { } id) { var group = index[id]; chain.Add(group); parent = group.ParentId; }
            if (chain.Any(l => !l.Visible || l.Locked || l.Opacity <= 0)) continue;
            // Cheap rejection before decoding thousands of retained CAD paths.
            var corners = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) };
            foreach (var ancestor in chain) for (int i = 0; i < corners.Length; i++) corners[i] = ancestor.Document(corners[i]);
            var bounds = Rect.Empty; foreach (var point in corners) bounds.Union(point);
            if (!rectangle.IntersectsWith(bounds)) continue;
            Geometry geometry = InParent(layer);
            foreach (var group in chain.Skip(1))
            {
                if (!DrawingLayers.IsContainer(group)) geometry = Clip(geometry, new RectangleGeometry(new Rect(0, 0, group.Pixels.Width, group.Pixels.Height)));
                if (group.Mask != null) geometry = Clip(geometry, Mask(group));
                geometry = Transform(geometry, group);
                if (group.Clipped)
                {
                    if (bases.GetValueOrDefault(group.Id) is not { } basis) { geometry = Geometry.Empty; break; }
                    if (!clipCache.TryGetValue(basis.Id, out var clip)) clipCache[basis.Id] = clip = InParent(basis, true);
                    geometry = Clip(geometry, clip);
                }
            }
            var inkBounds = geometry.Bounds;
            if (inkBounds.IsEmpty) continue;
            if (rectangle.Contains(inkBounds)) { selected.Add(layer.Id); continue; }
            if (crossing && rectangle.IntersectsWith(inkBounds))
            {
                var detail = area.FillContainsWithDetail(geometry, .01, ToleranceType.Absolute);
                if (detail != IntersectionDetail.Empty && detail != IntersectionDetail.NotCalculated) selected.Add(layer.Id);
            }
        }
        return selected.ToArray();
    }

    static Geometry LocalInk(Layer layer, bool alpha, CancellationToken token)
    {
        if (layer.Vector is { Format: VectorFormat.Paths } vector) return vectorInk.GetValue(vector, source => { var ink = DrawingInk(source.Drawing); ink.Freeze(); return ink; });
        if (layer.Shape is { } shape)
        {
            Geometry Outline(double inset)
            {
                if (shape.Width <= inset * 2 || shape.Height <= inset * 2) return Geometry.Empty;
                var rect = new Rect(inset, inset, shape.Width - inset * 2, shape.Height - inset * 2);
                return shape.Kind == ShapeKind.Ellipse ? new EllipseGeometry(rect)
                    : new RectangleGeometry(rect, Math.Max(0, shape.CornerRadius - inset), Math.Max(0, shape.CornerRadius - inset));
            }
            if (shape.FillEnabled && (shape.FillArgb >> 24) != 0) return Outline(0);
            return shape.StrokeEnabled && (shape.StrokeArgb >> 24) != 0 && shape.StrokeWidth > 0
                ? Geometry.Combine(Outline(0), Outline(shape.StrokeWidth), GeometryCombineMode.Exclude, null) : Geometry.Empty;
        }
        if (!alpha) return new RectangleGeometry(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
        var path = new StreamGeometry();
        using (var context = path.Open())
            for (int y = 0; y < layer.Pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < layer.Pixels.Width;)
                {
                    if (layer.Pixels.Data[(y * layer.Pixels.Width + x) * 4 + 3] == 0) { x++; continue; }
                    int left = x++; while (x < layer.Pixels.Width && layer.Pixels.Data[(y * layer.Pixels.Width + x) * 4 + 3] > 0) x++;
                    context.BeginFigure(new Point(left, y), true, true);
                    context.PolyLineTo([new Point(x, y), new Point(x, y + 1), new Point(left, y + 1)], true, false);
                }
            }
        return path;
    }
    static Geometry DrawingInk(Drawing drawing)
    {
        if (drawing is GeometryDrawing item)
        {
            var result = new GeometryGroup { FillRule = FillRule.Nonzero };
            if (item.Brush is { Opacity: > 0 } brush && brush is not SolidColorBrush { Color.A: 0 }) result.Children.Add(item.Geometry);
            if (item.Pen is { Thickness: > 0 } pen && pen.Brush is { Opacity: > 0 } && pen.Brush is not SolidColorBrush { Color.A: 0 }) result.Children.Add(item.Geometry.GetWidenedPathGeometry(pen, .01, ToleranceType.Absolute));
            return result;
        }
        if (drawing is DrawingGroup group && group.Opacity > 0)
        {
            Geometry result = new GeometryGroup { FillRule = FillRule.Nonzero, Children = new GeometryCollection(group.Children.Select(DrawingInk)) };
            if (group.ClipGeometry != null) result = Geometry.Combine(result, group.ClipGeometry, GeometryCombineMode.Intersect, null);
            var transformed = result.CloneCurrentValue(); transformed.Transform = group.Transform; return transformed;
        }
        return Geometry.Empty;
    }
    static Geometry Transform(Geometry source, Layer layer)
    {
        if (source.Bounds.IsEmpty || layer.Warp == null && layer.Matrix.IsIdentity) return source;
        if (layer.Warp == null)
        {
            var copy = source.CloneCurrentValue(); var matrix = copy.Transform?.Value ?? Matrix.Identity; matrix.Append(layer.Matrix); copy.Transform = new MatrixTransform(matrix); return copy;
        }
        var flat = source.GetFlattenedPathGeometry(.02, ToleranceType.Absolute); var path = new StreamGeometry { FillRule = flat.FillRule };
        Point Map(Point p) => layer.Document(flat.Transform?.Transform(p) ?? p);
        using (var context = path.Open()) foreach (var figure in flat.Figures)
        {
            context.BeginFigure(Map(figure.StartPoint), figure.IsFilled, figure.IsClosed);
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) context.PolyLineTo(poly.Points.Select(Map).ToArray(), poly.IsStroked, false);
                else if (segment is LineSegment line) context.LineTo(Map(line.Point), line.IsStroked, false);
        }
        return path;
    }
}
