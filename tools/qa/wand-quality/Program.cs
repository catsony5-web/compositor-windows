using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Compositor.Windows;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var doc = ProjectStore.Load(args[1]); string output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
            void Save(Raster raster, string name) { using var file = File.Create(Path.Combine(output, name)); raster.WritePng(file); }
            if (args[0] == "strip")
            {
                for (int y = 200; y < 1707; y += 500)
                {
                    int h = Math.Min(500, doc.Height - y); var area = new Rect(1140, y, 70, h);
                    Save(DesignRenderer.Render(doc, area, 420, h * 6), $"strip-{y}.png");
                }
                return 0;
            }
            var seed = new Point(double.Parse(args[3], CultureInfo.InvariantCulture), double.Parse(args[4], CultureInfo.InvariantCulture));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var precise = PrecisionWand.Select(doc, seed, 16, true, true, true, 32);
            precise = precise with { Contour = SelectionContours.Create(precise) };
            long duration = watch.ElapsedMilliseconds;
            var legacy = SelectionTools.MagicWand(Imaging.Render(doc), seed, 16);
            var crop = new Rect(seed.X - 17, seed.Y - 11, 34, 25);
            var sharp = DesignRenderer.Render(doc, crop, 1360, 1000);
            Raster Overlay(Geometry geometry) => Imaging.Draw(1360, 1000, dc =>
            {
                dc.DrawImage(sharp.Bitmap(), new Rect(0, 0, 1360, 1000));
                dc.PushTransform(new MatrixTransform(40, 0, 0, 40, -crop.X * 40, -crop.Y * 40));
                dc.DrawGeometry(null, new Pen(Brushes.White, .075), geometry);
                dc.DrawGeometry(null, new Pen(Brushes.Black, .04) { DashStyle = new DashStyle([4, 4], 0) }, geometry);
                dc.Pop();
            });
            Save(sharp, "source-4000.png"); Save(Overlay(precise.Contour), "precise-4000.png"); Save(Overlay(LegacyContour(legacy)), "previous-4000.png");
            Raster Mask(Selection selected)
            {
                var result = new Raster(1360, 1000);
                for (int y = 0; y < result.Height; y++) for (int x = 0; x < result.Width; x++)
                {
                    byte v = (byte)Math.Clamp((int)Math.Round(selected.Weight(crop.X + (x + .5) / 40, crop.Y + (y + .5) / 40) * 255), 0, 255);
                    int i = (y * result.Width + x) * 4; result.Data[i] = result.Data[i + 1] = result.Data[i + 2] = v; result.Data[i + 3] = 255;
                }
                return result;
            }
            Save(Mask(precise), "precise-mask.png"); Save(Mask(legacy), "previous-mask.png");
            var record = new { Seed = new { seed.X, seed.Y }, Crop = crop, Milliseconds = duration, Samples = precise.Coverage!.Length,
                SamplingBounds = precise.CoverageBounds, Bounds = precise.Bounds, ContourBounds = precise.Contour.Bounds, PreviousBounds = legacy.Bounds };
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(Path.Combine(output, "results.json"), json); Console.WriteLine(json);
            if (args[0] != "editor") return 0;
            var app = new Application(); Theme.Apply(app); var window = new MainWindow(null); var type = typeof(MainWindow);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("headlessTesting", flags)!.SetValue(window, true);
            type.GetMethod("AddTab", flags)!.Invoke(window, [doc, null]);
            type.GetMethod("SetWorkspaceMode", flags)!.Invoke(window, [true]); type.GetMethod("SetTool", flags)!.Invoke(window, [Tool.MagicWand]);
            type.GetField("selection", flags)!.SetValue(window, precise);
            window.RenderPreview(Path.Combine(output, "editor-fit.png"));
            var canvas = (CanvasView)type.GetField("canvas", flags)!.GetValue(window)!;
            canvas.Selection = precise; canvas.Zoom = 32;
            canvas.Pan = new Vector((doc.Width / 2d - seed.X) * canvas.Zoom, (doc.Height / 2d - seed.Y) * canvas.Zoom);
            var content = (FrameworkElement)window.Content;
            BitmapSource Render()
            {
                canvas.InvalidateVisual(); content.UpdateLayout(); var bitmap = new RenderTargetBitmap(1480, 920, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); return bitmap;
            }
            Render(); var frame = new DispatcherFrame(); watch.Restart(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { Render(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || watch.ElapsedMilliseconds > 30000) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            if (!canvas.IsDesignPreviewReady) throw new Exception("Design viewport did not become ready: " + canvas.DesignPreviewError);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render())); using (var file = File.Create(Path.Combine(output, "editor-wand-3200.png"))) encoder.Save(file);
            window.Close(); app.Shutdown(); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    // Retained here only to reproduce the released Preview 20 contour behavior.
    static Geometry LegacyContour(Selection selection)
    {
        var geometry = new StreamGeometry(); var mask = selection.Coverage!; int w = selection.CanvasWidth, h = selection.CanvasHeight;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)w * h / 2_000_000)));
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x] >= 128;
        using (var c = geometry.Open())
        {
            void Edge(int x1, int y1, int x2, int y2) { c.BeginFigure(new Point(x1, y1), false, false); c.LineTo(new Point(x2, y2), true, false); }
            for (int y = 0; y < h; y += step) for (int x = 0; x < w; x += step)
            {
                if (!Inside(x, y)) continue; int r = Math.Min(w, x + step), b = Math.Min(h, y + step);
                if (!Inside(x - step, y)) Edge(x, y, x, b); if (!Inside(x, y - step)) Edge(x, y, r, y);
                if (!Inside(x + step, y)) Edge(r, y, r, b); if (!Inside(x, y + step)) Edge(x, b, r, b);
            }
        }
        geometry.Freeze(); return geometry;
    }
}
