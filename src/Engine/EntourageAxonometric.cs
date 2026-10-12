using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Compositor.Windows;

/// <summary>
/// A parallel projection of world centimetres (x and y on the ground, z up) onto the drawing (y down):
/// X, Y and Z are where one unit along each world axis lands. All three keep vertical lines vertical
/// and look from the side of +x and +y, so a mirror image of a drawing (x ↔ y) is the same projection.
/// </summary>
internal readonly record struct AxoProjection(Vector X, Vector Y, Vector Z)
{
    public static AxoProjection Of(EntourageProjection projection)
    {
        switch (projection)
        {
            case EntourageProjection.Dimetric:
                // Orthographic from 30° above at 45° in plan, scaled so the plan axes are true length.
                double c = 2 / Math.Sqrt(5), s = 1 / Math.Sqrt(5);
                return new(new Vector(c, s), new Vector(-c, s), new Vector(0, -Math.Sqrt(1.2)));
            case EntourageProjection.Military:
                double h = Math.Sqrt(.5);
                return new(new Vector(h, h), new Vector(-h, h), new Vector(0, -1));
            default:
                return new(new Vector(Math.Sqrt(3) / 2, .5), new Vector(-Math.Sqrt(3) / 2, .5), new Vector(0, -1));
        }
    }

    public Point Project(Point3D p) => new(X.X * p.X + Y.X * p.Y + Z.X * p.Z, X.Y * p.X + Y.Y * p.Y + Z.Y * p.Z);
    public Vector Project(Vector3D v) => new(X.X * v.X + Y.X * v.Y + Z.X * v.Z, X.Y * v.X + Y.Y * v.Y + Z.Y * v.Z);

    /// <summary>Unit direction toward the viewer: what the projection flattens, pointing up.</summary>
    public Vector3D Toward
    {
        get
        {
            var d = Vector3D.CrossProduct(new Vector3D(X.X, Y.X, Z.X), new Vector3D(X.Y, Y.Y, Z.Y));
            if (d.Z < 0) d = -d;
            d.Normalize();
            return d;
        }
    }

    /// <summary>Larger is nearer to the viewer.</summary>
    public double Depth(Point3D p) => Vector3D.DotProduct((Vector3D)p, Toward);

    /// <summary>The ground point (z = 0, world units) that shows at a drawing offset from the ground origin.</summary>
    public Vector Ground(Vector screen)
    {
        double det = X.X * Y.Y - Y.X * X.Y;
        return new Vector((screen.X * Y.Y - Y.X * screen.Y) / det, (X.X * screen.Y - screen.X * X.Y) / det);
    }

    /// <summary>How a unit sphere shows: the 2×2 shape P·Pᵀ (a, b; b, d).</summary>
    public (double A, double B, double D) SphereShape => (X.X * X.X + Y.X * Y.X + Z.X * Z.X, X.X * X.Y + Y.X * Y.Y + Z.X * Z.Y, X.Y * X.Y + Y.Y * Y.Y + Z.Y * Z.Y);

    // The symmetric square root of the sphere shape maps circles of "sphere space" to projected spheres;
    // its inverse maps projected spheres to circles.
    public Matrix Back
    {
        get
        {
            var (a, b, d) = SphereShape; double root = Math.Sqrt(a * d - b * b), t = Math.Sqrt(a + d + 2 * root);
            return new Matrix((a + root) / t, b / t, b / t, (d + root) / t, 0, 0);
        }
    }
    public Matrix Round { get { var m = Back; m.Invert(); return m; } }

    /// <summary>Screen length of one unit along the vertical and along the first plan axis.</summary>
    public double VerticalScale => Z.Length;
    public double PlanScale => X.Length;
}

/// <summary>
/// Draws an item in a parallel projection: parts are modelled in centimetres around the item's ground
/// point (local x forward, y to the left, z up) and projected into an EntourageSketch. Round parts
/// (balls, rods, limbs) are exact projections of spheres and swept spheres; solids (boxes, prisms,
/// cylinders, cones) are convex, so their outline is the hull of their corners and only the edges
/// between two faces turned to the viewer are drawn. Parts are placed back to front, so each hides
/// the lines behind it as in the elevation set.
/// </summary>
internal sealed class AxoSketch
{
    readonly List<(Point3D Origin, double Cos, double Sin)> frames = [(new Point3D(), 1, 0)];
    readonly Matrix back, round;
    public AxoSketch(EntourageSketch sketch, AxoProjection projection)
    {
        S = sketch; P = projection; back = projection.Back; round = projection.Round; Toward = projection.Toward;
    }

    public EntourageSketch S { get; }
    public AxoProjection P { get; }
    public Vector3D Toward { get; }

