using System.Windows;

namespace Compositor.Windows;

public sealed record MagneticGuide(bool Vertical, double Position, double Start, double End);
public sealed record MagneticSnapResult(Vector Delta, IReadOnlyList<MagneticGuide> Guides);

/// <summary>Geometry captured at drag start; Resolve never renders or inspects raster pixels.</summary>
public sealed class MagneticSnapSession
{
    const double AcquireDistance = 6;
    const double ReleaseDistance = 10;
    readonly Rect movingBounds;
    readonly AxisTarget[] horizontalTargets;
    readonly AxisTarget[] verticalTargets;
    AxisLatch? horizontalLatch;
    AxisLatch? verticalLatch;

    readonly record struct AxisTarget(double Position, double Start, double End);
    readonly record struct AxisLatch(double Source, AxisTarget Target);

    public MagneticSnapSession(Document document, IReadOnlyCollection<Guid> movingRootIds,
        IReadOnlyList<(bool Vertical, double Position)> guides)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(movingRootIds);
        ArgumentNullException.ThrowIfNull(guides);
        var lookup = document.Layers.ToDictionary(layer => layer.Id);
        var movingIds = movingRootIds.ToHashSet();
        var ancestors = new HashSet<Guid>();
        movingBounds = Rect.Empty;
        foreach (var layer in document.Layers)
        {
            if (!movingIds.Contains(layer.Id)) continue;
            var parent = layer.ParentId;
            for (int depth = 0; parent is { } id && depth < 16; depth++)
            {
                if (!lookup.TryGetValue(id, out var group)) break;
                ancestors.Add(id); parent = group.ParentId;
            }
            if (layer.Kind != LayerKind.Adjustment && IsVisible(layer, lookup) &&
                !HasMovingAncestor(layer, lookup, movingIds))
            {
                var bounds = Bounds(document, layer);
                if (!bounds.IsEmpty) movingBounds.Union(bounds);
            }
        }

        var horizontal = new List<AxisTarget>();
        var vertical = new List<AxisTarget>();
        // Explicit guides win exact ties, then the canvas, then document stacking order.
        foreach (var guide in guides)
        {
            if (!double.IsFinite(guide.Position)) continue;
            if (guide.Vertical) horizontal.Add(new(guide.Position, 0, document.Height));
            else vertical.Add(new(guide.Position, 0, document.Width));
        }
        AddTargets(new Rect(0, 0, document.Width, document.Height), horizontal, vertical);
        foreach (var layer in document.Layers)
        {
            if (layer.Kind == LayerKind.Adjustment || movingIds.Contains(layer.Id) || ancestors.Contains(layer.Id) ||
                HasMovingAncestor(layer, lookup, movingIds) || !IsVisible(layer, lookup)) continue;
            var bounds = Bounds(document, layer);
            if (!bounds.IsEmpty) AddTargets(bounds, horizontal, vertical);
        }
        horizontalTargets = horizontal.ToArray();
        verticalTargets = vertical.ToArray();
    }

    /// <param name="constrainX">Allow X motion only; preserve the caller's Y component.</param>
    /// <param name="constrainY">Allow Y motion only; preserve the caller's X component.</param>
    public MagneticSnapResult Resolve(Vector rawDelta, double zoom, bool enabled = true,
        bool constrainX = false, bool constrainY = false)
    {
        if (!enabled || movingBounds.IsEmpty || !double.IsFinite(zoom) || zoom <= 0 ||
            !double.IsFinite(rawDelta.X) || !double.IsFinite(rawDelta.Y))
        {
            horizontalLatch = verticalLatch = null;
            return new(rawDelta, Array.Empty<MagneticGuide>());
        }

        double x = rawDelta.X, y = rawDelta.Y;
        if (constrainY) horizontalLatch = null;
        else x = ResolveAxis(x, zoom, movingBounds.Left, movingBounds.Width, horizontalTargets, ref horizontalLatch);
        if (constrainX) verticalLatch = null;
        else y = ResolveAxis(y, zoom, movingBounds.Top, movingBounds.Height, verticalTargets, ref verticalLatch);

        var lines = new List<MagneticGuide>(2);
        if (horizontalLatch is { } horizontal)
            lines.Add(new(true, horizontal.Target.Position, Math.Min(movingBounds.Top + y, horizontal.Target.Start),
                Math.Max(movingBounds.Bottom + y, horizontal.Target.End)));
        if (verticalLatch is { } vertical)
            lines.Add(new(false, vertical.Target.Position, Math.Min(movingBounds.Left + x, vertical.Target.Start),
                Math.Max(movingBounds.Right + x, vertical.Target.End)));
        return new(new Vector(x, y), lines);
    }

    static double ResolveAxis(double rawDelta, double zoom, double origin, double size,
        AxisTarget[] targets, ref AxisLatch? latch)
    {
        if (latch is { } current && Math.Abs(current.Target.Position - current.Source - rawDelta) * zoom <= ReleaseDistance)
            return current.Target.Position - current.Source;

        latch = null;
        double bestDistance = AcquireDistance;
        foreach (var target in targets)
        {
            for (int anchor = 0; anchor < 3; anchor++)
            {
                double source = origin + size * anchor / 2;
                double distance = Math.Abs(target.Position - source - rawDelta) * zoom;
                if (distance > AcquireDistance || latch != null && distance >= bestDistance) continue;
                bestDistance = distance;
                latch = new(source, target);
            }
        }
        return latch is { } acquired ? acquired.Target.Position - acquired.Source : rawDelta;
    }

    static void AddTargets(Rect bounds, List<AxisTarget> horizontal, List<AxisTarget> vertical)
    {
        for (int anchor = 0; anchor < 3; anchor++)
        {
            horizontal.Add(new(bounds.Left + bounds.Width * anchor / 2, bounds.Top, bounds.Bottom));
            vertical.Add(new(bounds.Top + bounds.Height * anchor / 2, bounds.Left, bounds.Right));
        }
    }

    static bool IsVisible(Layer layer, IReadOnlyDictionary<Guid, Layer> lookup)
    {
        for (int depth = 0; depth <= 16; depth++)
        {
            if (!layer.Visible || layer.Opacity <= 0) return false;
            if (layer.ParentId is not { } parent) return true;
            if (!lookup.TryGetValue(parent, out var group)) return false;
            layer = group;
        }
        return false;
    }

    static bool HasMovingAncestor(Layer layer, IReadOnlyDictionary<Guid, Layer> lookup, HashSet<Guid> movingIds)
    {
        var parent = layer.ParentId;
        for (int depth = 0; parent is { } id && depth < 16; depth++)
        {
            if (movingIds.Contains(id)) return true;
            if (!lookup.TryGetValue(id, out var group)) break;
            parent = group.ParentId;
        }
        return false;
    }

    static Rect Bounds(Document document, Layer layer)
    {
        var bounds = Rect.Empty;
        foreach (var local in new[] { new Point(), new Point(layer.Pixels.Width, 0),
            new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) })
        {
            var point = DocumentFeatures.ToDocumentSpace(document, layer, local);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return Rect.Empty;
            bounds.Union(point);
        }
        return bounds;
    }
}
