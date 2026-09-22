using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Compositor.Windows;

internal static class NativePdfLifetimeTests
{
    internal static void Run(Action<string, Action> test, string directory)
    {
        test("PDF renderer unloads before process teardown and can render again", () =>
        {
            string path = Path.Combine(directory, "pdf-lifetime.pdf");
            var document = new Document { Width = 16, Height = 16, Dpi = 96 };
            document.Add(new Layer { Pixels = Raster.Solid(16, 16, Colors.Red) });
            using (var output = File.Create(path)) PdfCompatibility.Write(document, output);
            for (int i = 0; i < 3; i++)
            {
                Render(path);
                NativePdf.ReleaseUnused();
                using var process = Process.GetCurrentProcess();
                if (process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("Windows.Data.Pdf.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception($"PDF activation factories or render objects still retain the native device after release {i + 1}.");
            }
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Render(string path)
    {
        var result = PdfCompatibility.ReadAsync(path, new CompatibilityOptions(Dpi: 96, PreservePdfLayers: false), default).GetAwaiter().GetResult();
        var pixels = result.Document.Active!.Pixels.Data;
        int center = (8 * 16 + 8) * 4;
        if (pixels[center + 2] < 250 || pixels[center] > 5 || pixels[center + 3] != 255)
            throw new Exception("PDF renderer did not preserve the page after a native reload.");
    }
}
