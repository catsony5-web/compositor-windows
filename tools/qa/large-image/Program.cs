using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Windows;

internal static class Program
{
    const int Width = 10_000, Height = 11_000;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    static int Main(string[] args)
    {
        string directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
        var log = new List<string>(); var watch = Stopwatch.StartNew();
        void Record(string message) { string line = $"{watch.Elapsed.TotalSeconds:F1}s {message}"; log.Add(line); Console.WriteLine(line); }
        try
        {
            string png = Path.Combine(directory, "110mp.png"), project = Path.Combine(directory, "110mp.moruproj");
            CreateFixture(png); Record("Created 10000 x 11000 PNG (110 MP / 440,000,000 decoded bytes).");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var app = new Application(); Theme.Apply(app);
            var window = new MainWindow(null);
            typeof(MainWindow).GetField("headlessTesting", Private)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("OpenImage", Private)!.Invoke(window, [png]);
            var doc = (Document)typeof(MainWindow).GetField("doc", Private)!.GetValue(window)!;
            Verify(doc); Record("Opened through MainWindow.OpenImage, including layer list.");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1480, 980)); content.Arrange(new Rect(0, 0, 1480, 980)); content.UpdateLayout();
            var canvas = (CanvasView)typeof(MainWindow).GetField("canvas", Private)!.GetValue(window)!;
            canvas.Composite = Imaging.Render(doc).Bitmap();
            Record("Composited all 110 million pixels and created the display bitmap.");
            canvas.Fit(); content.UpdateLayout();
            var preview = new RenderTargetBitmap(1480, 980, 96, 96, PixelFormats.Pbgra32); preview.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var file = File.Create(Path.Combine(directory, "110mp-editor.png"))) encoder.Save(file);
            Record("Rendered editor offscreen; no desktop window opened.");
            ProjectStore.Save(doc, project); Record("Saved native project using disk-backed image staging.");
            Verify(ProjectStore.Load(project)); Record("Reopened native project and verified dimensions and edge pixels.");
            Record($"PASS; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB.");
            File.WriteAllLines(Path.Combine(directory, "large-image-qa.txt"), log);
            app.Shutdown(); return 0;
        }
        catch (Exception e) { Record("FAIL " + e); File.WriteAllLines(Path.Combine(directory, "large-image-qa.txt"), log); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CreateFixture(string path)
    {
        var raster = Raster.Solid(Width, Height, Color.FromRgb(32, 100, 170));
        raster.Data[0] = 9; raster.Data[^4] = 211;
        using var file = File.Create(path); raster.WritePng(file);
    }
    static void Verify(Document doc)
    {
        doc.Validate(); var data = doc.Active!.Pixels.Data;
        if (doc.Width != Width || doc.Height != Height || data.LongLength != 440_000_000 || data[0] != 9 || data[^4] != 211 || data[^1] != 255)
            throw new InvalidDataException("Large image dimensions or pixels changed.");
    }
}
