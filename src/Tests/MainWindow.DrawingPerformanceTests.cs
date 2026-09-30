using System.Windows;
using System.Windows.Threading;

namespace Compositor.Windows;

// Large drawings: pan, wheel zoom and hover must not re-render or re-index every
// object per input event. These checks cover the mechanisms (counts, reuse,
// cancellation and exact output), never wall-clock thresholds.
public sealed partial class MainWindow
{
    internal static void RunDrawingPerformanceTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static Layer Topmost(Document doc) => doc.Layers.Last(l => l.Kind == LayerKind.Vector);
        static Point Ink(Document doc, Layer layer)
        {
            for (int y = 0; y < layer.Pixels.Height; y++) for (int x = 0; x < layer.Pixels.Width; x++)
                if (layer.Pixels.Data[(y * layer.Pixels.Width + x) * 4 + 3] == 255) return DocumentFeatures.ToDocumentSpace(doc, layer, new Point(x + .5, y + .5));
            throw new InvalidOperationException("Object preview has no ink");
        }
        static Document Photo(int width, int height)
        {
            var photo = new Document { Width = width, Height = height, Name = "Photo" };
            photo.Add(new Layer { Kind = LayerKind.Raster, Name = "사진", Pixels = Raster.Solid(width, height, System.Windows.Media.Colors.White) });
            return photo;
        }
        static bool SettleDesign(CanvasView canvas)
        {
            var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { canvas.UpdateLayout(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || watch.ElapsedMilliseconds > 30000) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); return canvas.IsDesignPreviewReady;
        }

