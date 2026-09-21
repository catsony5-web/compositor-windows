using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Windows;

// Headless component benchmark. It replays the work done by the previous
// per-event whole-document path and the current cached-background preview.
// No window is shown and no editor input is synthesized.
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        const int moves = 24, hudEvents = 120;
        var document = new Document { Width = 1280, Height = 800 };
        document.Add(new Layer { Pixels = Raster.Solid(1280, 800, Colors.SteelBlue) });
        var text = new Layer { Kind = LayerKind.Text, Text = new TextSpec(), Pixels = Raster.Solid(220, 70, Colors.Coral), X = 100, Y = 120 };
        document.Add(text);
        var view = new CanvasView { Document = document, Zoom = .5 };
        view.Measure(new Size(760, 520)); view.Arrange(new Rect(0, 0, 760, 520));
        var surface = new RenderTargetBitmap(760, 520, 96, 96, PixelFormats.Pbgra32);
        view.Composite = Imaging.Render(document).Bitmap(); surface.Render(view);
        void LegacyText()
        {
            view.MovePreviewBackground = null; view.MovePreviewLayer = null;
            for (int i = 0; i < moves; i++)
            {
                text.X = 100 + i * 6;
                view.Composite = Imaging.Render(document.Snapshot()).Bitmap();
                surface.Render(view);
            }
        }
        void CurrentText()
        {
            var background = document.Snapshot(); background.Layers.RemoveAll(layer => layer.Id == text.Id);
            view.MovePreviewBackground = Imaging.Render(background).Bitmap();
            view.MovePreviewLayer = text.Pixels.Bitmap();
            for (int i = 0; i < moves; i++)
            {
                text.X = 100 + i * 6;
                view.MovePreviewMatrix = text.Matrix;
                surface.Render(view);
            }
        }
        var (oldText, newText) = Compare(LegacyText, CurrentText);
        view.MovePreviewBackground = null; view.MovePreviewLayer = null;
        view.BrushPoint = new Point(400, 240); view.BrushRadius = 21; view.BrushHud = "42 px";
        surface.Render(view);
        void LegacyHud()
        {
            for (int i = 0; i < hudEvents; i++)
            {
                // Previous MoveBrushResize rebuilt the same HUD and requested
                // a full CanvasView OnRender for every subpixel mouse event.
                view.BrushHud = "42 px"; view.BrushRadius = 21;
                view.InvalidateVisual(); surface.Render(view);
            }
        }
        void CurrentHud()
        {
            double size = 42;
            for (int i = 0; i < hudEvents; i++)
            {
                double rounded = Math.Round(42 + (i % 4) * .08);
                if (rounded == size) continue;
                size = rounded; view.BrushRadius = size / 2;
                view.BrushHud = $"{size:0} px";
                view.InvalidateVisual(); surface.Render(view);
            }
        }
        var (oldHud, newHud) = Compare(LegacyHud, CurrentHud);
        string output = args.Length == 0 ? "performance-results.csv" : args[0];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllLines(output,
        [
            "scenario,legacy_median_ms,current_median_ms,samples,events,legacy_full_composites,current_full_composites,legacy_canvas_draws,current_canvas_draws",
            string.Join(",", "text_move", F(oldText), F(newText), 5, moves, moves, 1, moves, moves),
            string.Join(",", "unchanged_brush_hud", F(oldHud), F(newHud), 5, hudEvents, 0, 0, hudEvents, 0)
        ]);
        Console.WriteLine(File.ReadAllText(output));
    }
    static double Measure(Action action)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var stopwatch = Stopwatch.StartNew(); action(); stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
    static (double Legacy, double Current) Compare(Action legacy, Action current)
    {
        var oldSamples = new double[5]; var newSamples = new double[5];
        for (int i = 0; i < 5; i++)
        {
            if (i % 2 == 0) { oldSamples[i] = Measure(legacy); newSamples[i] = Measure(current); }
            else { newSamples[i] = Measure(current); oldSamples[i] = Measure(legacy); }
        }
        Array.Sort(oldSamples); Array.Sort(newSamples);
        return (oldSamples[2], newSamples[2]);
    }
    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
