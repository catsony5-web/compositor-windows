using System.Windows.Media;

namespace Compositor.Windows;

public static class GroupedRenderingTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static Layer AddGroup(Document document, Guid? parent = null, int? size = null)
        {
            var group = new Layer { Name = "CAD layer", Kind = LayerKind.Group,
                Pixels = new Raster(size ?? document.Width, size ?? document.Height), ParentId = parent };
            document.Layers.Add(group); return group;
        }
        static Layer AddObject(Document document, Guid? parent, Color color, int size = 5, double x = 1, double y = 1)
        {
            var layer = new Layer { ParentId = parent, Pixels = Raster.Solid(size, size, color), X = x, Y = y };
            document.Layers.Add(layer); return layer;
        }
        static Document ForceIsolatedGroups(Document document)
        {
            var reference = document.Snapshot();
            // An invisible adjustment has no visual effect but conservatively
            // disables the direct path, exercising the original isolated stack.
            foreach (var group in reference.Layers.Where(layer => layer.Kind == LayerKind.Group).ToArray())
                reference.Layers.Add(new Layer { ParentId = group.Id, Kind = LayerKind.Adjustment,
                    Visible = false, Pixels = new Raster(1, 1), Adjustment = new AdjustmentSpec { Kind = AdjustmentKind.Exposure } });
            return reference;
        }
        static void Near(Raster actual, Raster expected, int tolerance = 2)
        {
            Check(actual.Width == expected.Width && actual.Height == expected.Height, "Render dimensions changed");
            for (int i = 0; i < actual.Data.Length; i += 4)
            {
                Check(Math.Abs(actual.Data[i + 3] - expected.Data[i + 3]) <= tolerance, $"Grouped alpha differs at pixel {i / 4}");
                for (int channel = 0; channel < 3; channel++)
                {
                    // Transparent RGB is not visible; compare premultiplied
                    // channels so additional 8-bit group quantization is bounded.
                    double value = actual.Data[i + channel] * actual.Data[i + 3] / 255.0;
                    double reference = expected.Data[i + channel] * expected.Data[i + 3] / 255.0;
                    Check(Math.Abs(value - reference) <= tolerance,
                        $"Grouped pixel {i / 4}, channel {channel}: {value:0.##} instead of {reference:0.##}");
                }
            }
        }
        static Document Scene()
        {
            var document = new Document { Width = 12, Height = 12 };
            AddObject(document, null, Color.FromArgb(210, 35, 80, 170), 12, 0, 0);
            var group = AddGroup(document);
            AddObject(document, group.Id, Color.FromArgb(130, 240, 45, 20), 7, 1.25, 1.5);
            var nested = AddGroup(document, group.Id);
            AddObject(document, nested.Id, Color.FromArgb(170, 30, 220, 90), 6, 3, 3).Opacity = .73;
            AddObject(document, group.Id, Color.FromArgb(90, 230, 180, 90), 5, 4, 2);
            AddObject(document, null, Color.FromArgb(155, 220, 40, 180), 4, 5, 5);
            return document;
        }

        test("identity CAD groups preserve nested source-over paint order and fractional alpha", () =>
        {
            var document = Scene();
            Near(Imaging.Render(document), Imaging.Render(ForceIsolatedGroups(document)));
            Check(document.Layers.Where(layer => layer.Kind == LayerKind.Group).All(group => group.Pixels.Data.All(value => value == 0)),
                "Direct compositing wrote into a group's shared placeholder");
        });

        test("group compositor retains isolation for blending clipping masks transforms and adjustments", () =>
        {
            var changes = new Action<Document, Layer>[]
            {
                (_, group) => group.Opacity = .55,
                (_, group) => group.Blend = BlendMode.Multiply,
                (_, group) => group.Mask = Enumerable.Range(0, 144).Select(index => (byte)(index % 3 == 0 ? 255 : 90)).ToArray(),
                (_, group) => group.X = .75,
                (_, group) => group.Scale = .8,
                (_, group) => group.Warp = new WarpQuad(new(0, 0), new(1, .1), new(.9, 1), new(.1, .9)),
                (document, group) => document.Layers.First(layer => layer.ParentId == group.Id).Blend = BlendMode.Multiply,
                (document, group) => document.Layers.Last(layer => layer.ParentId == group.Id).Clipped = true,
                (document, group) => document.Layers.Add(new Layer { ParentId = group.Id, Kind = LayerKind.Adjustment,
                    Pixels = new Raster(12, 12), Adjustment = new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = .4 } }),
                (_, group) => group.Visible = false,
                (_, group) => group.Pixels = new Raster(8, 8),
                (_, group) => group.Clipped = true
            };
            foreach (var change in changes)
            {
                var document = Scene(); var group = document.Layers.First(layer => layer.Kind == LayerKind.Group);
                change(document, group);
                Near(Imaging.Render(document), Imaging.Render(ForceIsolatedGroups(document)));
            }
        });

        test("a clipped layer above a group keeps its isolated base alpha", () =>
        {
            var document = Scene();
            var group = document.Layers.First(layer => layer.Kind == LayerKind.Group);
            var clipped = new Layer { Pixels = Raster.Solid(12, 12, Colors.Yellow), Clipped = true, Opacity = .6 };
            document.Layers.Insert(document.Layers.IndexOf(group) + 1, clipped);
            Near(Imaging.Render(document), Imaging.Render(ForceIsolatedGroups(document)));
        });

        test("1000 CAD groups reuse the destination without full-canvas allocations per group", () =>
        {
            var document = new Document { Width = 128, Height = 128 };
            var shared = new Raster(128, 128); var red = Raster.Solid(1, 1, Colors.Red);
            for (int i = 0; i < 1000; i++)
            {
                var group = new Layer { Kind = LayerKind.Group, Pixels = shared };
                document.Layers.Add(group);
                document.Layers.Add(new Layer { ParentId = group.Id, Pixels = red, X = i % 100, Y = i / 100 });
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            var rendered = Imaging.Render(document);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(allocated < 32L * 1024 * 1024, $"Group rendering allocated {allocated:N0} bytes on the calling thread");
            Check(rendered.Data[(9 * 128 + 99) * 4 + 2] == 255 && rendered.Data[(9 * 128 + 99) * 4 + 3] == 255,
                "The final group's object was not rendered");
            Check(rendered.Data[(10 * 128 + 99) * 4 + 3] == 0 && shared.Data.All(value => value == 0),
                "Group bounds or shared transparent buffer changed");
        });

        test("direct group compositing keeps cancellation and hierarchy depth guards", () =>
        {
            var canceled = new CancellationToken(true);
            try { Imaging.Render(Scene(), canceled); throw new InvalidOperationException("Canceled rendering succeeded"); }
            catch (OperationCanceledException) { }
            var document = new Document { Width = 2, Height = 2 }; Guid? parent = null;
            for (int i = 0; i < 20; i++) parent = AddGroup(document, parent).Id;
            AddObject(document, parent, Colors.Red, 1, 0, 0);
            try { Imaging.Render(document); throw new InvalidOperationException("Over-deep groups rendered"); }
            catch (InvalidOperationException error) when (error.Message.Contains("그룹 계층")) { }
        });
    }
}
