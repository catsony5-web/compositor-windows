using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Geometry of diagram shapes (ShapeKind.Line and .Callout) and of dashed outlines: centerline
/// (straight segments or a centripetal Catmull–Rom curve through every point), dash patterns,
/// line-end marks, callout label placement, fitting the layer surface, hit tests and ink.
/// One retained WPF drawing per spec is shared by the pixel cache, the crisp design view, raster
/// exports and the vector PDF/.ai writer, so all of them draw the same strokes.
/// </summary>
public static class ShapeGeometry
{
    // Space between the fitted content and the layer edge, in layer pixels.
    const double FitMargin = 2;
    static readonly ConditionalWeakTable<ShapeSpec, Model> models = new();
    static readonly ConditionalWeakTable<ShapeSpec, DrawingGroup> drawings = new();
    static readonly ConditionalWeakTable<ShapeSpec, Geometry> inks = new();

    /// <summary>Whether the shape is drawn from its retained drawing (lines, callouts, dashed outlines) rather than sampled analytically.</summary>
    public static bool UsesDrawing(ShapeSpec spec) => spec.HasPoints || spec.StrokeEnabled && spec.StrokeWidth > 0 && spec.Dash != StrokeDash.Solid;

    /// <summary>One piece of a centerline: a straight segment or a cubic Bézier.</summary>
    public readonly record struct Segment(Point Start, Point Control1, Point Control2, Point End, bool Straight)
    {
        public static Segment Line(Point a, Point b) => new(a, a, b, b, true);
        public Point At(double t)
        {
            if (Straight) return Start + (End - Start) * t;
            double u = 1 - t;
            return new Point(u * u * u * Start.X + 3 * u * u * t * Control1.X + 3 * u * t * t * Control2.X + t * t * t * End.X,
                u * u * u * Start.Y + 3 * u * u * t * Control1.Y + 3 * u * t * t * Control2.Y + t * t * t * End.Y);
        }
        public (Segment Before, Segment After) Split(double t)
        {
            if (Straight) { var m = At(t); return (Line(Start, m), Line(m, End)); }
            Point L(Point a, Point b) => a + (b - a) * t;
            var p01 = L(Start, Control1); var p12 = L(Control1, Control2); var p23 = L(Control2, End);
            var p012 = L(p01, p12); var p123 = L(p12, p23); var mid = L(p012, p123);
            return (new(Start, p01, p012, mid, false), new(mid, p123, p23, End, false));
        }
        const int Samples = 32;
        public double Length()
        {
            if (Straight) return (End - Start).Length;
            double length = 0; var previous = Start;
            for (int i = 1; i <= Samples; i++) { var p = At(i / (double)Samples); length += (p - previous).Length; previous = p; }
            return length;
        }
        // Parameter at an arc length from the start (sampled; exact for straight segments).
        public double ParameterAt(double distance)
        {
            if (distance <= 0) return 0;
            if (Straight) { double total = (End - Start).Length; return total <= 0 ? 0 : Math.Min(1, distance / total); }
            double run = 0; var previous = Start;
            for (int i = 1; i <= Samples; i++)
            {
                var p = At(i / (double)Samples); double step = (p - previous).Length;
                if (run + step >= distance) return (i - 1 + (step <= 0 ? 0 : (distance - run) / step)) / Samples;
                run += step; previous = p;
            }
            return 1;
        }
        public Segment Reversed() => new(End, Control2, Control1, Start, Straight);
        public IEnumerable<Point> Flatten(int steps)
        {
            if (Straight) { yield return End; yield break; }
            for (int i = 1; i <= steps; i++) yield return At(i / (double)steps);
        }
    }

    sealed record Mark(Geometry Geometry, bool Filled);
    sealed class Model
    {
        public Segment[] Path = [];
        public Segment[] Visible = [];
        public bool Closed;
        public Mark[] Marks = [];
        public Point[] Flat = [];
        public (Point Tip, double Radius)[] MarkAreas = [];
        public DrawingGroup? Text;
        public Vector TextOffset;
        public Rect TextSurface = Rect.Empty, TextRect = Rect.Empty, LabelBox = Rect.Empty;
    }

    // ------------------------------------------------------------------ centerline

    static Point[] Distinct(IReadOnlyList<Point> points)
    {
        var result = new List<Point>(points.Count);
        foreach (var p in points) if (result.Count == 0 || (p - result[^1]).LengthSquared > 1e-12) result.Add(p);
        return result.ToArray();
    }

