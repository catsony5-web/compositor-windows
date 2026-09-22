using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunGroupedMovePreviewTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static (Document Doc, Layer Group, Layer Child) Scene()
        {
            var doc = new Document { Width = 24, Height = 20 };
            doc.Add(new Layer { Pixels = Raster.Solid(24, 20, Colors.White), Locked = true });
            var group = new Layer { Kind = LayerKind.Group, Name = "CAD 레이어", Pixels = new Raster(24, 20) }; doc.Add(group);
            // Deliberately put root foreground before descendants in the flat list.
            doc.Add(new Layer { Pixels = Raster.Solid(2, 20, Colors.Lime), X = 16 });
            var child = new Layer { ParentId = group.Id, Pixels = Raster.Solid(4, 4, Colors.Red), X = 2, Y = 3 }; doc.Add(child);
            doc.Add(new Layer { ParentId = group.Id, Pixels = Raster.Solid(2, 12, Colors.Blue), X = 12, Y = 1, Opacity = .5 });
            return (doc, group, child);
        }
        test("grouped object cached movement preserves hierarchy paint order and stationary siblings", () =>
        {
            var (doc, group, child) = Scene();
            Check(CanPreviewLayerMove(doc, child, 1), "Imported group disables smooth object movement");
            var (below, above) = CreateLayerMovePreviewStacks(doc, child.Id);
            below.Validate(); above.Validate();
            Check(child.ParentId == group.Id && doc.Layers.Contains(group), "Preview changed real group structure");
            Check(above.Layers.Select(l => l.Id).SequenceEqual(new[] { doc.Layers[4].Id, doc.Layers[2].Id }), "Preview follows flat-list order, not group paint order");
            child.X = 13;
            var combined = Imaging.Render(below);
            var flat = child.Snapshot(); flat.ParentId = null; Imaging.Composite(combined, flat);
            Imaging.Composite(combined, new Layer { Pixels = Imaging.Render(above) });
            var expected = Imaging.Render(doc);
            Check(combined.Data.Zip(expected.Data).All(pair => Math.Abs(pair.First - pair.Second) <= 2), "Grouped cache differs from final render");
            Check(doc.Layers[4].X == 12 && child.ParentId == group.Id, "Moving one object moved siblings or detached its group");
        });
        test("grouped movement uses full compositor for transformed cropped masked translucent or locked parents", () =>
        {
            var (doc, group, child) = Scene();
            group.X = 1; Check(!CanPreviewLayerMove(doc, child, 1), "Translated parent lost its transform"); group.X = 0;
            group.Pixels = new Raster(10, 10); Check(!CanPreviewLayerMove(doc, child, 1), "Cropped group lost its clipping"); group.Pixels = new Raster(24, 20);
            group.Mask = new byte[24 * 20]; Check(!CanPreviewLayerMove(doc, child, 1), "Masked group flattened"); group.Mask = null;
            group.Opacity = .5; Check(!CanPreviewLayerMove(doc, child, 1), "Group opacity applied independently"); group.Opacity = 1;
            group.Locked = true; Check(!CanPreviewLayerMove(doc, child, 1), "Locked ancestor bypassed"); group.Locked = false;
            group.Visible = false; Check(!CanPreviewLayerMove(doc, child, 1), "Hidden ancestor bypassed");
        });
    }
}
