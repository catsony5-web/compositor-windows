using System.Windows;

namespace Compositor.Windows;

public enum RetouchKind { Clone, Heal, Smudge, Liquify, Blur }

/// <summary>Raster retouching. Returned rasters own their storage; inputs are never modified.</summary>
public static class RetouchTools
{
    public static Raster Clone(Layer layer, Raster sourceSnapshot, Point sourceCenter, Point targetCenter,
        double radius, double hardness = .7, double opacity = 1, Selection? selection = null)
        => Stamp(layer, sourceSnapshot, sourceCenter, targetCenter, radius, hardness, opacity, selection, false);

    public static Raster Heal(Layer layer, Raster sourceSnapshot, Point sourceCenter, Point targetCenter,
        double radius, double hardness = .7, double opacity = 1, Selection? selection = null)
        => Stamp(layer, sourceSnapshot, sourceCenter, targetCenter, radius, hardness, opacity, selection, true);

    static Raster Stamp(Layer layer, Raster source, Point from, Point to, double radius, double hardness, double opacity, Selection? selection, bool heal, Raster? destination = null)
    {
        ValidateBrush(from, to, radius, opacity);
        if (!double.IsFinite(hardness)) throw new ArgumentOutOfRangeException(nameof(hardness));
        hardness = Math.Clamp(hardness, 0, 1);
        if (source.Width != layer.Pixels.Width || source.Height != layer.Pixels.Height) throw new ArgumentException("복제 원본과 레이어 크기가 다릅니다.");
        var result = destination ?? layer.Pixels.Clone(); var delta = from - to;
        var offset = new double[3];
        if (heal)
        {
            var sm = Mean(layer, source, from, radius); var dm = Mean(layer, layer.Pixels, to, radius);
            for (int c = 0; c < 3; c++) offset[c] = dm[c] - sm[c];
        }
        var bounds = LocalBounds(layer, to, radius);
        for (int y = bounds.Top; y < bounds.Bottom; y++) for (int x = bounds.Left; x < bounds.Right; x++)
        {
            var document = layer.Document(new Point(x + .5, y + .5));
            double coverage = Coverage(document, to, radius, hardness) * opacity * (selection?.Weight(document.X, document.Y) ?? 1);
            if (coverage <= 0) continue;
            var samplePoint = layer.Local(document + delta); var sample = Sample(source, samplePoint.X - .5, samplePoint.Y - .5);
            int i = (y * result.Width + x) * 4;
            if (heal)
            {
                // Match low-frequency neighborhood color while retaining source
                // texture. Existing alpha is held fixed during healing.
                double a = coverage * sample.A / 255;
                result.Data[i] = Imaging.Byte(result.Data[i] * (1 - a) + Math.Clamp(sample.B + offset[0], 0, 255) * a);
                result.Data[i + 1] = Imaging.Byte(result.Data[i + 1] * (1 - a) + Math.Clamp(sample.G + offset[1], 0, 255) * a);
                result.Data[i + 2] = Imaging.Byte(result.Data[i + 2] * (1 - a) + Math.Clamp(sample.R + offset[2], 0, 255) * a);
            }
            else Imaging.Over(result.Data, i, sample.B / 255, sample.G / 255, sample.R / 255, sample.A / 255 * coverage);
        }
        return result;
    }

    public static Raster Smudge(Layer layer, Point from, Point to, double radius, double strength = .6, Selection? selection = null)
        => WarpBrush(layer, from, to, radius, strength, selection, false);

    public static Raster Liquify(Layer layer, Point from, Point to, double radius, double strength = .8, Selection? selection = null)
        => WarpBrush(layer, from, to, radius, strength, selection, true);

