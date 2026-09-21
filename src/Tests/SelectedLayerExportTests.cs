using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class SelectedLayerExportTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
        static Layer Paint(string name, int width, int height, Color color, double x = 0, double y = 0) =>
            new() { Name = name, Pixels = Raster.Solid(width, height, color), X = x, Y = y };
        test("selected layer export isolates masked pixels and crops without changing document", () =>
        {
            var document = new Document { Width = 10, Height = 9, Dpi = 150 };
            document.Add(Paint("background", 10, 9, Colors.Blue));
            var selected = Paint("selected", 3, 2, Colors.Red, 4, 3); selected.Mask = [0, 128, 255, 0, 128, 255];
            document.Add(selected); var history = new History(); history.Reset(document);
            var before = document.Snapshot(); var pixels = selected.Pixels.Data.ToArray(); var mask = selected.Mask.ToArray();
            var cropped = SelectedLayerExport.Render(document, [selected.Id]);
            Check(cropped.CanvasBounds == new Int32Rect(5, 3, 2, 2), "Crop must follow nonzero mask coverage");
            Check(cropped.Image.Data.SequenceEqual(new byte[] { 0, 0, 255, 128, 0, 0, 255, 255, 0, 0, 255, 128, 0, 0, 255, 255 }), "Mask and alpha must survive isolation");
            var full = SelectedLayerExport.Render(document, [selected.Id], false);
            Check(full.Image.Width == 10 && full.Image.Height == 9 && full.Image.Data[3] == 0, "Canvas export included backdrop");
            Check(document.Revision == before.Revision && document.ActiveId == before.ActiveId && document.Layers.Count == 2 && selected.Pixels.Data.SequenceEqual(pixels) && selected.Mask.SequenceEqual(mask), "Export mutated document");
            Check(!history.Dirty(document) && !history.CanUndo, "Export affected history");
        });
        test("selected child export retains nested group transform masks and opacity without siblings", () =>
        {
            var document = new Document { Width = 12, Height = 12 };
            var outer = Paint("outer", 8, 8, Colors.Transparent, 2, 1); outer.Kind = LayerKind.Group; outer.Opacity = .5;
            var inner = Paint("inner", 4, 4, Colors.Transparent, 1, 1); inner.Kind = LayerKind.Group; inner.ParentId = outer.Id;
            inner.Mask = Enumerable.Repeat((byte)128, 16).ToArray();
            var child = Paint("child", 2, 2, Colors.Red); child.ParentId = inner.Id;
            var sibling = Paint("sibling", 8, 8, Colors.Blue); sibling.ParentId = outer.Id;
            document.Add(outer); document.Add(inner); document.Add(sibling); document.Add(child);
            var result = SelectedLayerExport.Render(document, [child.Id]);
            Check(result.CanvasBounds == new Int32Rect(3, 2, 2, 2), "Nested transform was lost");
            for (int i = 0; i < result.Image.Data.Length; i += 4)
                Check(result.Image.Data[i] == 0 && result.Image.Data[i + 2] == 255 && result.Image.Data[i + 3] == 64, "Parent mask/opacity changed or sibling leaked");
        });
        test("selected group includes visible subtree while explicit hidden selection exports visibly", () =>
        {
            var document = new Document { Width = 8, Height = 8 };
            var group = Paint("group", 8, 8, Colors.Transparent); group.Kind = LayerKind.Group; group.Visible = false;
            var child = Paint("child", 3, 2, Colors.Red, 1, 2); child.ParentId = group.Id;
            var hidden = Paint("hidden", 8, 8, Colors.Blue); hidden.ParentId = group.Id; hidden.Visible = false;
            document.Add(group); document.Add(child); document.Add(hidden);
            var selectedGroup = SelectedLayerExport.Render(document, [group.Id]);
            Check(selectedGroup.CanvasBounds == new Int32Rect(1, 2, 3, 2) && selectedGroup.Image.Data[2] == 255, "Group visibility or subtree selection incorrect");
            var hiddenOnly = SelectedLayerExport.Render(document, [hidden.Id]);
            Check(hiddenOnly.Image.Width == 8 && hiddenOnly.Image.Data[0] == 255, "Explicit hidden layer not exported");
            Check(!group.Visible && !hidden.Visible, "Source visibility changed");
        });
        test("selected clipping base preserves composite and absent base detaches without including it", () =>
        {
            var document = new Document { Width = 6, Height = 4 };
            var lower = Paint("lower", 1, 1, Colors.Green); document.Add(lower);
            var clipBase = Paint("base", 2, 2, Colors.Blue, 2, 1); document.Add(clipBase);
            var clip = Paint("clip", 6, 4, Colors.Red); clip.Clipped = true; document.Add(clip);
            var both = SelectedLayerExport.Render(document, [clipBase.Id, clip.Id]);
            Check(both.CanvasBounds == new Int32Rect(2, 1, 2, 2) && both.Image.Data[2] == 255 && both.IndependentClippingCount == 0, "Clipping pair changed");
            var alone = SelectedLayerExport.Render(document, [clip.Id]);
            Check(alone.Image.Width == 6 && alone.Image.Height == 4 && alone.IndependentClippingCount == 1, "Missing base should not hide or leak unselected pixels");
            var separated = SelectedLayerExport.Render(document, [lower.Id, clip.Id]);
            Check(separated.Image.Width == 6 && separated.Image.Height == 4 && separated.Image.Data[2] == 255, "Clip attached to an unrelated selected base");
            Check(clip.Clipped, "Export detached live clipping");
        });
        test("selected adjustments apply only to selected images and adjustment alone has no tight bounds", () =>
        {
            var document = new Document { Width = 3, Height = 3 };
            var image = Paint("image", 3, 3, Color.FromRgb(50, 50, 50)); document.Add(image);
            var adjustment = DocumentFeatures.CreateAdjustment(document, new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = 1 }, "exposure"); document.Add(adjustment);
            var imageOnly = SelectedLayerExport.Render(document, [image.Id]);
            Check(imageOnly.Image.Data[0] == 50, "Unselected adjustment was exported");
            var withAdjustment = SelectedLayerExport.Render(document, [image.Id, adjustment.Id]);
            Check(withAdjustment.Image.Data.SequenceEqual(Imaging.Render(document).Data) && withAdjustment.Image.Data[0] > 50, "Selected adjustment not applied");
            Reject<InvalidOperationException>(() => SelectedLayerExport.Render(document, [adjustment.Id]));
            var emptyCanvas = SelectedLayerExport.Render(document, [adjustment.Id], false);
            Check(Enumerable.Range(0, 9).All(i => emptyCanvas.Image.Data[i * 4 + 3] == 0), "Adjustment created opacity");
        });
        test("selected export trims fractional alpha but keeps canvas boundary and rejects stale selection", () =>
        {
            var document = new Document { Width = 3, Height = 3 };
            var image = Paint("image", 2, 2, Color.FromArgb(1, 255, 0, 0), -1, -1); document.Add(image);
            var result = SelectedLayerExport.Render(document, [image.Id]);
            Check(result.CanvasBounds == new Int32Rect(0, 0, 1, 1) && result.Image.Data[3] == 1, "Low alpha or canvas boundary lost");
            Reject<InvalidOperationException>(() => SelectedLayerExport.Render(document, []));
            Reject<InvalidOperationException>(() => SelectedLayerExport.Render(document, [Guid.NewGuid()]));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Reject<OperationCanceledException>(() => SelectedLayerExport.Render(document, [image.Id], token: cancellation.Token));
        });
        test("selected layer PNG TIFF and JPEG export preserve DPI and format alpha policy", () =>
        {
            Directory.CreateDirectory(directory);
            var document = new Document { Width = 8, Height = 8, Dpi = 300 };
            var image = Paint("asset", 2, 2, Color.FromArgb(128, 255, 0, 0), 3, 3); document.Add(image);
            foreach (string format in new[] { "png", "tiff", "jpg" })
            {
                string path = Path.Combine(directory, "selected-export." + format);
                SelectedLayerExport.Export(document, [image.Id], path, false);
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0]; var raster = Raster.FromBitmap(frame);
                Check(frame.PixelWidth == 8 && frame.PixelHeight == 8 && Math.Abs(frame.DpiX - 300) < .1, "Canvas dimensions or DPI lost in " + format);
                if (format == "jpg") Check(raster.Data[3] == 255 && raster.Data[2] > 245, "JPEG should flatten transparent corners to white");
                else Check(raster.Data[3] == 0 && raster.Data[(3 * 8 + 3) * 4 + 3] == 128, "Alpha lost in " + format);
            }
        });
        test("selected retained shape export uses geometry at transformed output scale", () =>
        {
            var document = new Document { Width = 24, Height = 24 };
            document.Add(Paint("unselected backdrop", 24, 24, Colors.Green));
            var shape = VectorShapes.Create(new ShapeSpec { Kind = ShapeKind.Ellipse, Width = 4, Height = 4, FillArgb = 0xFFFF0000 }, 2, 3);
            shape.Scale = 4; shape.Opacity = .5;
            // Geometry, rather than a stale low-resolution thumbnail, must drive export.
            shape.Pixels = Raster.Solid(4, 4, Colors.Blue); document.Add(shape);
            var result = SelectedLayerExport.Render(document, [shape.Id], false);
            int center = (11 * 24 + 10) * 4;
            Check(result.Image.Data[center] == 0 && result.Image.Data[center + 2] == 255 && result.Image.Data[center + 3] == 128, "Shape export used cached raster or lost opacity");
            Check(result.Image.Data[3] == 0, "Shape export included unrelated backdrop");
            Check(shape.Pixels.Data[0] == 255 && shape.Kind == LayerKind.Shape, "Export rewrote retained geometry or cache");
        });
        test("shape project load rejects mismatched dimensions and refreshes matching-size cache", () =>
        {
            Directory.CreateDirectory(directory);
            var document = new Document { Width = 8, Height = 8 };
            var shape = VectorShapes.Create(new ShapeSpec { Width = 4, Height = 3, FillArgb = 0xFFFF0000 });
            shape.Pixels = Raster.Solid(4, 3, Colors.Blue); document.Add(shape);
            string valid = Path.Combine(directory, "selected-shape-cache.moruproj"); ProjectStore.Save(document, valid);
            var loaded = ProjectStore.Load(valid);
            Check(loaded.Active!.Shape == shape.Shape && loaded.Active.Pixels.Data[2] == 255 && loaded.Active.Pixels.Data[0] == 0, "Shape cache disagrees with retained properties after loading");
            string malformed = Path.Combine(directory, "selected-shape-mismatch.moruproj"); File.Copy(valid, malformed, true);
            using (var zip = ZipFile.Open(malformed, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("document.json")!;
                JsonNode metadata;
                using (var reader = new StreamReader(entry.Open())) metadata = JsonNode.Parse(reader.ReadToEnd())!;
                metadata["Layers"]![0]!["Shape"]!["Width"] = 5;
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("document.json").Open()); writer.Write(metadata.ToJsonString());
            }
            Reject<InvalidDataException>(() => ProjectStore.Load(malformed));
            shape.Shape = shape.Shape! with { Width = 5 };
            Reject<InvalidDataException>(() => document.Validate());
            Reject<InvalidDataException>(() => SelectedLayerExport.Render(document, [shape.Id]));
            Reject<InvalidDataException>(() => Imaging.Composite(new Raster(8, 8), shape));
        });
    }
}