    /// <summary>Straight segments through the points, or a smooth curve that passes through every point.</summary>
    public static Segment[] Centerline(IReadOnlyList<Point> source, bool smooth, bool closed)
    {
        var points = Distinct(source);
        if (closed && points.Length > 2 && (points[0] - points[^1]).LengthSquared <= 1e-12) points = points[..^1];
        int n = points.Length; if (n < 2) return [];
        bool loop = closed && n > 2; int count = loop ? n : n - 1;
        var segments = new Segment[count];
        for (int i = 0; i < count; i++)
        {
            Point p1 = points[i], p2 = points[(i + 1) % n];
            if (!smooth || n == 2) { segments[i] = Segment.Line(p1, p2); continue; }
            // Neighbors; an open curve's ends mirror their next point for a natural end tangent.
            Point p0 = loop ? points[(i - 1 + n) % n] : i > 0 ? points[i - 1] : p1 + (p1 - p2);
            Point p3 = loop ? points[(i + 2) % n] : i + 2 < n ? points[i + 2] : p2 + (p2 - p1);
            segments[i] = CatmullRom(p0, p1, p2, p3);
        }
        return segments;
    }

    // Centripetal Catmull–Rom (alpha 0.5) as one cubic Bézier from p1 to p2: no cusps or loops
    // within a segment, even when the clicked points are spaced very unevenly.
    static Segment CatmullRom(Point p0, Point p1, Point p2, Point p3)
    {
        double d1 = Math.Sqrt((p1 - p0).Length), d2 = Math.Sqrt((p2 - p1).Length), d3 = Math.Sqrt((p3 - p2).Length);
        Point Mix(double a, Point pa, double b, Point pb, double c, Point pc, double divisor) =>
            new((a * pa.X + b * pb.X + c * pc.X) / divisor, (a * pa.Y + b * pb.Y + c * pc.Y) / divisor);
        var c1 = d1 < 1e-9 ? p1 + (p2 - p1) / 3 : Mix(d1 * d1, p2, -d2 * d2, p0, 2 * d1 * d1 + 3 * d1 * d2 + d2 * d2, p1, 3 * d1 * (d1 + d2));
        var c2 = d3 < 1e-9 ? p2 + (p1 - p2) / 3 : Mix(d3 * d3, p1, -d2 * d2, p3, 2 * d3 * d3 + 3 * d3 * d2 + d2 * d2, p2, 3 * d3 * (d3 + d2));
        return new Segment(p1, c1, c2, p2, false);
    }

    static double Length(Segment[] path) => path.Sum(s => s.Length());

    // The path with `start` and `end` pixels of arc length removed from its ends.
    static Segment[] Trim(Segment[] path, double start, double end)
    {
        if (start <= 0 && end <= 0) return path;
        double total = Length(path); if (total <= start + end + .01) return [];
        var list = path.ToList();
        static void TrimFront(List<Segment> list, double distance)
        {
            while (list.Count > 0 && distance > 0)
            {
                double length = list[0].Length();
                if (length <= distance) { distance -= length; list.RemoveAt(0); continue; }
                list[0] = list[0].Split(list[0].ParameterAt(distance)).After; break;
            }
        }
        TrimFront(list, start);
        list.Reverse(); for (int i = 0; i < list.Count; i++) list[i] = list[i].Reversed();
        TrimFront(list, end);
        list.Reverse(); for (int i = 0; i < list.Count; i++) list[i] = list[i].Reversed();
        return list.ToArray();
    }

    // The point at an arc length from the path start.
    static Point PointAt(Segment[] path, double distance)
    {
        foreach (var segment in path)
        {
            double length = segment.Length();
            if (distance <= length) return segment.At(segment.ParameterAt(distance));
            distance -= length;
        }
        return path.Length == 0 ? new Point() : path[^1].End;
    }

    public static PathGeometry PathOf(IReadOnlyList<Segment> path, bool closed)
    {
        var geometry = new PathGeometry();
        if (path.Count == 0) return geometry;
        var figure = new PathFigure { StartPoint = path[0].Start, IsClosed = closed, IsFilled = closed };
        foreach (var s in path)
            figure.Segments.Add(s.Straight ? new LineSegment(s.End, true) : new BezierSegment(s.Control1, s.Control2, s.End, true));
        geometry.Figures.Add(figure); return geometry;
    }

