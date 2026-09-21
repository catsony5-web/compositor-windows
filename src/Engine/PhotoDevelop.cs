namespace Compositor.Windows;

/// <summary>
/// Morupixel's RGB8 photo-development pipeline. White balance and exposure work in
/// linear sRGB; tonal regions use smooth luminance weights. Detail uses normalized,
/// alpha-weighted local contrast at two spatial scales. No proprietary RAW processing.
/// </summary>
public static class PhotoDevelop
{
    public static Raster Apply(Raster source, PhotoDevelopSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(spec);
        spec.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var result = source.Clone(); cancellationToken.ThrowIfCancellationRequested();
        if (spec.IsNeutral) return result;
        double exposure = Math.Pow(2, spec.Exposure), temperature = spec.Temperature / 100, tint = spec.Tint / 100;
        // Relative warm/cool and green/magenta gains. The UI deliberately does not label these Kelvin.
        double redGain = exposure * Math.Pow(2, temperature * .65 + tint * .2);
        double greenGain = exposure * Math.Pow(2, -tint * .4);
        double blueGain = exposure * Math.Pow(2, -temperature * .65 + tint * .2);
        var red = ChannelLut(redGain); var green = ChannelLut(greenGain); var blue = ChannelLut(blueGain);
        double contrast = Math.Pow(2, spec.Contrast / 100);
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, source.Height, options, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int i = (y * source.Width + x) * 4;
                if (source.Data[i + 3] == 0) continue; // Keep hidden RGB and coverage exactly intact.
                double r = red[source.Data[i + 2]], g = green[source.Data[i + 1]], b = blue[source.Data[i]];
                double luma = Luminance(r, g, b), tone = luma;
                if (spec.Contrast != 0 && tone > 0 && tone < 1)
                {
                    double bright = Math.Pow(tone, contrast), dark = Math.Pow(1 - tone, contrast);
                    tone = bright / (bright + dark);
                }
                // Shadows/highlights are broad regions; blacks/whites target the endpoints.
                tone += TonalShift(spec.Highlights, luma, Smooth(.45, .95, luma))
                    + TonalShift(spec.Shadows, luma, 1 - Smooth(.05, .55, luma))
                    + TonalShift(spec.Whites, luma, Smooth(.65, 1, luma))
                    + TonalShift(spec.Blacks, luma, 1 - Smooth(0, .35, luma));
                ShiftLuminance(ref r, ref g, ref b, luma, Math.Clamp(tone, 0, 1));
                if (spec.Dehaze > 0)
                {
                    // Remove a bounded neutral veil; inverse control deliberately adds a soft veil.
                    double veil = spec.Dehaze / 100 * .18;
                    r = Math.Clamp((r - veil) / (1 - veil), 0, 1);
                    g = Math.Clamp((g - veil) / (1 - veil), 0, 1);
                    b = Math.Clamp((b - veil) / (1 - veil), 0, 1);
                }
                else if (spec.Dehaze < 0)
                {
                    double veil = -spec.Dehaze / 100 * .25;
                    r += (.9 - r) * veil; g += (.9 - g) * veil; b += (.9 - b) * veil;
                }
                if (spec.Vibrance != 0 || spec.Saturation != 0)
                {
                    DocumentFeatures.ToHsl(r, g, b, out double hue, out double saturation, out double lightness);
                    // Saturation scales all colors. Vibrance gives muted colors more weight.
                    saturation *= (1 + spec.Saturation / 100) * (1 + spec.Vibrance / 100 * (1 - saturation));
                    (r, g, b) = DocumentFeatures.FromHsl(hue, Math.Clamp(saturation, 0, 1), lightness);
                }
                result.Data[i] = Imaging.Byte(b * 255); result.Data[i + 1] = Imaging.Byte(g * 255); result.Data[i + 2] = Imaging.Byte(r * 255);
            }
        });
        if (spec.Texture != 0 || spec.Clarity != 0)
        {
            // Reuse two bounded scratch planes for both scales (8 extra bytes/pixel).
            var sums = new float[source.Width * source.Height]; var weights = new float[sums.Length];
            if (spec.Texture != 0) LocalContrast(result, 2, spec.Texture / 100 * .8, false, sums, weights, cancellationToken);
            if (spec.Clarity != 0) LocalContrast(result, 12, spec.Clarity / 100 * .9, true, sums, weights, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }

    static double[] ChannelLut(double gain) => Enumerable.Range(0, 256).Select(value =>
    {
        double encoded = value / 255d;
        double linear = (encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4)) * gain;
        return Math.Clamp(linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055, 0, 1);
    }).ToArray();
    static double Luminance(double r, double g, double b) => .2126 * r + .7152 * g + .0722 * b;
    static double Smooth(double low, double high, double value)
    {
        double t = Math.Clamp((value - low) / (high - low), 0, 1); return t * t * (3 - 2 * t);
    }
    static double TonalShift(double amount, double luminance, double weight) => amount / 100 * .5 * weight * (amount >= 0 ? 1 - luminance : luminance);
    static void ShiftLuminance(ref double r, ref double g, ref double b, double current, double target)
    {
        if (target > current)
        {
            double factor = (target - current) / Math.Max(1e-12, 1 - current);
            r += (1 - r) * factor; g += (1 - g) * factor; b += (1 - b) * factor;
        }
        else if (target < current)
        {
            double factor = target / Math.Max(1e-12, current); r *= factor; g *= factor; b *= factor;
        }
    }

    static void LocalContrast(Raster image, int radius, double amount, bool midtones, float[] sums, float[] weights, CancellationToken token)
    {
        int width = image.Width, height = image.Height;
        var options = new ParallelOptions { CancellationToken = token };
        // Horizontal rolling sums exclude transparent neighbors instead of blurring their hidden RGB.
        Parallel.For(0, height, options, y =>
        {
            double sum = 0, weight = 0;
            void Include(int x, int sign)
            {
                int i = (y * width + x) * 4; double alpha = image.Data[i + 3] / 255d;
                sum += sign * alpha * Luminance(image.Data[i + 2] / 255d, image.Data[i + 1] / 255d, image.Data[i] / 255d); weight += sign * alpha;
            }
            for (int x = 0; x <= Math.Min(radius, width - 1); x++) Include(x, 1);
            for (int x = 0; x < width; x++)
            {
                if ((x & 255) == 0) token.ThrowIfCancellationRequested();
                int index = y * width + x; sums[index] = (float)sum; weights[index] = (float)weight;
                if (x - radius >= 0) Include(x - radius, -1);
                if (x + radius + 1 < width) Include(x + radius + 1, 1);
            }
        });
        // All source neighborhood reads are complete before RGB is written. Each column owns its pixels.
        Parallel.For(0, width, options, x =>
        {
            double sum = 0, weight = 0;
            void Include(int y, int sign) { int index = y * width + x; sum += sign * sums[index]; weight += sign * weights[index]; }
            for (int y = 0; y <= Math.Min(radius, height - 1); y++) Include(y, 1);
            for (int y = 0; y < height; y++)
            {
                if ((y & 255) == 0) token.ThrowIfCancellationRequested();
                int i = (y * width + x) * 4;
                if (image.Data[i + 3] != 0 && weight > 1e-8)
                {
                    double r = image.Data[i + 2] / 255d, g = image.Data[i + 1] / 255d, b = image.Data[i] / 255d;
                    double luma = Luminance(r, g, b), detail = luma - sum / weight;
                    double target = Math.Clamp(luma + amount * detail * (midtones ? 4 * luma * (1 - luma) : 1), 0, 1);
                    ShiftLuminance(ref r, ref g, ref b, luma, target);
                    image.Data[i] = Imaging.Byte(b * 255); image.Data[i + 1] = Imaging.Byte(g * 255); image.Data[i + 2] = Imaging.Byte(r * 255);
                }
                if (y - radius >= 0) Include(y - radius, -1);
                if (y + radius + 1 < height) Include(y + radius + 1, 1);
            }
        });
    }
}
