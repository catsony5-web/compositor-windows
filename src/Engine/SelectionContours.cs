using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class SelectionContours
{
    // Trace the 50% boundary with marching squares. No stride or pixel omission:
    // tiny holes and narrow islands survive on large drawings at any zoom.
    public static Geometry Create(Selection selection, CancellationToken token = default)
    {
        if (selection.Contour is { } cached) return cached;
        if (selection.Coverage == null)
        {
            Geometry simple = selection.Ellipse ? new EllipseGeometry(selection.Bounds) : new RectangleGeometry(selection.Bounds);
            simple.Freeze(); return simple;
        }
        var mask = selection.Coverage; int w = selection.CanvasWidth, h = selection.CanvasHeight;
        var area = selection.CoverageBounds ?? new Rect(0, 0, w, h);
        double sx = area.Width / w, sy = area.Height / h;
        var geometry = new StreamGeometry();
        if (selection.Bounds.Width <= 0 || selection.Bounds.Height <= 0) { geometry.Freeze(); return geometry; }
        int left = Math.Max(-1, (int)Math.Floor((selection.Bounds.Left - area.Left) / sx) - 1);
        int top = Math.Max(-1, (int)Math.Floor((selection.Bounds.Top - area.Top) / sy) - 1);
        int right = Math.Min(w - 1, (int)Math.Ceiling((selection.Bounds.Right - area.Left) / sx));
        int bottom = Math.Min(h - 1, (int)Math.Ceiling((selection.Bounds.Bottom - area.Top) / sy));
        var segments = new Dictionary<long, (Point Point, long Next)>();
        byte Value(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? (byte)0 : mask[y * w + x];
        for (int y = top; y <= bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = left; x <= right; x++)
            {
                byte a = Value(x, y), b = Value(x + 1, y), c = Value(x + 1, y + 1), d = Value(x, y + 1);
                int code = (a >= 128 ? 1 : 0) | (b >= 128 ? 2 : 0) | (c >= 128 ? 4 : 0) | (d >= 128 ? 8 : 0);
                if (code is 0 or 15) continue;
                (long Key, Point Point) Edge(int edge)
                {
                    int xx = x, yy = y; bool vertical = edge is 1 or 3; byte first, last;
                    if (edge == 0) { first = a; last = b; }
                    else if (edge == 1) { xx++; first = b; last = c; }
                    else if (edge == 2) { yy++; first = d; last = c; }
                    else { first = a; last = d; }
                    double t = (127.5 - first) / (last - (double)first);
                    var point = new Point(area.X + (xx + .5 + (vertical ? 0 : t)) * sx, area.Y + (yy + .5 + (vertical ? t : 0)) * sy);
                    long key = (((long)(yy + 1) * (w + 2) + xx + 1) << 1) | (vertical ? 1L : 0L);
                    return (key, point);
                }
                void Segment(int start, int end)
                {
                    var p = Edge(start); segments.Add(p.Key, (p.Point, Edge(end).Key));
                }
                switch (code)
                {
                    case 1: Segment(3, 0); break; case 2: Segment(0, 1); break; case 3: Segment(3, 1); break;
                    case 4: Segment(1, 2); break; case 5: Segment(3, 0); Segment(1, 2); break;
                    case 6: Segment(0, 2); break; case 7: Segment(3, 2); break; case 8: Segment(2, 3); break;
                    case 9: Segment(2, 0); break; case 10: Segment(0, 1); Segment(2, 3); break;
                    case 11: Segment(2, 1); break; case 12: Segment(1, 3); break; case 13: Segment(1, 0); break; case 14: Segment(0, 3); break;
                }
            }
        }
        using (var context = geometry.Open())
        {
            foreach (long start in segments.Keys.ToArray())
            {
                if (!segments.ContainsKey(start)) continue;
                token.ThrowIfCancellationRequested();
                long next = start;
                var points = new List<Point>();
                do
                {
                    if ((points.Count & 8191) == 0) token.ThrowIfCancellationRequested();
                    if (!segments.Remove(next, out var segment)) throw new InvalidOperationException("선택 경계를 연결하지 못했습니다.");
                    points.Add(segment.Point); next = segment.Next;
                } while (next != start);
                context.BeginFigure(points[0], true, true);
                for (int i = 1; i < points.Count; i++)
                {
                    var before = points[i] - points[i - 1]; var after = points[(i + 1) % points.Count] - points[i];
                    if (i == points.Count - 1 || Math.Abs(Vector.CrossProduct(before, after)) > 1e-12) context.LineTo(points[i], true, false);
                }
            }
        }
        geometry.Freeze(); return geometry;
    }
}
