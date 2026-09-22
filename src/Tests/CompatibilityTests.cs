using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using PsdSharp;

namespace Compositor.Windows;

public static class CompatibilityTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "compatibility"); Directory.CreateDirectory(root);
        string PathFor(string name) => Path.Combine(root, name);
        void Assert(bool condition, string message = "Compatibility assertion failed") { if (!condition) throw new Exception(message); }
        CompatibilityResult Read(string path, CompatibilityOptions? options = null) => Task.Run(() => CompatibilityImport.ReadAsync(path, options ?? new CompatibilityOptions())).GetAwaiter().GetResult();
        Document Single() { var d = new Document { Width = 64, Height = 40, Dpi = 150, Name = "한글 문서" }; d.Add(new Layer { Name = "배경", Pixels = Raster.Solid(64, 40, Colors.Red) }); return d; }
        string Fixture(string name) { string path = PathFor(name); using var source = typeof(CompatibilityTests).Assembly.GetManifestResourceStream("Morupixel.Compatibility." + name) ?? throw new Exception("Missing fixture " + name); using var output = File.Create(path); source.CopyTo(output); return path; }
        void Throws(Action action) { bool threw = false; try { action(); } catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or EndOfStreamException) { threw = true; } Assert(threw, "Expected a bounded import error"); }

        test("PDF export preserves physical page size and native renderer reads pixels", () =>
        {
            var doc = Single(); string path = PathFor("rgb.pdf"); ProjectStore.AtomicWrite(path, s => PdfCompatibility.Write(doc, s));
            var info = Task.Run(() => PdfCompatibility.InspectAsync(path)).GetAwaiter().GetResult(); Assert(info.Pages == 1); Assert(Math.Abs(info.WidthAt96Dpi - 64 * 96d / 150) < .01);
            var result = Read(path, new(Dpi: 150)); Assert(Math.Abs(result.Document.Width - 64) <= 1 && Math.Abs(result.Document.Height - 40) <= 1);
            var pixels = result.Document.Active!.Pixels; int i = (pixels.Height / 2 * pixels.Width + pixels.Width / 2) * 4; Assert(pixels.Data[i + 2] > 250 && pixels.Data[i] < 5);
        });
        test("PDF alpha soft mask renders on white and orientation stays upright", () =>
        {
            var doc = Single(); var raster = doc.Active!.Pixels; for (int y = 0; y < 40; y++) for (int x = 0; x < 64; x++) { int i = (y * 64 + x) * 4; if (y < 20) { raster.Data[i] = 255; raster.Data[i + 2] = 0; } else raster.Data[i + 3] = 0; }
            string path = PathFor("alpha.pdf"); ProjectStore.AtomicWrite(path, s => PdfCompatibility.Write(doc, s)); var actual = Read(path, new(Dpi: 150)).Document.Active!.Pixels;
            int top = (5 * actual.Width + 5) * 4, bottom = ((actual.Height - 5) * actual.Width + 5) * 4;
            Assert(actual.Data[top] > 245 && actual.Data[top + 2] < 10); Assert(actual.Data[bottom] > 245 && actual.Data[bottom + 1] > 245 && actual.Data[bottom + 2] > 245);
        });
        test("PDF multi page selection imports requested page and rejects missing page", () =>
        {
            string path = PathFor("two-pages.pdf"); WriteTwoPages(path); var info = Task.Run(() => PdfCompatibility.InspectAsync(path)).GetAwaiter().GetResult(); Assert(info.Pages == 2);
            var first = Read(path, new(Page: 1, Dpi: 72)).Document.Active!.Pixels; var second = Read(path, new(Page: 2, Dpi: 72)).Document.Active!.Pixels;
            int i = (20 * first.Width + 20) * 4; Assert(first.Data[i + 2] > 245 && first.Data[i] < 10); Assert(second.Data[i] > 245 && second.Data[i + 2] < 10);
            Throws(() => Read(path, new(Page: 3))); Throws(() => Read(path, new(Page: 1, Dpi: 1000)));
        });
        test("AI PDF compatible data opens and PostScript AI has actionable rejection", () =>
        {
            string path = PathFor("compatible.ai"); ProjectStore.AtomicWrite(path, s => PdfCompatibility.Write(Single(), s)); Assert(Read(path).Document.Layers.Count == 1);
            path = PathFor("legacy.ai"); File.WriteAllText(path, "%!PS-Adobe-3.0\n%%Creator: Illustrator\n"); Throws(() => Read(path));
        });
        test("Photoshop merged export is externally parseable and preserves alpha DPI", () =>
        {
            var doc = Single(); doc.Active!.Pixels.Data[3] = 42; string path = PathFor("flat.psd"); ProjectStore.AtomicWrite(path, s => PhotoshopCompatibility.Write(doc, s, false));
            using var input = File.OpenRead(path); var psd = PsdFile.Open(input); Assert(psd.Header.WidthInPixels == 64 && psd.Header.NumberOfChannels == 4 && psd.Layers.Count == 1);
            var actual = Read(path).Document; Assert(actual.Dpi == 150); Assert(actual.Active!.Pixels.Data.SequenceEqual(doc.Active.Pixels.Data));
        });
        test("Photoshop layer export preserves order unicode names visibility blend and pixels", () =>
        {
            var doc = Single(); doc.Add(new Layer { Name = "위쪽 글씨 가나다", Pixels = Raster.Solid(12, 9, Color.FromArgb(170, 0, 0, 255)), X = 4, Y = 5, Opacity = .6, Blend = BlendMode.Screen });
            doc.Add(new Layer { Name = "숨김", Pixels = Raster.Solid(3, 3, Colors.Green), Visible = false });
            string path = PathFor("layers.psd"); ProjectStore.AtomicWrite(path, s => PhotoshopCompatibility.Write(doc, s, true)); var actual = Read(path, new(SeparateLayers: true)).Document;
            Assert(actual.Layers.Count == 3 && actual.Layers[1].Name == "위쪽 글씨 가나다" && actual.Layers[1].Blend == BlendMode.Screen && !actual.Layers[2].Visible);
            var expectedPixels = Imaging.Render(doc).Data; var actualPixels = Imaging.Render(actual).Data; Assert(expectedPixels.Zip(actualPixels, (a, b) => Math.Abs(a - b)).Max() <= 1);
            ProjectStore.Save(actual, PathFor("layers.moruproj")); Assert(ProjectStore.Load(PathFor("layers.moruproj")).Layers.Count == 3);
        });
        test("Photoshop transformed masked layer export bakes geometry without changing original", () =>
        {
            var doc = Single(); doc.Active!.X = -3; doc.Active.Rotation = 8; doc.Active.Mask = Enumerable.Repeat((byte)128, 64 * 40).ToArray(); var before = Imaging.Render(doc).Data;
            string path = PathFor("masked.psd"); ProjectStore.AtomicWrite(path, s => PhotoshopCompatibility.Write(doc, s, true)); var actual = Read(path, new(SeparateLayers: true)).Document;
            var after = Imaging.Render(actual).Data; double maximum = 0; int alphaDifference = 0;
            for (int i = 0; i < before.Length; i += 4) { alphaDifference = Math.Max(alphaDifference, Math.Abs(before[i + 3] - after[i + 3])); for (int c = 0; c < 3; c++) maximum = Math.Max(maximum, Math.Abs(before[i + c] * before[i + 3] / 255d - after[i + c] * after[i + 3] / 255d)); }
            Assert(maximum <= 1 && alphaDifference <= 1, $"Masked transform premultiplied difference {maximum}, alpha {alphaDifference}"); Assert(doc.Active.Mask[0] == 128 && doc.Active.Rotation == 8);
        });
        test("Photoshop unsupported layered export does not replace existing destination", () =>
        {
            var doc = Single(); doc.Add(new Layer { Name = "그룹", Kind = LayerKind.Group, Pixels = new Raster(1, 1) });
            string path = PathFor("preserved.psd"); File.WriteAllText(path, "preserve"); Throws(() => ProjectStore.AtomicWrite(path, s => PhotoshopCompatibility.Write(doc, s, true))); Assert(File.ReadAllText(path) == "preserve");
        });
        test("Photoshop real external two layer PSD reads composite and pixel layers", () =>
        {
            string path = Fixture("2layers.psd"); var merged = Read(path); var layered = Read(path, new(SeparateLayers: true));
            Assert(merged.Document.Width == layered.Document.Width && layered.Document.Layers.Count >= 2); Assert(Imaging.Render(layered.Document).Data.Any(b => b != 0));
        });
        test("Photoshop real external mask PSD composite is supported", () =>
        {
            var result = Read(Fixture("layer_mask_data.psd")); Assert(result.Document.Width > 0 && result.Document.Active!.Pixels.Data.Any(b => b != 0));
        });
        test("Photoshop oversized header is rejected before decoding allocation", () =>
        {
            byte[] header = new byte[26]; "8BPS"u8.CopyTo(header); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14), 50000); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), 50000);
            string path = PathFor("huge.psd"); File.WriteAllBytes(path, header); Throws(() => Read(path));
            path = PathFor("broken.psd"); File.WriteAllBytes(path, [1, 2, 3]); Throws(() => Read(path));
        });
        test("Photoshop complex external mask document rejects unsupported layer structure", () =>
        {
            Throws(() => Read(Fixture("layer_mask_data.psd"), new(SeparateLayers: true)));
        });
        test("Photoshop independent layer mask respects document position and outside default", () =>
        {
            string path = PathFor("mask-reference.psd"); WriteMaskPsd(path); var doc = Read(path, new(SeparateLayers: true)).Document;
            Assert(doc.Active!.X == 2 && doc.Active.Y == 1 && doc.Active.Mask!.SequenceEqual(new byte[] { 0, 128, 0, 255 }));
        });
        test("PSD PSB independently encoded raw RLE ZIP prediction decode to reference pixels", () =>
        {
            foreach (bool psb in new[] { false, true }) foreach (int compression in new[] { 0, 1, 2, 3 })
            {
                string path = PathFor($"reference-{psb}-{compression}" + (psb ? ".psb" : ".psd")); WriteFlatPsd(path, psb, compression);
                var raster = Read(path).Document.Active!.Pixels;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 16; x++) { int i = (y * 16 + x) * 4; Assert(raster.Data[i] == 170 && raster.Data[i + 1] == y * 31 && raster.Data[i + 2] == x * 17 && raster.Data[i + 3] == 255, $"{psb}/{compression}/{x}/{y}"); }
            }
        });
        test("CAD independently written DXF renders color coordinates and splits layers", () =>
        {
            string path = PathFor("simple.dxf"); File.WriteAllText(path, DxfFixture()); var result = Read(path, new(CadLongEdge: 600, SeparateLayers: true));
            Assert(result.Document.Width == 600); Assert(result.Document.Layers.Count == 3); Assert(result.Document.Layers.Any(l => l.Name == "RED") && result.Document.Layers.Any(l => l.Name == "BLUE"));
            var raster = Imaging.Render(result.Document); Assert(raster.Data.Where((b, i) => i % 4 == 0).Any(b => b < 240));
            var blueLayer = result.Document.Layers.Single(l => l.Name == "BLUE"); var redLayer = result.Document.Layers.Single(l => l.Name == "RED");
            var blue = blueLayer.Pixels; var red = redLayer.Pixels;
            double CenterY(Raster r) { long sum = 0, count = 0; for (int y = 0; y < r.Height; y++) for (int x = 0; x < r.Width; x++) if (r.Data[(y * r.Width + x) * 4 + 3] > 10) { sum += y; count++; } return sum / (double)Math.Max(1, count); }
            Assert(CenterY(blue) + blueLayer.Y < CenterY(red) + redLayer.Y, "CAD positive Y should be above lower Y");
        });
        test("CAD unsupported entities are reported rather than silently disappearing", () =>
        {
            string path = PathFor("with-ray.dxf"); File.WriteAllText(path, DxfFixture(true)); var result = Read(path, new(CadLongEdge: 512)); Assert(result.Warnings.Any(w => w.Contains("RAY")));
        });
        test("DWG real external binary file opens without AutoCAD or external converter", () =>
        {
            var result = Read(Fixture("block-rotation.dwg"), new(CadLongEdge: 700)); Assert(result.Document.Layers.Count >= 2); Assert(Math.Max(result.Document.Width, result.Document.Height) == 700);
            Assert(result.Document.Layers.Skip(1).Any(l => l.Pixels.Data.Where((b, i) => i % 4 == 3).Any(b => b > 0)));
        });
        test("Compatibility cancellation and empty files do not create documents", () =>
        {
            string path = PathFor("cancel.pdf"); ProjectStore.AtomicWrite(path, s => PdfCompatibility.Write(Single(), s)); using var cts = new CancellationTokenSource(); cts.Cancel(); bool canceled = false;
            try { Task.Run(() => CompatibilityImport.ReadAsync(path, new(), cts.Token)).GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; } Assert(canceled);
            path = PathFor("empty.dwg"); File.WriteAllBytes(path, []); Throws(() => Read(path));
        });
    }
    static string DxfFixture(bool ray = false) => "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1015\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nRED\n62\n1\n10\n0\n20\n0\n30\n0\n11\n100\n21\n0\n31\n0\n0\nLINE\n8\nBLUE\n62\n5\n10\n0\n20\n30\n30\n0\n11\n100\n21\n30\n31\n0\n" + (ray ? "0\nRAY\n8\nRED\n10\n0\n20\n0\n30\n0\n11\n1\n21\n1\n31\n0\n" : "") + "0\nENDSEC\n0\nEOF\n";
    static void WriteMaskPsd(string path)
    {
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        void U16(int value) => writer.Write(new[] { (byte)(value >> 8), (byte)value });
        void U32(int value) => writer.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        void Section(Action body) { long p = stream.Position; U32(0); body(); long end = stream.Position; stream.Position = p; U32((int)(end - p - 4)); stream.Position = end; }
        writer.Write("8BPS"u8); U16(1); writer.Write(new byte[6]); U16(4); U32(4); U32(5); U16(8); U16(3); U32(0); U32(0);
        Section(() => { Section(() => {
            U16(-1); U32(1); U32(2); U32(3); U32(4); U16(5);
            foreach (int channel in new[] { 0, 1, 2, -1, -2 }) { U16(channel); U32(channel == -2 ? 4 : 6); }
            writer.Write("8BIMnorm"u8); writer.Write(new byte[] { 255, 0, 0, 0 });
            Section(() => { U32(20); U32(1); U32(3); U32(3); U32(4); writer.Write(new byte[4]); U32(0); writer.Write(new byte[] { 1, 77, 0, 0 }); });
            foreach (byte value in new byte[] { 255, 0, 0, 255 }) { U16(0); writer.Write(Enumerable.Repeat(value, 4).ToArray()); } U16(0); writer.Write(new byte[] { 128, 255 });
        }); U32(0); }); U16(0); writer.Write(new byte[5 * 4 * 4]);
    }
    static void WriteFlatPsd(string path, bool psb, int compression)
    {
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        void U16(int value) => writer.Write(new[] { (byte)(value >> 8), (byte)value });
        void U32(int value) => writer.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        writer.Write("8BPS"u8); U16(psb ? 2 : 1); writer.Write(new byte[6]); U16(3); U32(8); U32(16); U16(8); U16(3); U32(0); U32(0); if (psb) U32(0); U32(0); U16(compression);
        byte[] data = new byte[16 * 8 * 3]; for (int y = 0; y < 8; y++) for (int x = 0; x < 16; x++) { data[y * 16 + x] = (byte)(x * 17); data[128 + y * 16 + x] = (byte)(y * 31); data[256 + y * 16 + x] = 170; }
        if (compression == 0) writer.Write(data);
        else if (compression == 1) { for (int row = 0; row < 24; row++) { if (psb) U32(17); else U16(17); } for (int row = 0; row < 24; row++) { writer.Write((byte)15); writer.Write(data, row * 16, 16); } }
        else
        {
            if (compression == 3) for (int row = 0; row < 24; row++) for (int x = 15; x > 0; x--) data[row * 16 + x] = unchecked((byte)(data[row * 16 + x] - data[row * 16 + x - 1]));
            using var z = new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionLevel.Optimal, true); z.Write(data);
        }
    }
    static void WriteTwoPages(string path)
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 48] /Resources << >> /Contents 5 0 R >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 48] /Resources << >> /Contents 6 0 R >>", "", "" };
        var red = "1 0 0 rg 0 0 72 48 re f\n"; var blue = "0 0 1 rg 0 0 72 48 re f\n";
        objects[4] = $"<< /Length {red.Length} >>\nstream\n{red}endstream"; objects[5] = $"<< /Length {blue.Length} >>\nstream\n{blue}endstream";
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length; text.Append("xref\n0 7\n0000000000 65535 f \n"); foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n"); text.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }
}
