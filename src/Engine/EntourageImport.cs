using System.IO;

namespace Compositor.Windows;

// How a picked image becomes a 내 점경 item: a PNG that already has transparency is kept as it is;
// a photo is cut out with the bundled background-removal model (the same as AI 배경 제거); a drawing
// on paper becomes ink lines with the shared line-art core (LineArt, as for 내 패턴), so it takes the
// line colour like the built-in set.
public enum EntourageImportMode { Transparent, RemoveBackground, LineDrawing }

public static class EntourageImport
{
    // Images are reduced to this before conversion; the stored item is at most EntourageStoreLimits.MaxSide.
    const int WorkSide = 2048;

    public static Raster Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new InvalidDataException("이미지 파일을 찾을 수 없습니다.");
        if (file.Length > MaterialTextures.MaxEncodedBytes) throw new InvalidDataException("점경 이미지는 96MiB 이하로 사용하세요.");
        var image = ImportExport.LoadImage(path);
        return Fit(image, WorkSide);
    }

    public static bool HasTransparency(Raster image)
    {
        for (int i = 3; i < image.Data.Length; i += 4) if (image.Data[i] < 250) return true;
        return false;
    }

    /// <summary>The mode a picked image suggests: transparency kept, a light-paper drawing as lines, otherwise a cut-out.</summary>
    public static EntourageImportMode Suggest(Raster image)
    {
        if (HasTransparency(image)) return EntourageImportMode.Transparent;
        long light = 0, total = 0, saturated = 0; var d = image.Data;
        for (int p = 0; p < d.Length; p += 16)
        {
            int b = d[p], g = d[p + 1], r = d[p + 2], max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            total++; if (min > 200) light++; if (max - min > 60) saturated++;
        }
        return light > total * .6 && saturated < total * .08 ? EntourageImportMode.LineDrawing : EntourageImportMode.RemoveBackground;
    }

    /// <summary>
    /// Converts and normalizes an image: the transparent margins are trimmed and the result reduced to
    /// EntourageStoreLimits.MaxSide. `mask` cuts out a photo (BackgroundRemoval.CreateMask in the app).
    /// Line drawings use `threshold` (0..1 darkness) or the automatic one.
    /// </summary>
    public static Raster Convert(Raster image, EntourageImportMode mode, Func<Raster, byte[]>? mask = null, double? threshold = null, CancellationToken token = default)
    {
        Raster result;
        switch (mode)
        {
            case EntourageImportMode.LineDrawing:
                var darkness = LineArt.Darkness(image, token);
                var band = LineArt.Measure(darkness, threshold ?? LineArt.Otsu(darkness));
                result = new Raster(image.Width, image.Height);
                for (int i = 0; i < darkness.Length; i++) result.Data[i * 4 + 3] = (byte)Math.Round(band.Coverage(darkness[i]) * 255);
                break;
            case EntourageImportMode.RemoveBackground:
                var coverage = (mask ?? (source => BackgroundRemoval.CreateMask(source, cancellationToken: token)))(image);
                if (coverage.Length != image.Width * image.Height) throw new InvalidDataException("배경 마스크 크기가 이미지와 다릅니다.");
                result = image.Clone();
                for (int i = 0; i < coverage.Length; i++) result.Data[i * 4 + 3] = (byte)((result.Data[i * 4 + 3] * coverage[i] + 127) / 255);
                break;
            default: result = image; break;
        }
        token.ThrowIfCancellationRequested();
        var trimmed = Trim(result) ?? throw new InvalidDataException(mode == EntourageImportMode.LineDrawing ? "이미지에서 선을 찾지 못했습니다." : "이미지에 보이는 부분이 없습니다.");
        return Fit(trimmed, EntourageStoreLimits.MaxSide);
    }

    // Crops to the pixels that are visible (alpha above 8); null when nothing is.
    public static Raster? Trim(Raster image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            if (image.Data[(y * image.Width + x) * 4 + 3] > 8) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) return null;
        if (left == 0 && top == 0 && right == image.Width - 1 && bottom == image.Height - 1) return image;
        int w = right - left + 1, h = bottom - top + 1; var output = new Raster(w, h);
        for (int y = 0; y < h; y++) Buffer.BlockCopy(image.Data, ((y + top) * image.Width + left) * 4, output.Data, y * w * 4, w * 4);
        return output;
    }

    // Area-averaged reduction in premultiplied alpha, so cut-out edges do not darken.
    public static Raster Fit(Raster image, int maxSide)
    {
        double scale = Math.Min(1, maxSide / (double)Math.Max(image.Width, image.Height));
        if (scale >= 1) return image;
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var output = new Raster(w, h); var s = image.Data;
        Parallel.For(0, h, y =>
        {
            double y0 = y / scale, y1 = Math.Min(image.Height, (y + 1) / scale);
            for (int x = 0; x < w; x++)
            {
                double x0 = x / scale, x1 = Math.Min(image.Width, (x + 1) / scale), a = 0, r = 0, g = 0, b = 0, weight = 0;
                for (int sy = (int)y0; sy < Math.Ceiling(y1); sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = (int)x0; sx < Math.Ceiling(x1); sx++)
                    {
                        double wgt = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0)); int i = (sy * image.Width + sx) * 4; double alpha = s[i + 3];
                        b += s[i] * alpha * wgt; g += s[i + 1] * alpha * wgt; r += s[i + 2] * alpha * wgt; a += alpha * wgt; weight += wgt;
                    }
                }
                int o = (y * w + x) * 4;
                if (a > 0) { output.Data[o] = Imaging.Byte(b / a); output.Data[o + 1] = Imaging.Byte(g / a); output.Data[o + 2] = Imaging.Byte(r / a); }
                output.Data[o + 3] = Imaging.Byte(weight > 0 ? a / weight : 0);
            }
        });
        return output;
    }

    /// <summary>A sensible real size for a new item of a category and view.</summary>
    public static double SuggestedMeters(EntourageCategory category, EntourageView view) => (category, view) switch
    {
        (EntourageCategory.People, EntourageView.Elevation) => 1.7, (EntourageCategory.People, _) => .6,
        (EntourageCategory.Plants, EntourageView.Elevation) => 6, (EntourageCategory.Plants, _) => 5,
        (EntourageCategory.Vehicles, EntourageView.Elevation) => 1.5, (EntourageCategory.Vehicles, _) => 4.5,
        _ => 1
    };
}
