using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunStudioTests(Action<string, Action> test)
    {
        test("studio histogram excludes transparent colors and weights partial alpha", () =>
        {
            var pixels = new Raster(3, 1); new byte[] { 0, 0, 255, 255, 0, 255, 0, 0, 255, 0, 0, 128 }.CopyTo(pixels.Data, 0);
            var histogram = new HistogramView(); histogram.Update(pixels);
            if (histogram.Bins[0][255] != 1 || histogram.Bins[1][255] != 0 || Math.Abs(histogram.Bins[2][255] - 128 / 255.0) > .0001) throw new Exception("Incorrect alpha-weighted histogram");
        });
        test("new document presets create correct backgrounds and reject invalid sizes", () =>
        {
            var doc = NewDocumentDialog.CreateDocument("  测试  ", "12", "8", 0);
            if (doc.Width != 12 || doc.Height != 8 || doc.Name != "测试" || doc.Active!.Pixels.Data.Any(b => b != 0)) throw new Exception("Document setup mismatch");
            if (NewDocumentDialog.CreateDocument("", "1", "1", 1).Active!.Pixels.Data.Any(b => b != 255)) throw new Exception("White background mismatch");
            foreach (string w in new[] { "0", "-1", "2.5", "9000" })
            { bool rejected = false; try { NewDocumentDialog.CreateDocument("test", w, "1", 0); } catch { rejected = true; } if (!rejected) throw new Exception("Invalid dimensions accepted"); }
        });
        test("studio navigation and brush settings preserve pixels and redo", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.doc = new Document { Width = 16, Height = 16 };
            window.doc.Add(new Layer { Pixels = Raster.Solid(16, 16, Colors.Blue) });
            window.history.Reset(window.doc); window.InitializeWorkspace();
            window.NudgeSelected(new Vector(1, 0)); window.Undo(); var pixels = window.doc.Active!.Pixels;
            foreach (int page in new[] { 1, 2, 1, 3, 0, 1, 3 }) window.ShowStudioPage(page);
            window.studioDiameter!.SetValue(96, true); window.studioHardness!.SetValue(25, true);
            if (window.brushSize != 96 || window.hardness != .25 || window.hardnessSlider.Value != .25 || !ReferenceEquals(pixels, window.doc.Active!.Pixels) || !window.history.CanRedo || window.history.Dirty(window.doc)) throw new Exception("Studio controls edited the document or failed synchronization");
        });
    }
}
