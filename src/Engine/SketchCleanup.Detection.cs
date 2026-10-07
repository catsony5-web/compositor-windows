using System.Windows;

namespace Compositor.Windows;

// Finding the sheet in a photo: on a copy reduced to WorkSide px, paper is told from the table by being
// bright and little saturated (thin lines and text are closed away first); an Otsu threshold with
// hysteresis lets shadowed paper join the lit paper, the largest region with its holes filled is the
// sheet, its convex hull is reduced to the quadrilateral that adds the least area (so a corner hidden
// under a pen is restored from the two sides that meet there), the sides are refitted to the region's
// edge, then to the strongest light-to-dark step along each side at full resolution. Confidence comes
// from the sheet's size in the frame, its contrast with the surroundings, how well the quadrilateral
// matches the region and its shape; below MinConfidence the whole image is used instead.
public static partial class SketchCleanup
{
    public const int WorkSide = 640;
    public const double MinConfidence = .5;

    public static SheetDetection DetectSheet(Raster photo, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var whole = ImageCorners(photo.Width, photo.Height);
        if (photo.Width < 48 || photo.Height < 48) return new(whole, 0, false);
        var work = PreviewScaling.Fit(photo, WorkSide, token);
        int w = work.Width, h = work.Height;
        double sx = photo.Width / (double)w, sy = photo.Height / (double)h;
        var luma = new float[w * h]; var saturation = new float[w * h];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
        {
            double a = work.Data[p + 3] / 255d, paper = 255 * (1 - a);
            double b = work.Data[p] * a + paper, g = work.Data[p + 1] * a + paper, r = work.Data[p + 2] * a + paper;
            double max = Math.Max(b, Math.Max(g, r)), min = Math.Min(b, Math.Min(g, r));
            luma[i] = (float)((.0722 * b + .7152 * g + .2126 * r) / 255);
            saturation[i] = (float)(max <= 1 ? 0 : (max - min) / max);
        }
        token.ThrowIfCancellationRequested();
        // Lines, text and thin shadows on the paper are closed away; thin colored strokes opened away.
        var closed = Morphology(Morphology(luma, w, h, 2, true), w, h, 2, false);
        var opened = Morphology(Morphology(saturation, w, h, 2, false), w, h, 2, true);
        var score = new float[w * h];
        for (int i = 0; i < score.Length; i++) score[i] = (float)Math.Clamp(closed[i] * (1 - .8 * opened[i]), 0, 1);
        var histogram = new long[256];
        foreach (var s in score) histogram[Math.Clamp((int)(s * 255 + .5), 0, 255)]++;
        double cut = LineArt.Otsu(histogram);
        double below = 0, above = 0; long belowCount = 0, aboveCount = 0;
        foreach (var s in score) if (s < cut) { below += s; belowCount++; } else { above += s; aboveCount++; }
        if (belowCount == 0 || aboveCount == 0) return new(whole, 0, false);
        below /= belowCount; above /= aboveCount;
        double low = below + .35 * (above - below);
        var mask = Hysteresis(score, w, h, cut, low);
        mask = Largest(mask, w, h);
        FillHoles(mask, w, h);
        // A thin bright bridge to another object (a cable, a second sheet's corner) is cut.
        mask = Largest(Binary(Morphology(Morphology(Floats(mask), w, h, 2, false), w, h, 2, true)), w, h);
        token.ThrowIfCancellationRequested();
        int area = mask.Count(v => v != 0);
        if (area < w * h * .04) return new(whole, 0, false);
        var hull = Hull(BoundaryCorners(mask, w, h));
        if (hull.Count < 4) return new(whole, 0, false);
        var quad = ReduceToQuad(hull);
        if (quad == null) return new(whole, 0, false);
        quad = RefitSides(quad, EdgePoints(mask, w, h)) ?? quad;
        var coarse = Order(quad.Select(p => new Point(p.X * sx, p.Y * sy)).ToArray());
        var refined = RefineAtFullResolution(photo, coarse, Math.Max(sx, sy), token) ?? coarse;
        double confidence = Confidence(refined, photo.Width, photo.Height, score, mask, w, h, sx, sy);
        return confidence >= MinConfidence ? new(refined, confidence, true) : new(whole, confidence, false);
    }