    // ---- Local frames ---------------------------------------------------------------------

    /// <summary>Models the next parts around `origin` (current frame) turned by `heading` degrees about the vertical.</summary>
    public void Push(Point3D origin, double heading)
    {
        var o = World(origin); var f = frames[^1]; double a = heading * Math.PI / 180;
        frames.Add((o, f.Cos * Math.Cos(a) - f.Sin * Math.Sin(a), f.Sin * Math.Cos(a) + f.Cos * Math.Sin(a)));
    }
    public void Pop() { if (frames.Count > 1) frames.RemoveAt(frames.Count - 1); }

    public Point3D World(Point3D p) { var f = frames[^1]; return new Point3D(f.Origin.X + p.X * f.Cos - p.Y * f.Sin, f.Origin.Y + p.X * f.Sin + p.Y * f.Cos, f.Origin.Z + p.Z); }
    public Vector3D World(Vector3D v) { var f = frames[^1]; return new Vector3D(v.X * f.Cos - v.Y * f.Sin, v.X * f.Sin + v.Y * f.Cos, v.Z); }
    public Point At(Point3D local) => P.Project(World(local));
    public Point At(double x, double y, double z) => At(new Point3D(x, y, z));
    public double Depth(Point3D local) => P.Depth(World(local));
    public bool Faces(Vector3D localNormal) => Vector3D.DotProduct(World(localNormal), Toward) > 1e-9;

    static Point3D Q(double x, double y, double z) => new(x, y, z);

    // ---- Round parts ------------------------------------------------------------------------

    Point Rounded(Point3D local) => round.Transform(At(local));
    Geometry Unround(Geometry g) { var t = new MatrixTransform(back); t.Freeze(); return EntourageShapes.Transformed(g, t); }

    public Geometry Ball(Point3D c, double r) { var q = Rounded(c); return Unround(EntourageShapes.Circle(q.X, q.Y, r)); }
    /// <summary>Part of a ball's outline, in degrees on the sheet (0° right, 90° down).</summary>
    public Geometry SphereArc(Point3D c, double r, double from, double to) => Unround(EntourageShapes.Arc(Rounded(c), r, from, to));
    public Geometry Rod(Point3D a, double ra, Point3D b, double rb) => Unround(EntourageShapes.Capsule(Rounded(a), ra, Rounded(b), rb));
    public Geometry Limb(params (Point3D P, double R)[] joints) => Unround(EntourageShapes.Limb(joints.Select(j => (Rounded(j.P), j.R)).ToArray()));

    /// <summary>An ellipsoid with semi-axes along the local frame.</summary>
    public Geometry Ellipsoid(Point3D c, double rx, double ry, double rz) => Ellipsoid(c, new Vector3D(rx, 0, 0), new Vector3D(0, ry, 0), new Vector3D(0, 0, rz));

    /// <summary>An ellipsoid with three perpendicular semi-axes (local vectors).</summary>
    public Geometry Ellipsoid(Point3D c, Vector3D first, Vector3D second, Vector3D third)
    {
        var a = P.Project(World(first)); var b = P.Project(World(second)); var z = P.Project(World(third));
        double m11 = a.X * a.X + b.X * b.X + z.X * z.X, m12 = a.X * a.Y + b.X * b.Y + z.X * z.Y, m22 = a.Y * a.Y + b.Y * b.Y + z.Y * z.Y;
        return Ellipse(At(c), m11, m12, m22);
    }

    /// <summary>The middle joint of a two-bone limb from root to target, bending toward `bend`.</summary>
    public static Point3D Reach(Point3D root, Point3D target, double first, double second, Vector3D bend)
    {
        var d = target - root; double length = Math.Clamp(d.Length, Math.Abs(first - second) + .01, first + second - .01);
        var direction = d; if (direction.LengthSquared < 1e-12) direction = new Vector3D(0, 0, -1); direction.Normalize();
        double along = (first * first - second * second + length * length) / (2 * length), h = Math.Sqrt(Math.Max(0, first * first - along * along));
        var normal = bend - direction * Vector3D.DotProduct(bend, direction);
        if (normal.LengthSquared < 1e-12) normal = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
        normal.Normalize();
        return root + direction * along + normal * h;
    }

    // The ellipse {x : xᵀ M⁻¹ x ≤ 1} around a centre.
    static Geometry Ellipse(Point center, double m11, double m12, double m22)
    {
        double mean = (m11 + m22) / 2, diff = (m11 - m22) / 2, root = Math.Sqrt(diff * diff + m12 * m12);
        double major = Math.Sqrt(Math.Max(1e-12, mean + root)), minor = Math.Sqrt(Math.Max(1e-12, mean - root));
        double angle = .5 * Math.Atan2(2 * m12, m11 - m22) * 180 / Math.PI;
        return EntourageShapes.Ellipse(center.X, center.Y, major, minor, angle);
    }

