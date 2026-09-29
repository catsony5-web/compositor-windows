namespace Compositor.Windows;

/// <summary>Screen-size copies of rendered images for dialogs that keep several previews at once.</summary>
public static class PreviewScaling
{
    /// <summary>
    /// Returns <paramref name="source"/> itself when both sides fit in <paramref name="maxSide"/>; otherwise a
    /// box-filtered copy whose long side is <paramref name="maxSide"/>. Color is averaged by coverage so hidden RGB
    /// under transparent pixels never bleeds into visible edges.
    /// </summary>
    public static Raster Fit(Raster source, int maxSide, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxSide < 1) throw new ArgumentOutOfRangeException(nameof(maxSide));
        if (source.Width <= maxSide && source.Height <= maxSide) return source;
        double scale = maxSide / (double)Math.Max(source.Width, source.Height);
        int width = Math.Clamp((int)Math.Round(source.Width * scale), 1, maxSide), height = Math.Clamp((int)Math.Round(source.Height * scale), 1, maxSide);
        var output = new Raster(width, height);
        // Every source pixel lands in exactly one output cell, so the result is an exact area average.
        var column = new int[source.Width];
        for (int x = 0; x < source.Width; x++) column[x] = (int)((long)x * width / source.Width);
        var b = new double[width]; var g = new double[width]; var r = new double[width]; var a = new double[width]; var count = new int[width];
        byte[] data = source.Data, target = output.Data;
        int row = 0;
        void Flush(int y)
        {
            for (int x = 0; x < width; x++)
            {
                int d = (y * width + x) * 4;
                if (count[x] == 0) continue;
                if (a[x] > 0) { target[d] = Imaging.Byte(b[x] / a[x]); target[d + 1] = Imaging.Byte(g[x] / a[x]); target[d + 2] = Imaging.Byte(r[x] / a[x]); }
                target[d + 3] = Imaging.Byte(a[x] / count[x]);
                b[x] = g[x] = r[x] = a[x] = 0; count[x] = 0;
            }
        }
        for (int sy = 0; sy < source.Height; sy++)
        {
            if ((sy & 31) == 0) token.ThrowIfCancellationRequested();
            int y = (int)((long)sy * height / source.Height);
            if (y != row) { Flush(row); row = y; }
            int i = sy * source.Width * 4;
            for (int sx = 0; sx < source.Width; sx++, i += 4)
            {
                int x = column[sx]; double alpha = data[i + 3];
                b[x] += data[i] * alpha; g[x] += data[i + 1] * alpha; r[x] += data[i + 2] * alpha; a[x] += alpha; count[x]++;
            }
        }
        Flush(row);
        return output;
    }
}
