using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed record ExportPreview(Raster Image, long ByteCount);

/// <summary>8-bit sRGB import/export. WIC decodes installed image codecs; no network uploads.</summary>
public static class ImportExport
{
    public static Raster LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        try { return LoadImage(stream); }
        catch (Exception e) when ((e is NotSupportedException || e is COMException || e is FileFormatException) &&
            (Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".heif", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException("HEIC/HEIF 이미지를 읽을 수 없습니다. Windows HEIF 이미지 확장과 해당 HEVC 코덱이 필요합니다. 설치되어 있다면 파일 손상 여부를 확인하거나 PNG/JPEG로 변환해 주세요.", e);
        }
    }

    public static Raster LoadImage(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("이미지 프레임이 없습니다.");
        var frame = decoder.Frames[0];
        Raster.ValidateSize(frame.PixelWidth, frame.PixelHeight);
        BitmapSource source = frame;
        // WIC performs the profile conversion before quantizing to the editor's 8-bit sRGB surface.
        // A broken profile is an import error, never silently interpreted as sRGB.
        if (frame.ColorContexts is { Count: > 0 })
            source = new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        var raster = Raster.FromBitmap(source);
        int orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    var value = metadata.GetQuery(query);
                    if (value != null) { orientation = Convert.ToInt32(value); break; }
                }
                catch (Exception e) when (e is NotSupportedException || e is ArgumentException || e is COMException) { }
            }
        }
        return Orient(raster, orientation);
    }

    public static Raster Orient(Raster source, int orientation)
    {
        if (orientation <= 1 || orientation > 8) return source;
        bool transpose = orientation >= 5;
        var output = new Raster(transpose ? source.Height : source.Width, transpose ? source.Width : source.Height);
        for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
        {
            (int dx, int dy) = orientation switch
            {
                2 => (source.Width - 1 - x, y),
                3 => (source.Width - 1 - x, source.Height - 1 - y),
                4 => (x, source.Height - 1 - y),
                5 => (y, x),
                6 => (source.Height - 1 - y, x),
                7 => (source.Height - 1 - y, source.Width - 1 - x),
                8 => (y, source.Width - 1 - x),
                _ => (x, y)
            };
            Buffer.BlockCopy(source.Data, (y * source.Width + x) * 4, output.Data, (dy * output.Width + dx) * 4, 4);
        }
        return output;
    }

    public static void Export(Document document, string path, int quality = 95)
    {
        document.Validate();
        var raster = Imaging.Render(document);
        ProjectStore.AtomicWrite(path, stream => Write(raster, stream, Path.GetExtension(path), quality, document.Dpi));
    }

    public static ExportPreview PreviewExport(Document document, string format, int quality = 95, int maxSide = 720)
    {
        if (maxSide < 1 || maxSide > 8192) throw new ArgumentOutOfRangeException(nameof(maxSide));
        document.Validate();
        using var encoded = new MemoryStream();
        Write(Imaging.Render(document), encoded, format, quality, document.Dpi);
        long bytes = encoded.Length; encoded.Position = 0;
        var decoded = Raster.Load(encoded);
        double scale = Math.Min(1, maxSide / (double)Math.Max(decoded.Width, decoded.Height));
        return new ExportPreview(scale == 1 ? decoded : Resize(decoded, Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale))), bytes);
    }

    public static void Write(Raster raster, Stream stream, string format, int quality = 95, double dpi = 96)
    {
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality), "JPEG 품질은 1–100 범위입니다.");
        format = format.TrimStart('.').ToLowerInvariant();
        BitmapEncoder encoder = format switch
        {
            "jpg" or "jpeg" => new JpegBitmapEncoder { QualityLevel = quality },
            "png" => new PngBitmapEncoder(),
            "tif" or "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new NotSupportedException("내보내기는 PNG, JPEG, TIFF 형식을 지원합니다.")
        };
        var output = raster;
        if (encoder is JpegBitmapEncoder)
        {
            output = raster.Clone();
            for (int i = 0; i < output.Data.Length; i += 4)
            {
                double alpha = output.Data[i + 3] / 255.0;
                for (int ch = 0; ch < 3; ch++) output.Data[i + ch] = Imaging.Byte(output.Data[i + ch] * alpha + 255 * (1 - alpha));
                output.Data[i + 3] = 255;
            }
        }
        encoder.Frames.Add(BitmapFrame.Create(output.Bitmap(dpi)));
        if (stream.CanSeek) encoder.Save(stream);
        else { using var buffer = new MemoryStream(); encoder.Save(buffer); buffer.Position = 0; buffer.CopyTo(stream); }
    }

    /// <summary>Separable Lanczos-3, widened on reduction, in premultiplied alpha. The smaller intermediate is selected.</summary>
    public static Raster Resize(Raster source, int width, int height)
    {
        Raster.ValidateSize(width, height);
        if (width == source.Width && height == source.Height) return source.Clone();
        var wx = Weights(source.Width, width); var wy = Weights(source.Height, height);
        bool horizontalFirst = (long)width * source.Height <= (long)source.Width * height;
        int tw = horizontalFirst ? width : source.Width, th = horizontalFirst ? source.Height : height;
        var temp = new float[checked(tw * th * 4)];
        Parallel.For(0, th, y =>
        {
            for (int x = 0; x < tw; x++)
            {
                var taps = horizontalFirst ? wx[x] : wy[y]; int di = (y * tw + x) * 4;
                foreach (var (index, weight) in taps)
                {
                    int si = horizontalFirst ? (y * source.Width + index) * 4 : (index * source.Width + x) * 4;
                    double a = source.Data[si + 3] / 255.0;
                    for (int ch = 0; ch < 3; ch++) temp[di + ch] += (float)(source.Data[si + ch] * a * weight);
                    temp[di + 3] += (float)(source.Data[si + 3] * weight);
                }
            }
        });
        var output = new Raster(width, height);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                foreach (var (index, weight) in horizontalFirst ? wy[y] : wx[x])
                {
                    int ti = horizontalFirst ? (index * tw + x) * 4 : (y * tw + index) * 4;
                    b += temp[ti] * weight; g += temp[ti + 1] * weight; r += temp[ti + 2] * weight; a += temp[ti + 3] * weight;
                }
                int di = (y * width + x) * 4;
                output.Data[di + 3] = Imaging.Byte(a);
                if (a > .0001)
                {
                    output.Data[di] = Imaging.Byte(b * 255 / a);
                    output.Data[di + 1] = Imaging.Byte(g * 255 / a);
                    output.Data[di + 2] = Imaging.Byte(r * 255 / a);
                }
            }
        });
        return output;
    }

    static (int Index, double Weight)[][] Weights(int input, int output)
    {
        var result = new (int, double)[output][];
        double scale = Math.Max(1, input / (double)output), support = 3 * scale;
        static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
        for (int i = 0; i < output; i++)
        {
            double center = (i + .5) * input / output - .5;
            var weights = new Dictionary<int, double>();
            for (int j = (int)Math.Ceiling(center - support); j <= (int)Math.Floor(center + support); j++)
            {
                double z = (j - center) / scale, weight = Math.Abs(z) >= 3 ? 0 : Sinc(z) * Sinc(z / 3);
                int index = Math.Clamp(j, 0, input - 1);
                weights[index] = weights.GetValueOrDefault(index) + weight;
            }
            double total = weights.Values.Sum(); result[i] = weights.Select(pair => (pair.Key, pair.Value / total)).ToArray();
        }
        return result;
    }
}