    // ---- Flat circles -----------------------------------------------------------------------

    static (Vector3D U, Vector3D V) Basis(Vector3D normal)
    {
        normal.Normalize();
        var helper = Math.Abs(normal.Z) < .9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
        var u = Vector3D.CrossProduct(helper, normal); u.Normalize();
        var v = Vector3D.CrossProduct(normal, u);
        return (u, v);
    }

    // Maps the unit circle of (u, v) to the projected circle.
    Transform CircleMap(Point3D c, Vector3D localNormal, double r, out Vector3D u, out Vector3D v)
    {
        (u, v) = Basis(World(localNormal));
        var pu = P.Project(u) * r; var pv = P.Project(v) * r; var o = At(c);
        var t = new MatrixTransform(pu.X, pu.Y, pv.X, pv.Y, o.X, o.Y); t.Freeze();
        return t;
    }

    /// <summary>A circle (filled disc) of radius r around c, in the plane facing `normal`.</summary>
    public Geometry Disc(Point3D c, Vector3D normal, double r) => EntourageShapes.Transformed(EntourageShapes.Circle(0, 0, 1), CircleMap(c, normal, r, out _, out _));
    public Geometry Ring(Point3D c, Vector3D normal, double outer, double inner) =>
        EntourageShapes.Transformed(EntourageShapes.Ring(0, 0, 1, inner / outer), CircleMap(c, normal, outer, out _, out _));

    /// <summary>The half of a circle nearer to the viewer (the visible half of a rim seen across a solid), or a part of it.</summary>
    public Geometry NearArc(Point3D c, Vector3D normal, double r, double fraction = 1, double offsetDegrees = 0)
    {
        var map = CircleMap(c, normal, r, out var u, out var v);
        double near = Math.Atan2(Vector3D.DotProduct(v, Toward), Vector3D.DotProduct(u, Toward)) * 180 / Math.PI + offsetDegrees, half = 90 * Math.Clamp(fraction, .05, 2);
        return EntourageShapes.Transformed(EntourageShapes.Arc(new Point(0, 0), 1, near - half, near + half), map);
    }

    /// <summary>A part of a circle from `from` to `to` degrees measured from the side nearest the viewer.</summary>
    public Geometry CircleArc(Point3D c, Vector3D normal, double r, double from, double to)
    {
        var map = CircleMap(c, normal, r, out var u, out var v);
        double near = Math.Atan2(Vector3D.DotProduct(v, Toward), Vector3D.DotProduct(u, Toward)) * 180 / Math.PI;
        return EntourageShapes.Transformed(EntourageShapes.Arc(new Point(0, 0), 1, near + from, near + to), map);
    }

    // ---- Lines ---------------------------------------------------------------------------

    public Geometry Segment(Point3D a, Point3D b) => EntourageShapes.Segment(At(a), At(b));
    public Geometry Polyline(bool closed, params Point3D[] points) => EntourageShapes.Poly(closed, points.Select(At).ToArray());
    public Geometry Curve(bool closed, params Point3D[] points) => EntourageShapes.Spline(closed, points.Select(At).ToArray());

    // ---- Solids -----------------------------------------------------------------------------

    /// <summary>A convex solid: its outline (filled) and the visible edges inside it.</summary>
    public sealed record Solid(Geometry Outline, Geometry Edges);

