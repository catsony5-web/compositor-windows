using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

public static class VectorContentTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Document Drawing()
    {
        var doc = new Document { Width = 64, Height = 64, Name = "Vector quality" };
        var geometry = new LineGeometry(new Point(10.3, 0), new Point(10.3, 64));
        var source = VectorContent.FromPaths(64, 64, [new(geometry, Colors.Black, false, .15)]);
        doc.Add(new Layer { Name = "Thin vector", Kind = LayerKind.Vector, Vector = source, Pixels = Imaging.Draw(64, 64, dc => dc.DrawDrawing(source.Drawing)) });
        return doc;
    }
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "vector-content"); Directory.CreateDirectory(root);
        test("Vector paths render thin details from geometry at 1600 percent", () =>
        {
            var doc = Drawing(); var zoom = DesignRenderer.Render(doc, new Rect(0, 0, 64, 64), 1024, 1024);
            byte maximum = 0; int solid = 0; for (int x = 0; x < zoom.Width; x++) { byte a = zoom.Data[(512 * zoom.Width + x) * 4 + 3]; maximum = Math.Max(maximum, a); if (a > 230) solid++; }
            Assert(maximum > 245 && solid is >= 1 and <= 3, "Subpixel vector was enlarged from its blurred raster preview");
            var cached = doc.Active!.Pixels; Assert(Enumerable.Range(0, cached.Width).Max(x => cached.Data[(32 * cached.Width + x) * 4 + 3]) < 100, "Test requires a genuinely subpixel source line");
        });
        test("Vector source survives project roundtrip and rasterize undo", () =>
        {
            var doc = Drawing(); string path = Path.Combine(root, "source.moruproj"); ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            var area = new Rect(8, 10, 6, 20);
            Assert(DesignRenderer.Render(doc, area, 120, 400).Data.SequenceEqual(DesignRenderer.Render(loaded, area, 120, 400).Data), "Saved project lost vector source or coordinates");
            var history = new History(); history.Reset(loaded); var before = loaded.Snapshot(); var pixels = loaded.Active!.Pixels;
            DocumentFeatures.Rasterize(loaded.Active); history.Commit("Rasterize", before, loaded);
            Assert(loaded.Active.Vector == null && ReferenceEquals(pixels, loaded.Active.Pixels), "Rasterizing changed the document-resolution pixels");
            Assert(history.RetainedBytes(loaded) >= before.Active!.Vector!.ByteLength, "History omitted retained vector source memory");
            Assert(history.Undo(loaded).Active!.Vector != null, "Undo cannot restore the vector source");
        });
        test("Affine vector groups retain detail and group visibility", () =>
        {
            var doc = Drawing(); var layer = doc.Active!; var group = DocumentFeatures.CreateGroup(doc); group.Scale = 2; group.X = -8; group.Y = -5;
            doc.Layers.Insert(0, group); layer.ParentId = group.Id;
            var grouped = DesignRenderer.Render(doc, new Rect(0, 0, 64, 64), 512, 512);
            var direct = layer.Snapshot(); direct.ParentId = null; direct.Scale = 2; direct.X = -8; direct.Y = -5;
            var reference = new Document { Width = 64, Height = 64 }; reference.Add(direct);
            Assert(grouped.Data.SequenceEqual(DesignRenderer.Render(reference, new Rect(0, 0, 64, 64), 512, 512).Data), "Group scaled a bitmap instead of its children");
            group.Visible = false; Assert(DesignRenderer.Render(doc, new Rect(0, 0, 64, 64), 512, 512).Data.All(b => b == 0), "Hidden group still rendered");
        });
        test("Layered PDF vector viewport crops in correct page coordinates", () =>
        {
            string path = Path.Combine(root, "layers.pdf"); LayeredCompatibilityTests.WritePdf(path, false);
            var doc = CompatibilityImport.ReadAsync(path, new(Dpi: 72)).GetAwaiter().GetResult().Document;
            Assert(doc.Layers.Any(l => l.Vector?.Format == VectorFormat.Pdf), "PDF import discarded original paint streams");
            var image = DesignRenderer.Render(doc, new Rect(30, 20, 20, 20), 400, 400);
            int red = (100 * 400 + 100) * 4, green = (300 * 400 + 100) * 4;
            Assert(image.Data[red + 2] > 245 && image.Data[red + 1] < 10, "PDF source crop moved the red region");
            Assert(image.Data[green + 1] > 245 && image.Data[green + 2] < 10, "PDF source crop lost the interleaved green region");
            string project = Path.Combine(root, "pdf-source.moruproj"); ProjectStore.Save(doc, project);
            Assert(image.Data.SequenceEqual(DesignRenderer.Render(ProjectStore.Load(project), new Rect(30, 20, 20, 20), 400, 400).Data), "Embedded PDF source did not survive save/load");
        });
        test("Vector PDF export emits paths and preserves affine placement", () =>
        {
            var doc = Drawing(); doc.Active!.X = 10; doc.Active.Y = 4; doc.Active.Rotation = 13;
            using var encoded = new MemoryStream(); VectorPdfExport.Write(doc, encoded); encoded.Position = 0;
            using (var pdf = PdfReader.Open(encoded, PdfDocumentOpenMode.Import)) Assert(pdf.Pages[0].Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject") == null, "Vector-only PDF unexpectedly contains image XObjects");
            encoded.Position = 0; var source = VectorContent.FromPdf(64, 64, 1, encoded);
            var actual = PdfCompatibility.RenderRegionAsync(source, new Rect(0, 0, 64, 64), 640, 640, default).GetAwaiter().GetResult();
            var expected = DesignRenderer.Render(doc, new Rect(0, 0, 64, 64), 640, 640);
            Assert(actual.Data.Zip(expected.Data, (a, b) => Math.Abs(a - b)).Average() < 1, "Vector PDF changed geometry position or line width");
        });
        test("Raster photo pixels remain unchanged across design rendering", () =>
        {
            var doc = new Document { Width = 20, Height = 20 }; var photo = Raster.Solid(20, 20, Colors.Red); photo.Data[0] = 255;
            doc.Add(new Layer { Pixels = photo }); var before = photo.Data.ToArray();
            var result = DesignRenderer.Render(doc, new Rect(0, 0, 20, 20), 20, 20);
            Assert(result.Data.SequenceEqual(before) && photo.Data.SequenceEqual(before), "Design view resampled or mutated the original photo");
        });
        test("Enlarged grouped vector exports redraw original paths at output resolution", () =>
        {
            var doc = Drawing(); var layer = doc.Active!; var group = DocumentFeatures.CreateGroup(doc); group.Scale = 16;
            layer.ParentId = group.Id; doc.Layers.Insert(0, group); doc.Width = doc.Height = 1024;
            string path = Path.Combine(root, "enlarged.png"); ImportExport.Export(doc, path); var pixels = Raster.Load(path);
            Assert(Enumerable.Range(0, pixels.Width).Max(x => pixels.Data[(512 * pixels.Width + x) * 4 + 3]) > 245, "PNG export enlarged the low-resolution vector cache");
            Assert(doc.Active!.Vector != null, "Raster export discarded the editable vector source");
        });
        test("Vector PDF retains clipped arc paths without bitmap substitution", () =>
        {
            var path = new StreamGeometry(); using (var dc = path.Open()) { dc.BeginFigure(new Point(2, 20), false, false); dc.ArcTo(new Point(38, 20), new Size(18, 18), 0, false, SweepDirection.Clockwise, true, false); }
            var source = VectorContent.FromPaths(40, 40, [new(path, Colors.Red, false, 1.2, new RectangleGeometry(new Rect(4, 4, 30, 30)))]);
            var doc = new Document { Width = 40, Height = 40 }; doc.Add(new Layer { Kind = LayerKind.Vector, Vector = source, Pixels = new Raster(40, 40) });
            using var encoded = new MemoryStream(); VectorPdfExport.Write(doc, encoded); encoded.Position = 0;
            var saved = VectorContent.FromPdf(40, 40, 1, encoded); var actual = PdfCompatibility.RenderRegionAsync(saved, new Rect(0, 0, 40, 40), 400, 400, default).GetAwaiter().GetResult();
            Assert(actual.Data.Where((_, i) => i % 4 == 3).Any(a => a > 200), "Clipped arc disappeared in vector PDF export");
        });
        test("Canvas updates retained viewport after zoom and rejects stale requests", () =>
        {
            var doc = Drawing(); var canvas = new CanvasView { Document = doc, Composite = Imaging.Render(doc).Bitmap(), DesignMode = true, Zoom = 8 };
            var size = new Size(300, 240); canvas.Measure(size); canvas.Arrange(new Rect(size)); canvas.UpdateLayout();
            void Render() { canvas.InvalidateVisual(); canvas.UpdateLayout(); var bitmap = new RenderTargetBitmap(300, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas); }
            Render(); canvas.Zoom = 12; canvas.Pan = new Vector(100, 10); Render();
            var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { Render(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || watch.ElapsedMilliseconds > 10000) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            Assert(canvas.IsDesignPreviewReady && canvas.DesignPreviewError == null, "Viewport did not refresh: " + canvas.DesignPreviewError);
            canvas.DesignMode = false; Render(); Assert(!canvas.IsDesignPreviewReady, "Photo mode retained a stale vector viewport"); canvas.CancelDesignPreview();
        });
    }
}