    static float[] Floats(byte[] mask) { var f = new float[mask.Length]; for (int i = 0; i < f.Length; i++) f[i] = mask[i]; return f; }
    static byte[] Binary(float[] values) { var b = new byte[values.Length]; for (int i = 0; i < b.Length; i++) b[i] = values[i] >= .5f ? (byte)1 : (byte)0; return b; }

    // Pixels at least `high`, grown through 8-connected neighbours at least `low`.
    static byte[] Hysteresis(float[] score, int w, int h, double high, double low)
    {
        var mask = new byte[w * h]; var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++) if (score[i] >= high) { mask[i] = 1; stack.Push(i); }
        while (stack.Count > 0)
        {
            int p = stack.Pop(), x = p % w, y = p / w;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                {
                    int q = yy * w + xx;
                    if (mask[q] == 0 && score[q] >= low) { mask[q] = 1; stack.Push(q); }
                }
        }
        return mask;
    }

    // The largest 4-connected region of a mask.
    static byte[] Largest(byte[] mask, int w, int h)
    {
        var label = new int[mask.Length]; var stack = new Stack<int>(); int best = 0, bestSize = 0, next = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || label[start] != 0) continue;
            int id = ++next, size = 0; label[start] = id; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(), x = p % w, y = p / w; size++;
                void Visit(int q) { if (mask[q] != 0 && label[q] == 0) { label[q] = id; stack.Push(q); } }
                if (x > 0) Visit(p - 1); if (x < w - 1) Visit(p + 1); if (y > 0) Visit(p - w); if (y < h - 1) Visit(p + w);
            }
            if (size > bestSize) { bestSize = size; best = id; }
        }
        var result = new byte[mask.Length];
        for (int i = 0; i < result.Length; i++) if (label[i] == best && best != 0) result[i] = 1;
        return result;
    }

    // Background reachable from the image border stays background; enclosed holes become mask.
    static void FillHoles(byte[] mask, int w, int h)
    {
        var outside = new byte[mask.Length]; var stack = new Stack<int>();
        void Seed(int p) { if (mask[p] == 0 && outside[p] == 0) { outside[p] = 1; stack.Push(p); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (stack.Count > 0)
        {
            int p = stack.Pop(), x = p % w, y = p / w;
            if (x > 0) Seed(p - 1); if (x < w - 1) Seed(p + 1); if (y > 0) Seed(p - w); if (y < h - 1) Seed(p + w);
        }
        for (int i = 0; i < mask.Length; i++) if (outside[i] == 0) mask[i] = 1;
    }

    static bool Inside(byte[] mask, int w, int h, int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x] != 0;

    // Pixel corners of the region's boundary pixels: their convex hull is the region's hull.
    static List<Point> BoundaryCorners(byte[] mask, int w, int h)
    {
        var points = new List<Point>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (mask[y * w + x] == 0) continue;
                if (Inside(mask, w, h, x - 1, y) && Inside(mask, w, h, x + 1, y) && Inside(mask, w, h, x, y - 1) && Inside(mask, w, h, x, y + 1)) continue;
                points.Add(new(x, y)); points.Add(new(x + 1, y)); points.Add(new(x, y + 1)); points.Add(new(x + 1, y + 1));
            }
        return points;
    }

    // Midpoints of the pixel edges between the region and the outside: the region's outline.
    static List<Point> EdgePoints(byte[] mask, int w, int h)
    {
        var points = new List<Point>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (mask[y * w + x] == 0) continue;
                if (!Inside(mask, w, h, x - 1, y)) points.Add(new(x, y + .5));
                if (!Inside(mask, w, h, x + 1, y)) points.Add(new(x + 1, y + .5));
                if (!Inside(mask, w, h, x, y - 1)) points.Add(new(x + .5, y));
                if (!Inside(mask, w, h, x, y + 1)) points.Add(new(x + .5, y + 1));
            }
        return points;
    }

    static double Cross(Vector a, Vector b) => a.X * b.Y - a.Y * b.X;

    // Andrew's monotone chain; the hull is returned in one consistent turning direction.
    static List<Point> Hull(List<Point> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        var hull = new List<Point>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (var p in sorted)
            {
                while (hull.Count >= start + 2 && Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            sorted.Reverse();
        }
        return hull;
    }

    // Reduces a convex polygon to four sides by repeatedly dropping the side whose removal (extending its
    // two neighbours until they meet) adds the least area. Null when no quadrilateral remains.
    internal static Point[]? ReduceToQuad(List<Point> polygon)
    {
        var p = polygon.ToList();
        while (p.Count > 4)
        {
            int m = p.Count, best = -1; double bestArea = double.MaxValue; Point bestPoint = default;
            for (int i = 0; i < m; i++)
            {
                Point a = p[(i - 1 + m) % m], b = p[i], c = p[(i + 1) % m], d = p[(i + 2) % m];
                var ab = b - a; var dc = c - d;
                double denominator = Cross(ab, dc);
                if (Math.Abs(denominator) < 1e-9) continue;
                // Intersection of line a→b with line d→c.
                double t = Cross(d - a, dc) / denominator;
                var x = a + ab * t;
                if (t < 1 - 1e-9 || Vector.Multiply(x - d, dc) < dc.LengthSquared * (1 - 1e-9)) continue;
                double added = Math.Abs(Cross(c - b, x - b)) / 2;
                if (added < bestArea) { bestArea = added; best = i; bestPoint = x; }
            }
            if (best < 0)
            {
                // No side can be dropped by extension (a nearly triangular outline): drop the flattest corner.
                int flattest = 0; double smallest = double.MaxValue;
                for (int i = 0; i < m; i++)
                {
                    double area = Math.Abs(Cross(p[i] - p[(i - 1 + m) % m], p[(i + 1) % m] - p[i])) / 2;
                    if (area < smallest) { smallest = area; flattest = i; }
                }
                p.RemoveAt(flattest);
                continue;
            }
            p[best] = bestPoint; p.RemoveAt((best + 1) % m);
        }
        return p.Count == 4 ? p.ToArray() : null;
    }

    // Refits each side to the outline points that lie along it (total least squares) and intersects the
    // neighbouring sides again. Null when a side has too few points or the result is not a quadrilateral.
    static Point[]? RefitSides(Point[] quad, List<Point> outline)
    {
        var lines = new (Point Origin, Vector Direction)[4];
        for (int k = 0; k < 4; k++)
        {
            Point a = quad[k], b = quad[(k + 1) % 4]; var side = b - a; double length = side.Length;
            if (length < 4) return null;
            var u = side / length; var n = new Vector(-u.Y, u.X);
            var near = outline.Where(e => { var v = e - a; double t = Vector.Multiply(v, u); return Math.Abs(Vector.Multiply(v, n)) <= 2.5 && t >= length * .1 && t <= length * .9; }).ToList();
            lines[k] = near.Count >= 10 ? FitLine(near) : (a, u);
        }
        var result = new Point[4];
        for (int k = 0; k < 4; k++)
        {
            if (Intersect(lines[(k + 3) % 4], lines[k]) is not { } corner || (corner - quad[k]).Length > 12) return null;
            result[k] = corner;
        }
        return result;
    }

    // Least-squares line through points (principal direction through their centroid).
    static (Point Origin, Vector Direction) FitLine(IReadOnlyList<Point> points)
    {
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y), xx = 0, xy = 0, yy = 0;
        foreach (var p in points) { double dx = p.X - mx, dy = p.Y - my; xx += dx * dx; xy += dx * dy; yy += dy * dy; }
        double angle = .5 * Math.Atan2(2 * xy, xx - yy);
        return (new Point(mx, my), new Vector(Math.Cos(angle), Math.Sin(angle)));
    }

    static Point? Intersect((Point Origin, Vector Direction) a, (Point Origin, Vector Direction) b)
    {
        double denominator = Cross(a.Direction, b.Direction);
        if (Math.Abs(denominator) < 1e-6) return null;
        double t = Cross(b.Origin - a.Origin, b.Direction) / denominator;
        return a.Origin + a.Direction * t;
    }

    // Top-left, top-right, bottom-right, bottom-left: clockwise on screen, starting with the corner whose
    // following side points most nearly to the right (the sheet's top edge).
    internal static Point[] Order(Point[] quad)
    {
        var points = SignedArea(quad) < 0 ? quad.Reverse().ToArray() : quad.ToArray();
        int start = 0; double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var side = points[(i + 1) % 4] - points[i];
            double angle = Math.Abs(Math.Atan2(side.Y, side.X));
            if (angle < best) { best = angle; start = i; }
        }
        return Enumerable.Range(0, 4).Select(i => points[(start + i) % 4]).ToArray();
    }

    // Moves each side onto the strongest step from bright paper (inside) to the darker surroundings,
    // searched along the side's normal in the full-resolution photo, and intersects the refitted sides.
    // Null when too few steps are found or the refit strays from the coarse sides.
    static Point[]? RefineAtFullResolution(Raster photo, Point[] quad, double scale, CancellationToken token)
    {
        double reach = Math.Max(3, 2 * scale), along = Math.Max(1, scale * .75);
        var center = new Point(quad.Average(p => p.X), quad.Average(p => p.Y));
        var lines = new (Point Origin, Vector Direction)[4];
        for (int k = 0; k < 4; k++)
        {
            token.ThrowIfCancellationRequested();
            Point a = quad[k], b = quad[(k + 1) % 4]; var side = b - a; double length = side.Length;
            if (length < 8) return null;
            var u = side / length; var n = new Vector(-u.Y, u.X);
            if (Vector.Multiply(a + side / 2 - center, n) < 0) n = -n; // outward
            var found = new List<Point>();
            const int samples = 32;
            for (int j = 0; j < samples; j++)
            {
                var basePoint = a + side * (.12 + .76 * j / (samples - 1));
                double Profile(double s)
                {
                    double sum = 0;
                    for (int t = -2; t <= 2; t++) { var q = basePoint + n * s + u * (t * along / 2); sum += Luminance(photo, q.X, q.Y); }
                    return sum / 5;
                }
                double bestStep = 0, bestAt = 0;
                var steps = new List<(double At, double Step)>();
                for (double s = -reach; s <= reach + 1e-9; s += .5)
                {
                    double step = Profile(s - 1.5) - Profile(s + 1.5);
                    steps.Add((s, step));
                    if (step > bestStep) { bestStep = step; bestAt = s; }
                }
                if (bestStep < .035) continue;
                // Parabola through the peak and its neighbours for a sub-sample position.
                int index = steps.FindIndex(e => e.At == bestAt);
                if (index > 0 && index < steps.Count - 1)
                {
                    double l = steps[index - 1].Step, c = steps[index].Step, r = steps[index + 1].Step, curvature = l - 2 * c + r;
                    if (curvature < 0) bestAt += .5 * .5 * (l - r) / curvature;
                }
                found.Add(basePoint + n * bestAt);
            }
            if (found.Count < 8) return null;
            var line = FitLine(found);
            for (int round = 0; round < 2; round++)
            {
                var normal = new Vector(-line.Direction.Y, line.Direction.X);
                var kept = found.OrderBy(p => Math.Abs(Vector.Multiply(p - line.Origin, normal))).Take(Math.Max(6, (int)(found.Count * .7))).ToList();
                line = FitLine(kept);
            }
            double turn = Math.Abs(Cross(line.Direction, u));
            var middle = a + side / 2;
            double shift = Math.Abs(Vector.Multiply(middle - line.Origin, new Vector(-line.Direction.Y, line.Direction.X)));
            if (turn > Math.Sin(3 * Math.PI / 180) || shift > reach) return null;
            lines[k] = line;
        }
        var result = new Point[4];
        for (int k = 0; k < 4; k++)
        {
            if (Intersect(lines[(k + 3) % 4], lines[k]) is not { } corner || (corner - quad[k]).Length > reach * 3) return null;
            result[k] = corner;
        }
        return SignedArea(result) > 0 ? result : null;
    }

    static double Luminance(Raster image, double px, double py)
    {
        double sx = Math.Clamp(px - .5, 0, image.Width - 1), sy = Math.Clamp(py - .5, 0, image.Height - 1);
        int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(image.Width - 1, x0 + 1), y1 = Math.Min(image.Height - 1, y0 + 1);
        double fx = sx - x0, fy = sy - y0;
        double At(int x, int y)
        {
            int i = (y * image.Width + x) * 4; double a = image.Data[i + 3] / 255d, paper = 255 * (1 - a);
            return (.0722 * (image.Data[i] * a + paper) + .7152 * (image.Data[i + 1] * a + paper) + .2126 * (image.Data[i + 2] * a + paper)) / 255;
        }
        return (At(x0, y0) * (1 - fx) + At(x1, y0) * fx) * (1 - fy) + (At(x0, y1) * (1 - fx) + At(x1, y1) * fx) * fy;
    }

    // 0..1: the sheet's share of the frame (not a sliver, not the whole frame), paper brighter than its
    // surroundings, the quadrilateral matching the found region, and a plausible shape.
    static double Confidence(Point[] corners, int width, int height, float[] score, byte[] mask, int w, int h, double sx, double sy)
    {
        double share = Math.Abs(SignedArea(corners)) / ((double)width * height);
        double size = Math.Clamp((share - .06) / .09, 0, 1) * Math.Clamp((.98 - share) / .04, 0, 1);
        var work = corners.Select(p => new Point(p.X / sx, p.Y / sy)).ToArray();
        double Inside(Point p)
        {
            // Signed distance to the quadrilateral's sides (positive inside, clockwise order).
            double distance = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                var a = work[k]; var side = work[(k + 1) % 4] - a; side.Normalize();
                distance = Math.Min(distance, Cross(side, p - a));
            }
            return distance;
        }
        double inSum = 0, outSum = 0; long inCount = 0, outCount = 0, overlap = 0, union = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Inside(new Point(x + .5, y + .5)); int i = y * w + x;
                bool quad = d >= 0, region = mask[i] != 0;
                if (quad && region) overlap++;
                if (quad || region) union++;
                if (d >= 3) { inSum += score[i]; inCount++; }
                else if (d <= -3 && d >= -14) { outSum += score[i]; outCount++; }
            }
        double fit = union == 0 ? 0 : Math.Clamp((overlap / (double)union - .8) / .12, 0, 1);
        double contrast = inCount == 0 ? 0 : outCount == 0 ? 1 : Math.Clamp((inSum / inCount - outSum / outCount - .06) / .12, 0, 1);
        double shape = 1;
        for (int k = 0; k < 4; k++)
        {
            var a = corners[(k + 3) % 4] - corners[k]; var b = corners[(k + 1) % 4] - corners[k];
            double angle = Vector.AngleBetween(a, b);
            if (Math.Abs(angle) is < 35 or > 145) shape = 0;
        }
        static double Length(Point a, Point b) => (a - b).Length;
        double shortest = Enumerable.Range(0, 4).Min(k => Length(corners[k], corners[(k + 1) % 4])), longest = Enumerable.Range(0, 4).Max(k => Length(corners[k], corners[(k + 1) % 4]));
        if (shortest < longest * .2) shape = 0;
        int onBorder = corners.Count(p => p.X <= 1.5 * sx || p.Y <= 1.5 * sy || p.X >= width - 1.5 * sx || p.Y >= height - 1.5 * sy);
        double border = onBorder >= 3 ? 0 : onBorder == 2 ? .6 : 1;
        return size * fit * contrast * shape * border;
    }
}