    // ------------------------------------------------------------------ dashes and pens

    /// <summary>Visible lengths of the dash pattern in pixels (on, off, …): what a ruler would measure on the canvas.</summary>
    public static double[]? VisibleDashLengths(ShapeSpec spec)
    {
        if (spec.Dash == StrokeDash.Solid || !(spec.StrokeWidth > 0)) return null;
        double w = spec.StrokeWidth, unit = Math.Max(w, 1) * spec.DashScale;
        return spec.Dash switch
        {
            StrokeDash.Dotted => [w, 2 * unit],
            StrokeDash.Dashed => [5 * unit, 3 * unit],
            _ => [10 * unit, 3 * unit, w, 3 * unit]
        };
    }

    /// <summary>
    /// Pen dash lengths in pixels. Round and square caps grow every dash by half the stroke width at
    /// both ends, so the pen pattern is shortened by that much (a dot becomes a zero-length dash).
    /// </summary>
    public static double[]? DashLengths(ShapeSpec spec)
    {
        if (VisibleDashLengths(spec) is not { } visible) return null;
        double w = spec.StrokeWidth; bool capped = spec.Cap != StrokeCap.Flat;
        return capped ? visible.Select((v, i) => i % 2 == 0 ? Math.Max(0, v - w) : v + w).ToArray() : visible;
    }

    public static PenLineCap PenCap(StrokeCap cap) => cap switch { StrokeCap.Round => PenLineCap.Round, StrokeCap.Square => PenLineCap.Square, _ => PenLineCap.Flat };

    static Pen StrokePen(ShapeSpec spec, bool dashed)
    {
        var brush = new SolidColorBrush(VectorShapes.Color(spec.StrokeArgb)); brush.Freeze();
        var cap = PenCap(spec.Cap);
        var pen = new Pen(brush, spec.StrokeWidth) { StartLineCap = cap, EndLineCap = cap, DashCap = cap, LineJoin = spec.Cap == StrokeCap.Round ? PenLineJoin.Round : PenLineJoin.Miter, MiterLimit = 4 };
        if (dashed && DashLengths(spec) is { } dashes) pen.DashStyle = new DashStyle(dashes.Select(d => d / spec.StrokeWidth), 0);
        pen.Freeze(); return pen;
    }

    // ------------------------------------------------------------------ marks

    /// <summary>Arrow length used for a mark: at least 2.5 stroke widths so the stroke ends inside the head.</summary>
    public static double MarkLength(ShapeSpec spec) => Math.Max(spec.MarkSize, spec.StrokeWidth * 2.5);
    static double DotRadius(ShapeSpec spec) => Math.Max(MarkLength(spec) * .3, spec.StrokeWidth * .9);
    static double RingRadius(ShapeSpec spec) => Math.Max(MarkLength(spec) * .3, spec.StrokeWidth * 1.5);

    // How much of the line a mark replaces at its end.
    static double MarkTrim(ShapeSpec spec, LineMark mark) => mark switch
    {
        LineMark.Arrow => MarkLength(spec) * .75,
        LineMark.OpenArrow => spec.StrokeWidth / 2,
        LineMark.Dot => DotRadius(spec),
        LineMark.Ring => RingRadius(spec) + spec.StrokeWidth / 2,
        _ => 0
    };

    // How far a mark reaches from its tip along the line (label spacing of callouts).
    static double MarkReach(ShapeSpec spec, LineMark mark) => mark switch
    {
        LineMark.Dot => DotRadius(spec), LineMark.Ring => RingRadius(spec) + spec.StrokeWidth / 2, LineMark.Bar => spec.StrokeWidth / 2, _ => 0
    };

