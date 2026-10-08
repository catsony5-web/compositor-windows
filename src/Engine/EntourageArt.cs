using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// One built-in entourage drawing in authoring units (centimetres, y down): the silhouette (the
/// union of the body parts, filled for white, grey and solid styles and cast by shadows) and the
/// line work drawn over it. Elevation items stand on y = 0 with x = 0 at their ground point; plan
/// items are centred on the origin. OutlineWeight scales the silhouette's own outline (0 = none).
/// </summary>
internal sealed record EntourageArt(Geometry Silhouette, IReadOnlyList<EntourageStroke> Lines, Rect Bounds, double OutlineWeight)
{
    // A plan symbol is placed by its centre: move the drawing so its bounds are centred on the origin.
    public EntourageArt Centered()
    {
        var shift = new TranslateTransform(-(Bounds.X + Bounds.Width / 2), -(Bounds.Y + Bounds.Height / 2)); shift.Freeze();
        Geometry Move(Geometry g) { var moved = EntourageShapes.Transformed(g, shift); moved.Freeze(); return moved; }
        var bounds = Bounds; bounds.Offset(shift.X, shift.Y);
        return new(Move(Silhouette), Lines.Select(l => new EntourageStroke(Move(l.Geometry), l.Weight, l.Clip is { } clip ? Move(clip) : null)).ToArray(), bounds, OutlineWeight);
    }
}

/// <summary>A line of the drawing: weight is relative to the item's pen; Clip limits it (e.g. to the body it crosses).</summary>
internal sealed record EntourageStroke(Geometry Geometry, double Weight, Geometry? Clip = null);

// Collects body parts and lines while an item is drawn, then unions the parts once.
internal sealed class EntourageSketch
{
    readonly List<Geometry> body = [];
    readonly List<EntourageStroke> lines = [];
    public double OutlineWeight { get; set; } = 1;
    // Elevation items stand on y = 0: nothing of their body reaches below the ground line.
    public bool Ground { get; init; }

    public void Body(params Geometry[] parts) => body.AddRange(parts);
    public void Line(Geometry geometry, double weight = .55, Geometry? clip = null) => lines.Add(new(geometry, weight, clip));
    // A limb or part drawn over the rest of the body: its outline shows where it crosses the other
    // parts, and the lines drawn so far are hidden where it covers them.
    public Geometry Over(Geometry part, double weight = .6)
    {
        var rest = EntourageShapes.Union(body);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Geometry.Bounds.IntersectsWith(part.Bounds))
                lines[i] = lines[i] with { Clip = EntourageShapes.Subtract(lines[i].Clip ?? EntourageShapes.Everything, part) };
        body.Add(part);
        if (!rest.Bounds.IsEmpty) lines.Add(new(part, weight, rest));
        return part;
    }

    public EntourageArt Finish()
    {
        var silhouette = EntourageShapes.Union(body);
        if (Ground) silhouette = EntourageShapes.Intersect(silhouette, new RectangleGeometry(new Rect(-10_000, -10_000, 20_000, 10_000)));
        silhouette.Freeze();
        var bounds = silhouette.Bounds;
        foreach (var line in lines) { line.Geometry.Freeze(); line.Clip?.Freeze(); var b = line.Geometry.Bounds; if (!b.IsEmpty) bounds.Union(b); }
        return new(silhouette, lines.ToArray(), bounds, OutlineWeight);
    }
}

internal static class EntourageShapes
{
    public const double Tolerance = .01;
    // Larger than any item (centimetres): the starting clip of a line that something later covers.
    public static Geometry Everything => new RectangleGeometry(new Rect(-10_000, -10_000, 20_000, 20_000));

    public static Point P(double x, double y) => new(x, y);
    public static Geometry Circle(double x, double y, double r) => new EllipseGeometry(new Point(x, y), r, r);
    public static Geometry Ellipse(double x, double y, double rx, double ry, double angle = 0)
    {
        var e = new EllipseGeometry(new Point(x, y), rx, ry);
        if (angle != 0) e.Transform = new RotateTransform(angle, x, y);
        return e;
    }

    // Two circles joined by their outer tangents: a tapered limb segment, closed counter-clockwise.
    public static Geometry Capsule(Point a, double ra, Point b, double rb)
    {
        var d = b - a; double length = d.Length;
        if (length <= Math.Abs(ra - rb) + 1e-6) return ra >= rb ? Circle(a.X, a.Y, ra) : Circle(b.X, b.Y, rb);
        double theta = Math.Atan2(d.Y, d.X), gamma = Math.Acos((ra - rb) / length);
        Point On(Point c, double r, double angle) => new(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle));
        var a1 = On(a, ra, theta + gamma); var b1 = On(b, rb, theta + gamma);
        var a2 = On(a, ra, theta - gamma); var b2 = On(b, rb, theta - gamma);
        var figure = new PathFigure { StartPoint = a1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(b1, true));
        figure.Segments.Add(new ArcSegment(b2, new Size(rb, rb), 0, 2 * gamma > Math.PI, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(a2, true));
        figure.Segments.Add(new ArcSegment(a1, new Size(ra, ra), 0, 2 * Math.PI - 2 * gamma > Math.PI, SweepDirection.Counterclockwise, true));
        return new PathGeometry([figure]);
    }

