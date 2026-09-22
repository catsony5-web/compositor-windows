using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

internal static class UnifiedWorkspaceTests
{
    internal static void Run(Action<string, Action> test, string directory)
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        test("Import STA releases its WPF dispatcher before task completion", () =>
        {
            System.Windows.Threading.Dispatcher? owner = null;
            var pixels = CompatibilityImport.OnSta(() =>
            {
                owner = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                return Imaging.Draw(8, 8, dc => dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 8, 8)));
            }, default).GetAwaiter().GetResult();
            Assert(owner?.HasShutdownFinished == true && pixels.Data[2] == 255, "Import task outlived its native rendering owner");
        });
        Document Scene()
        {
            var doc = new Document { Width = 160, Height = 120 };
            doc.Add(VectorShapes.Create(new ShapeSpec { Width = 160, Height = 120, FillArgb = 0xFFFFFFFF }));
            var group = new Layer { Kind = LayerKind.Group, Name = "CAD objects", Pixels = new Raster(160, 120), X = 3, Y = 2 };
            doc.Add(group);
            for (int i = 0; i < 200; i++)
            {
                var source = VectorContent.FromPaths(8, 8, [new(new RectangleGeometry(new Rect(.4, .4, 7, 7)), Colors.Black, false, .2)]);
                doc.Add(new Layer { Name = "Object " + i, Kind = LayerKind.Vector, Vector = source,
                    ParentId = group.Id, Pixels = Imaging.Draw(8, 8, dc => dc.DrawDrawing(source.Drawing)), X = i % 20 * 8, Y = i / 20 * 8 });
            }
            return doc;
        }
        test("Unified CAD object groups keep precise vector selection after native save", () =>
        {
            var doc = Scene(); string path = System.IO.Path.Combine(directory, "unified-objects.moruproj");
            ProjectStore.Save(doc, path); var saved = ProjectStore.Load(path);
            Assert(saved.Layers.Count == 202 && saved.Layers.Count(l => l.Vector != null) == 200, "Object structure was flattened");
            var selection = PrecisionWand.Select(saved, new Point(6, 5), 16, true, true, true, 32);
            Assert(selection.Weight(6, 5) > .99 && selection.Weight(14, 5) == 0, "Wand leaked between adjacent CAD objects");
            Assert(selection.CoverageBounds != null && selection.CanvasWidth / selection.CoverageBounds.Value.Width >= 16,
                "Design selection discarded subpixel precision");
        });
        test("Batched CAD paths retain transformed group clipping and hidden object visibility", () =>
        {
            var doc = Scene(); doc.Layers[2].Visible = false;
            var area = new Rect(0, 0, 20, 15);
            var actual = DesignRenderer.Render(doc, area, 320, 240);
            var reference = doc.Snapshot();
            foreach (var layer in reference.Layers.Where(l => l.Vector != null))
                layer.Mask = Enumerable.Repeat((byte)255, layer.Pixels.Width * layer.Pixels.Height).ToArray();
            var expected = DesignRenderer.Render(reference, area, 320, 240);
            Assert(actual.Data.Zip(expected.Data).Max(pair => Math.Abs(pair.First - pair.Second)) <= 2,
                "Batched paths differ from isolated masked compositing");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool rejected = false;
            try { DesignRenderer.Render(doc, area, 320, 240, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            Assert(rejected, "CAD path batching ignored cancellation");
        });
    }
}