    /// <summary>
    /// The outline of one end mark with its tip at <paramref name="tip"/>, pointing along <paramref name="direction"/>
    /// (from the line toward the tip). Arrows are filled triangles; open arrows, rings and bars are strokes.
    /// </summary>
    public static (Geometry Geometry, bool Filled) MarkGeometry(ShapeSpec spec, LineMark mark, Point tip, Vector direction)
    {
        if (direction.LengthSquared < 1e-18) direction = new Vector(1, 0);
        direction.Normalize(); var normal = new Vector(-direction.Y, direction.X);
        double length = MarkLength(spec), half = length * .36;
        Geometry geometry; bool filled;
        switch (mark)
        {
            case LineMark.Arrow:
            case LineMark.OpenArrow:
                var back = tip - direction * length;
                var figure = new PathFigure { StartPoint = back + normal * half, IsClosed = mark == LineMark.Arrow, IsFilled = mark == LineMark.Arrow };
                figure.Segments.Add(new LineSegment(tip, true)); figure.Segments.Add(new LineSegment(back - normal * half, true));
                var path = new PathGeometry(); path.Figures.Add(figure); geometry = path; filled = mark == LineMark.Arrow; break;
            case LineMark.Dot: geometry = new EllipseGeometry(tip, DotRadius(spec), DotRadius(spec)); filled = true; break;
            case LineMark.Ring: geometry = new EllipseGeometry(tip, RingRadius(spec), RingRadius(spec)); filled = false; break;
            case LineMark.Bar: geometry = new LineGeometry(tip + normal * length * .4, tip - normal * length * .4); filled = false; break;
            default: return (Geometry.Empty, false);
        }
        geometry.Freeze(); return (geometry, filled);
    }

    // Direction at a path end, measured over the length the mark covers so it follows the visible stroke.
    static Vector EndDirection(Segment[] path, bool atEnd, double reach)
    {
        double total = Length(path); if (total <= 0) return new Vector(1, 0);
        double s = Math.Clamp(reach, Math.Min(.5, total), total);
        var tip = atEnd ? path[^1].End : path[0].Start;
        var from = atEnd ? PointAt(path, total - s) : PointAt(path, s);
        var direction = tip - from;
        if (direction.LengthSquared < 1e-12) direction = atEnd ? path[^1].End - path[^1].Control2 : path[0].Start - path[0].Control1;
        return direction;
    }

    // ------------------------------------------------------------------ models

    static Model ModelOf(ShapeSpec spec) => models.GetValue(spec, Build);

    static Model Build(ShapeSpec spec)
    {
        var model = new Model();
        if (!spec.HasPoints) return model;
        var points = spec.Points!;
        double w = spec.StrokeWidth;
        LineMark startMark = spec.StartMark, endMark = spec.EndMark;
        if (spec.Kind == ShapeKind.Line)
        {
            model.Closed = spec.Closed;
            model.Path = Centerline(points, spec.Smooth, spec.Closed);
            if (model.Closed) startMark = endMark = LineMark.None;
        }
        else
        {
            Point a = points[DiagramEditing.Anchor], e = points[DiagramEditing.Elbow], l = points[DiagramEditing.LabelPoint];
            model.Path = Centerline(spec.Leader == CalloutLeader.Elbow ? [a, e, l] : [a, l], false, false);
            Label(spec, model, a, e, l);
        }
        var marks = new List<Mark>(); var areas = new List<(Point, double)>();
        if (model.Path.Length > 0 && spec.StrokeEnabled && w > 0)
        {
            double startTrim = MarkTrim(spec, startMark), endTrim = MarkTrim(spec, endMark);
            model.Visible = Trim(model.Path, startTrim, endTrim);
            double reach = MarkLength(spec) * .75;
            if (startMark != LineMark.None)
            {
                var tip = model.Path[0].Start; var (geometry, filled) = MarkGeometry(spec, startMark, tip, EndDirection(model.Path, false, reach));
                marks.Add(new(geometry, filled)); areas.Add((tip, MarkLength(spec) * .5 + w));
            }
            if (endMark != LineMark.None)
            {
                var tip = model.Path[^1].End; var (geometry, filled) = MarkGeometry(spec, endMark, tip, EndDirection(model.Path, true, reach));
                marks.Add(new(geometry, filled)); areas.Add((tip, MarkLength(spec) * .5 + w));
            }
        }
        else model.Visible = model.Path;
        model.Marks = marks.ToArray(); model.MarkAreas = areas.ToArray();
        var flat = new List<Point>();
        if (model.Path.Length > 0)
        {
            flat.Add(model.Path[0].Start);
            foreach (var segment in model.Path) flat.AddRange(segment.Flatten(24));
        }
        model.Flat = flat.ToArray();
        return model;
    }

