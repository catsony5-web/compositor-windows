using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using Compositor.Windows;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args[0] == "batch")
            {
                string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder); var records = new List<object>(); int index = 0;
                var files = args.Skip(2).SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.dwg", SearchOption.AllDirectories) : new[] { p }).Distinct().Order().ToArray();
                foreach (var path in files)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var doc = CompatibilityImport.ReadAsync(path, new(SeparateLayers: true)).GetAwaiter().GetResult().Document; doc.Validate();
                    double scale = 1000d / Math.Max(doc.Width, doc.Height); var view = DesignRenderer.Render(doc, new Rect(0, 0, doc.Width, doc.Height), (int)(doc.Width * scale), (int)(doc.Height * scale));
                    long ink = 0; for (int i = 0; i < view.Data.Length; i += 4) if (Math.Min(view.Data[i], Math.Min(view.Data[i + 1], view.Data[i + 2])) < 220) ink++;
                    if (ink < 100 || !doc.Layers.Any(l => l.Vector != null)) throw new Exception("No retained visible drawing: " + path);
                    string preview = $"{++index:D3}-" + Path.GetFileNameWithoutExtension(path) + ".png"; using (var file = File.Create(Path.Combine(folder, preview))) view.WritePng(file);
                    var record = new { File = path, Preview = preview, VectorLayers = doc.Layers.Count(l => l.Vector != null), VectorBytes = doc.Layers.Sum(l => l.Vector?.ByteLength ?? 0), Ink = ink, Milliseconds = watch.ElapsedMilliseconds };
                    records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record)); GC.Collect();
                }
                File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })); return 0;
            }
            if (args[0] == "editor")
            {
                var app = new Application(); Theme.Apply(app); var window = new MainWindow(null); var type = typeof(MainWindow);
                var doc = ProjectStore.Load(args[1]); var editorOutput = Path.GetFullPath(args[2]); Directory.CreateDirectory(editorOutput);
                type.GetField("headlessTesting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                type.GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [doc, null]);
                type.GetMethod("SetWorkspaceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
                window.RenderPreview(Path.Combine(editorOutput, "editor-fit.png"));
                var canvas = (CanvasView)type.GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                canvas.Zoom = 8; var center = new Point(doc.Width * .42 + 37.5, doc.Height * .45 + 25);
                canvas.Pan = new Vector((doc.Width / 2d - center.X) * canvas.Zoom, (doc.Height / 2d - center.Y) * canvas.Zoom);
                var content = (FrameworkElement)window.Content;
                BitmapSource Render()
                {
                    canvas.InvalidateVisual(); content.UpdateLayout(); var bitmap = new RenderTargetBitmap(1480, 920, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); return bitmap;
                }
                void Save(string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render())); using var file = File.Create(Path.Combine(editorOutput, name)); encoder.Save(file); }
                Render(); var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) => { Render(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || clock.ElapsedMilliseconds > 30000) frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                if (!canvas.IsDesignPreviewReady) throw new Exception("Design viewport did not become ready: " + canvas.DesignPreviewError);
                Save("editor-vector-800.png"); type.GetMethod("SetWorkspaceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]); Save("editor-raster-800.png");
                window.Close(); app.Shutdown(); return 0;
            }
            string input = args[0], output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            var results = new List<object>(); int pages = Path.GetExtension(input).ToLowerInvariant() is ".ai" or ".pdf" ? (int)PdfCompatibility.InspectAsync(input).GetAwaiter().GetResult().Pages : 1;
            for (int page = 1; page <= pages; page++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var doc = CompatibilityImport.ReadAsync(input, new(Page: page, SeparateLayers: true, CadStructure: CadImportStructure.Objects)).GetAwaiter().GetResult().Document;
                doc.Validate();
                void Save(Raster raster, string suffix) { using var file = File.Create(Path.Combine(output, $"page-{page}-{suffix}.png")); raster.WritePng(file); }
                double scale = Math.Min(1, 1200d / Math.Max(doc.Width, doc.Height));
                Save(DesignRenderer.Render(doc, new Rect(0, 0, doc.Width, doc.Height), (int)(doc.Width * scale), (int)(doc.Height * scale)), "fit");
                var cached = Imaging.Render(doc); var rasterDoc = new Document { Width = doc.Width, Height = doc.Height }; rasterDoc.Add(new Layer { Pixels = cached });
                double x = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : doc.Width * .42;
                double y = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : doc.Height * .45;
                var crop = new Rect(x, y, 75, 50);
                var sharp = DesignRenderer.Render(doc, crop, 1200, 800); Save(sharp, "vector-1600"); Save(DesignRenderer.Render(rasterDoc, crop, 1200, 800), "raster-1600");
                string project = Path.Combine(output, $"page-{page}.moruproj"); ProjectStore.Save(doc, project); var loaded = ProjectStore.Load(project);
                var roundtrip = DesignRenderer.Render(loaded, crop, 1200, 800);
                if (!sharp.Data.SequenceEqual(roundtrip.Data)) throw new Exception("Vector project roundtrip changed zoomed pixels");
                string pdf = Path.Combine(output, $"page-{page}-vector.pdf"); using (var file = File.Create(pdf)) VectorPdfExport.Write(doc, file);
                var exported = CompatibilityImport.ReadAsync(pdf, new(PreservePdfLayers: false)).GetAwaiter().GetResult().Document;
                var exportCrop = new Rect(crop.X * exported.Width / doc.Width, crop.Y * exported.Height / doc.Height, crop.Width * exported.Width / doc.Width, crop.Height * exported.Height / doc.Height);
                Save(DesignRenderer.Render(exported, exportCrop, 1200, 800), "pdf-1600");
                var report = new { Page = page, doc.Width, doc.Height, Layers = doc.Layers.Count, VectorLayers = doc.Layers.Count(l => l.Vector != null),
                    VectorBytes = doc.Layers.Sum(l => l.Vector?.ByteLength ?? 0), ProjectBytes = new FileInfo(project).Length, PdfBytes = new FileInfo(pdf).Length,
                    Crop = new { crop.X, crop.Y, crop.Width, crop.Height }, RoundtripExact = true, Milliseconds = timer.ElapsedMilliseconds };
                results.Add(report); Console.WriteLine(JsonSerializer.Serialize(report)); GC.Collect();
            }
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
