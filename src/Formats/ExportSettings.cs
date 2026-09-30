using System.IO;
using System.Windows;

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

    /// <summary>
    /// The document at the output size. Drawings, text and hatch patterns are drawn at that resolution
    /// rather than resampled from 1x pixels; a document of photo layers only is resized as before.
    /// </summary>
    public Raster Render(Document document, CancellationToken token = default)
    {
        var (w, h) = OutputSize(document.Width, document.Height);
        if (w == document.Width && h == document.Height) return DesignRenderer.RenderOutput(document, token);
        return Scaled(document, new Rect(0, 0, document.Width, document.Height), w, h, () => DesignRenderer.RenderOutput(document, token), token);
    }

    /// <summary>An area of the document at width×height: redrawn when it holds drawings, otherwise the 1x pixels resized.</summary>
    internal static Raster Scaled(Document document, Rect area, int width, int height, Func<Raster> pixels, CancellationToken token)
        => DesignRenderer.HasRetainedContent(document) || document.Layers.Any(DrawingLayers.IsContainer)
            ? DesignRenderer.RenderScaled(document, area, width, height, token) : ImportExport.Resize(pixels(), width, height);

    /// <summary>
    /// Returns a raster ready for <see cref="ImportExport.Write"/>; never mutates the source. A source
    /// already rendered at the output size (<see cref="Render"/>) is not resized again.
    /// </summary>
    public Raster Prepare(Raster source, bool atOutputSize = false)
    {
        var (w, h) = atOutputSize ? (source.Width, source.Height) : OutputSize(source.Width, source.Height);
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

    public void Write(Raster source, Stream stream, double dpi, bool atOutputSize = false) => ImportExport.Write(Prepare(source, atOutputSize), stream, Extension, Quality, dpi);

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
