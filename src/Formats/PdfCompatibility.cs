using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Compositor.Windows;

public sealed record PdfPageInfo(uint Pages, double WidthAt96Dpi, double HeightAt96Dpi);

public static class PdfCompatibility
{
    static void CheckHeader(string path)
    {
        CompatibilityImport.ValidateFile(path);
        using var stream = File.OpenRead(path); var header = new byte[Math.Min(1024, (int)stream.Length)]; stream.ReadExactly(header);
        if (!Encoding.ASCII.GetString(header).Contains("%PDF-", StringComparison.Ordinal))
            throw new NotSupportedException("PDF 데이터가 없는 파일입니다. Illustrator에서 ‘PDF 호환 파일 만들기’를 켜서 AI를 저장하거나 PDF로 내보내 주세요. EPS / 이전 PostScript AI는 지원하지 않습니다.");
    }
    public static async Task<PdfPageInfo> InspectAsync(string path, CancellationToken token = default)
    {
        CheckHeader(path);
        using var input = File.OpenRead(path); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        if (pdf.PageCount == 0) throw new InvalidDataException("PDF 페이지가 없습니다.");
        using var page = pdf.GetPage(0); return new(pdf.PageCount, page.Size.Width, page.Size.Height);
    }
    public static async Task<CompatibilityResult> ReadAsync(string path, CompatibilityOptions options, CancellationToken token)
    {
        CheckHeader(path);
        if (!double.IsFinite(options.Dpi) || options.Dpi < 36 || options.Dpi > 600) throw new ArgumentOutOfRangeException(nameof(options), "PDF 해상도는 36~600 DPI입니다.");
        using var input = File.OpenRead(path); using var random = input.AsRandomAccessStream();
        var pdf = await PdfDocument.LoadFromStreamAsync(random).AsTask(token).ConfigureAwait(false);
        if (options.Page < 1 || options.Page > pdf.PageCount) throw new ArgumentOutOfRangeException(nameof(options), $"페이지는 1~{pdf.PageCount} 범위입니다.");
        using var page = pdf.GetPage((uint)options.Page - 1);
        int width = checked((int)Math.Ceiling(page.Size.Width * options.Dpi / 96));
        int height = checked((int)Math.Ceiling(page.Size.Height * options.Dpi / 96));
        try { Raster.ValidateSize(width, height); }
        catch (InvalidDataException e) { throw new InvalidDataException($"선택한 해상도는 {width:N0} × {height:N0}px입니다. DPI를 낮춰 주세요. " + e.Message); }
        using var rendered = new InMemoryRandomAccessStream();
        var renderOptions = new PdfPageRenderOptions { DestinationWidth = (uint)width, DestinationHeight = (uint)height, BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255) };
        await page.RenderToStreamAsync(rendered, renderOptions).AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); rendered.Seek(0);
        using var decoded = rendered.AsStreamForRead(); var raster = Raster.Load(decoded);
        return new(CompatibilityImport.Single(path, raster, options.Dpi, $"페이지 {options.Page} · {options.Dpi:0.#} DPI"),
            [$"{pdf.PageCount}페이지 중 {options.Page}페이지를 픽셀 이미지로 가져왔습니다. PDF/AI의 문자·벡터·레이어는 개별 편집되지 않습니다. 원본 파일은 변경하지 않습니다."]);
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
