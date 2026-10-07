using System.Windows;

namespace Compositor.Windows;

/// <summary>One closed area of a drawing in map pixels: its size, the radius of the largest circle that fits, bounds and centre.</summary>
public sealed record RegionInfo(int Label, int Area, double Radius, Int32Rect Bounds, Point Center);

/// <summary>
/// Closed areas of a line drawing on a raster map. Labels: 0 is ink, -1 is the outside (every
/// area that reaches the border) and k ≥ 1 is region k (<see cref="Regions"/>[k - 1]).
/// <see cref="Scale"/> is map pixels per document pixel.
/// </summary>
public sealed class RegionMap(int width, int height, double scale, int[] labels, bool[] ink, IReadOnlyList<RegionInfo> regions)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double Scale { get; } = scale;
    public int[] Labels { get; } = labels;
    public bool[] Ink { get; } = ink;
    public IReadOnlyList<RegionInfo> Regions { get; } = regions;
}

// Closed areas (rooms, wall cavities, columns) found from the line work alone: ink pixels of a
// render, connected areas between them, the outside ignored. Sizes use the inscribed radius
// (a chamfer distance to the nearest line) so thin wall cavities and small columns can be told
// apart from rooms; neighbours are found across the lines between areas; outlines are traced
// with marching squares, grown under the lines they touch and simplified for material regions.
public static class RegionDetection
{
    /// <summary>Ink of a rendered drawing: dark, opaque pixels. White paper and transparency are not ink.</summary>
    public static bool[] Ink(Raster image, double threshold = .22)
    {
        var ink = new bool[image.Width * image.Height]; var data = image.Data;
        Parallel.For(0, image.Height, y =>
        {
            for (int x = 0, i = y * image.Width, p = i * 4; x < image.Width; x++, i++, p += 4)
            {
                double alpha = data[p + 3] / 255d, luminance = (.0722 * data[p] + .7152 * data[p + 1] + .2126 * data[p + 2]) / 255d;
                ink[i] = alpha * (1 - luminance) >= threshold;
            }
        });
        return ink;
    }

