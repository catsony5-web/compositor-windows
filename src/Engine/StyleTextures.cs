namespace Compositor.Windows;

public enum TextureKind
{
    /// <summary>Photocopy toner: sparse black specks and a faint uneven haze (Multiply).</summary>
    Toner,
    /// <summary>Cyanotype paper: light mottled fibres (Multiply).</summary>
    Paper,
    /// <summary>Rough print grain around mid grey (Overlay).</summary>
    Print,
    /// <summary>Charcoal grain on black: dark grey speckle (Screen).</summary>
    Section,
    /// <summary>Very soft grain around mid grey (Overlay or Soft light).</summary>
    Soft
}

// Seeded, document-anchored texture pixels for design styles. Every value comes from a
// coordinate hash, so a texture is the same on every run and on every tile of the image.
public static class StyleTextures
{
    /// <summary>A stable value in [0, 1) for a pixel and seed (SplitMix-style mixing).</summary>
    public static double Hash(int x, int y, uint seed)
    {
        ulong v = unchecked((ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL ^ seed * 0x165667B19E3779F9UL);
        v = unchecked((v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL); v = unchecked((v ^ (v >> 27)) * 0x94D049BB133111EBUL); v ^= v >> 31;
        return (v >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Smooth value noise in [0, 1) with unit lattice cells.</summary>
    public static double Smooth(double x, double y, uint seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double tx = x - ix, ty = y - iy;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        double a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    /// <summary>
    /// A texture of width × height layer pixels, each covering <paramref name="factor"/> document
    /// pixels (sizes are in document pixels). <paramref name="amount"/> is 0–1.
    /// </summary>
    public static Raster Render(TextureKind kind, int width, int height, int factor, double amount, uint seed, CancellationToken token = default)
    {
        var output = new Raster(width, height); var data = output.Data; amount = Math.Clamp(amount, 0, 1);
        double scale = Math.Max(width, height) * factor / 1600d;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4; double dx = x * factor, dy = y * factor;
                byte gray; double alpha;
                switch (kind)
                {
                    case TextureKind.Toner:
                    {
                        // Specks in clumps, more where the copy was uneven; a light grey haze in large blotches.
                        double blotch = Smooth(dx / (90 * scale), dy / (90 * scale), seed ^ 11);
                        double density = (.0015 + .012 * amount) * (.4 + 1.2 * blotch);
                        double speck = Hash((int)(dx / 2), (int)(dy / 2), seed) * .55 + Hash(x, y, seed ^ 5) * .45;
                        double haze = Math.Max(0, Smooth(dx / (260 * scale), dy / (260 * scale), seed ^ 23) - .62) * 26 * amount;
                        if (speck > 1 - density) { gray = (byte)(18 + Hash(x, y, seed ^ 9) * 40); alpha = 255; }
                        else { gray = 120; alpha = haze; }
                        break;
                    }
                    case TextureKind.Paper:
                    {
                        double fibre = Smooth(dx / (2.2 * scale), dy / (11 * scale), seed) * .5 + Smooth(dx / (14 * scale), dy / (3 * scale), seed ^ 3) * .5;
                        double mottle = Smooth(dx / (120 * scale), dy / (120 * scale), seed ^ 7);
                        double fine = Hash(x, y, seed ^ 13);
                        gray = Imaging.Byte(255 - amount * (14 * fibre + 18 * mottle + 10 * fine));
                        alpha = 255;
                        break;
                    }
                    case TextureKind.Print:
                    {
                        double fine = Hash(x, y, seed) - .5, clump = Smooth(dx / (2.5 * scale), dy / (2.5 * scale), seed ^ 5) - .5;
                        double wash = Smooth(dx / (180 * scale), dy / (180 * scale), seed ^ 17) - .5;
                        gray = Imaging.Byte(128 + amount * (150 * fine + 110 * clump + 50 * wash));
                        alpha = 255;
                        break;
                    }
                    case TextureKind.Section:
                    {
                        double fine = Hash(x, y, seed), cloud = Smooth(dx / (70 * scale), dy / (70 * scale), seed ^ 3);
                        gray = Imaging.Byte(amount * (fine * fine * 46 + cloud * 8));
                        alpha = 255;
                        break;
                    }
                    default:
                    {
                        double fine = Hash(x, y, seed) - .5, soft = Smooth(dx / (3 * scale), dy / (3 * scale), seed ^ 5) - .5;
                        gray = Imaging.Byte(128 + amount * (60 * fine + 40 * soft));
                        alpha = 255;
                        break;
                    }
                }
                data[i] = data[i + 1] = data[i + 2] = gray; data[i + 3] = Imaging.Byte(alpha);
            }
        });
        return output;
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
