using System.Windows.Media;

namespace Compositor.Windows;

public static class EditingDialogTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        test("adjustment preview before disables target without touching original", () =>
        {
            var doc = new Document { Width = 2, Height = 1 };
            doc.Add(new Layer { Pixels = Raster.Solid(2, 1, Color.FromRgb(80, 100, 120)) });
            var spec = new AdjustmentSpec { Kind = AdjustmentKind.Levels, Gamma = 2 };
            var layer = DocumentFeatures.CreateAdjustment(doc, spec); doc.Add(layer);
            var before = AdjustmentDialog.PreviewDocument(doc, layer.Id, spec, false, null);
            var after = AdjustmentDialog.PreviewDocument(doc, layer.Id, spec with { Gamma = 3 }, true, null);
            Assert(Imaging.Render(before).Data[2] == 80);
            Assert(Imaging.Render(after).Data[2] > Imaging.Render(doc).Data[2]);
            Assert(layer.Visible && layer.Adjustment!.Gamma == 2);
        });
        test("new adjustment preview respects the same selection mask as commit", () =>
        {
            var doc = new Document { Width = 2, Height = 1 };
            doc.Add(new Layer { Pixels = Raster.Solid(2, 1, Color.FromRgb(80, 100, 120)) });
            var preview = AdjustmentDialog.PreviewDocument(doc, null, new AdjustmentSpec { Kind = AdjustmentKind.Levels, Gamma = 2 }, true, [255, 0]);
            var image = Imaging.Render(preview);
            Assert(image.Data[2] > 80 && image.Data[6] == 80 && doc.Layers.Count == 1);
        });
    }
}