    // A chain of joints (position, radius) as one rounded limb.
    public static Geometry Limb(params (Point P, double R)[] joints)
    {
        var parts = new List<Geometry>();
        for (int i = 1; i < joints.Length; i++) parts.Add(Capsule(joints[i - 1].P, joints[i - 1].R, joints[i].P, joints[i].R));
        return parts.Count == 1 ? parts[0] : Union(parts);
    }

    public static Geometry Poly(bool closed, params Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = closed };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        return new PathGeometry([figure]);
    }

    public static Geometry Segment(Point a, Point b) => Poly(false, a, b);

    // A smooth curve through the points (centripetal Catmull-Rom as cubic Béziers), closed or open.
    public static Geometry Spline(bool closed, params Point[] points)
    {
        if (points.Length < 3) return Poly(closed, points);
        int n = points.Length;
        Point At(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = closed };
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            Point p0 = At(i - 1), p1 = At(i), p2 = At(i + 1), p3 = At(i + 2);
            if (!closed && i == 0) p0 = p1 - (p2 - p1);
            if (!closed && i == segments - 1) p3 = p2 + (p2 - p1);
            double d1 = Math.Pow(Math.Max(1e-9, (p1 - p0).Length), .5), d2 = Math.Pow(Math.Max(1e-9, (p2 - p1).Length), .5), d3 = Math.Pow(Math.Max(1e-9, (p3 - p2).Length), .5);
            // Tangents of the centripetal parameterization, scaled to the Bézier handles of this span.
            var m1 = ((p1 - p0) / d1 - (p2 - p0) / (d1 + d2) + (p2 - p1) / d2) * d2;
            var m2 = ((p2 - p1) / d2 - (p3 - p1) / (d2 + d3) + (p3 - p2) / d3) * d2;
            figure.Segments.Add(new BezierSegment(p1 + m1 / 3, p2 - m2 / 3, p2, true));
        }
        return new PathGeometry([figure]);
    }

    public static Geometry Arc(Point center, double r, double fromDegrees, double toDegrees)
    {
        double a = fromDegrees * Math.PI / 180, b = toDegrees * Math.PI / 180;
        var start = new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
        var end = new Point(center.X + r * Math.Cos(b), center.Y + r * Math.Sin(b));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, Math.Abs(b - a) > Math.PI, b > a ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
        return new PathGeometry([figure]);
    }

    public static Geometry Ring(double x, double y, double outer, double inner) =>
        Geometry.Combine(Circle(x, y, outer), Circle(x, y, inner), GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);

    public static Geometry Group(IEnumerable<Geometry> parts)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var part in parts) group.Children.Add(part);
        return group;
    }

    // Balanced pairwise union keeps each boolean step small.
    public static Geometry Union(IEnumerable<Geometry> parts)
    {
        var list = parts.Where(p => !p.Bounds.IsEmpty).ToList();
        if (list.Count == 0) return Geometry.Empty;
        while (list.Count > 1)
        {
            var next = new List<Geometry>((list.Count + 1) / 2);
            for (int i = 0; i < list.Count; i += 2)
                next.Add(i + 1 < list.Count ? Geometry.Combine(list[i], list[i + 1], GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute) : list[i]);
            list = next;
        }
        return list[0] is PathGeometry ? list[0] : Geometry.Combine(list[0], Geometry.Empty, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
    }

    public static Geometry Subtract(Geometry a, Geometry b) => Geometry.Combine(a, b, GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
    public static Geometry Intersect(Geometry a, Geometry b) => Geometry.Combine(a, b, GeometryCombineMode.Intersect, null, Tolerance, ToleranceType.Absolute);

    public static Geometry Transformed(Geometry geometry, Transform transform)
    {
        var copy = geometry.CloneCurrentValue();
        copy.Transform = copy.Transform == null || copy.Transform.Value.IsIdentity ? transform : new MatrixTransform(copy.Transform.Value * transform.Value);
        return copy;
    }

    public static Point Rotate(Point p, Point center, double degrees)
    {
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var d = p - center;
        return new Point(center.X + d.X * c - d.Y * s, center.Y + d.X * s + d.Y * c);
    }

    // A point at `length` from `from`, `degrees` from straight down (positive turns toward +x).
    public static Point Hang(Point from, double length, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(from.X + length * Math.Sin(a), from.Y + length * Math.Cos(a));
    }
}

/// <summary>Deterministic random numbers (SplitMix64) for the procedural items and the scatter placement.</summary>
internal struct EntourageRandom
{
    ulong state;
    public EntourageRandom(ulong seed) => state = seed;
    public EntourageRandom(string key, int variant) : this(Hash(key) ^ (ulong)(variant + 1) * 0x9E3779B97F4A7C15UL) { }

    public ulong Next()
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double Unit() => (Next() >> 11) * (1.0 / (1UL << 53));
    public double Range(double low, double high) => low + (high - low) * Unit();
    public int Below(int count) => count <= 1 ? 0 : (int)(Unit() * count) % count;
    public bool Chance(double p) => Unit() < p;

    // FNV-1a: string.GetHashCode is randomized per process, so it cannot seed a stable drawing.
    public static ulong Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }
}
