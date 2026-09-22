using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Compositor.Windows;

static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: DrawingWorkspaceQa <drawing.dwg> <output-directory>"); return 2; }
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var app = new Application(); Theme.Apply(app); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var timer = Stopwatch.StartNew();
        var imported = CompatibilityImport.ReadAsync(args[0], new CompatibilityOptions(CadStructure: CadImportStructure.Objects, GroupDrawingObjects: true)).GetAwaiter().GetResult();
        var doc = imported.Document; Console.WriteLine($"Imported {doc.Layers.Count:N0} nodes in {timer.Elapsed.TotalSeconds:0.00}s");
        var window = new MainWindow(null); Set("headlessTesting", true);
        object? Invoke(string method, params object?[] values) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, values);
        T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        try
        {
            Invoke("AddTab", doc, null); Invoke("SetWorkspaceMode", true);
            var root = (FrameworkElement)window.Content; var canvas = Get<CanvasView>("canvas");
            void Capture(string name, int width = 1480, int height = 980, double dpi = 1)
            {
                Get<ScrollViewer>("studioScroll").Height = (double)Invoke("PreferredStudioHeight", (double)height)!;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); canvas.Fit();
                var current = Get<Document>("doc"); var image = DesignRenderer.Render(current, new Rect(0, 0, current.Width, current.Height), Math.Min(current.Width, 1600), Math.Max(1, (int)Math.Round(current.Height * Math.Min(1, 1600d / current.Width))));
                canvas.Composite = image.Bitmap(); canvas.DesignMode = false; canvas.InvalidateVisual(); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
                Console.WriteLine($"Captured {name}");
            }
            Capture("drawing-collapsed");
            Get<HashSet<Guid>>("collapsedGroups").Remove(doc.Layers.Single(l => l.ParentId == null).Id); Invoke("BuildLayers"); Capture("drawing-groups", 1200, 780);
            var area = new Rect(doc.Width * .25, doc.Height * .25, doc.Width * .4, doc.Height * .4);
            timer.Restart(); var contained = ObjectSelection.Find(doc, area, false); var crossed = ObjectSelection.Find(doc, area, true);
            if (!contained.All(crossed.Contains) || crossed.Length == 0) throw new Exception("Invalid directional selection results");
            Console.WriteLine($"Window {contained.Length:N0}, crossing {crossed.Length:N0}; two queries {timer.Elapsed.TotalSeconds:0.00}s");
            Invoke("ApplyObjectSelection", crossed, SelectionCombine.Replace); canvas.ObjectMarquee = area; canvas.CrossingSelection = true; Capture("crossing-selection"); canvas.ObjectMarquee = null;
            Invoke("SetTool", Tool.Artboard); Invoke("AddArtboard"); Capture("artboards", 1480, 980, 1.5); Capture("artboards-small", 1200, 780);
            var changed = Get<Document>("doc"); var path = Path.Combine(output, "drawing-artboards.moruproj"); ProjectStore.Save(changed, path);
            var loaded = ProjectStore.Load(path); if (loaded.Artboards.Count != 2 || loaded.Layers.Count != changed.Layers.Count) throw new Exception("Real drawing roundtrip failed");
            Console.WriteLine("PASS actual drawing grouping, directional selection, artboard roundtrip and offscreen rendering"); return 0;
        }
        finally
        {
            Invoke("StopRenderingForShutdown");
            typeof(Document).Assembly.GetType("Compositor.Windows.NativePdf")?.GetMethod("Shutdown", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
            app.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }
}
