using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Compositor.Windows;

public sealed record PdfPageInfo(uint Pages, double WidthAt96Dpi, double HeightAt96Dpi, int Layers = 0);

public static class PdfCompatibility
{
    static void CheckHeader(string path)
    {
        CompatibilityImport.ValidateFile(path);
        using var stream = File.OpenRead(path); var header = new byte[(int)Math.Min(1024L, stream.Length)]; stream.ReadExactly(header);
        if (!Encoding.ASCII.GetString(header).Contains("%PDF-", StringComparison.Ordinal))
            throw new NotSupportedException("PDF 데이터가 없는 파일입니다. Illustrator에서 ‘PDF 호환 파일 만들기’를 켜서 AI를 저장하거나 PDF로 내보내 주세요. EPS / 이전 PostScript AI는 지원하지 않습니다.");
    }
    public static async Task<PdfPageInfo> InspectAsync(string path, CancellationToken token = default)
    {
        CheckHeader(path);
        using var input = File.OpenRead(path); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        if (pdf.PageCount == 0) throw new InvalidDataException("PDF 페이지가 없습니다.");
        using var page = pdf.GetPage(0); return new(pdf.PageCount, page.Size.Width, page.Size.Height, PdfLayerImport.Count(path));
    }
    public static async Task<CompatibilityResult> ReadAsync(string path, CompatibilityOptions options, CancellationToken token)
    {
        CheckHeader(path);
        if (!double.IsFinite(options.Dpi) || options.Dpi < 36 || options.Dpi > 600) throw new ArgumentOutOfRangeException(nameof(options), "PDF 해상도는 36~600 DPI입니다.");
        if (options.PreservePdfLayers && await PdfLayerImport.ReadAsync(path, options, token).ConfigureAwait(false) is { } layered) return layered;
        using var input = File.OpenRead(path); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        if (options.Page < 1 || options.Page > pdf.PageCount) throw new ArgumentOutOfRangeException(nameof(options), $"페이지는 1~{pdf.PageCount} 범위입니다.");
        using var page = pdf.GetPage((uint)options.Page - 1);
        int width = checked((int)Math.Ceiling(page.Size.Width * options.Dpi / 96));
        int height = checked((int)Math.Ceiling(page.Size.Height * options.Dpi / 96));
        try { Raster.ValidateSize(width, height); }
        catch (InvalidDataException e) { throw new InvalidDataException($"선택한 해상도는 {width:N0} × {height:N0}px입니다. DPI를 낮춰 주세요. " + e.Message); }
        var raster = await RenderPageAsync(input, options.Page, width, height, false, token).ConfigureAwait(false);
        var document = CompatibilityImport.Single(path, raster, options.Dpi, $"페이지 {options.Page}");
        if (options.RetainVectors) { using var source = File.OpenRead(path); document.Active!.Vector = VectorContent.FromPdf(width, height, options.Page, source); document.Active.Kind = LayerKind.Vector; }
        return new(document, [$"{pdf.PageCount}페이지 중 {options.Page}페이지를 가져왔습니다. " + (options.PreservePdfLayers ? "이 파일에는 분리할 PDF 레이어가 없습니다. " : "레이어 유지를 끈 상태입니다. ") + (options.RetainVectors ? "원본 PDF 벡터를 보존합니다." : "픽셀 이미지로 가져왔습니다.")]);
    }
    internal static async Task<(int Width, int Height)> InspectPageAsync(string path, int number, double dpi, CancellationToken token)
    {
        using var input = File.OpenRead(path); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        using var page = pdf.GetPage((uint)number - 1);
        int width = checked((int)Math.Ceiling(page.Size.Width * dpi / 96)), height = checked((int)Math.Ceiling(page.Size.Height * dpi / 96));
        Raster.ValidateSize(width, height); return (width, height);
    }
    internal static async Task<Raster> RenderPageAsync(Stream input, int number, int width, int height, bool transparent, CancellationToken token)
    {
        input.Position = 0; using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        using var page = pdf.GetPage((uint)number - 1); using var rendered = new InMemoryRandomAccessStream();
        var renderOptions = new PdfPageRenderOptions { DestinationWidth = (uint)width, DestinationHeight = (uint)height, BackgroundColor = global::Windows.UI.Color.FromArgb(transparent ? (byte)0 : (byte)255, 255, 255, 255) };
        await page.RenderToStreamAsync(rendered, renderOptions).AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); rendered.Seek(0);
        using var decoded = rendered.AsStreamForRead(); var raster = Raster.Load(decoded);
        return raster;
    }
    internal static async Task<Raster> RenderRegionAsync(VectorContent source, System.Windows.Rect area, int width, int height, CancellationToken token)
    {
        using var input = source.Open(); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        using var page = pdf.GetPage((uint)source.Page - 1); using var rendered = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions { DestinationWidth = (uint)width, DestinationHeight = (uint)height,
            SourceRect = new global::Windows.Foundation.Rect(area.X * page.Size.Width / source.Width, area.Y * page.Size.Height / source.Height,
                area.Width * page.Size.Width / source.Width, area.Height * page.Size.Height / source.Height),
            BackgroundColor = global::Windows.UI.Color.FromArgb(0, 255, 255, 255) };
        await page.RenderToStreamAsync(rendered, options).AsTask(token).ConfigureAwait(false); rendered.Seek(0);
        using var decoded = rendered.AsStreamForRead(); return Raster.Load(decoded);
    }

    // A standard single-page PDF with an RGB image and an optional lossless alpha soft mask.
    // Physical page dimensions follow document DPI; no native engine is bundled for writing.
    public static void Write(Document document, Stream destination, CancellationToken token = default)
    {
        document.Validate(); var raster = Imaging.Render(document, token);
        using var writer = new BinaryWriter(destination, Encoding.ASCII, true);
        var offsets = new List<long> { 0 }; int pixels = checked(raster.Width * raster.Height);
        void Ascii(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        void Begin(int id) { offsets.Add(destination.Position); Ascii($"{id} 0 obj\n"); }
        void End() => Ascii("\nendobj\n");
        byte[] Compress(byte[] bytes) { using var output = new MemoryStream(); using (var z = new ZLibStream(output, CompressionLevel.Optimal, true)) z.Write(bytes); return output.ToArray(); }
        var rgb = new byte[checked(pixels * 3)]; var alpha = new byte[pixels]; bool transparent = false;
        for (int i = 0; i < pixels; i++)
        { if ((i & 65535) == 0) token.ThrowIfCancellationRequested(); rgb[i * 3] = raster.Data[i * 4 + 2]; rgb[i * 3 + 1] = raster.Data[i * 4 + 1]; rgb[i * 3 + 2] = raster.Data[i * 4]; alpha[i] = raster.Data[i * 4 + 3]; transparent |= alpha[i] != 255; }
        string w = (document.Width * 72d / document.Dpi).ToString("0.########", CultureInfo.InvariantCulture);
        string h = (document.Height * 72d / document.Dpi).ToString("0.########", CultureInfo.InvariantCulture);
        Ascii("%PDF-1.4\n%Morupixel\n");
        Begin(1); Ascii("<< /Type /Catalog /Pages 2 0 R >>"); End();
        Begin(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"); End();
        Begin(3); Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"); End();
        var imageBytes = Compress(rgb);
        Begin(4); Ascii($"<< /Type /XObject /Subtype /Image /Width {raster.Width} /Height {raster.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {imageBytes.Length}" + (transparent ? " /SMask 6 0 R" : "") + " >>\nstream\n"); writer.Write(imageBytes); Ascii("\nendstream"); End();
        var content = Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n");
        Begin(5); Ascii($"<< /Length {content.Length} >>\nstream\n"); writer.Write(content); Ascii("endstream"); End();
        if (transparent) { var maskBytes = Compress(alpha); Begin(6); Ascii($"<< /Type /XObject /Subtype /Image /Width {raster.Width} /Height {raster.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {maskBytes.Length} >>\nstream\n"); writer.Write(maskBytes); Ascii("\nendstream"); End(); }
        long xref = destination.Position; Ascii($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Ascii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
