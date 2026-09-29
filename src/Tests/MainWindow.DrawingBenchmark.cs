using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Compositor.Windows;

// Hidden measurement: Morupixel.exe --benchmark-drawing <report.txt>
// Builds the synthetic 20k-object drawing offscreen (no visible window) and
// times the UI-thread work of pan moves, wheel zoom steps and pointer hover,
// plus how long the crisp design viewport takes to settle afterwards.
public sealed partial class MainWindow
{
    public static int RunDrawingBenchmark(string output)
    {
        var lines = new List<string>();
        void Report(string line) { Console.WriteLine(line); lines.Add(line); File.WriteAllLines(output, lines); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var watch = Stopwatch.StartNew();
        var synthetic = SyntheticDrawing.Create();
        Report($"synthetic drawing: {synthetic.Width}x{synthetic.Height}, {synthetic.Layers.Count(l => l.Kind != LayerKind.Group):N0} objects, {synthetic.Layers.Count(l => l.Kind == LayerKind.Group)} groups, built in {watch.ElapsedMilliseconds} ms");
        MeasureDrawingInteraction("synthetic", synthetic, Report);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    static ulong Cycles() { QueryThreadCycleTime(GetCurrentThread(), out var cycles); return cycles; }

    static void MeasureDrawingInteraction(string label, Document document, Action<string> report)
    {
        var window = new MainWindow(null) { headlessTesting = true };
        try
        {
            window.AddTab(document, null); window.SetWorkspaceMode(true);
            window.SetTool(Tool.Move); window.autoSelectToggle.IsChecked = true;
            var content = (FrameworkElement)window.Content; var size = new Size(1600, 1000);
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            var canvas = window.canvas; canvas.Fit();
            var watch = Stopwatch.StartNew();
            canvas.Composite = Imaging.Render(document).Bitmap();
            report($"[{label}] full pixel composite (photo render): {watch.ElapsedMilliseconds} ms");
            void Frame() { canvas.InvalidateVisual(); content.UpdateLayout(); }
            // Wall time until the crisp viewport is shown, and UI-thread CPU spent meanwhile.
            void Settle(string what)
            {
                var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); ulong ui = 0;
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (_, _) => { content.UpdateLayout(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || clock.ElapsedMilliseconds > 60000) frame.Continue = false; };
                ulong before = Cycles(); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); ui = Cycles() - before;
                report($"[{label}] {what}: {clock.ElapsedMilliseconds} ms (UI thread {ui / 1e6:0.0} Mcyc){(canvas.DesignPreviewError is { } error ? " " + error : "")} requests {canvas.DesignRequests} started {canvas.DesignRendersStarted} completed {canvas.DesignRendersCompleted} canceled {canvas.DesignRendersCanceled}");
            }
            // Median wall time and median UI-thread cycles per event. Cycles exclude
            // time preempted by other processes, so they are steadier on a busy PC.
            void Time(string what, int count, Action<int> action)
            {
                var ms = new double[count]; var cycles = new double[count];
                for (int i = 0; i < count; i++)
                {
                    ulong c0 = Cycles(); var tick = Stopwatch.StartNew(); action(i);
                    ms[i] = tick.Elapsed.TotalMilliseconds; cycles[i] = (Cycles() - c0) / 1e6;
                }
                Array.Sort(ms); Array.Sort(cycles);
                report($"[{label}] {what}: {ms[count / 2]:0.00} ms median, {cycles[count / 2]:0.00} Mcyc median, {ms[^1]:0.00} ms worst");
            }
            watch.Restart(); Frame(); Settle("first crisp design viewport at fit (cold vector decode)");
            var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            canvas.ZoomAt(1.0001, center); Frame(); Settle("warm crisp design viewport at fit");

            // Warm up tiered JIT so the medians describe steady interaction.
            for (int i = 0; i < 80; i++) { Frame(); var p = canvas.ToDocument(new Point(200 + i * 7, 300 + i * 3)); window.UpdatePointerHover(p); _ = window.CanvasCursorAt(p); }
            window.ClearPointerHover(); Frame();
            Time("steady frame (no change)", 40, _ => Frame());
            Time("pan move (UI thread incl. OnRender)", 60, i => { canvas.Pan += new Vector(i % 2 == 0 ? 6 : -4, 3); Frame(); });
            Settle("crisp viewport settled after pan");
            Time("wheel zoom-in step (UI thread incl. OnRender)", 12, i => { window.ClearPointerHover(); canvas.ZoomAt(1.15, center); window.UpdateStatus(); Frame(); });
            Settle("crisp viewport settled after wheel burst (zoom-in)");
            Time("wheel zoom-out step", 12, i => { window.ClearPointerHover(); canvas.ZoomAt(1 / 1.15, center); window.UpdateStatus(); Frame(); });
            Settle("crisp viewport settled after wheel burst (zoom-out)");
            for (int i = 0; i < 8; i++) { canvas.ZoomAt(1.15, center); Frame(); }
            Settle($"crisp viewport at {canvas.Zoom * 100:0}% zoom");
            Time("pan move while zoomed in", 60, i => { canvas.Pan += new Vector(i % 2 == 0 ? 6 : -4, 3); Frame(); });
            Settle("crisp viewport settled after zoomed-in pan");
            canvas.Fit(); Frame(); Settle("crisp viewport back at fit");

            Time("pointer hover move (pick + cursor)", 200, i =>
            {
                var screen = new Point(canvas.ActualWidth * (.15 + .7 * i / 200d), canvas.ActualHeight * (.2 + .6 * ((i * 37) % 200) / 200d));
                var point = canvas.ToDocument(screen);
                window.UpdatePointerHover(point); _ = window.CanvasCursorAt(point);
            });
            Time("pointer hover move incl. frame", 60, i =>
            {
                var point = canvas.ToDocument(new Point(canvas.ActualWidth * (.2 + .6 * i / 60d), canvas.ActualHeight * .5));
                window.UpdatePointerHover(point); _ = window.CanvasCursorAt(point); Frame();
            });

            window.SetWorkspaceMode(false); Frame();
            Time("photo mode pan move", 60, i => { canvas.Pan += new Vector(i % 2 == 0 ? 6 : -4, 3); Frame(); });
            Time("photo mode wheel step", 12, i => { window.ClearPointerHover(); canvas.ZoomAt(i < 6 ? 1.15 : 1 / 1.15, center); window.UpdateStatus(); Frame(); });

            var area = new Rect(0, 0, document.Width, document.Height);
            void Direct(string what, Rect region)
            {
                var best = double.MaxValue;
                for (int i = 0; i < 3; i++) { watch.Restart(); DesignRenderer.Render(document, region, 1504, (int)(1504d * region.Height / region.Width)); best = Math.Min(best, watch.Elapsed.TotalMilliseconds); }
                report($"[{label}] {what}: {best:0} ms best of 3");
            }
            Direct("direct design render, whole drawing at ~1500 px", area);
            Direct("direct design render, 15% window at ~1500 px", new Rect(document.Width * .4, document.Height * .4, document.Width * .15, document.Height * .15));
            Time("document snapshot", 10, _ => document.Snapshot());
        }
        finally { window.StopRenderingForShutdown(); }
    }
}
