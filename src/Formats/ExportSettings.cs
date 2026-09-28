using System.IO;

namespace Compositor.Windows;

public enum ExportFormat { Png, Jpeg, Tiff }

/// <summary>Options chosen in the export dialog, applied to a rendered raster before encoding.</summary>
public sealed record ExportSettings(ExportFormat Format = ExportFormat.Png, double Scale = 1, bool KeepTransparency = true, int Quality = 95)
{
    public const double MinScale = .05, MaxScale = 8;

    public string Extension => Format switch { ExportFormat.Jpeg => ".jpg", ExportFormat.Tiff => ".tiff", _ => ".png" };
    /// <summary>JPEG has no alpha channel, so the transparency option only applies to PNG and TIFF.</summary>
    public bool SupportsTransparency => Format != ExportFormat.Jpeg;
    public bool UsesQuality => Format == ExportFormat.Jpeg;

    public (int Width, int Height) OutputSize(int width, int height)
    {
        double s = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, MinScale, MaxScale);
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }

    /// <summary>Returns a raster ready for <see cref="ImportExport.Write"/>; never mutates the source.</summary>
    public Raster Prepare(Raster source)
    {
        var (w, h) = OutputSize(source.Width, source.Height);
        var output = w == source.Width && h == source.Height ? source : ImportExport.Resize(source, w, h);
        if (SupportsTransparency && !KeepTransparency)
        {
            if (ReferenceEquals(output, source)) output = source.Clone();
            for (int i = 0; i < output.Data.Length; i += 4)
            {
                double alpha = output.Data[i + 3] / 255.0;
                for (int ch = 0; ch < 3; ch++) output.Data[i + ch] = Imaging.Byte(output.Data[i + ch] * alpha + 255 * (1 - alpha));
                output.Data[i + 3] = 255;
            }
        }
        return output;
    }

    public void Write(Raster source, Stream stream, double dpi) => ImportExport.Write(Prepare(source), stream, Extension, Quality, dpi);

    /// <summary>File name typed in the dialog, stripped of invalid characters and given this format's extension.</summary>
    public string FileName(string typed, string fallback)
    {
        string name = (typed ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff") name = name[..^ext.Length];
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = string.IsNullOrWhiteSpace(fallback) ? "export" : fallback;
        return name + Extension;
    }
}
