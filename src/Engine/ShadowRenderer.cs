using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>8-bit coverage over a rectangle of a (possibly scaled) parent coordinate space.</summary>
public sealed class AlphaMap
{
    public AlphaMap(Int32Rect bounds, byte[]? data = null)
    {
        if (bounds.Width < 1 || bounds.Height < 1) throw new ArgumentOutOfRangeException(nameof(bounds));
        Bounds = bounds; Data = data ?? new byte[checked(bounds.Width * bounds.Height)];
        if (Data.Length != bounds.Width * bounds.Height) throw new ArgumentException("그림자 영역 크기가 맞지 않습니다.", nameof(data));
    }
    public Int32Rect Bounds { get; }
    public byte[] Data { get; }
    public int X => Bounds.X;
    public int Y => Bounds.Y;
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public byte At(int x, int y) => x < X || y < Y || x >= X + Width || y >= Y + Height ? (byte)0 : Data[(y - Y) * Width + x - X];
}

/// <summary>Source coverage in the parent space of the source layers, at <see cref="Scale"/> of its pixels.</summary>
public sealed record ShadowSilhouette(Guid? ParentId, int SpaceWidth, int SpaceHeight, double Scale, IReadOnlyList<AlphaMap> Items);

/// <summary>
/// Builds shadow layers from the rendered coverage of their source layers. Work is bounded by the
/// source and shadow extent inside the canvas, never by an unclipped shadow length. Large blurs run on
/// a reduced grid, and every pass observes cancellation so dialog previews can be replaced cheaply.
/// </summary>
public static class ShadowRenderer
{
    const byte ContactThreshold = 24, WallThreshold = 48;
    const string MissingSources = "그림자의 원본 레이어가 없습니다. 원본을 지웠다면 그림자를 새로 만드세요.";
    const string SameGroup = "같은 그룹 안의 레이어를 선택하세요. 그림자는 원본과 같은 그룹에 만들어집니다.";

    internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Right <= Left || Bottom <= Top;
        public Box Intersect(Box other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        public Box Union(Box other) => IsEmpty ? other : other.IsEmpty ? this : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
        public Box Inflate(int margin) => new(Left - margin, Top - margin, Right + margin, Bottom + margin);
        // Bilinear placement at a fractional offset touches floor(left + dx) to ceil(right + dx).
        public Box Offset(double dx, double dy) => new((int)Math.Floor(Left + dx), (int)Math.Floor(Top + dy), (int)Math.Ceiling(Right + dx), (int)Math.Ceiling(Bottom + dy));
        public Int32Rect Rect => new(Left, Top, Width, Height);
        public static Box Of(AlphaMap map) => new(map.X, map.Y, map.X + map.Width, map.Y + map.Height);
    }

    static ParallelOptions Options(CancellationToken token) => new() { CancellationToken = token };

    // ---- Sources ------------------------------------------------------------------------

    /// <summary>Selected layers that can cast a shadow, bottom to top. Groups include their children.</summary>
    public static Guid[] ResolveSources(Document doc, IEnumerable<Guid> selected)
    {
        ArgumentNullException.ThrowIfNull(doc); ArgumentNullException.ThrowIfNull(selected);
        var lookup = doc.Layers.ToDictionary(l => l.Id);
        var ids = selected.Where(lookup.ContainsKey).ToHashSet();
        bool Covered(Layer layer)
        {
            var parent = layer.ParentId;
            for (int depth = 0; parent is { } id && depth < 17; depth++)
            {
                if (ids.Contains(id)) return true;
                parent = lookup.TryGetValue(id, out var group) ? group.ParentId : null;
            }
            return false;
        }
        var sources = ids.Select(id => lookup[id]).Where(l => l.Shadow == null && l.Kind != LayerKind.Adjustment && !Covered(l)).ToList();
        if (sources.Count == 0) throw new InvalidOperationException("그림자를 만들 이미지·도형·텍스트·그룹 레이어를 선택하세요.");
        if (sources.Select(l => l.ParentId).Distinct().Count() > 1) throw new InvalidOperationException(SameGroup);
        if (sources.Count > ShadowSpec.MaxSources) throw new InvalidOperationException($"그림자는 한 번에 {ShadowSpec.MaxSources}개 레이어까지 만들 수 있습니다. 여러 레이어를 그룹으로 묶어 선택하세요.");
        var order = Order(doc);
        return sources.OrderBy(l => order[l.Id]).Select(l => l.Id).ToArray();
    }

    static Dictionary<Guid, int> Order(Document doc)
    {
        var order = new Dictionary<Guid, int>(doc.Layers.Count);
        for (int i = 0; i < doc.Layers.Count; i++) order[doc.Layers[i].Id] = i;
        return order;
    }

    // Identity parent chains share the document canvas; transformed groups keep their own surface.
    public static (int Width, int Height) SpaceSize(Document doc, Guid? parentId)
    {
        if (parentId is not { } id) return (doc.Width, doc.Height);
        var lookup = doc.Layers.ToDictionary(l => l.Id);
        if (!lookup.TryGetValue(id, out var parent)) throw new InvalidOperationException("그룹이 없습니다.");
        bool identity = true; Guid? current = id;
        for (int depth = 0; current is { } next && depth < 17 && lookup.TryGetValue(next, out var group); depth++, current = group.ParentId)
            identity &= group.Warp == null && group.Matrix.IsIdentity;
        return identity ? (doc.Width, doc.Height) : (parent.Pixels.Width, parent.Pixels.Height);
    }