    /// <summary>
    /// A convex solid between two rings of corners (same count, matching order): a box, prism,
    /// frustum or, with a tiny top, a pyramid or cone. Smooth sides (a cylinder or cone) show no
    /// edges between side faces; their rims show where they meet a visible cap.
    /// </summary>
    public Solid Loft(Point3D[] bottom, Point3D[] top, bool smooth = false)
    {
        int n = bottom.Length;
        var world = bottom.Concat(top).Select(World).ToArray();
        var centroid = new Point3D(world.Average(p => p.X), world.Average(p => p.Y), world.Average(p => p.Z));
        var faces = new List<int[]> { Enumerable.Range(0, n).ToArray(), Enumerable.Range(n, n).ToArray() };
        for (int i = 0; i < n; i++) faces.Add([i, (i + 1) % n, n + (i + 1) % n, n + i]);
        bool[] visible = new bool[faces.Count];
        for (int f = 0; f < faces.Count; f++)
        {
            // Newell's normal, turned outward.
            var normal = new Vector3D(); var center = new Vector3D();
            var face = faces[f];
            for (int i = 0; i < face.Length; i++)
            {
                var a = world[face[i]]; var b = world[face[(i + 1) % face.Length]];
                normal.X += (a.Y - b.Y) * (a.Z + b.Z); normal.Y += (a.Z - b.Z) * (a.X + b.X); normal.Z += (a.X - b.X) * (a.Y + b.Y);
                center += (Vector3D)a;
            }
            center /= face.Length;
            if (normal.LengthSquared < 1e-12) { visible[f] = false; continue; }
            if (Vector3D.DotProduct(normal, (Point3D)center - centroid) < 0) normal = -normal;
            normal.Normalize();
            visible[f] = Vector3D.DotProduct(normal, Toward) > 1e-6;
        }
        var points = world.Select(P.Project).ToArray();
        var outline = EntourageShapes.Poly(true, Hull(points));
        // Edges shared by two visible faces.
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Count; f++)
        {
            var face = faces[f];
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i], b = face[(i + 1) % face.Length];
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = [];
                list.Add(f);
            }
        }
        var edges = new List<Geometry>();
        foreach (var ((a, b), list) in edgeFaces)
        {
            if (list.Count != 2 || !visible[list[0]] || !visible[list[1]]) continue;
            bool sideSide = list[0] >= 2 && list[1] >= 2;
            if (smooth && sideSide) continue;
            if ((points[a] - points[b]).LengthSquared < 1e-8) continue;
            edges.Add(EntourageShapes.Segment(points[a], points[b]));
        }
        return new Solid(outline, edges.Count == 0 ? Geometry.Empty : EntourageShapes.Group(edges));
    }

    public Solid Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        Loft([Q(x0, y0, z0), Q(x1, y0, z0), Q(x1, y1, z0), Q(x0, y1, z0)], [Q(x0, y0, z1), Q(x1, y0, z1), Q(x1, y1, z1), Q(x0, y1, z1)]);

    /// <summary>A cylinder or cone frustum from `a` to `b` with radii ra and rb (rb near 0 makes a cone).</summary>
    public Solid Cylinder(Point3D a, double ra, Point3D b, double rb, int sides = 40, bool smooth = true)
    {
        var axis = b - a; var (u, v) = Basis(axis);
        Point3D[] Ring(Point3D c, double r) => Enumerable.Range(0, sides).Select(i =>
        {
            double t = i * Math.PI * 2 / sides; return c + u * (Math.Cos(t) * r) + v * (Math.Sin(t) * r);
        }).ToArray();
        return Loft(Ring(a, ra), Ring(b, Math.Max(rb, .01)), smooth);
    }

    /// <summary>An upright prism of a plan polygon (local x, y), optionally tapering to a scaled top.</summary>
    public Solid Prism(Point[] plan, double z0, double z1, double topScale = 1, bool smooth = false)
    {
        double cx = plan.Average(p => p.X), cy = plan.Average(p => p.Y);
        return Loft(plan.Select(p => Q(p.X, p.Y, z0)).ToArray(), plan.Select(p => Q(cx + (p.X - cx) * topScale, cy + (p.Y - cy) * topScale, z1)).ToArray(), smooth);
    }

    // ---- Placing parts --------------------------------------------------------------------

    /// <summary>A solid over what is drawn so far: its outline where it crosses the rest, and its visible edges.</summary>
    public void Over(Solid solid, double outline = .55, double edges = .45)
    {
        S.Over(solid.Outline, outline);
        if (!solid.Edges.Bounds.IsEmpty) S.Line(solid.Edges, edges);
    }
    public void Body(Solid solid, double edges = .45)
    {
        S.Body(solid.Outline);
        if (!solid.Edges.Bounds.IsEmpty) S.Line(solid.Edges, edges);
    }

    /// <summary>The convex hull of points (Andrew's monotone chain).</summary>
    internal static Point[] Hull(Point[] input)
    {
        var points = input.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (points.Length < 3) return points.Length == 0 ? [new Point()] : points;
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new Point[points.Length * 2]; int k = 0;
        foreach (var p in points) { while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 1e-9) k--; hull[k++] = p; }
        for (int i = points.Length - 2, lower = k + 1; i >= 0; i--) { var p = points[i]; while (k >= lower && Cross(hull[k - 2], hull[k - 1], p) <= 1e-9) k--; hull[k++] = p; }
        return hull[..(k - 1)];
    }
}

/// <summary>Parts sorted back to front by the depth of a reference point before they are drawn.</summary>
internal sealed class AxoPainter(AxoSketch sketch)
{
    readonly List<(double Depth, int Order, Action Draw)> parts = [];
    public void Add(Point3D local, Action draw) => parts.Add((sketch.Depth(local), parts.Count, draw));
    public void Draw() { foreach (var part in parts.OrderBy(p => p.Depth).ThenBy(p => p.Order)) part.Draw(); parts.Clear(); }
}