    // Callout label: beside the leader end, on the side the leader comes from, its middle level with
    // the leader end. The label box (fill) wraps the text with the same padding.
    static void Label(ShapeSpec spec, Model model, Point anchor, Point elbow, Point end)
    {
        var label = spec.Label!;
        var (text, surfaceWidth, surfaceHeight) = DocumentFeatures.TextDrawing(label);
        double inset = 4 + label.OutlineExtent;
        double width = Math.Max(1, surfaceWidth - 2 * inset), height = Math.Max(1, surfaceHeight - 2 * inset);
        double from = spec.Leader == CalloutLeader.Elbow && Math.Abs(end.X - elbow.X) > 1e-9 ? elbow.X : anchor.X;
        int side = end.X > from ? 1 : end.X < from ? -1 : 1;
        double pad = Padding(label);
        double near = end.X + side * (MarkReach(spec, spec.EndMark) + pad);
        double left = side > 0 ? near : near - width, top = end.Y - height / 2;
        model.Text = text; model.TextOffset = new Vector(left - inset, top - inset);
        model.TextRect = new Rect(left, top, width, height);
        model.TextSurface = new Rect(left - inset, top - inset, surfaceWidth, surfaceHeight);
        if (spec.FillEnabled) model.LabelBox = new Rect(left - pad, top - pad, width + 2 * pad, height + 2 * pad);
    }

    /// <summary>Space between a callout's leader end (or label box edge) and its text.</summary>
    public static double Padding(TextSpec label) => Math.Max(3, label.FontSize * .4);

    // ------------------------------------------------------------------ drawing

    /// <summary>The retained drawing in layer pixels (frozen; cached per spec).</summary>
    public static DrawingGroup Drawing(ShapeSpec spec) => drawings.GetValue(spec, BuildDrawing);

    static DrawingGroup BuildDrawing(ShapeSpec spec)
    {
        var group = new DrawingGroup();
        Brush Fill() { var brush = new SolidColorBrush(VectorShapes.Color(spec.FillArgb)); brush.Freeze(); return brush; }
        Brush Ink() { var brush = new SolidColorBrush(VectorShapes.Color(spec.StrokeArgb)); brush.Freeze(); return brush; }
        using (var dc = group.Open())
        {
            if (!spec.HasPoints)
            {
                Geometry Outline(double inset)
                {
                    var rect = new Rect(inset, inset, Math.Max(.001, spec.Width - inset * 2), Math.Max(.001, spec.Height - inset * 2));
                    return spec.Kind == ShapeKind.Ellipse ? new EllipseGeometry(rect) : new RectangleGeometry(rect, Math.Max(0, spec.CornerRadius - inset), Math.Max(0, spec.CornerRadius - inset));
                }
                if (spec.FillEnabled) dc.DrawGeometry(Fill(), null, Outline(0));
                if (spec.StrokeEnabled && spec.StrokeWidth > 0) dc.DrawGeometry(null, StrokePen(spec, true), Outline(spec.StrokeWidth / 2));
            }
            else
            {
                var model = ModelOf(spec);
                if (!model.LabelBox.IsEmpty)
                {
                    double radius = Math.Min(spec.CornerRadius, Math.Min(model.LabelBox.Width, model.LabelBox.Height) / 2);
                    dc.DrawGeometry(Fill(), null, new RectangleGeometry(model.LabelBox, radius, radius));
                }
                if (spec.Kind == ShapeKind.Line && model.Closed && spec.FillEnabled && model.Path.Length > 0) dc.DrawGeometry(Fill(), null, PathOf(model.Path, true));
                if (spec.StrokeEnabled && spec.StrokeWidth > 0)
                {
                    bool whole = model.Visible.Length == model.Path.Length && ReferenceEquals(model.Visible, model.Path);
                    if (model.Visible.Length > 0) dc.DrawGeometry(null, StrokePen(spec, true), PathOf(model.Visible, model.Closed && whole));
                    if (model.Marks.Length > 0)
                    {
                        // Marks are always solid; open arrows and bars share the line's ends, rings stay round.
                        var solid = StrokePen(spec with { Dash = StrokeDash.Solid }, false);
                        var round = StrokePen(spec with { Dash = StrokeDash.Solid, Cap = StrokeCap.Round }, false);
                        foreach (var mark in model.Marks)
                            if (mark.Filled) dc.DrawGeometry(Ink(), null, mark.Geometry);
                            else dc.DrawGeometry(null, mark.Geometry is LineGeometry ? solid : round, mark.Geometry);
                    }
                }
                if (model.Text != null)
                {
                    dc.PushTransform(new TranslateTransform(model.TextOffset.X, model.TextOffset.Y));
                    dc.DrawDrawing(model.Text); dc.Pop();
                }
            }
        }
        group.Freeze(); return group;
    }

