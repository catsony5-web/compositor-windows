namespace Compositor.Windows;

// Small pixel helpers of the design styles: a coordinate hash (the same value on every run and every
// tile of an image) and the blur behind the translucent panel. Paper, print and photocopy textures
// are the 종이·인쇄 질감 adjustment (StyleEffects).
public static class StyleTextures
{
    /// <summary>A stable value in [0, 1) for a pixel and seed (SplitMix-style mixing).</summary>
    public static double Hash(int x, int y, uint seed)
    {
        ulong v = unchecked((ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL ^ seed * 0x165667B19E3779F9UL);
        v = unchecked((v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL); v = unchecked((v ^ (v >> 27)) * 0x94D049BB133111EBUL); v ^= v >> 31;
        return (v >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Three box blurs (close to a Gaussian of the same radius) in premultiplied colour.</summary>
    public static Raster BoxBlur(Raster source, double radius, CancellationToken token = default)
    {
        int w = source.Width, h = source.Height; var buffer = new float[w * h * 4]; var spare = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            float a = source.Data[i * 4 + 3] / 255f;
            buffer[i * 4] = source.Data[i * 4] * a; buffer[i * 4 + 1] = source.Data[i * 4 + 1] * a; buffer[i * 4 + 2] = source.Data[i * 4 + 2] * a; buffer[i * 4 + 3] = a * 255;
        }
        // Box widths for three passes approximating a Gaussian with sigma ≈ radius / 2.
        double sigma = Math.Max(.5, radius / 2); int ideal = (int)Math.Floor(Math.Sqrt(12 * sigma * sigma / 3 + 1)); if (ideal % 2 == 0) ideal--;
        int[] boxes = [ideal / 2, ideal / 2, ideal / 2 + 1];
        foreach (int r in boxes)
        {
            token.ThrowIfCancellationRequested();
            Pass(buffer, spare, w, h, r, true, token); Pass(spare, buffer, w, h, r, false, token);
        }
        var output = new Raster(w, h);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4; float a = buffer[i + 3];
                output.Data[i + 3] = Imaging.Byte(a);
                if (a > .01f) for (int c = 0; c < 3; c++) output.Data[i + c] = Imaging.Byte(buffer[i + c] * 255 / a);
            }
        });
        return output;
    }

    // One box pass with clamped edges, horizontally or vertically.
    static void Pass(float[] from, float[] to, int w, int h, int r, bool horizontal, CancellationToken token)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h; float scale = 1f / (2 * r + 1);
        Parallel.For(0, lines, new ParallelOptions { CancellationToken = token }, line =>
        {
            int Index(int k) => horizontal ? (line * w + Math.Clamp(k, 0, w - 1)) * 4 : (Math.Clamp(k, 0, h - 1) * w + line) * 4;
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += from[Index(k) + c];
                for (int k = 0; k < length; k++)
                {
                    to[Index(k) + c] = sum * scale;
                    sum += from[Index(k + r + 1) + c] - from[Index(k - r) + c];
                }
            }
        });
    }
}
