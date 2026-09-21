using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunChromeTests(Action<string, Action> test, string directory)
    {
        test("closing another document preserves the active document and viewport", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.AddTab(NewDocumentDialog.CreateDocument("첫 문서", "8", "8", 0), null);
            var other = new Document { Width = 8, Height = 8, Name = "다른 문서" };
            other.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue) });
            window.AddTab(other, null); window.canvas.Zoom = 1.5; window.canvas.Pan = new Vector(17, 23);
            var history = window.history;
            window.CloseTabAt(0);
            if (window.tabs.Count != 1 || !ReferenceEquals(window.doc, other) || !ReferenceEquals(window.history, history) || window.canvas.Zoom != 1.5 || window.canvas.Pan != new Vector(17, 23))
                throw new Exception("Closing inactive tab changed the active workspace");
            window.CloseTabAt(99);
            if (window.tabs.Count != 1) throw new Exception("Invalid close target removed a document");
        });
        test("closing the final clean document returns to an empty workspace", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.AddTab(NewDocumentDialog.CreateDocument("닫을 문서", "8", "8", 0), null);
            window.CloseTabAt(0);
            if (window.tabs.Count != 0 || window.HasDocument || window.canvas.Document != null || window.doc.Layers.Count != 0 || window.history.Dirty(window.doc))
                throw new Exception("Final tab close left a phantom document");
        });
        RunStartupTests(test, directory);
    }
}
