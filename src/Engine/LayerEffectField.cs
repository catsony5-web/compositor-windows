using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// A layer's coverage on an effect grid, with the distance field and the per-effect coverage planes
/// derived from it. The grid follows the layer's own axes (it is drawn through the layer's transform
/// like its pixels), so moving or rotating the layer reuses it. Its cells are square in the parent
/// space: <see cref="Cell"/> parent pixels each, chosen from the output scale, so strokes and glows are
/// as sharp as the output and their lengths stay in parent pixels at any layer scale. The layer's local
/// origin sits <see cref="Margin"/> cells inside the grid, leaving room for what reaches beyond its edges.
/// Planes depend only on an effect's shape (kind, size, spread, position, corners); color, opacity,
/// blend, angle and distance are applied when painting, so changing them reuses the planes.
/// </summary>
internal sealed class LayerEffectField
{
    public int Width { get; }
    public int Height { get; }
    public int Margin { get; }
    public double Cell { get; }
    public double Kx { get; }
    public double Ky { get; }
    // Coverage of the content (mask applied) and its largest value: semi-transparent content is
    // measured against its own opacity, so a 40% shape still gets a stroke around it.
    public byte[] Alpha { get; }
    public byte AlphaMax { get; }
    readonly object gate = new();
    float[]? round, square;
    readonly Dictionary<(LayerEffectKind Kind, double Size, double Spread, StrokePosition Position, bool Rounded), byte[]> planes = [];
    public long Bytes { get; private set; }

    public LayerEffectField(int width, int height, int margin, double cell, double kx, double ky, byte[] alpha)
    {
        Width = width; Height = height; Margin = margin; Cell = cell; Kx = kx; Ky = ky; Alpha = alpha;
        byte max = 0; foreach (byte a in alpha) if (a > max) { max = a; if (max == 255) break; }
        AlphaMax = max; Bytes = alpha.LongLength;
    }

    public Matrix GridToLocal => new(1 / Kx, 0, 0, 1 / Ky, -Margin / Kx, -Margin / Ky);
    public Matrix LocalToGrid => new(Kx, 0, 0, Ky, Margin, Margin);

    /// <summary>The coverage plane of one effect (computed once per shape of the effect).</summary>
    public byte[] Plane(LayerEffect effect, CancellationToken token)
    {
        var key = (effect.Kind, effect.Size, effect.Kind == LayerEffectKind.Stroke ? 0 : effect.Spread,
            effect.Kind == LayerEffectKind.Stroke ? effect.Position : StrokePosition.Outside, effect.Kind != LayerEffectKind.Stroke || effect.Rounded);
        lock (gate)
        {
            if (planes.TryGetValue(key, out var known)) return known;
            var plane = effect.Kind switch
            {
                LayerEffectKind.Stroke => Stroke(effect, token),
                LayerEffectKind.DropShadow => Shadow(effect, token),
                LayerEffectKind.OuterGlow => Glow(effect, false, token),
                _ => Glow(effect, true, token)
            };
            planes[key] = plane; Bytes += plane.LongLength;
            return plane;
        }
    }

    // ---- Distance --------------------------------------------------------------------------