        test("scene state follows in-place layer edits without reporting unchanged frames", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 3, objectsPerGroup: 40, width: 400, height: 300, materials: 1);
            var state = new SceneState(); long version = state.Update(doc);
            Check(state.Update(doc) == version, "An unchanged scene advanced its version");
            var item = Topmost(doc);
            void Changed(string what, Action edit) { edit(); long next = state.Update(doc); Check(next != version, what + " was not detected"); version = next; Check(state.Update(doc) == version, what + " kept reporting a change"); }
            Changed("An in-place move", () => item.X += 1);
            Changed("A visibility change", () => item.Visible = false);
            Changed("An opacity change", () => item.Opacity = .5);
            Changed("A new pixel buffer", () => item.Pixels = item.Pixels.Clone());
            var paper = doc.Layers.First(l => l.Kind == LayerKind.Shape);
            Changed("A shape definition change", () => paper.Shape = paper.Shape! with { FillArgb = 0xFFEEEEEE });
            Changed("A reordered stack", () => { doc.Layers.Remove(item); doc.Layers.Insert(2, item); });
            Changed("A removed layer", () => doc.Layers.Remove(item));
            Changed("Another document", () => doc = doc.Snapshot());
        });

        test("cached hover picking matches a full scan and rebuilds only after the scene changes", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 6, objectsPerGroup: 150, width: 900, height: 640, materials: 2);
            var cache = new LayerPicking.Cache(); var random = new Random(3); int objects = 0;
            double[] zooms = [.5, 1, 3];
            for (int i = 0; i < 400; i++)
            {
                var point = new Point(random.NextDouble() * doc.Width, random.NextDouble() * doc.Height); double zoom = zooms[i % zooms.Length];
                var expected = LayerPicking.PickNearByScan(doc, point, zoom);
                Check(ReferenceEquals(LayerPicking.PickNear(doc, point, zoom), expected), "The indexed picker disagreed with a full scan");
                Check(ReferenceEquals(cache.PickNear(doc, point, zoom), expected), "The cached picker disagreed with a full scan");
                if (expected?.Kind == LayerKind.Vector) objects++;
            }
            Check(objects > 40, "Probes rarely reached drawing objects; the comparison is not meaningful");
            Check(cache.Builds == 1, $"Unchanged hover probes rebuilt the index {cache.Builds} times");
            // A drag moves the object in place before any history revision exists.
            var target = Topmost(doc); target.X += 37; target.Y -= 11; var ink = Ink(doc, target);
            Check(ReferenceEquals(cache.PickNear(doc, ink, 1), target) && cache.Builds == 2, "Hover kept a stale index after an in-place move");
            target.Visible = false;
            Check(!ReferenceEquals(cache.PickNear(doc, ink, 1), target) && cache.Builds == 3, "Hover still picked a hidden object");
            Check(ReferenceEquals(cache.PickNear(doc, ink, 1), LayerPicking.PickNearByScan(doc, ink, 1)), "The rebuilt index disagreed with a full scan");
        });

        test("pointer hover over a large drawing reuses one object index", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 150, width: 800, height: 560, materials: 1);
                window.AddTab(doc, null); window.SetTool(Tool.Move); window.autoSelectToggle.IsChecked = true;
                window.doc.ActiveId = window.doc.Layers.First(l => l.Kind == LayerKind.Shape).Id; window.canvas.Zoom = 1;
                int builds = window.pickCache.Builds;
                for (int i = 0; i < 60; i++) window.UpdatePointerHover(new Point(40 + i * 12, 60 + i * 7));
                Check(window.pickCache.Builds == builds + 1, $"Hover rebuilt the object index {window.pickCache.Builds - builds} times for one scene");
                var target = Topmost(window.doc); target.X -= 23; var ink = Ink(window.doc, target);
                window.UpdatePointerHover(ink);
                Check(window.canvas.HoveredLayerId == target.Id && window.pickCache.Builds == builds + 2, "Hover did not follow the moved object");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        // A closed or no longer shown drawing must not stay referenced by the
        // canvas' scene state, its cached viewports or the hover index.
        test("the design preview releases a drawing once the canvas stops drawing it", () =>
        {
            var drawing = SyntheticDrawing.Create(groups: 2, objectsPerGroup: 40, width: 400, height: 300, materials: 1);
            var photo = Photo(400, 300);
            var canvas = new CanvasView();
            var size = new Size(480, 360); canvas.Measure(size); canvas.Arrange(new Rect(size)); canvas.UpdateLayout();
            void Frame() { canvas.InvalidateVisual(); canvas.UpdateLayout(); }
            void Show(Document document)
            {
                canvas.Document = document; canvas.Composite = Imaging.Render(document).Bitmap(); canvas.DesignMode = true; canvas.Fit(); Frame();
                Check(SettleDesign(canvas), "Design viewport failed: " + canvas.DesignPreviewError);
                Check(canvas.HoldsDesignScene(document), "The shown drawing was not tracked; the check is not meaningful");
            }
            try
            {
                Show(drawing);
                canvas.Document = photo; canvas.Composite = Imaging.Render(photo).Bitmap(); Frame();
                Check(!canvas.HoldsDesignScene(drawing), "A photo shown after a drawing kept the drawing's scene state or viewports");
                Show(drawing);
                canvas.DesignMode = false; Frame();
                Check(!canvas.HoldsDesignScene(drawing), "Photo editing mode kept the drawing's scene state or viewports");
                Show(drawing);
                canvas.Document = null; canvas.Composite = null; Frame();
                Check(!canvas.HoldsDesignScene(drawing), "An empty canvas kept the closed drawing's scene state or viewports");
            }
            finally { canvas.CancelDesignPreview(); }
        });

        test("closing a drawing tab releases its hover index", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(Photo(800, 560), null);
                window.AddTab(SyntheticDrawing.Create(groups: 3, objectsPerGroup: 60, width: 800, height: 560, materials: 1), null);
                window.SetTool(Tool.Move); window.autoSelectToggle.IsChecked = true;
                window.doc.ActiveId = window.doc.Layers.First(l => l.Kind == LayerKind.Shape).Id; window.canvas.Zoom = 1;
                for (int i = 0; i < 20; i++) window.UpdatePointerHover(new Point(40 + i * 30, 60 + i * 20));
                var drawing = window.doc;
                Check(window.pickCache.Holds(drawing), "Hover did not index the drawing; the check is not meaningful");
                window.CloseTab();
                Check(window.tabs.Count == 1 && !ReferenceEquals(window.doc, drawing), "The drawing tab did not close");
                Check(!window.pickCache.Holds(drawing), "The hover index kept the closed drawing");
                Check(!window.canvas.HoldsDesignScene(drawing), "The canvas kept the closed drawing");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("path passes and viewport culling leave settled design pixels unchanged", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 400, width: 800, height: 560, materials: 2);
            var page = new Rect(0, 0, doc.Width, doc.Height);
            var single = DesignRenderer.Render(doc, page, 800, 560, default, int.MaxValue);
            var passes = 0; var split = DesignRenderer.Render(doc, page, 800, 560, default, 100, () => passes++);
            Check(passes >= 16, $"Expected at least 16 passes, got {passes}");
            Check(single.Data.AsSpan().SequenceEqual(split.Data), "Rendering in passes changed pixels");
            // A zoomed viewport draws only objects in view; its pixels equal the same
            // region of a whole-page render at that scale. (Tiled material fills are
            // not shift-invariant to the last level, so this page has none.)
            doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 400, width: 800, height: 560, materials: 0);
            var full = DesignRenderer.Render(doc, page, 2400, 1680, default, int.MaxValue);
            var part = DesignRenderer.Render(doc, new Rect(200, 140, 160, 120), 480, 360);
            for (int y = 0; y < 360; y++)
                Check(part.Data.AsSpan(y * 480 * 4, 480 * 4).SequenceEqual(full.Data.AsSpan(((y + 420) * 2400 + 600) * 4, 480 * 4)), $"Culled viewport row {y} differs from the full render");
        });

        test("a canceled design viewport stops after the current path pass", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 300, width: 800, height: 560, materials: 0);
            using var cancel = new CancellationTokenSource(); int passes = 0; bool stopped = false;
            try { DesignRenderer.Render(doc, new Rect(0, 0, doc.Width, doc.Height), 800, 560, cancel.Token, 100, () => { if (++passes == 1) cancel.Cancel(); }); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && passes == 1, $"A canceled render continued for {passes} passes");
        });

        test("wheel zoom reuses the settled design viewport and renders once after input pauses", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 120, width: 800, height: 560, materials: 1);
            var canvas = new CanvasView { Document = doc, Composite = Imaging.Render(doc).Bitmap(), DesignMode = true };
            var size = new Size(480, 360); canvas.Measure(size); canvas.Arrange(new Rect(size)); canvas.UpdateLayout(); canvas.Fit();
            void Frame() { canvas.InvalidateVisual(); canvas.UpdateLayout(); }
            bool Settle()
            {
                var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => { canvas.UpdateLayout(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || watch.ElapsedMilliseconds > 30000) frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); return canvas.IsDesignPreviewReady;
            }
            try
            {
                Frame(); Check(Settle(), "Initial design viewport failed: " + canvas.DesignPreviewError);
                int started = canvas.DesignRendersStarted, completed = canvas.DesignRendersCompleted, reused = canvas.DesignReusedFrames;
                var center = new Point(240, 180);
                for (int i = 0; i < 6; i++) { canvas.ZoomAt(1.15, center); Frame(); }
                Check(canvas.DesignRendersStarted == started, "A wheel step rendered the drawing before input paused");
                Check(canvas.DesignReusedFrames == reused + 6, "Wheel frames did not draw the previous viewport scaled");
                Check(!canvas.IsDesignPreviewReady, "A stale viewport was reported as current");
                Check(Settle(), "Settled viewport failed: " + canvas.DesignPreviewError);
                Check(canvas.DesignRendersStarted == started + 1 && canvas.DesignRendersCompleted == completed + 1, "A wheel burst should render only its final viewport");
                var (area, width, height) = canvas.DesignViewport!.Value;
                var expected = DesignRenderer.Render(doc, area, width, height);
                Check(Raster.FromBitmap(canvas.DesignImage!).Data.AsSpan().SequenceEqual(expected.Data), "The settled viewport differs from a full render");

                // Pan: a queued viewport is canceled as soon as a newer one is requested.
                started = canvas.DesignRendersStarted; completed = canvas.DesignRendersCompleted;
                canvas.Pan += new Vector(40, 25); Frame();
                var stale = canvas.PendingDesignToken;
                canvas.Pan += new Vector(-15, 10); Frame();
                Check(stale is { IsCancellationRequested: true }, "An older viewport request stayed active");
                Check(Settle() && canvas.DesignRendersStarted == started + 1 && canvas.DesignRendersCompleted == completed + 1, "Only the newest pan viewport should render");

                // Returning to a view shown before (here the fitted page) is immediate.
                started = canvas.DesignRendersStarted; int hits = canvas.DesignCacheHits;
                canvas.Fit(); Frame();
                Check(canvas.IsDesignPreviewReady && canvas.DesignCacheHits == hits + 1 && canvas.PendingDesignToken == null, "The fitted page was not reused from the viewport cache");
                Check(Settle() && canvas.DesignRendersStarted == started, "A cached view was rendered again");
                Check(Raster.FromBitmap(canvas.DesignImage!).Data.AsSpan().SequenceEqual(DesignRenderer.Render(doc, canvas.DesignViewport!.Value.Area, canvas.DesignViewport.Value.Width, canvas.DesignViewport.Value.Height).Data), "The cached fitted page differs from a full render");
            }
            finally { canvas.CancelDesignPreview(); }
        });
    }
}