    /// <summary>Thickens ink by one pixel (8-neighbourhood) so hairline gaps where lines nearly meet still close an area.</summary>
    public static bool[] Close(bool[] ink, int width, int height)
    {
        var result = (bool[])ink.Clone();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x; if (ink[i]) continue;
                for (int dy = -1; dy <= 1 && !result[i]; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx; if (xx < 0 || xx >= width) continue;
                        if (ink[yy * width + xx]) { result[i] = true; break; }
                    }
                }
            }
        });
        return result;
    }

    public static RegionMap Find(bool[] ink, int width, int height, double scale, CancellationToken token = default)
    {
        if (ink.Length != width * height) throw new ArgumentException("잉크 크기가 맞지 않습니다.", nameof(ink));
        var labels = new int[ink.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = ink[i] ? 0 : int.MinValue;
        var regions = new List<(int Area, int Left, int Top, int Right, int Bottom, double Sx, double Sy, bool Outside)>();
        var stack = new Stack<int>();
        for (int start = 0; start < labels.Length; start++)
        {
            if (labels[start] != int.MinValue) continue;
            if ((regions.Count & 255) == 0) token.ThrowIfCancellationRequested();
            int label = regions.Count + 1, area = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1; double sx = 0, sy = 0; bool outside = false;
            labels[start] = label; stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % width, y = i / width;
                area++; sx += x; sy += y;
                if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1) outside = true;
                if (x > 0 && labels[i - 1] == int.MinValue) { labels[i - 1] = label; stack.Push(i - 1); }
                if (x < width - 1 && labels[i + 1] == int.MinValue) { labels[i + 1] = label; stack.Push(i + 1); }
                if (y > 0 && labels[i - width] == int.MinValue) { labels[i - width] = label; stack.Push(i - width); }
                if (y < height - 1 && labels[i + width] == int.MinValue) { labels[i + width] = label; stack.Push(i + width); }
            }
            regions.Add((area, left, top, right, bottom, sx, sy, outside));
        }
        // Areas that reach the border are the outside; the others are renumbered 1..n.
        var renumber = new int[regions.Count + 1]; int next = 0;
        for (int k = 0; k < regions.Count; k++) renumber[k + 1] = regions[k].Outside ? -1 : ++next;
        for (int i = 0; i < labels.Length; i++) if (labels[i] > 0) labels[i] = renumber[labels[i]];
        var radius = InscribedRadius(labels, width, height, next, token);
        var infos = new List<RegionInfo>(next);
        for (int k = 0; k < regions.Count; k++)
        {
            var r = regions[k]; if (r.Outside) continue;
            int label = renumber[k + 1];
            infos.Add(new RegionInfo(label, r.Area, radius[label], new Int32Rect(r.Left, r.Top, r.Right - r.Left + 1, r.Bottom - r.Top + 1), new Point(r.Sx / r.Area, r.Sy / r.Area)));
        }
        return new RegionMap(width, height, scale, labels, ink, infos);
    }

    // Largest chamfer (3-4) distance from each region's pixels to a pixel of another label, in pixels.
    static double[] InscribedRadius(int[] labels, int width, int height, int count, CancellationToken token)
    {
        var distance = new int[labels.Length]; const int Far = int.MaxValue / 4;
        for (int i = 0; i < distance.Length; i++) distance[i] = labels[i] > 0 ? Far : 0;
        int Get(int x, int y) => x < 0 || y < 0 || x >= width || y >= height ? 0 : distance[y * width + x];
        for (int y = 0; y < height; y++)
        {
            if ((y & 127) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x; if (distance[i] == 0) continue;
                distance[i] = Math.Min(distance[i], Math.Min(Math.Min(Get(x - 1, y) + 3, Get(x, y - 1) + 3), Math.Min(Get(x - 1, y - 1) + 4, Get(x + 1, y - 1) + 4)));
            }
        }
        for (int y = height - 1; y >= 0; y--)
        {
            if ((y & 127) == 0) token.ThrowIfCancellationRequested();
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x; if (distance[i] == 0) continue;
                distance[i] = Math.Min(distance[i], Math.Min(Math.Min(Get(x + 1, y) + 3, Get(x, y + 1) + 3), Math.Min(Get(x + 1, y + 1) + 4, Get(x - 1, y + 1) + 4)));
            }
        }
        var radius = new double[count + 1];
        for (int i = 0; i < labels.Length; i++) if (labels[i] > 0) radius[labels[i]] = Math.Max(radius[labels[i]], distance[i] / 3d);
        return radius;
    }

    /// <summary>Pairs of regions (smaller label first) separated only by a line at most <paramref name="reach"/> pixels thick.</summary>
    public static HashSet<(int, int)> Neighbors(RegionMap map, int reach, CancellationToken token = default)
    {
        int width = map.Width, height = map.Height; var labels = map.Labels;
        var owner = new int[labels.Length]; var depth = new int[labels.Length];
        var queue = new Queue<int>(); var pairs = new HashSet<(int, int)>();
        for (int i = 0; i < labels.Length; i++) { owner[i] = labels[i] > 0 ? labels[i] : 0; if (labels[i] > 0) queue.Enqueue(i); }
        void Visit(int from, int to)
        {
            if (labels[to] < 0) return;
            if (owner[to] == 0)
            {
                if (depth[from] >= reach) return;
                owner[to] = owner[from]; depth[to] = depth[from] + 1; queue.Enqueue(to);
            }
            else if (owner[to] != owner[from]) pairs.Add((Math.Min(owner[to], owner[from]), Math.Max(owner[to], owner[from])));
        }
        int steps = 0;
        while (queue.Count > 0)
        {
            if ((++steps & 65535) == 0) token.ThrowIfCancellationRequested();
            int i = queue.Dequeue(), x = i % width, y = i / width;
            if (x > 0) Visit(i, i - 1); if (x < width - 1) Visit(i, i + 1);
            if (y > 0) Visit(i, i - width); if (y < height - 1) Visit(i, i + width);
        }
        return pairs;
    }

    /// <summary>
    /// Outlines of the given regions in document pixels: the regions grown <paramref name="grow"/> pixels
    /// into the ink around them (so a fill reaches under the lines), traced, simplified to
    /// <paramref name="tolerance"/> map pixels. Outer outlines and holes alternate (even-odd fill).
    /// </summary>
    public static List<Point[]> Outlines(RegionMap map, IReadOnlyCollection<int> regions, int grow, double tolerance, CancellationToken token = default)
    {
        int width = map.Width, height = map.Height; var labels = map.Labels; var chosen = regions.ToHashSet();
        var mask = new bool[labels.Length];
        for (int i = 0; i < labels.Length; i++) mask[i] = labels[i] > 0 && chosen.Contains(labels[i]);
        for (int step = 0; step < grow; step++)
        {
            token.ThrowIfCancellationRequested();
            var next = (bool[])mask.Clone();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = y * width + x; if (mask[i] || labels[i] != 0) continue;
                if (x > 0 && mask[i - 1] || x < width - 1 && mask[i + 1] || y > 0 && mask[i - width] || y < height - 1 && mask[i + width]) next[i] = true;
            }
            mask = next;
        }
        var loops = Trace(mask, width, height, token);
        var result = new List<Point[]>(loops.Count);
        foreach (var loop in loops)
        {
            var simple = Simplify(loop, tolerance);
            if (simple.Count < 3 || Math.Abs(SignedArea(simple)) < 2) continue;
            result.Add(simple.Select(p => new Point(p.X / map.Scale, p.Y / map.Scale)).ToArray());
        }
        return result;
    }

    // Marching squares on a binary mask with crossings at edge midpoints. Each loop is a list of
    // points in pixel-centre coordinates (pixel x covers x..x+1), closed implicitly.
    static List<List<Point>> Trace(bool[] mask, int width, int height, CancellationToken token)
    {
        var segments = new Dictionary<long, (Point Point, long Next)>();
        bool Value(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x];
        long Key(int x, int y, bool vertical) => (((long)(y + 1) * (width + 2) + x + 1) << 1) | (vertical ? 1L : 0L);
        for (int y = -1; y < height; y++)
        {
            if ((y & 127) == 0) token.ThrowIfCancellationRequested();
            for (int x = -1; x < width; x++)
            {
                bool a = Value(x, y), b = Value(x + 1, y), c = Value(x + 1, y + 1), d = Value(x, y + 1);
                int code = (a ? 1 : 0) | (b ? 2 : 0) | (c ? 4 : 0) | (d ? 8 : 0);
                if (code is 0 or 15) continue;
                (long Key, Point Point) Edge(int edge) => edge switch
                {
                    0 => (Key(x, y, false), new Point(x + 1, y + .5)),
                    1 => (Key(x + 1, y, true), new Point(x + 1.5, y + 1)),
                    2 => (Key(x, y + 1, false), new Point(x + 1, y + 1.5)),
                    _ => (Key(x, y, true), new Point(x + .5, y + 1))
                };
                void Segment(int from, int to) { var start = Edge(from); segments[start.Key] = (start.Point, Edge(to).Key); }
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
        var loops = new List<List<Point>>();
        foreach (long start in segments.Keys.ToArray())
        {
            if (!segments.ContainsKey(start)) continue;
            var points = new List<Point>(); long next = start;
            while (segments.Remove(next, out var segment))
            {
                points.Add(segment.Point); next = segment.Next;
                if (next == start) break;
            }
            if (points.Count >= 3) loops.Add(points);
        }
        return loops;
    }

    // Douglas–Peucker on a closed loop, split at its two farthest points.
    internal static List<Point> Simplify(IReadOnlyList<Point> loop, double tolerance)
    {
        if (loop.Count < 4 || tolerance <= 0) return loop.ToList();
        int far = 0; double best = -1;
        for (int i = 1; i < loop.Count; i++) { double d = (loop[i] - loop[0]).LengthSquared; if (d > best) { best = d; far = i; } }
        var keep = new bool[loop.Count]; keep[0] = keep[far] = true;
        // Spans to reduce; indices wrap, so the second half runs from `far` back to 0 through the end of the list.
        var pending = new Stack<(int From, int To)>(); pending.Push((0, far)); pending.Push((far, 0));
        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();
            int count = (to - from + loop.Count) % loop.Count; if (count < 2) continue;
            var a = loop[from]; var b = loop[to]; var ab = b - a; double length = ab.Length;
            int index = -1; double max = tolerance;
            for (int step = 1; step < count; step++)
            {
                int k = (from + step) % loop.Count; var p = loop[k];
                double d = length < 1e-9 ? (p - a).Length : Math.Abs(Vector.CrossProduct(ab, p - a)) / length;
                if (d > max) { max = d; index = k; }
            }
            if (index < 0) continue;
            keep[index] = true; pending.Push((from, index)); pending.Push((index, to));
        }
        var result = new List<Point>();
        for (int i = 0; i < loop.Count; i++) if (keep[i]) result.Add(loop[i]);
        return result;
    }

    internal static double SignedArea(IReadOnlyList<Point> loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; i++) { var a = loop[i]; var b = loop[(i + 1) % loop.Count]; sum += a.X * b.Y - b.X * a.Y; }
        return sum / 2;
    }
}