    // Signed distance in cells to the content's edge, positive outside. Inside means at least half
    // of the content's own opacity. Rounded uses Euclidean distance (round corners), square the
    // chessboard distance (square corners). Cells touching the edge take their anti-aliased coverage,
    // which keeps outlines smooth: a cell next to the edge reads 0.5 − coverage instead of ±0.5.
    float[] Distance(bool rounded, CancellationToken token)
    {
        var known = rounded ? round : square;
        if (known != null) return known;
        int w = Width, h = Height, n = w * h;
        int threshold = Math.Max(1, (AlphaMax + 1) / 2);
        var inside = new bool[n];
        Parallel.For(0, h, Options(token), y => { for (int i = y * w, end = i + w; i < end; i++) inside[i] = Alpha[i] >= threshold; });
        var result = new float[n]; var scratch = new float[n];
        // Distance to the nearest inside cell for outside cells, then to the nearest outside cell for inside cells.
        foreach (bool toInside in new[] { true, false })
        {
            if (rounded) Euclidean(inside, toInside, scratch, w, h, token); else Chessboard(inside, toInside, scratch, w, h, token);
            Parallel.For(0, h, Options(token), y =>
            {
                for (int i = y * w, end = i + w; i < end; i++)
                {
                    if (inside[i] == toInside) continue;
                    double d = rounded ? Math.Sqrt(scratch[i]) : scratch[i];
                    // A cell next to the edge: its own coverage places the edge inside it (0.5 − coverage
                    // meets d − 0.5 at full and empty coverage, so the field stays continuous).
                    double sd = d <= 1.0001 ? .5 - Math.Min(1, Alpha[i] / (double)Math.Max((byte)1, AlphaMax)) : toInside ? d - .5 : .5 - d;
                    result[i] = (float)sd;
                }
            });
        }
        Bytes += (long)n * sizeof(float);
        return rounded ? round = result : square = result;
    }

    static ParallelOptions Options(CancellationToken token) => new() { CancellationToken = token };