    static Raster WarpBrush(Layer layer, Point from, Point to, double radius, double strength, Selection? selection, bool liquify, Raster? destination = null)
    {
        ValidateBrush(from, to, radius, strength); var source = layer.Pixels; var result = destination ?? source.Clone();
        var bounds = LocalBounds(layer, to, radius); var delta = to - from;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width == 0 || height == 0) return result;
        // Buffer only the brush footprint. All samples see the same pre-dab
        // pixels even when a batched stroke writes back into its working raster.
        var patch = new Raster(width, height);
        for (int y = 0; y < height; y++) Buffer.BlockCopy(source.Data, ((y + bounds.Top) * source.Width + bounds.Left) * 4, patch.Data, y * width * 4, width * 4);
        for (int y = bounds.Top; y < bounds.Bottom; y++) for (int x = bounds.Left; x < bounds.Right; x++)
        {
            var p = layer.Document(new Point(x + .5, y + .5)); double falloff = Coverage(p, to, radius, liquify ? 0 : .2);
            if (falloff <= 0) continue;
            var sample = layer.Local(p - delta * (liquify ? falloff * strength : 1));
            double weight = (selection?.Weight(p.X, p.Y) ?? 1) * (liquify ? 1 : falloff * strength);
            int patchIndex = ((y - bounds.Top) * width + x - bounds.Left) * 4;
            BlendPixel(patch, patch, patchIndex, Sample(source, sample.X - .5, sample.Y - .5), weight);
        }
        for (int y = 0; y < height; y++) Buffer.BlockCopy(patch.Data, y * width * 4, result.Data, ((y + bounds.Top) * source.Width + bounds.Left) * 4, width * 4);
        return result;
    }

    public static Raster Blur(Layer layer, Point center, double radius, double hardness = .7, double opacity = 1,
        Selection? selection = null, int blurRadius = 3, CancellationToken token = default)
        => BlurDab(layer, center, radius, hardness, opacity, selection, blurRadius, token);

    static Raster BlurDab(Layer layer, Point center, double radius, double hardness, double opacity,
        Selection? selection, int blurRadius, CancellationToken token, Raster? destination = null)
    {
        token.ThrowIfCancellationRequested(); ValidateBrush(center, center, radius, opacity);
        if (!double.IsFinite(hardness) || blurRadius < 1 || blurRadius > 30) throw new ArgumentOutOfRangeException(nameof(blurRadius));
        hardness = Math.Clamp(hardness, 0, 1);
        var source = layer.Pixels; var result = destination ?? source.Clone(); var bounds = LocalBounds(layer, center, radius);
        int width = bounds.Right - bounds.Left; if (width <= 0 || bounds.Bottom <= bounds.Top) return result;
        int sampleTop = Math.Max(0, bounds.Top - blurRadius), sampleBottom = Math.Min(source.Height, bounds.Bottom + blurRadius);
        double sigma = Math.Max(.5, blurRadius / 2.0);
        var kernel = Enumerable.Range(-blurRadius, 2 * blurRadius + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        double total = kernel.Sum(); for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
        var horizontal = new float[checked(width * (sampleBottom - sampleTop) * 4)];
        // Only calculate the footprint and vertical halo, rather than filtering
        // every pixel of a potentially 16MP image for a small brush dab.
        for (int y = sampleTop; y < sampleBottom; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                double a = 0, b = 0, g = 0, r = 0;
                for (int k = -blurRadius; k <= blurRadius; k++)
                {
                    int index = (y * source.Width + Math.Clamp(x + k, 0, source.Width - 1)) * 4; double alpha = source.Data[index + 3] / 255.0 * kernel[k + blurRadius];
                    a += alpha; b += source.Data[index] * alpha; g += source.Data[index + 1] * alpha; r += source.Data[index + 2] * alpha;
                }
                int i = ((y - sampleTop) * width + x - bounds.Left) * 4;
                horizontal[i] = (float)b; horizontal[i + 1] = (float)g; horizontal[i + 2] = (float)r; horizontal[i + 3] = (float)a;
            }
        }
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                var p = layer.Document(new Point(x + .5, y + .5)); double weight = Coverage(p, center, radius, hardness) * opacity * (selection?.Weight(p.X, p.Y) ?? 1);
                if (weight <= 0) continue;
                double a = 0, b = 0, g = 0, r = 0;
                for (int k = -blurRadius; k <= blurRadius; k++)
                {
                    int sy = Math.Clamp(y + k, 0, source.Height - 1), i = ((sy - sampleTop) * width + x - bounds.Left) * 4; double wt = kernel[k + blurRadius];
                    b += horizontal[i] * wt; g += horizontal[i + 1] * wt; r += horizontal[i + 2] * wt; a += horizontal[i + 3] * wt;
                }
                BlendPixel(source, result, (y * source.Width + x) * 4, a <= 0 ? default : new Pixel(b / a, g / a, r / a, a * 255), weight);
            }
        }
        return result;
    }

    /// <summary>Apply interpolated dabs with one full-raster clone per pointer event.</summary>
    public static Raster ApplyStroke(Layer layer, RetouchKind kind, Raster sourceSnapshot, Point? cloneOrigin,
        Point strokeStart, Point previous, IReadOnlyList<Point> samples, double radius, double hardness,
        double opacity, Selection? selection = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var working = layer.Snapshot(); working.Pixels = layer.Pixels.Clone(); var result = working.Pixels;
        foreach (var point in samples)
        {
            if (kind is RetouchKind.Clone or RetouchKind.Heal)
            {
                if (cloneOrigin == null) throw new ArgumentException("복제 참조 위치가 필요합니다.");
                Stamp(working, sourceSnapshot, cloneOrigin.Value + (point - strokeStart), point, radius, hardness, opacity, selection, kind == RetouchKind.Heal, result);
            }
            else if (kind is RetouchKind.Smudge or RetouchKind.Liquify)
                WarpBrush(working, previous, point, radius, opacity * (kind == RetouchKind.Smudge ? .65 : .7), selection, kind == RetouchKind.Liquify, result);
            else BlurDab(working, point, radius, hardness, opacity, selection, Math.Clamp((int)(radius * 2 / 15), 1, 10), default, result);
            previous = point;
        }
        return result;
    }

    public static Raster ContentAwareFill(Layer layer, Selection selection, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var source = layer.Pixels; int w = source.Width, h = source.Height;
        var weight = LocalSelection(layer, selection); var unknown = new bool[w * h]; int remaining = 0;
        for (int i = 0; i < unknown.Length; i++) if (weight[i] > 0) { unknown[i] = true; remaining++; }
        var synthesized = source.Clone(); if (remaining == 0) return synthesized;
        var originalUnknown = (bool[])unknown.Clone();
        // Donors must come from the original unselected image. A fill therefore
        // cannot feed the removed object back into later matching patches.
        var donors = new List<int>(); int stride = Math.Max(1, (int)Math.Sqrt((long)w * h / 8192.0));
        for (int y = 0; y < h; y += stride) for (int x = 0; x < w; x += stride)
        {
            if (x == 0) token.ThrowIfCancellationRequested();
            int i = y * w + x;
            if (!originalUnknown[i] && source.Data[i * 4 + 3] > 0) donors.Add(i);
        }
        if (donors.Count == 0)
        {
            for (int i = 0; i < unknown.Length && donors.Count < 8192; i++) if (!originalUnknown[i] && source.Data[i * 4 + 3] > 0) donors.Add(i);
        }
        if (donors.Count == 0) throw new InvalidOperationException("선택 영역 밖에 채우기에 사용할 불투명 픽셀이 필요합니다.");
        var queue = new Queue<int>(); var queued = new bool[unknown.Length];
        bool KnownNeighbor(int i)
        {
            int x = i % w, y = i / w;
            return x > 0 && !unknown[i - 1] || x + 1 < w && !unknown[i + 1] || y > 0 && !unknown[i - w] || y + 1 < h && !unknown[i + w];
        }
        for (int i = 0; i < unknown.Length; i++) if (unknown[i] && KnownNeighbor(i)) { queue.Enqueue(i); queued[i] = true; }
        int iteration = 0;
        while (queue.TryDequeue(out int target))
        {
            if ((iteration++ & 31) == 0) token.ThrowIfCancellationRequested();
            if (!unknown[target]) continue;
            int tx = target % w, ty = target / w, best = donors[0]; double bestScore = double.PositiveInfinity;
            // Compare boundary texture in 5x5 patches. Globally distributed
            // exemplars plus nearby donors permit repeated textures and edges.
            void Evaluate(int candidate)
            {
                if (candidate < 0 || candidate >= unknown.Length || originalUnknown[candidate] || source.Data[candidate * 4 + 3] == 0) return;
                int cx = candidate % w, cy = candidate / w; double error = 0, samples = 0;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                {
                    int xx = tx + dx, yy = ty + dy, sx = cx + dx, sy = cy + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h || sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                    int ti = yy * w + xx, si = sy * w + sx;
                    if (unknown[ti] || originalUnknown[si]) continue;
                    double local = 1.0 / (1 + dx * dx + dy * dy);
                    double aa = synthesized.Data[ti * 4 + 3] / 255.0, ba = source.Data[si * 4 + 3] / 255.0;
                    for (int ch = 0; ch < 3; ch++) { double d = synthesized.Data[ti * 4 + ch] * aa - source.Data[si * 4 + ch] * ba; error += d * d * local; }
                    double alphaDelta = 255 * (aa - ba); error += alphaDelta * alphaDelta * local; samples += local;
                }
                if (samples == 0) return;
                // A tiny distance preference breaks equal-score ties coherently.
                double score = error / samples + .0001 * ((cx - tx) * (double)(cx - tx) + (cy - ty) * (double)(cy - ty));
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            int count = Math.Min(96, donors.Count); int start = (int)((uint)(target * 2654435761L) % (uint)donors.Count);
            for (int n = 0; n < count; n++) Evaluate(donors[(int)((start + (long)n * donors.Count / count) % donors.Count)]);
            for (int r = 1; r <= 32; r *= 2) for (int dy = -r; dy <= r; dy += r) for (int dx = -r; dx <= r; dx += r)
                if (tx + dx >= 0 && tx + dx < w && ty + dy >= 0 && ty + dy < h) Evaluate((ty + dy) * w + tx + dx);
            int bx = best % w, by = best / w;
            // Copy a small matching patch, not an averaged color. This preserves
            // real texture and accelerates filling large contiguous selections.
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = tx + dx, yy = ty + dy, sx = bx + dx, sy = by + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h || sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                int ti = yy * w + xx, si = sy * w + sx; if (!unknown[ti] || originalUnknown[si] || source.Data[si * 4 + 3] == 0) continue;
                Buffer.BlockCopy(source.Data, si * 4, synthesized.Data, ti * 4, 4); unknown[ti] = false; remaining--;
                void Add(int next) { if (unknown[next] && !queued[next]) { queued[next] = true; queue.Enqueue(next); } }
                if (xx > 0) Add(ti - 1); if (xx + 1 < w) Add(ti + 1); if (yy > 0) Add(ti - w); if (yy + 1 < h) Add(ti + w);
            }
        }
        token.ThrowIfCancellationRequested();
        if (remaining > 0) throw new InvalidOperationException("이 선택 영역은 주변에 채우기 참조 픽셀이 없습니다.");
        var result = source.Clone();
        for (int i = 0; i < weight.Length; i++) if (weight[i] > 0)
        {
            int p = i * 4; BlendPixel(source, result, p, new Pixel(synthesized.Data[p], synthesized.Data[p + 1], synthesized.Data[p + 2], synthesized.Data[p + 3]), weight[i] / 255.0);
        }
        return result;
    }

    public static Raster RemoveBackgroundByColor(Layer layer, double tolerance = 35, double feather = 1, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(tolerance) || !double.IsFinite(feather)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        tolerance = Math.Clamp(tolerance, 0, 255); feather = Math.Clamp(feather, 0, 64);
        var source = layer.Pixels; int w = source.Width, h = source.Height;
        // Use the median perimeter color as a robust flat-background estimate.
        // This deliberately remains color-based extraction, not AI segmentation.
        var samples = new List<int>(); int step = Math.Max(1, (w + h) / 512);
        for (int x = 0; x < w; x += step) { samples.Add(x * 4); samples.Add(((h - 1) * w + x) * 4); }
        for (int y = 0; y < h; y += step) { samples.Add(y * w * 4); samples.Add((y * w + w - 1) * 4); }
        var background = new double[3];
        for (int c = 0; c < 3; c++) { var values = samples.Where(i => source.Data[i + 3] > 0).Select(i => source.Data[i + c]).Order().ToArray(); background[c] = values.Length == 0 ? 0 : values[values.Length / 2]; }
        bool Match(int index)
        {
            int p = index * 4; if (source.Data[p + 3] == 0) return true;
            double sum = 0; for (int c = 0; c < 3; c++) { double d = source.Data[p + c] - background[c]; sum += d * d; }
            return Math.Sqrt(sum / 3) <= tolerance;
        }
        var mask = new byte[w * h]; var seen = new bool[mask.Length]; var queue = new Queue<int>();
        void Enqueue(int i) { if (!seen[i]) { seen[i] = true; if (Match(i)) { mask[i] = 255; queue.Enqueue(i); } } }
        for (int x = 0; x < w; x++) { Enqueue(x); Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Enqueue(y * w); Enqueue(y * w + w - 1); }
        int work = 0;
        while (queue.TryDequeue(out int i))
        {
            if ((work++ & 4095) == 0) token.ThrowIfCancellationRequested();
            int x = i % w, y = i / w; if (x > 0) Enqueue(i - 1); if (x + 1 < w) Enqueue(i + 1); if (y > 0) Enqueue(i - w); if (y + 1 < h) Enqueue(i + w);
        }
        if (feather > 0)
        {
            // Blur the retained foreground, avoiding the gray canvas-edge fringe
            // caused by zero-padding a background-removal mask instead.
            for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(255 - mask[i]);
            mask = SelectionTools.Mask(SelectionTools.Feather(SelectionTools.FromMask(mask, w, h), w, h, feather, token), w, h);
        }
        else for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(255 - mask[i]);
        var result = source.Clone(); for (int i = 0; i < mask.Length; i++) result.Data[i * 4 + 3] = Imaging.Byte(source.Data[i * 4 + 3] * mask[i] / 255.0);
        token.ThrowIfCancellationRequested(); return result;
    }

    internal readonly record struct Pixel(double B, double G, double R, double A);
    internal static Pixel Sample(Raster raster, double x, double y, bool clamp = false)
    {
        if (clamp) { x = Math.Clamp(x, 0, raster.Width - 1); y = Math.Clamp(y, 0, raster.Height - 1); }
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= -1 || y <= -1 || x >= raster.Width || y >= raster.Height) return default;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y); double fx = x - x0, fy = y - y0, a = 0, b = 0, g = 0, r = 0;
        void Add(int xx, int yy, double weight)
        {
            if (xx < 0 || yy < 0 || xx >= raster.Width || yy >= raster.Height) return;
            int i = (yy * raster.Width + xx) * 4; double alpha = raster.Data[i + 3] / 255.0 * weight;
            a += alpha; b += raster.Data[i] * alpha; g += raster.Data[i + 1] * alpha; r += raster.Data[i + 2] * alpha;
        }
        Add(x0, y0, (1 - fx) * (1 - fy)); Add(x0 + 1, y0, fx * (1 - fy)); Add(x0, y0 + 1, (1 - fx) * fy); Add(x0 + 1, y0 + 1, fx * fy);
        return a <= 0 ? default : new Pixel(b / a, g / a, r / a, a * 255);
    }

    internal static void BlendPixel(Raster original, Raster output, int i, Pixel sample, double amount)
    {
        amount = Math.Clamp(amount, 0, 1); if (amount == 0) return;
        double oa = original.Data[i + 3] / 255.0, sa = sample.A / 255, a = oa * (1 - amount) + sa * amount;
        output.Data[i + 3] = Imaging.Byte(a * 255);
        if (a <= 0) { output.Data[i] = output.Data[i + 1] = output.Data[i + 2] = 0; return; }
        output.Data[i] = Imaging.Byte((original.Data[i] * oa * (1 - amount) + sample.B * sa * amount) / a);
        output.Data[i + 1] = Imaging.Byte((original.Data[i + 1] * oa * (1 - amount) + sample.G * sa * amount) / a);
        output.Data[i + 2] = Imaging.Byte((original.Data[i + 2] * oa * (1 - amount) + sample.R * sa * amount) / a);
    }

    static double[] Mean(Layer layer, Raster source, Point center, double radius)
    {
        var result = new double[3]; double total = 0;
        // A fixed sampling grid bounds per-dab work even at very large brush sizes.
        for (int yy = -6; yy <= 6; yy++) for (int xx = -6; xx <= 6; xx++)
        {
            if (xx * xx + yy * yy > 36) continue;
            var p = layer.Local(center + new Vector(xx * radius / 6, yy * radius / 6)); var sample = Sample(source, p.X - .5, p.Y - .5);
            double a = sample.A / 255; result[0] += sample.B * a; result[1] += sample.G * a; result[2] += sample.R * a; total += a;
        }
        if (total > 0) for (int c = 0; c < 3; c++) result[c] /= total;
        return result;
    }

    static (int Left, int Top, int Right, int Bottom) LocalBounds(Layer layer, Point center, double radius)
    {
        // Sample the brush perimeter as well as its center. A full layer fallback
        // for perspective warps is conservative and leaves coverage authoritative.
        if (layer.Warp != null) return (0, 0, layer.Pixels.Width, layer.Pixels.Height);
        var points = Enumerable.Range(0, 32).Select(i => layer.Local(center + new Vector(Math.Cos(i * Math.PI / 16) * radius, Math.Sin(i * Math.PI / 16) * radius))).Append(layer.Local(center)).ToArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return (0, 0, layer.Pixels.Width, layer.Pixels.Height);
        return ((int)Math.Clamp(Math.Floor(points.Min(p => p.X)) - 1, 0, layer.Pixels.Width), (int)Math.Clamp(Math.Floor(points.Min(p => p.Y)) - 1, 0, layer.Pixels.Height),
            (int)Math.Clamp(Math.Ceiling(points.Max(p => p.X)) + 1, 0, layer.Pixels.Width), (int)Math.Clamp(Math.Ceiling(points.Max(p => p.Y)) + 1, 0, layer.Pixels.Height));
    }

    static byte[] LocalSelection(Layer layer, Selection selection)
    {
        var result = new byte[layer.Pixels.Width * layer.Pixels.Height];
        for (int y = 0; y < layer.Pixels.Height; y++) for (int x = 0; x < layer.Pixels.Width; x++)
        { var p = layer.Document(new Point(x + .5, y + .5)); result[y * layer.Pixels.Width + x] = Imaging.Byte(selection.Weight(p.X, p.Y) * 255); }
        return result;
    }

    static double Coverage(Point p, Point center, double radius, double hardness)
    {
        double d = (p - center).Length / radius; if (d >= 1) return 0;
        return d <= hardness ? 1 : BrushStroke.Falloff((d - hardness) / (1 - hardness));
    }

    static void ValidateBrush(Point from, Point to, double radius, double strength)
    {
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) || !double.IsFinite(to.X) || !double.IsFinite(to.Y) || !double.IsFinite(radius) || radius <= 0 || radius > 8192 || !double.IsFinite(strength) || strength < 0 || strength > 1)
            throw new ArgumentOutOfRangeException(nameof(radius), "브러시 크기와 강도를 확인하세요.");
    }
}