    // ------------------------------------------------------------------ fitting

    /// <summary>Everything the shape paints, in its own pixel coordinates (stroke widths, marks and the whole label surface included).</summary>
    public static Rect ContentBounds(ShapeSpec spec)
    {
        var bounds = Drawing(spec).Bounds;
        var model = ModelOf(spec);
        if (!model.TextSurface.IsEmpty) bounds.Union(model.TextSurface);
        if (bounds.IsEmpty && spec.Points is { Count: > 0 } points) bounds = new Rect(points[0], new Size(0, 0));
        return bounds;
    }

    /// <summary>
    /// Moves the points of a line or callout so its content starts just inside the layer surface and
    /// sizes the surface to fit it. Returns the fitted spec and how far the points moved (whole pixels).
    /// </summary>
    public static (ShapeSpec Spec, Vector Shift) Fit(ShapeSpec spec)
    {
        if (!spec.HasPoints) return (spec, default);
        var points = spec.Points ?? throw new InvalidDataException("선의 점 정보가 없습니다.");
        points.Validate(spec.Kind == ShapeKind.Callout ? 3 : spec.Closed ? 3 : 2, spec.Kind == ShapeKind.Callout ? 3 : ShapePoints.MaxCount);
        if (spec.Kind == ShapeKind.Callout) (spec.Label ?? throw new InvalidDataException("지시선 라벨 정보가 없습니다.")).Validate();
        // Measure with a placeholder size; the surface size never changes the drawing.
        var probe = spec with { Width = Math.Max(1, spec.Width), Height = Math.Max(1, spec.Height) };
        var bounds = ContentBounds(probe);
        if (!double.IsFinite(bounds.Left + bounds.Top + bounds.Width + bounds.Height)) throw new InvalidDataException("선의 점 좌표가 올바르지 않습니다.");
        var shift = new Vector(Math.Round(FitMargin - bounds.Left), Math.Round(FitMargin - bounds.Top));
        double width = bounds.Right + shift.X + FitMargin, height = bounds.Bottom + shift.Y + FitMargin;
        if (width > Raster.MaxDimension + 1 || height > Raster.MaxDimension + 1)
            throw new InvalidDataException($"선과 지시선은 한 변 {Raster.MaxDimension:N0}px 안에 그려 주세요.");
        int w = Math.Max(1, (int)Math.Ceiling(width - 1e-6)), h = Math.Max(1, (int)Math.Ceiling(height - 1e-6));
        Raster.ValidateSize(w, h);
        return (spec with { Points = points.Offset(shift), Width = w, Height = h }, shift);
    }

    // ------------------------------------------------------------------ hit tests and ink

    static double SegmentDistance(Point p, Point a, Point b)
    {
        var ab = b - a; double lengthSquared = ab.LengthSquared;
        double t = lengthSquared <= 0 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    static bool InsidePolygon(Point p, IReadOnlyList<Point> polygon)
    {
        int winding = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Point a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
            if (a.Y <= p.Y) { if (b.Y > p.Y && cross > 0) winding++; }
            else if (b.Y <= p.Y && cross < 0) winding--;
        }
        return winding != 0;
    }

    /// <summary>
    /// Whether a layer-pixel point touches the line or callout: within half the stroke width plus
    /// <paramref name="tolerance"/> of the centerline (dash gaps count as line), on a mark, inside a
    /// closed filled line or on the label. Rectangles and ellipses use their pixels instead.
    /// </summary>
    public static bool Hit(ShapeSpec spec, Point p, double tolerance = 0)
    {
        if (!spec.HasPoints) return false;
        var model = ModelOf(spec);
        if (!model.TextRect.IsEmpty) { var area = model.LabelBox.IsEmpty ? model.TextRect : model.LabelBox; area.Inflate(tolerance, tolerance); if (area.Contains(p)) return true; }
        var flat = model.Flat;
        if (model.Closed && spec.FillEnabled && flat.Length > 2 && InsidePolygon(p, flat)) return true;
        if (spec.StrokeEnabled || flat.Length > 0)
        {
            double reach = Math.Max(.5, spec.StrokeWidth / 2) + tolerance;
            for (int i = 1; i < flat.Length; i++) if (SegmentDistance(p, flat[i - 1], flat[i]) <= reach) return true;
            if (model.Closed && flat.Length > 2 && SegmentDistance(p, flat[^1], flat[0]) <= reach) return true;
        }
        foreach (var (tip, radius) in model.MarkAreas) if ((p - tip).Length <= radius + tolerance) return true;
        return false;
    }

