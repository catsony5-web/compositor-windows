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
        // Async preview work resumes on this thread, as it does inside the running app.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
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

            // Object drag. The fast preview flattens the stack once, then each pointer move only
            // updates the mover's matrix. Before, any Multiply material layer in the document made
            // every drag frame queue a full composite of all objects instead.
            var dragged = document.Layers.Last(l => l.Kind == LayerKind.Vector && l.Visible && !l.Locked);
            document.ActiveId = dragged.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(dragged.Id);
            bool fast = CanPreviewLayerMove(document, dragged, 1);
            report($"[{label}] drag eligible for fast move preview (materials below mover): {fast}");
            if (fast)
            {
                watch.Restart(); var (below, above) = CreateLayerMovePreviewStacks(document, dragged.Id);
                var background = Imaging.Render(below); var foreground = above.Layers.Count == 0 ? null : Imaging.Render(above);
                report($"[{label}] drag start: flatten below/above once: {watch.ElapsedMilliseconds} ms ({below.Layers.Count} below, {above.Layers.Count} above)");
                canvas.MovePreviewBackground = background.Bitmap(); canvas.MovePreviewLayer = dragged.Pixels.Bitmap(); canvas.MovePreviewForeground = foreground?.Bitmap();
                Time("drag move frame (fast preview: matrix + frame)", 60, i => { dragged.X += i % 2 == 0 ? 3 : -2; canvas.MovePreviewMatrix = dragged.Matrix; Frame(); });
                canvas.MovePreviewBackground = null; canvas.MovePreviewLayer = null; canvas.MovePreviewForeground = null;
            }
            Time("drag move frame (full composite, the old path)", 5, i => { dragged.X += i % 2 == 0 ? 3 : -2; canvas.Composite = Imaging.Render(document).Bitmap(); Frame(); });

            // Drag start through the real preview path: wall time from the first moved pointer
            // event until a frame shows the object at its new place, and until the full planes
            // are on screen. The old path waited for the full flatten of the drawing.
            if (fast)
            {
                var start = Imaging.Render(document); window.RememberCurrentComposite(start); canvas.Composite = start.Bitmap(); Frame();
                var objects = document.Layers.Where(l => l.Kind == LayerKind.Vector && l.Visible && !l.Locked).ToArray();
                var middle = objects[objects.Length / 2];
                void DragStart(string what, Layer layer, bool interim, bool keepPlanes, Action? before = null)
                {
                    window.ClearTextMovePreview(); if (!keepPlanes) window.DropMovePlanes();
                    window.moveInterimEnabled = interim;
                    document.ActiveId = layer.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(layer.Id);
                    before?.Invoke();
                    window.beforeGesture = document.Snapshot(); window.dragging = true; window.moveStarted = true; window.CaptureMoveInterimSource();
                    int builds = window.MovePlaneBuilds; ulong c0 = Cycles(); var clock = Stopwatch.StartNew();
                    layer.X += 4; window.TryPreviewTextMove(); Frame();
                    double sync = clock.Elapsed.TotalMilliseconds, syncCycles = (Cycles() - c0) / 1e6;
                    PumpDispatcherUntil(() => canvas.MovePreviewBackground != null && canvas.MovePreviewLayer != null, 60000); Frame();
                    double first = clock.Elapsed.TotalMilliseconds; bool interimShown = window.textPreviewInterim;
                    PumpDispatcherUntil(() => window.textPreviewPlanes is { Ready: true } && !window.textPreviewInterim, 60000); Frame();
                    double full = clock.Elapsed.TotalMilliseconds;
                    report($"[{label}] drag start, {what}: first moved frame {first:0.0} ms ({(interimShown ? "interim from composite" : "full planes")}), full planes on screen {full:0.0} ms, UI thread at drag start {sync:0.0} ms / {syncCycles:0.0} Mcyc, new flattens {window.MovePlaneBuilds - builds}");
                    layer.X -= 4; window.dragging = false; window.moveStarted = false; window.beforeGesture = null; window.ClearTextMovePreview();
                }
                DragStart("old path (nothing cached, wait for the flatten), top object", dragged, false, false);
                DragStart("new, first drag of a just-picked top object", dragged, true, false);
                DragStart("new, same object dragged again after its drop", dragged, true, true);
                DragStart("old path, object in the middle of the stack", middle, false, false);
                DragStart("new, first drag of a just-picked middle object", middle, true, false);
                DragStart("new, object selected a moment before the drag", middle, true, false, () =>
                {
                    var planes = window.PrepareMovePlanes(); var clock = Stopwatch.StartNew();
                    PumpDispatcherUntil(() => planes is not { Ready: false, Failed: false }, 60000);
                    report($"[{label}] speculative planes for the selected object ready after {clock.ElapsedMilliseconds} ms");
                });
                // How far the footprint patch of the interim frame is from the whole-page render of the
                // layers below (hatch tiles are not shift-invariant to the last level).
                {
                    var (below, _) = CreateLayerMovePreviewStacks(document, dragged.Id);
                    var whole = Imaging.Render(below); int worst = 0, differing = 0, total = 0;
                    foreach (var other in objects.Where((_, i) => i % 50 == 0))
                    {
                        var footprint = MoveFootprint(document, other); if (footprint.Width <= 0) continue;
                        var patch = RenderMoveRegion(below, footprint, true, default);
                        for (int y = 0; y < footprint.Height; y++) for (int x = 0; x < footprint.Width; x++)
                        {
                            int i = (y * footprint.Width + x) * 4, j = ((footprint.Y + y) * document.Width + footprint.X + x) * 4, d = 0;
                            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(patch.Data[i + c] - whole.Data[j + c]));
                            worst = Math.Max(worst, d); if (d > 0) differing++; total++;
                        }
                    }
                    report($"[{label}] interim footprint patches vs whole-page render below: {differing:N0} of {total:N0} pixels differ, worst channel difference {worst}");
                }
                window.moveInterimEnabled = true; window.DropMovePlanes();
                document.ActiveId = dragged.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(dragged.Id);
            }

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
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); window.StopRenderingForShutdown(); }
    }
}
