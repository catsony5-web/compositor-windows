using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;

namespace Compositor.Windows;

// NativePdf declares the Windows PDF renderer's interfaces by hand instead of shipping the
// generated Windows SDK projection. These checks tie its constants to the Windows Runtime rules
// and to the renderer installed on this machine, and cover the paths the projection used to handle.
internal static class NativePdfInteropTests
{
    const string PdfDocumentSignature = "rc(Windows.Data.Pdf.PdfDocument;{ac7ebedd-80fa-4089-846e-81b77ff5a86c})";

    // A parameterized interface identifier is a name-based (version 5) UUID: SHA-1 over a fixed
    // namespace followed by the UTF-8 type signature.
    internal static Guid ParameterizedId(string signature)
    {
        byte[] space = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
        byte[] hash = SHA1.HashData([.. space, .. Encoding.UTF8.GetBytes(signature)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    internal static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "native-pdf"); Directory.CreateDirectory(root);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string WritePdf(string name, Action<Raster> paint, int width = 32, int height = 16)
        {
            var document = new Document { Width = width, Height = height, Dpi = 96 };
            var raster = Raster.Solid(width, height, Colors.Red); paint(raster); document.Add(new Layer { Pixels = raster });
            string path = Path.Combine(root, name);
            using (var output = File.Create(path)) PdfCompatibility.Write(document, output);
            return path;
        }
        // Left half red, right half blue.
        void Halves(Raster r) { for (int y = 0; y < r.Height; y++) for (int x = r.Width / 2; x < r.Width; x++) { int i = (y * r.Width + x) * 4; r.Data[i] = 255; r.Data[i + 1] = 0; r.Data[i + 2] = 0; } }

        test("PDF interop identifiers follow the Windows Runtime signature rules", () =>
        {
            // Published identifiers of IIterable<String> and IVector<String> validate the algorithm.
            Check(ParameterizedId("pinterface({faa585ea-6214-4217-afda-7f46de5869b3};string)") == new Guid("e2fcc7c1-3bfc-5a0b-b2b0-72e769d1cb7e"), "IIterable<String> identifier differs");
            Check(ParameterizedId("pinterface({913337e9-11a1-4345-a3a2-4e7f956e222d};string)") == new Guid("98b9acc1-4b56-532e-ac73-03d5291cca90"), "IVector<String> identifier differs");
            Check(ParameterizedId($"pinterface({{fcdcf02c-e5d8-4478-915a-4d90b74b83a5}};{PdfDocumentSignature})") == NativePdf.DocumentLoadedHandlerId, "Completion handler identifier differs");
            Check(ParameterizedId($"pinterface({{9fc2b0bb-e446-44e2-aa61-9cab8f636af2}};{PdfDocumentSignature})") == NativePdf.DocumentLoadOperationId, "Load operation identifier differs");
        });

        test("Windows PDF loading operation answers the declared interface", () =>
        {
            string path = WritePdf("identity.pdf", _ => { });
            using var input = File.OpenRead(path);
            Check(NativePdf.LoadOperationSupportsAsync(input, NativePdf.DocumentLoadOperationId).GetAwaiter().GetResult(), "The installed renderer does not answer IAsyncOperation<PdfDocument>");
            Check(!NativePdf.LoadOperationSupportsAsync(input, Guid.NewGuid()).GetAwaiter().GetResult(), "The interface check accepts any identifier");
        });

        test("Windows PDF renderer reads size, source region and transparent background", () =>
        {
            string path = WritePdf("halves.pdf", Halves, 64, 32);
            var info = PdfCompatibility.InspectAsync(path).GetAwaiter().GetResult();
            Check(info.Pages == 1 && Math.Abs(info.WidthAt96Dpi - 64) < .01 && Math.Abs(info.HeightAt96Dpi - 32) < .01, $"Page size {info.WidthAt96Dpi} x {info.HeightAt96Dpi}");
            VectorContent vector; using (var source = File.OpenRead(path)) vector = VectorContent.FromPdf(64, 32, 1, source);
            // A region inside the blue half, scaled to 12 x 16: blue away from its outer edge.
            var right = PdfCompatibility.RenderRegionAsync(vector, new Rect(40, 0, 24, 32), 12, 16, default).GetAwaiter().GetResult();
            Check(right.Width == 12 && right.Height == 16, "Region size differs");
            for (int y = 1; y < 15; y++) for (int x = 1; x < 11; x++)
            {
                int i = (y * 12 + x) * 4;
                Check(right.Data[i] > 220 && right.Data[i + 2] < 40 && right.Data[i + 3] > 240, $"The source region did not select the blue half at {x},{y}");
            }
            using var stream = File.OpenRead(path);
            var full = PdfCompatibility.RenderPageAsync(stream, 1, 64, 32, true, default).GetAwaiter().GetResult();
            int left = (16 * 64 + 8) * 4, rightPixel = (16 * 64 + 56) * 4;
            Check(full.Data[left + 2] > 240 && full.Data[left] < 15 && full.Data[rightPixel] > 240 && full.Data[rightPixel + 2] < 15, "Full page colors differ");
        });

        test("Windows PDF renderer serves parallel renders from one stream", () =>
        {
            string path = WritePdf("parallel.pdf", Halves);
            using var shared = File.OpenRead(path);
            var renders = Enumerable.Range(0, 4).Select(_ => PdfCompatibility.RenderPageAsync(shared, 1, 32, 16, false, default)).ToArray();
            Task.WaitAll(renders);
            foreach (var raster in renders.Select(r => r.Result))
                Check(raster.Data[(8 * 32 + 4) * 4 + 2] > 240 && raster.Data[(8 * 32 + 28) * 4] > 240, "A parallel render read the wrong bytes");
        });

        test("Windows PDF renderer reports damaged, protected and canceled loads as errors", () =>
        {
            string broken = Path.Combine(root, "broken.pdf");
            File.WriteAllText(broken, "%PDF-1.4\n%Morupixel\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 9 0 R >>\n%%EOF\n");
            Exception? damaged = null;
            try { PdfCompatibility.InspectAsync(broken).GetAwaiter().GetResult(); } catch (Exception e) { damaged = e; }
            Check(damaged != null && damaged is not NullReferenceException && damaged is not AccessViolationException, "A damaged PDF did not report an error: " + damaged);

            string locked = Path.Combine(root, "locked.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.SecuritySettings.UserPassword = "morupixel"; pdf.Save(locked); }
            Exception? protectedError = null;
            try { PdfCompatibility.InspectAsync(locked).GetAwaiter().GetResult(); } catch (Exception e) { protectedError = e; }
            // CompatibilityDialog recognizes ERROR_WRONG_PASSWORD to explain password-protected files.
            Check(protectedError?.HResult == unchecked((int)0x8007052B), "A password-protected PDF lost its error code: " + protectedError);

            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool stopped = false;
            try { PdfCompatibility.InspectAsync(WritePdf("cancel.pdf", _ => { }), canceled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "A canceled PDF load still completed");
        });
    }
}