    /// <summary>
    /// Renders the coverage of the sources in their parent's space (the canvas for top-level layers).
    /// Sources are drawn fully visible and opaque with normal blending: a shadow follows an object's
    /// form, not its current transparency. <paramref name="separate"/> keeps one map per source.
    /// </summary>
    public static ShadowSilhouette Silhouette(Document doc, IReadOnlyList<Guid> sourceIds, double scale, bool separate, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(doc); ArgumentNullException.ThrowIfNull(sourceIds);
        if (!double.IsFinite(scale) || scale < .001 || scale > 1) throw new ArgumentOutOfRangeException(nameof(scale));
        var lookup = doc.Layers.ToDictionary(l => l.Id); var order = Order(doc);
        var sources = sourceIds.Distinct().Where(lookup.ContainsKey).Select(id => lookup[id]).OrderBy(l => order[l.Id]).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException(MissingSources);
        var parentId = sources[0].ParentId;
        if (sources.Any(l => l.ParentId != parentId)) throw new InvalidOperationException(SameGroup);
        var (width, height) = SpaceSize(doc, parentId);
        var space = new Box(0, 0, Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)));
        var children = doc.Layers.Where(l => l.ParentId != null).ToLookup(l => l.ParentId!.Value);
        IEnumerable<Layer[]> groups = separate ? sources.Select(s => new[] { s }) : new[] { sources };
        var items = new List<AlphaMap>();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var bounds = Rect.Empty;
            foreach (var source in group) Accumulate(source, p => p, children, ref bounds, 0, true);
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width + bounds.Height)) continue;
            var box = new Box((int)Math.Floor(bounds.Left * scale) - 1, (int)Math.Floor(bounds.Top * scale) - 1,
                (int)Math.Ceiling(bounds.Right * scale) + 1, (int)Math.Ceiling(bounds.Bottom * scale) + 1).Intersect(space);
            if (box.IsEmpty) continue;
            var subtree = new Document { Width = width, Height = height, Name = doc.Name };
            foreach (var source in group) Include(subtree, source, children, 0, true);
            var alpha = group.Length == 1 && group[0].Kind is LayerKind.Raster or LayerKind.Shape ? CompositeAlpha(subtree.Layers[0], box, scale, token) : RenderAlpha(subtree, box, scale, token);
            if (Trim(alpha, box) is { } map) items.Add(map);
        }
        return new ShadowSilhouette(parentId, space.Width, space.Height, scale, items);
    }

    // Bounds of visible content: leaf corners mapped through the subtree to the parent space.
    static void Accumulate(Layer layer, Func<Point, Point> toSpace, ILookup<Guid, Layer> children, ref Rect bounds, int depth, bool root)
    {
        if (depth > 17 || !root && !layer.Visible || layer.Kind == LayerKind.Adjustment) return;
        Point Map(Point p) => toSpace(layer.Document(p));
        if (layer.Kind == LayerKind.Group)
        {
            foreach (var child in children[layer.Id]) Accumulate(child, Map, children, ref bounds, depth + 1, false);
            return;
        }
        foreach (var corner in new[] { new Point(0, 0), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) })
        {
            var p = Map(corner);
            if (double.IsFinite(p.X) && double.IsFinite(p.Y)) bounds.Union(p);
        }
    }

    static void Include(Document subtree, Layer layer, ILookup<Guid, Layer> children, int depth, bool root)
    {
        var copy = layer.Snapshot();
        if (root) { copy.ParentId = null; copy.Visible = true; copy.Opacity = 1; copy.Blend = BlendMode.Normal; copy.Clipped = false; }
        subtree.Layers.Add(copy);
        if (depth < 17) foreach (var child in children[layer.Id]) Include(subtree, child, children, depth + 1, false);
    }

    static byte[] CompositeAlpha(Layer layer, Box box, double scale, CancellationToken token)
    {
        var world = layer.Matrix; world.Append(new Matrix(scale, 0, 0, scale, -box.Left, -box.Top));
        var surface = new Raster(box.Width, box.Height);
        Imaging.Composite(surface, layer, token, world);
        var alpha = new byte[box.Width * box.Height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = surface.Data[i * 4 + 3];
        return alpha;
    }

    // The screen renderer draws retained content at the requested scale; tiles keep each surface bounded.
    static byte[] RenderAlpha(Document subtree, Box box, double scale, CancellationToken token)
    {
        const int Tile = 4096;
        var alpha = new byte[checked(box.Width * box.Height)];
        for (int ty = 0; ty < box.Height; ty += Tile)
            for (int tx = 0; tx < box.Width; tx += Tile)
            {
                token.ThrowIfCancellationRequested();
                int tw = Math.Min(Tile, box.Width - tx), th = Math.Min(Tile, box.Height - ty);
                var area = new Rect((box.Left + tx) / scale, (box.Top + ty) / scale, tw / scale, th / scale);
                var tile = DesignRenderer.Render(subtree, area, tw, th, token);
                for (int row = 0; row < th; row++)
                {
                    int source = row * tw * 4 + 3, target = (ty + row) * box.Width + tx;
                    for (int col = 0; col < tw; col++) alpha[target + col] = tile.Data[source + col * 4];
                }
            }
        return alpha;
    }

    static AlphaMap? Trim(byte[] data, Box box)
    {
        int w = box.Width, h = box.Height, left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            var line = data.AsSpan(y * w, w); int first = line.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            left = Math.Min(left, first); right = Math.Max(right, line.LastIndexOfAnyExcept((byte)0));
            top = Math.Min(top, y); bottom = y;
        }
        if (right < 0) return null;
        if (left == 0 && top == 0 && right == w - 1 && bottom == h - 1) return new AlphaMap(box.Rect, data);
        var map = new AlphaMap(new Int32Rect(box.Left + left, box.Top + top, right - left + 1, bottom - top + 1));
        for (int y = 0; y < map.Height; y++) Buffer.BlockCopy(data, (top + y) * w + left, map.Data, y * map.Width, map.Width);
        return map;
    }

    // ---- Casting ------------------------------------------------------------------------

    /// <summary>Shadow coverage (before color and opacity), clipped to the silhouette's space.</summary>
    public static AlphaMap? Cast(ShadowSilhouette silhouette, ShadowSpec spec, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(silhouette); ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        double s = silhouette.Scale; var space = new Box(0, 0, silhouette.SpaceWidth, silhouette.SpaceHeight);
        bool realistic = spec.Style == ShadowStyle.Realistic;
        double soft = realistic ? spec.Softness * s : 0, angle = spec.Angle * Math.PI / 180, ux = Math.Cos(angle), uy = Math.Sin(angle);
        var parts = new List<AlphaMap>();
        foreach (var item in silhouette.Items)
        {
            token.ThrowIfCancellationRequested();
            var source = spec.FillClosed ? FillClosed(item, token) : item;
            var cast = spec.Projection switch
            {
                ShadowProjection.Plan => Plan(source, ux, uy, spec.PlanLength() * s, soft, space, token),
                ShadowProjection.Ground => Ground(source, ux, uy, spec.GroundRatio(), soft, space, token),
                _ => Drop(source, ux * spec.Distance * s, uy * spec.Distance * s, soft, space, token)
            };
            if (cast != null) parts.Add(cast);
        }
        if (parts.Count == 0) return null;
        var result = parts.Count == 1 ? parts[0] : Combine(parts);
        if (!realistic && spec.OutlineOnly) result = Outline(result, Math.Max(1, (int)Math.Round(spec.OutlineWidth * s)), token);
        return result;
    }

    static AlphaMap Combine(List<AlphaMap> parts)
    {
        var box = parts.Select(Box.Of).Aggregate((a, b) => a.Union(b));
        var output = new AlphaMap(box.Rect);
        foreach (var part in parts) MaxInto(output, part);
        return output;
    }

    static int BlurMargin(double radius) => radius <= 0 ? 0 : (int)Math.Ceiling(radius * 1.5) + 2;

    // A lifted object: one offset copy blurred by the softness.
    static AlphaMap? Drop(AlphaMap a, double dx, double dy, double soft, Box space, CancellationToken token)
    {
        var box = Box.Of(a).Offset(dx, dy).Inflate(BlurMargin(soft)).Intersect(space);
        if (box.IsEmpty) return null;
        var output = new AlphaMap(box.Rect);
        Shift(a, output, dx, dy, false, token);
        Blur(output, soft / 2, token);
        return output;
    }

    // A footprint swept along the sun direction. The sweep also records each pixel's distance from the
    // object along the direction; partition-of-unity distance bands are blurred more the farther they
    // lie, then added up. Edges stay sharp where the shadow leaves the object and soften toward its end.
    static AlphaMap? Plan(AlphaMap a, double ux, double uy, double length, double soft, Box space, CancellationToken token)
    {
        if (length < .5) return Drop(a, 0, 0, soft, space, token);
        int bands = soft > 1 ? Math.Clamp((int)Math.Ceiling(soft / 2), 2, 16) : 1;
        var box = Box.Of(a).Union(Box.Of(a).Offset(ux * length, uy * length)).Inflate(BlurMargin(soft) + 1).Intersect(space);
        if (box.IsEmpty) return null;
        var sweep = Sweep(a, box, ux, uy, length, bands > 1, token);
        if (bands == 1) { Blur(sweep.Coverage, soft / 2, token); return sweep.Coverage; }
        int w = box.Width, h = box.Height; var coverage = sweep.Coverage.Data; var distance = sweep.Distance!;
        // The sweep's spare planes become the band plane and the running sum.
        var band = sweep.SpareCoverage; var sum = sweep.SpareDistance!; Array.Clear(sum);
        var temp = new byte[coverage.Length];
        double spacing = length / (bands - 1), unit = sweep.Step / spacing;
        for (int k = 0; k < bands; k++)
        {
            token.ThrowIfCancellationRequested();
            double radius = soft * k / (bands - 1), center = spacing * k;
            var bandBox = Box.Of(a).Offset(ux * (center - spacing), uy * (center - spacing)).Union(Box.Of(a).Offset(ux * (center + spacing), uy * (center + spacing)))
                .Inflate(BlurMargin(radius) + 2).Intersect(box);
            if (bandBox.IsEmpty) continue;
            int bw = bandBox.Width, bh = bandBox.Height, band0 = k;
            Parallel.For(0, bh, Options(token), row =>
            {
                int source = (bandBox.Top - box.Top + row) * w + bandBox.Left - box.Left, target = row * bw;
                for (int x = 0; x < bw; x++)
                {
                    int i = source + x; byte value = coverage[i]; double weight = 0;
                    if (value != 0)
                    {
                        int steps = distance[i];
                        // Anti-aliased fringe pixels outside the nearest-neighbor distance take a neighbor's distance.
                        if (steps == ushort.MaxValue) steps = Neighbor(distance, i, w, h);
                        weight = steps == ushort.MaxValue ? (band0 == 0 ? 1 : 0) : Math.Max(0, 1 - Math.Abs(steps * unit - band0));
                    }
                    band[target + x] = (byte)(value * weight + .5);
                }
            });
            Blur(band, bw, bh, radius / 2, temp, token);
            Parallel.For(0, bh, Options(token), row =>
            {
                int target = (bandBox.Top - box.Top + row) * w + bandBox.Left - box.Left, source = row * bw;
                for (int x = 0; x < bw; x++) sum[target + x] += band[source + x];
            });
        }
        Parallel.For(0, h, Options(token), row =>
        {
            for (int i = row * w, end = i + w; i < end; i++) coverage[i] = (byte)Math.Min(255, (int)sum[i]);
        });
        return sweep.Coverage;
    }

    static int Neighbor(ushort[] distance, int i, int w, int h)
    {
        int x = i % w, best = ushort.MaxValue;
        if (x > 0) best = Math.Min(best, distance[i - 1]);
        if (x + 1 < w) best = Math.Min(best, distance[i + 1]);
        if (i >= w) best = Math.Min(best, distance[i - w]);
        if (i + w < distance.Length) best = Math.Min(best, distance[i + w]);
        return best;
    }

    sealed record SweepResult(AlphaMap Coverage, ushort[]? Distance, byte[] SpareCoverage, ushort[]? SpareDistance, double Step);

    // Union of every offset t·u for t in [0, length] by doubling: log2(length) shift-max passes.
    // With distances, a nearest-neighbor min pass records the first offset (in steps) that covers a pixel.
    static SweepResult Sweep(AlphaMap a, Box box, double ux, double uy, double length, bool distances, CancellationToken token)
    {
        int n = Math.Max(1, (int)Math.Ceiling(length - 1e-6)); double step = length / n;
        var current = new AlphaMap(box.Rect); var next = new AlphaMap(box.Rect);
        Shift(a, current, 0, 0, false, token);
        ushort[]? reach = null, spare = null;
        if (distances)
        {
            reach = new ushort[current.Data.Length]; spare = new ushort[current.Data.Length];
            for (int i = 0; i < reach.Length; i++) reach[i] = current.Data[i] == 0 ? ushort.MaxValue : (ushort)0;
        }
        long count = 1; // current covers offsets 0 .. count-1 steps
        void Pass(long offset)
        {
            double dx = ux * step * offset, dy = uy * step * offset;
            Buffer.BlockCopy(current.Data, 0, next.Data, 0, current.Data.Length);
            Shift(current, next, dx, dy, true, token);
            (current, next) = (next, current);
            if (reach == null) return;
            Buffer.BlockCopy(reach, 0, spare!, 0, reach.Length * sizeof(ushort));
            ShiftMin(reach, spare!, box.Width, box.Height, (int)Math.Round(dx), (int)Math.Round(dy), (int)Math.Min(offset, ushort.MaxValue - 1), token);
            (reach, spare) = (spare, reach);
        }
        while (count * 2 <= n + 1) { Pass(count); count *= 2; }
        if (count < n + 1) Pass(n + 1 - count);
        return new SweepResult(current, reach, next.Data, spare, step);
    }

    // target(p) = min(target(p), source(p - o) + add); target starts as a copy of source.
    static void ShiftMin(ushort[] source, ushort[] target, int w, int h, int ox, int oy, int add, CancellationToken token)
    {
        Parallel.For(0, h, Options(token), y =>
        {
            int sy = y - oy; if (sy < 0 || sy >= h) return;
            int row = y * w, sourceRow = sy * w;
            for (int x = Math.Max(0, ox); x < Math.Min(w, w + ox); x++)
            {
                int value = source[sourceRow + x - ox];
                if (value == ushort.MaxValue) continue;
                int reached = Math.Min(ushort.MaxValue - 1, value + add);
                if (reached < target[row + x]) target[row + x] = (ushort)reached;
            }
        });
    }

    // A standing object laid on the floor: each row at height h above the contact line moves by
    // h·ratio along the direction. Partition-of-unity height bands blur more the farther they are
    // from the contact line, then add up to one continuous, contact-hardened shadow.
    static AlphaMap? Ground(AlphaMap a, double ux, double uy, double ratio, double soft, Box space, CancellationToken token)
    {
        int baseRow = a.Height - 1;
        while (baseRow > 0 && !RowHas(a, baseRow, ContactThreshold)) baseRow--;
        double contact = a.Y + baseRow + 1, height = Math.Max(1, contact - a.Y);
        double shear = ratio * ux, lift = ratio * uy, minimum = .06 * ratio;
        // A horizontal sun direction would flatten the shadow to a line; keep a thin receding band.
        if (Math.Abs(lift) < minimum) lift = lift > 0 ? minimum : -minimum;
        Box Footprint(double from, double to, int margin)
        {
            double xs0 = double.MaxValue, xs1 = double.MinValue, ys0 = double.MaxValue, ys1 = double.MinValue;
            foreach (double y in new[] { from, to })
                foreach (double x in new double[] { a.X, a.X + a.Width })
                {
                    double h = contact - y, u = x + h * shear, v = contact + h * lift;
                    xs0 = Math.Min(xs0, u); xs1 = Math.Max(xs1, u); ys0 = Math.Min(ys0, v); ys1 = Math.Max(ys1, v);
                }
            return new Box((int)Math.Floor(xs0) - 1, (int)Math.Floor(ys0) - 1, (int)Math.Ceiling(xs1) + 1, (int)Math.Ceiling(ys1) + 1).Inflate(margin);
        }
        var box = Footprint(a.Y, a.Y + a.Height, BlurMargin(soft)).Intersect(space);
        if (box.IsEmpty) return null;
        int bands = soft > 1 ? Math.Clamp((int)Math.Ceiling(soft / 2), 2, 16) : 1;
        if (bands == 1)
        {
            var flat = new AlphaMap(box.Rect);
            Lay(a, flat.Data, Box.Of(flat), contact, shear, lift, height, 0, 1, token);
            Blur(flat, soft / 2, token);
            return flat;
        }
        var sum = new ushort[box.Width * box.Height];
        var scratch = new byte[sum.Length]; var temp = new byte[sum.Length];
        for (int k = 0; k < bands; k++)
        {
            token.ThrowIfCancellationRequested();
            double center = height * k / (bands - 1), reach = height / (bands - 1), radius = soft * k / (bands - 1);
            var bandBox = Footprint(contact - center - reach, contact - center + reach, BlurMargin(radius)).Intersect(box);
            if (bandBox.IsEmpty) continue;
            Array.Clear(scratch, 0, bandBox.Width * bandBox.Height);
            Lay(a, scratch, bandBox, contact, shear, lift, height, k, bands, token);
            Blur(scratch, bandBox.Width, bandBox.Height, radius / 2, temp, token);
            for (int y = 0; y < bandBox.Height; y++)
            {
                int target = (bandBox.Top - box.Top + y) * box.Width + bandBox.Left - box.Left, source = y * bandBox.Width;
                for (int x = 0; x < bandBox.Width; x++) sum[target + x] += scratch[source + x];
            }
        }
        var output = new AlphaMap(box.Rect);
        for (int i = 0; i < sum.Length; i++) output.Data[i] = (byte)Math.Min(255, (int)sum[i]);
        return output;
    }

    static bool RowHas(AlphaMap map, int row, byte threshold)
    {
        int start = row * map.Width;
        for (int x = 0; x < map.Width; x++) if (map.Data[start + x] >= threshold) return true;
        return false;
    }

    // Inverse mapping per output row: the ground map keeps rows horizontal, so each output row reads
    // one (box-filtered) source row shifted by its shear. Compressed shadows average every row they cover.
    static void Lay(AlphaMap a, byte[] target, Box box, double contact, double shear, double lift, double height, int band, int bands, CancellationToken token)
    {
        double inverse = 1 / lift, bandScale = bands > 1 ? (bands - 1) / height : 0;
        Parallel.For(0, box.Height, Options(token), () => new double[a.Width], (row, _, line) =>
        {
            double v = box.Top + row + .5, h = (v - contact) * inverse;
            double weight = bands == 1 ? 1 : Math.Max(0, 1 - Math.Abs(h * bandScale - band));
            if (weight <= 0) return line;
            // Source rows covered by this output row (pixel-center coordinates, local indices).
            double h0 = (v - .5 - contact) * inverse, h1 = (v + .5 - contact) * inverse;
            double y0 = contact - Math.Max(h0, h1) - .5 - a.Y, y1 = contact - Math.Min(h0, h1) - .5 - a.Y;
            if (y1 < -1 || y0 > a.Height) return line;
            Array.Clear(line); double total = 1;
            if (y1 - y0 <= 1)
            {
                double center = (y0 + y1) / 2; int top = (int)Math.Floor(center); double f = center - top;
                if (top >= 0 && top < a.Height) AddRow(a, top, 1 - f, line);
                if (top + 1 >= 0 && top + 1 < a.Height) AddRow(a, top + 1, f, line);
            }
            else
            {
                int first = (int)Math.Ceiling(y0), last = (int)Math.Floor(y1);
                total = Math.Max(1, last - first + 1);
                for (int r = Math.Max(0, first); r <= Math.Min(a.Height - 1, last); r++) AddRow(a, r, 1, line);
            }
            double scale = weight / total, offset = h * shear;
            // Output x = source x + offset (pixel centers): sample the filtered line at x - offset.
            double start = box.Left + .5 - offset - .5 - a.X; int ix = (int)Math.Floor(start); double fx = start - ix;
            int destination = row * box.Width;
            for (int col = 0; col < box.Width; col++)
            {
                int x0 = ix + col;
                double value = (x0 >= 0 && x0 < a.Width ? line[x0] * (1 - fx) : 0) + (x0 + 1 >= 0 && x0 + 1 < a.Width ? line[x0 + 1] * fx : 0);
                if (value > 0) target[destination + col] = (byte)Math.Min(255, value * scale + .5);
            }
            return line;
        }, _ => { });
    }

    static void AddRow(AlphaMap a, int row, double weight, double[] line)
    {
        int start = row * a.Width;
        for (int x = 0; x < a.Width; x++) line[x] += a.Data[start + x] * weight;
    }

    // target(p) = source(p - d) with bilinear weights; with max, keeps the brighter of both.
    static void Shift(AlphaMap source, AlphaMap target, double dx, double dy, bool max, CancellationToken token) => Shift(source, target.Data, Box.Of(target), dx, dy, max, token);

    static void Shift(AlphaMap source, byte[] dst, Box targetBox, double dx, double dy, bool max, CancellationToken token)
    {
        double ox = -dx, oy = -dy; int ix = (int)Math.Floor(ox), iy = (int)Math.Floor(oy);
        int fx = (int)Math.Round((ox - ix) * 256), fy = (int)Math.Round((oy - iy) * 256);
        if (fx == 256) { ix++; fx = 0; }
        if (fy == 256) { iy++; fy = 0; }
        int w00 = (256 - fx) * (256 - fy), w10 = fx * (256 - fy), w01 = (256 - fx) * fy, w11 = fx * fy;
        int sw = source.Width, sh = source.Height, tw = targetBox.Width; var src = source.Data;
        Parallel.For(0, targetBox.Height, Options(token), row =>
        {
            int sy = targetBox.Top + row + iy - source.Y, destination = row * tw;
            if (sy < -1 || sy >= sh) { if (!max) Array.Clear(dst, destination, tw); return; }
            bool top = sy >= 0, bottom = sy + 1 < sh; int upper = sy * sw, lower = (sy + 1) * sw;
            for (int col = 0; col < tw; col++)
            {
                int sx = targetBox.Left + col + ix - source.X, value = 0;
                if (sx >= -1 && sx < sw)
                {
                    bool left = sx >= 0, right = sx + 1 < sw; int sum = 0;
                    if (top) { if (left) sum += src[upper + sx] * w00; if (right) sum += src[upper + sx + 1] * w10; }
                    if (bottom) { if (left) sum += src[lower + sx] * w01; if (right) sum += src[lower + sx + 1] * w11; }
                    value = (sum + 32768) >> 16;
                }
                if (!max) dst[destination + col] = (byte)value;
                else if (value > dst[destination + col]) dst[destination + col] = (byte)value;
            }
        });
    }

    static void MaxInto(AlphaMap target, AlphaMap part) => MaxInto(target, part.Data, Box.Of(part));

    static void MaxInto(AlphaMap target, byte[] part, Box partBox)
    {
        var box = Box.Of(target).Intersect(partBox);
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int t = (y - target.Y) * target.Width + box.Left - target.X, p = (y - partBox.Top) * partBox.Width + box.Left - partBox.Left;
            for (int x = 0; x < box.Width; x++) if (part[p + x] > target.Data[t + x]) target.Data[t + x] = part[p + x];
        }
    }

    // ---- Blur ---------------------------------------------------------------------------

    // Three box passes approximate a Gaussian of the given sigma. Large blurs run on a reduced grid.
    internal static void Blur(AlphaMap map, double sigma, CancellationToken token) => Blur(map.Data, map.Width, map.Height, sigma, null, token);

    // Works on the first w*h bytes of data; temp (if given) must hold as many.
    static void Blur(byte[] data, int w, int h, double sigma, byte[]? temp, CancellationToken token)
    {
        if (!(sigma >= .35)) return;
        int factor = sigma >= 6 ? Math.Min(16, (int)(sigma / 3)) : 1;
        if (factor > 1)
        {
            int sw = (w + factor - 1) / factor, sh = (h + factor - 1) / factor;
            var small = Downsample(data, w, h, factor, sw, sh, token);
            BoxBlur(small, sw, sh, sigma / factor, null, token);
            Upsample(small, sw, sh, factor, data, w, h, token);
        }
        else BoxBlur(data, w, h, sigma, temp, token);
    }

    static void BoxBlur(byte[] data, int w, int h, double sigma, byte[]? temp, CancellationToken token)
    {
        foreach (int radius in BoxRadii(sigma))
        {
            if (radius < 1) continue;
            temp ??= new byte[w * h];
            Horizontal(data, temp, w, h, radius, token); Vertical(temp, data, w, h, radius, token);
        }
    }

    static int[] BoxRadii(double sigma)
    {
        const int passes = 3;
        double ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        int lower = (int)Math.Floor(ideal); if (lower % 2 == 0) lower--; lower = Math.Max(1, lower);
        int upper = lower + 2;
        int count = (int)Math.Round((12 * sigma * sigma - passes * lower * lower - 4 * passes * lower - 3 * passes) / (-4.0 * lower - 4));
        return Enumerable.Range(0, passes).Select(i => ((i < count ? lower : upper) - 1) / 2).ToArray();
    }

    static void Horizontal(byte[] source, byte[] target, int w, int h, int radius, CancellationToken token)
    {
        int size = radius * 2 + 1, half = size / 2;
        Parallel.For(0, h, Options(token), y =>
        {
            int row = y * w, sum = 0;
            for (int x = 0; x < Math.Min(radius, w); x++) sum += source[row + x];
            for (int x = 0; x < w; x++)
            {
                if (x + radius < w) sum += source[row + x + radius];
                target[row + x] = (byte)((sum + half) / size);
                if (x - radius >= 0) sum -= source[row + x - radius];
            }
        });
    }

    static void Vertical(byte[] source, byte[] target, int w, int h, int radius, CancellationToken token)
    {
        const int Block = 64; int size = radius * 2 + 1, half = size / 2;
        Parallel.For(0, (w + Block - 1) / Block, Options(token), block =>
        {
            int x0 = block * Block, n = Math.Min(w, x0 + Block) - x0; var sum = new int[n];
            for (int y = 0; y < Math.Min(radius, h); y++) for (int i = 0; i < n; i++) sum[i] += source[y * w + x0 + i];
            for (int y = 0; y < h; y++)
            {
                if (y + radius < h) { int add = (y + radius) * w + x0; for (int i = 0; i < n; i++) sum[i] += source[add + i]; }
                int destination = y * w + x0; for (int i = 0; i < n; i++) target[destination + i] = (byte)((sum[i] + half) / size);
                if (y - radius >= 0) { int remove = (y - radius) * w + x0; for (int i = 0; i < n; i++) sum[i] -= source[remove + i]; }
            }
        });
    }

    static byte[] Downsample(byte[] data, int w, int h, int factor, int sw, int sh, CancellationToken token)
    {
        var small = new byte[sw * sh]; int area = factor * factor;
        Parallel.For(0, sh, Options(token), () => new int[sw], (sy, _, sums) =>
        {
            Array.Clear(sums);
            for (int y = sy * factor; y < Math.Min(h, (sy + 1) * factor); y++) { int row = y * w; for (int x = 0; x < w; x++) sums[x / factor] += data[row + x]; }
            for (int sx = 0; sx < sw; sx++) small[sy * sw + sx] = (byte)((sums[sx] + area / 2) / area);
            return sums;
        }, _ => { });
        return small;
    }

    static void Upsample(byte[] small, int sw, int sh, int factor, byte[] data, int w, int h, CancellationToken token)
    {
        var x0 = new int[w]; var x1 = new int[w]; var fx = new double[w];
        for (int x = 0; x < w; x++)
        {
            double sx = (x + .5) / factor - .5; int ix = (int)Math.Floor(sx);
            fx[x] = sx - ix; x0[x] = Math.Clamp(ix, 0, sw - 1); x1[x] = Math.Clamp(ix + 1, 0, sw - 1);
        }
        Parallel.For(0, h, Options(token), y =>
        {
            double sy = (y + .5) / factor - .5; int iy = (int)Math.Floor(sy); double fy = sy - iy;
            int upper = Math.Clamp(iy, 0, sh - 1) * sw, lower = Math.Clamp(iy + 1, 0, sh - 1) * sw, row = y * w;
            for (int x = 0; x < w; x++)
            {
                double top = small[upper + x0[x]] * (1 - fx[x]) + small[upper + x1[x]] * fx[x];
                double bottom = small[lower + x0[x]] * (1 - fx[x]) + small[lower + x1[x]] * fx[x];
                data[row + x] = (byte)(top * (1 - fy) + bottom * fy + .5);
            }
        });
    }

    // ---- Form helpers -------------------------------------------------------------------

    // Closed outlines become solid footprints: everything the outside cannot reach is filled.
    internal static AlphaMap FillClosed(AlphaMap a, CancellationToken token)
    {
        int w = a.Width, h = a.Height; var src = a.Data; var outside = new bool[src.Length];
        var stack = new Stack<int>();
        void Seed(int i) { if (!outside[i] && src[i] < WallThreshold) { outside[i] = true; stack.Push(i); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        long visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            int i = stack.Pop(), x = i % w;
            if (x > 0) Seed(i - 1);
            if (x + 1 < w) Seed(i + 1);
            if (i >= w) Seed(i - w);
            if (i + w < src.Length) Seed(i + w);
        }
        var result = new byte[src.Length];
        Parallel.For(0, h, Options(token), y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (outside[i]) { result[i] = src[i]; continue; }
                // Walls facing the outside keep their anti-aliased edge; enclosed pixels become solid.
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 || outside[i - 1] || outside[i + 1] || outside[i - w] || outside[i + w];
                result[i] = edge ? src[i] : (byte)255;
            }
        });
        return new AlphaMap(a.Bounds, result);
    }

    // An inner stroke of the given width: coverage minus its erosion (van Herk/Gil-Werman min filter).
    internal static AlphaMap Outline(AlphaMap a, int radius, CancellationToken token)
    {
        int w = a.Width, h = a.Height; var rows = new byte[a.Data.Length]; var eroded = new byte[a.Data.Length];
        Parallel.For(0, h, Options(token), y => ErodeLine(a.Data, y * w, 1, w, rows, radius));
        Parallel.For(0, w, Options(token), x => ErodeLine(rows, x, w, h, eroded, radius));
        var output = new AlphaMap(a.Bounds);
        for (int i = 0; i < output.Data.Length; i++) output.Data[i] = Math.Min(a.Data[i], (byte)(255 - eroded[i]));
        return output;
    }

    // Min over a centered window of 2r+1 samples; samples outside the line count as empty.
    static void ErodeLine(byte[] source, int start, int stride, int count, byte[] target, int radius)
    {
        int size = radius * 2 + 1, padded = count + radius * 2;
        var values = new byte[padded]; var prefix = new byte[padded]; var suffix = new byte[padded];
        for (int i = 0; i < count; i++) values[i + radius] = source[start + i * stride];
        for (int i = 0; i < padded; i++) prefix[i] = i % size == 0 ? values[i] : Math.Min(prefix[i - 1], values[i]);
        for (int i = padded - 1; i >= 0; i--) suffix[i] = i == padded - 1 || (i + 1) % size == 0 ? values[i] : Math.Min(suffix[i + 1], values[i]);
        for (int i = 0; i < count; i++) target[start + i * stride] = Math.Min(suffix[i], prefix[i + radius * 2]);
    }

    /// <summary>Colored shadow pixels trimmed to their coverage, with the offset of the top-left pixel.</summary>
    public static (Raster Pixels, int X, int Y)? Colorize(AlphaMap? shadow, ShadowSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (shadow == null || Trim(shadow.Data, Box.Of(shadow)) is not { } trimmed) return null;
        var color = DocumentFeatures.Color(spec.ColorArgb); double gain = spec.Opacity * color.A / 255.0;
        var pixels = new Raster(trimmed.Width, trimmed.Height);
        var ramp = Enumerable.Range(0, 256).Select(v => Imaging.Byte(v * gain)).ToArray();
        Parallel.For(0, trimmed.Height, y =>
        {
            for (int x = 0; x < trimmed.Width; x++)
            {
                int i = y * trimmed.Width + x, p = i * 4;
                pixels.Data[p] = color.B; pixels.Data[p + 1] = color.G; pixels.Data[p + 2] = color.R; pixels.Data[p + 3] = ramp[trimmed.Data[i]];
            }
        });
        return (pixels, trimmed.X, trimmed.Y);
    }

    // ---- Layers -------------------------------------------------------------------------

    // Further sources are counted rather than listed; the number needs no translation.
    public static string DefaultName(Layer first, int count) => "그림자 · " + first.Name + (count > 1 ? $" +{count - 1}" : "");

    /// <summary>A new shadow layer for <paramref name="spec"/>'s sources; not yet added to the document.</summary>
    public static Layer Create(Document doc, ShadowSpec spec, string? name = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(doc); ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var lookup = doc.Layers.ToDictionary(l => l.Id); var order = Order(doc);
        var sources = spec.Sources.Where(id => lookup.TryGetValue(id, out var layer) && layer.Shadow == null && layer.Kind != LayerKind.Adjustment)
            .OrderBy(id => order[id]).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException(MissingSources);
        spec = spec with { Sources = sources };
        var silhouette = Silhouette(doc, sources, 1, spec.Projection == ShadowProjection.Ground, token);
        if (silhouette.Items.Count == 0) throw new InvalidOperationException("선택한 레이어에 그림자를 만들 불투명한 부분이 캔버스 안에 없습니다.");
        var colored = Colorize(Cast(silhouette, spec, token), spec);
        var first = lookup[sources[0]];
        var category = first.ParentId == null && DrawingLayers.Categories(doc)[first.Id] == LayerCategory.Drawing ? LayerCategory.Drawing : LayerCategory.Automatic;
        return new Layer
        {
            Name = name ?? DefaultName(first, sources.Length), Kind = LayerKind.Raster, Blend = BlendMode.Multiply, ParentId = first.ParentId, Category = category,
            Pixels = colored?.Pixels ?? new Raster(1, 1), X = colored?.X ?? 0, Y = colored?.Y ?? 0, Shadow = spec
        };
    }

    /// <summary>Stack position directly below the lowest source, never inside a clipping run.</summary>
    internal static int InsertionIndex(Document doc, IReadOnlyCollection<Guid> sources)
    {
        var set = sources.ToHashSet();
        var first = doc.Layers.FirstOrDefault(l => set.Contains(l.Id)) ?? throw new InvalidOperationException(MissingSources);
        var siblings = doc.Layers.Where(l => l.ParentId == first.ParentId).ToList();
        int lowest = siblings.FindIndex(l => set.Contains(l.Id));
        while (lowest > 0 && siblings[lowest].Clipped) lowest--;
        return doc.Layers.IndexOf(siblings[lowest]);
    }

    /// <summary>Creates the shadow and places it directly below its sources in the same group.</summary>
    public static Layer Insert(Document doc, ShadowSpec spec, string? name = null, CancellationToken token = default)
    {
        var layer = Create(doc, spec, name, token);
        doc.Add(layer);
        doc.Layers.Remove(layer); doc.Layers.Insert(InsertionIndex(doc, layer.Shadow!.Sources), layer);
        doc.Validate();
        return layer;
    }

    /// <summary>
    /// Recomputes a shadow layer from its current sources (and optionally new settings). The layer keeps
    /// its name, visibility, opacity, blend and stacking; a mask follows the parent-space position.
    /// </summary>
    public static void Regenerate(Document doc, Guid shadowId, ShadowSpec? spec = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var shadow = doc.Layers.Find(l => l.Id == shadowId) ?? throw new InvalidOperationException("그림자 레이어가 없습니다.");
        spec ??= shadow.Shadow ?? throw new InvalidOperationException("그림자 레이어를 선택하세요.");
        var created = Create(doc, spec, shadow.Name, token);
        bool moved = created.ParentId != shadow.ParentId;
        if (shadow.Mask != null) shadow.Mask = moved ? null : RemapMask(shadow, created);
        shadow.Pixels = created.Pixels; shadow.X = created.X; shadow.Y = created.Y; shadow.Shadow = created.Shadow; shadow.Kind = LayerKind.Raster;
        shadow.Scale = shadow.ScaleX = shadow.ScaleY = 1; shadow.Rotation = 0; shadow.FlipX = shadow.FlipY = false; shadow.Warp = null;
        if (moved)
        {
            // The sources left the shadow's group: follow them so the shadow stays in their space.
            shadow.ParentId = created.ParentId; shadow.Clipped = false; shadow.Category = created.Category;
            doc.Layers.Remove(shadow); doc.Layers.Insert(InsertionIndex(doc, created.Shadow!.Sources), shadow);
        }
        doc.Validate();
    }

    // Keep painted mask areas where they were in the parent space; newly covered pixels are visible.
    static byte[] RemapMask(Layer old, Layer next)
    {
        var mask = new byte[next.Pixels.Width * next.Pixels.Height]; var previous = old.Mask!;
        int ow = old.Pixels.Width, oh = old.Pixels.Height;
        Parallel.For(0, next.Pixels.Height, y =>
        {
            for (int x = 0; x < next.Pixels.Width; x++)
            {
                var local = old.Local(new Point(next.X + x + .5, next.Y + y + .5));
                int lx = (int)Math.Floor(local.X), ly = (int)Math.Floor(local.Y);
                mask[y * next.Pixels.Width + x] = double.IsFinite(local.X) && double.IsFinite(local.Y) && lx >= 0 && ly >= 0 && lx < ow && ly < oh ? previous[ly * ow + lx] : (byte)255;
            }
        });
        return mask;
    }
}