    // Squared Euclidean distance to the nearest cell whose inside flag equals `target`
    // (Felzenszwalb & Huttenlocher): a row pass, then the lower envelope of parabolas per column.
    // Cells beyond the grid count as outside, so the grid border always holds outside seeds.
    static void Euclidean(bool[] inside, bool target, float[] output, int w, int h, CancellationToken token)
    {
        const float Far = 1e30f;
        Parallel.For(0, h, Options(token), y =>
        {
            int row = y * w; double last = double.NegativeInfinity;
            for (int x = 0; x < w; x++)
            {
                if (inside[row + x] == target) last = x;
                output[row + x] = double.IsNegativeInfinity(last) ? Far : (float)((x - last) * (x - last));
            }
            // Outside the grid is a seed when looking for outside cells.
            double next = target ? double.PositiveInfinity : w;
            for (int x = w - 1; x >= 0; x--)
            {
                if (inside[row + x] == target) next = x;
                if (!double.IsPositiveInfinity(next)) output[row + x] = (float)Math.Min(output[row + x], (next - x) * (next - x));
            }
            if (!target) for (int x = 0; x < w; x++) output[row + x] = (float)Math.Min(output[row + x], (double)(x + 1) * (x + 1));
        });
        // Sixteen columns fill a cache line per row; each worker keeps one block of columns.
        const int Block = 16;
        Parallel.For(0, (w + Block - 1) / Block, Options(token), () => (Column: new double[(h + 2) * Block], F: new double[h + 2], D: new double[h + 2], V: new int[h + 2], Z: new double[h + 3]), (block, _, local) =>
        {
            int x0 = block * Block, count = Math.Min(Block, w - x0);
            // Outside the grid (one cell above and below) is an outside seed.
            int n = target ? h : h + 2, offset = target ? 0 : 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w + x0;
                for (int j = 0; j < count; j++) local.Column[j * n + y + offset] = output[row + j];
            }
            for (int j = 0; j < count; j++)
            {
                if (!target) { local.Column[j * n] = 0; local.Column[j * n + n - 1] = 0; }
                Array.Copy(local.Column, j * n, local.F, 0, n);
                Envelope(local.F, n, local.D, local.V, local.Z);
                Array.Copy(local.D, 0, local.Column, j * n, n);
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * w + x0;
                for (int j = 0; j < count; j++) output[row + j] = (float)Math.Min(Far, local.Column[j * n + y + offset]);
            }
            return local;
        }, _ => { });
    }

    // d[q] = min over p of (q − p)² + f[p].
    static void Envelope(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    // Chessboard distance by the two-pass chamfer scan with unit weights (exact for this metric).
    static void Chessboard(bool[] inside, bool target, float[] output, int w, int h, CancellationToken token)
    {
        const float Far = 1e9f;
        for (int i = 0; i < output.Length; i++) output[i] = inside[i] == target ? 0 : Far;
        float At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? (target ? Far : 0) : output[y * w + x];
        for (int y = 0; y < h; y++)
        {
            if ((y & 255) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; float d = output[i]; if (d == 0) continue;
                d = Math.Min(d, Math.Min(Math.Min(At(x - 1, y), At(x - 1, y - 1)), Math.Min(At(x, y - 1), At(x + 1, y - 1))) + 1);
                output[i] = d;
            }
        }
        for (int y = h - 1; y >= 0; y--)
        {
            if ((y & 255) == 0) token.ThrowIfCancellationRequested();
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x; float d = output[i]; if (d == 0) continue;
                d = Math.Min(d, Math.Min(Math.Min(At(x + 1, y), At(x + 1, y + 1)), Math.Min(At(x, y + 1), At(x - 1, y + 1))) + 1);
                output[i] = d;
            }
        }
    }

    // ---- Planes ----------------------------------------------------------------------------

    // Coverage of a band of signed distances [low, high] over one cell (a box filter of the edge).
    static byte Band(double sd, double low, double high) => Imaging.Byte(Math.Clamp(Math.Min(high, sd + .5) - Math.Max(low, sd - .5), 0, 1) * 255);

    // Outside strokes start one cell inside the edge and are drawn under the layer, so the layer's own
    // anti-aliased edge blends into the stroke instead of leaving a seam. Inside strokes run to the
    // edge and beyond; they are clipped to the layer's coverage when painted.
    byte[] Stroke(LayerEffect effect, CancellationToken token)
    {
        var sd = Distance(effect.Rounded, token); double s = effect.Size / Cell;
        var (low, high) = effect.Position switch
        {
            StrokePosition.Outside => (-1d, s),
            StrokePosition.Center => (-s / 2, s / 2),
            _ => (-s, double.PositiveInfinity)
        };
        var plane = new byte[sd.Length];
        Parallel.For(0, Height, Options(token), y => { for (int i = y * Width, end = i + Width; i < end; i++) plane[i] = Band(sd[i], low, high); });
        return plane;
    }

    // Glows grow the shape by their solid part plus a quarter of the soft part, then blur by the rest:
    // strong at the edge (about three quarters), fading out at about the effect's size (LayerEffect.Reach).
    // An inner glow does the same from the outside inward; it is clipped to the layer's coverage when painted.
    byte[] Glow(LayerEffect effect, bool inner, CancellationToken token)
    {
        var sd = Distance(true, token);
        double grow = (effect.Hard + .25 * effect.Soft) / Cell, sigma = .38 * effect.Soft / Cell;
        double scale = inner ? 1 : AlphaMax / 255.0;
        var plane = new byte[sd.Length];
        Parallel.For(0, Height, Options(token), y =>
        {
            for (int i = y * Width, end = i + Width; i < end; i++)
                plane[i] = Imaging.Byte(Math.Clamp(inner ? sd[i] + grow + .5 : grow + .5 - sd[i], 0, 1) * scale * 255);
        });
        ShadowRenderer.Blur(new AlphaMap(new Int32Rect(0, 0, Width, Height), plane), sigma, token);
        return plane;
    }

    // A shadow is the layer's coverage, grown by the solid part and blurred by the soft part. Its
    // offset is applied when painting.
    byte[] Shadow(LayerEffect effect, CancellationToken token)
    {
        var plane = (byte[])Alpha.Clone();
        if (effect.Hard > 0)
        {
            var sd = Distance(true, token); double grow = effect.Hard / Cell, scale = AlphaMax / 255.0;
            Parallel.For(0, Height, Options(token), y =>
            {
                for (int i = y * Width, end = i + Width; i < end; i++)
                    plane[i] = Math.Max(plane[i], Imaging.Byte(Math.Clamp(grow + .5 - sd[i], 0, 1) * scale * 255));
            });
        }
        ShadowRenderer.Blur(new AlphaMap(new Int32Rect(0, 0, Width, Height), plane), effect.Soft / 2.5 / Cell, token);
        return plane;
    }
}
