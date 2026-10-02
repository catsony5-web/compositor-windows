using System.Windows;
using System.Windows.Threading;

namespace Compositor.Windows;

// After a drop in the drawing view, the crisp frame the drag began from is reused:
// only the object's old and new footprints are drawn again, with the transforms and
// surfaces of a fresh render, so the settled frame equals a fresh crisp render.
public sealed partial class MainWindow
{
    internal static void RunMoveSettleTests(Action<string, Action> run)
    {
        void test(string name, Action body) => run(name, () =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        test("a drop redraws only the object's footprints into the kept crisp frame and equals a fresh crisp render", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 120, width: 800, height: 560, materials: 2);
                window.AddTab(document, null); window.SetWorkspaceMode(true); window.SetTool(Tool.Move);
                var content = (FrameworkElement)window.Content; var size = new Size(1280, 860);
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                var canvas = window.canvas; canvas.Fit(); canvas.ZoomAt(1.37, new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
                void Frame() { canvas.InvalidateVisual(); content.UpdateLayout(); }
                bool Crisp() => PumpDispatcherUntil(() => { Frame(); return canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null; }) && canvas.IsDesignPreviewReady;
                bool Fresh()
                {
                    var (area, width, height) = canvas.DesignViewport!.Value;
                    return Raster.FromBitmap(canvas.DesignImage!).Data.AsSpan().SequenceEqual(DesignRenderer.Render(window.doc, area, width, height).Data);
                }
                var objects = window.doc.Layers.Where(l => l.Kind == LayerKind.Vector && !l.Locked).ToArray();
                int started = 0, patches = 0;
                void Drag(Layer layer, double dx, double dy, Action? meanwhile = null)
                {
                    window.doc.ActiveId = layer.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(layer.Id);
                    window.RememberCurrentComposite(Imaging.Render(window.doc)); canvas.Composite = window.composite!.Bitmap();
                    Check(Crisp(), "The crisp view did not settle before the drag: " + canvas.DesignPreviewError);
                    window.beforeGesture = window.doc.Snapshot(); window.dragging = true; window.moveStarted = false; window.CaptureMoveInterimSource();
                    Check(canvas.HoldsMoveBase, "The crisp frame on screen was not kept at pointer down");
                    window.moveStarted = true; layer.X += dx; layer.Y += dy; meanwhile?.Invoke(); window.TryPreviewTextMove();
                    Check(PumpDispatcherUntil(() => window.canvas.MovePreviewLayer != null), "The drag never showed the object moving");
                    Frame();
                    started = canvas.DesignRendersStarted; patches = canvas.DesignPatchesCompleted;
                    window.dragging = false; window.CommitPointerGesture(canvas.ToDocument(new Point(400, 300)));
                }

                var mover = objects[objects.Length / 2];
                Drag(mover, 37, -21);
                Check(Crisp(), "No crisp frame after the drop: " + canvas.DesignPreviewError);
                Check(canvas.DesignPatchesCompleted == patches + 1 && canvas.DesignRendersStarted == started + 1, "The drop rendered the whole view instead of the object's footprints");
                Check(window.canvas.MovePreviewSettled && window.canvas.MovePreviewBackground != null, "The check does not exercise the crisp frame over the kept preview");
                Check(Fresh(), "The settled crisp frame differs from a fresh crisp render");

                // The full composite of the same state arrives: no further crisp render.
                started = canvas.DesignRendersStarted;
                window.composite = Imaging.Render(window.doc); canvas.Composite = window.composite.Bitmap();
                window.ClearTextMovePreview(); canvas.AdoptComposite(); Frame();
                Check(canvas.IsDesignPreviewReady && canvas.DesignRendersStarted == started, "The full composite made the settled frame render again");
                Check(PumpDispatcherUntil(() => { Frame(); return canvas.PendingDesignToken == null; }, 2000) && canvas.DesignRendersStarted == started && Fresh(), "The adopted frame was replaced or differs");

                // Another layer changed during the drag: the whole view is rendered as before.
                var other = objects[3];
                Drag(objects[^5], -15, 12, () => other.X += 4);
                Check(Crisp() && canvas.DesignPatchesCompleted == patches, "A drop that changed another layer patched the kept frame");
                Check(Fresh(), "The re-rendered frame differs from a fresh crisp render");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("a dirty-rectangle render equals a fresh render inside the rectangle at any zoom", () =>
        {
            var document = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 150, width: 800, height: 560, materials: 2);
            foreach (var (zoom, x, y) in new[] { (.6271, 13.37, 7.91), (1.0, 0, 0), (2.3317, 151.3, 87.77) })
            {
                var area = new Rect(x, y, 640 / zoom, 420 / zoom); area.Intersect(new Rect(0, 0, document.Width, document.Height));
                int width = (int)Math.Ceiling(area.Width * zoom), height = (int)Math.Ceiling(area.Height * zoom);
                var fresh = DesignRenderer.Render(document, area, width, height);
                var random = new Random(9);
                for (int k = 0; k < 6; k++)
                {
                    var dirty = new Int32Rect(random.Next(width - 120), random.Next(height - 90), 40 + random.Next(80), 30 + random.Next(60));
                    var part = DesignRenderer.RenderDirty(document, area, width, height, dirty);
                    for (int row = dirty.Y; row < dirty.Y + dirty.Height; row++)
                        Check(part.Data.AsSpan((row * width + dirty.X) * 4, dirty.Width * 4).SequenceEqual(fresh.Data.AsSpan((row * width + dirty.X) * 4, dirty.Width * 4)),
                            $"Dirty render differs from the fresh render at zoom {zoom}, row {row}");
                }
            }
        });
    }
}