    /// <summary>What the shape covers in layer pixels for selection rectangles: solid stroke, marks, fill and label.</summary>
    public static Geometry Ink(ShapeSpec spec) => inks.GetValue(spec, s =>
    {
        var model = ModelOf(s); var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (model.Path.Length > 0)
        {
            var solid = StrokePen(s with { Dash = StrokeDash.Solid, StrokeWidth = Math.Max(1, s.StrokeWidth) }, false);
            group.Children.Add(PathOf(model.Path, model.Closed).GetWidenedPathGeometry(solid, .05, ToleranceType.Absolute));
            if (model.Closed && s.FillEnabled) group.Children.Add(PathOf(model.Path, true));
        }
        foreach (var mark in model.Marks) group.Children.Add(mark.Filled ? mark.Geometry : mark.Geometry.GetWidenedPathGeometry(StrokePen(s with { Dash = StrokeDash.Solid }, false), .05, ToleranceType.Absolute));
        if (!model.TextRect.IsEmpty) group.Children.Add(new RectangleGeometry(model.LabelBox.IsEmpty ? model.TextRect : model.LabelBox));
        group.Freeze(); return (Geometry)group;
    });

    /// <summary>The closed outline of a closed line, for material boundaries; null when the line is open.</summary>
    public static Geometry? ClosedOutline(ShapeSpec spec)
    {
        if (spec.Kind != ShapeKind.Line || !spec.Closed) return null;
        var model = ModelOf(spec); if (model.Path.Length < 2) return null;
        var path = PathOf(model.Path, true); path.Freeze(); return path;
    }

    /// <summary>
    /// The place on a line nearest to a layer-pixel point: the index a new point gets so it lands on that
    /// segment, the point itself and its distance. Null for callouts and degenerate lines.
    /// </summary>
    public static (int InsertIndex, Point At, double Distance)? Nearest(ShapeSpec spec, Point p)
    {
        if (spec.Kind != ShapeKind.Line || spec.Points is not { Count: >= 2 } points) return null;
        var segments = Centerline(points, spec.Smooth, spec.Closed);
        if (segments.Length == 0) return null;
        // Segment i of the centerline runs from distinct point i to i + 1; map it back to the stored index.
        var distinct = Distinct(points); var storedIndex = new int[distinct.Length]; int k = 0;
        for (int i = 0; i < points.Count && k < distinct.Length; i++) if (points[i] == distinct[k]) storedIndex[k++] = i;
        (int Index, Point At, double Distance)? best = null;
        for (int i = 0; i < segments.Length; i++)
        {
            var previous = segments[i].Start;
            foreach (var next in segments[i].Flatten(48).Prepend(segments[i].Start).Skip(1))
            {
                var ab = next - previous; double lengthSquared = ab.LengthSquared;
                double t = lengthSquared <= 0 ? 0 : Math.Clamp(Vector.Multiply(p - previous, ab) / lengthSquared, 0, 1);
                var at = previous + ab * t; double distance = (p - at).Length;
                int insert = i + 1 < storedIndex.Length ? storedIndex[i + 1] : points.Count;
                if (best == null || distance < best.Value.Distance) best = (insert, at, distance);
                previous = next;
            }
        }
        return best;
    }

    /// <summary>The label text rectangle of a callout in layer pixels (empty for other kinds).</summary>
    public static Rect LabelRect(ShapeSpec spec) => spec.Kind == ShapeKind.Callout ? ModelOf(spec).TextRect : Rect.Empty;

    /// <summary>The visible (trimmed) centerline, for checks and the PDF writer's tests.</summary>
    internal static Segment[] VisiblePath(ShapeSpec spec) => ModelOf(spec).Visible;
    internal static Segment[] FullPath(ShapeSpec spec) => ModelOf(spec).Path;
    internal static (Geometry Geometry, bool Filled)[] Marks(ShapeSpec spec) => ModelOf(spec).Marks.Select(m => (m.Geometry, m.Filled)).ToArray();
}
