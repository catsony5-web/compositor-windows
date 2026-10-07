using System.IO;

namespace Compositor.Windows;

// The line-art core shared by 이미지로 패턴 추가 (LinePatternSource, small tiles) and 스케치 사진 정리
// (SketchCleanup, whole photos): dark marks on light paper become ink coverage. Darkness runs from 0
// (paper) to 1 (ink); an automatic threshold (Otsu) separates the two tones, and the band halfway
// between the paper tone and the threshold up to halfway between the threshold and the ink tone keeps
// antialiased edges while paper grain and grey ink are cleaned up. Every step works at any resolution
// (parallel where the result does not depend on the order of the work).
public static class LineArt
{
    // Rec. 709 luminance of straight BGRA, scaled by the pixel's own opacity: transparency is paper.
    public static float[] Darkness(Raster image, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var darkness = new float[image.Width * image.Height];
        byte[] data = image.Data; int width = image.Width;
        Parallel.For(0, image.Height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int i = y * width, end = i + width, p = i * 4; i < end; i++, p += 4)
            {
                double luminance = (.0722 * data[p] + .7152 * data[p + 1] + .2126 * data[p + 2]) / 255;
                darkness[i] = (float)(data[p + 3] / 255d * (1 - luminance));
            }
        });
        return darkness;
    }

    // 256-bin histogram of darkness values (bin = round(d × 255)). Counts are exact, so the parallel
    // partial histograms add up to the same table as a single pass.
    public static long[] Histogram(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = new long[256];
        const int chunk = 1 << 18;
        Parallel.For(0, (values.Length + chunk - 1) / chunk, () => new long[256], (part, _, local) =>
        {
            for (int i = part * chunk, end = Math.Min(values.Length, i + chunk); i < end; i++) local[Math.Clamp((int)(values[i] * 255 + .5), 0, 255)]++;
            return local;
        }, local => { lock (total) for (int b = 0; b < 256; b++) total[b] += local[b]; });
        return total;
    }

    public static double Otsu(float[] values) => Otsu(Histogram(values));

    // Threshold (0..1) that best separates paper from ink in a 256-bin darkness histogram.
    public static double Otsu(long[] histogram)
    {
        if (histogram.Length != 256) throw new ArgumentException("A 256-bin histogram is required.", nameof(histogram));
        double total = 0, sum = 0;
        for (int i = 0; i < 256; i++) { total += histogram[i]; sum += i * (double)histogram[i]; }
        var between = new double[255]; double below = 0, belowSum = 0, best = -1;
        for (int t = 0; t < 255; t++)
        {
            below += histogram[t]; belowSum += t * (double)histogram[t];
            double above = total - below;
            between[t] = -1;
            if (below == 0 || above == 0) continue;
            double meanBelow = belowSum / below, meanAbove = (sum - belowSum) / above;
            between[t] = below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            best = Math.Max(best, between[t]);
        }
        if (best <= 0) return .5;
        // Empty bins between paper and ink give equal splits: take the middle of that plateau, so the
        // threshold sits between the tones rather than at the edge of the paper's grain.
        int first = Array.FindIndex(between, v => v >= best * (1 - 1e-9)), last = Array.FindLastIndex(between, v => v >= best * (1 - 1e-9));
        // The class boundary lies between bins t and t+1.
        return Math.Clamp(((first + last) / 2d + .5) / 255, .02, .98);
    }

    // The paper and ink tones either side of a threshold and the coverage ramp between them.
    public readonly record struct Band(double Threshold, double Paper, double Ink, double Low, double High)
    {
        // 0 below the band (paper), 1 above it (solid ink), linear in between (antialiased edges).
        public double Coverage(double darkness) => Math.Clamp((darkness - Low) / (High - Low), 0, 1);
    }

    // Measures the band for `threshold` (clamped to 0.01..0.99). Paper below the band, halfway from the
    // paper tone to the threshold, becomes clear; ink above it, halfway to the ink tone, becomes solid.
    // Throws when nothing reaches the threshold or paper and ink are too close to tell apart.
    public static Band Measure(float[] darkness, double threshold)
    {
        ArgumentNullException.ThrowIfNull(darkness);
        threshold = Math.Clamp(threshold, .01, .99);
        // One ordered pass: the sums (and so the band) do not depend on scheduling.
        double paper = 0, ink = 0; long papers = 0, inks = 0;
        foreach (var d in darkness) if (d < threshold) { paper += d; papers++; } else { ink += d; inks++; }
        if (inks == 0) throw new InvalidDataException("이미지에서 선을 찾지 못했습니다. 선 인식 기준을 낮춰 보세요.");
        paper = papers > 0 ? paper / papers : 0; ink /= inks;
        if (ink - paper < .06) throw new InvalidDataException("선과 바탕의 밝기 차이가 너무 작습니다. 밝은 바탕에 어두운 선이 있는 이미지를 사용하세요.");
        double low = (paper + threshold) / 2, high = Math.Max(low + .002, (threshold + ink) / 2);
        return new(threshold, paper, ink, low, high);
    }
}
