using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Design style folders are pass-through: their adjustments (threshold, halftone, paper texture) read
// every layer below them. The fast move preview must draw exactly what the compositor draws with such a
// folder below, around or above the dragged object, or fall back to the full compositor; a drop's
// footprint redraw must equal a fresh crisp render.
public sealed partial class MainWindow
{
    internal static void RunMovePreviewStyleTests(Action<string, Action> run)
    {
        void test(string name, Action body) => run(name, () =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        // The three planes composed as CanvasView draws them, after moving the object.
        static Raster Planes(Document document, Guid movingId, double dx, double dy)
        {
            var (below, above) = CreateLayerMovePreviewStacks(document, movingId);
            var moved = document.Layers.Single(l => l.Id == movingId).Snapshot(); moved.X += dx; moved.Y += dy; moved.ParentId = null;
            var composed = Imaging.Render(below);
            Imaging.Composite(composed, moved);
            if (above.Layers.Count > 0) Imaging.Composite(composed, new Layer { Pixels = Imaging.Render(above) });
            return composed;
        }
        static Raster Expected(Document document, Guid movingId, double dx, double dy, bool passThrough = true)
        {
            var moved = document.Snapshot(); var layer = moved.Layers.Single(l => l.Id == movingId); layer.X += dx; layer.Y += dy;
            if (!passThrough) foreach (var folder in moved.Layers.Where(l => l.PassThrough)) folder.PassThrough = false;
            return Imaging.Render(moved);
        }
        static void Exact(Document document, Layer mover, string what)
        {
            Check(CanPreviewLayerMove(document, mover, 1), what + ": not eligible for the fast move preview");
            Check(Planes(document, mover.Id, 7, 3).Data.AsSpan().SequenceEqual(Expected(document, mover.Id, 7, 3).Data), what + ": the move preview differs from the full render");
        }
        static Layer Solid(int w, int h, Color color, double x, double y, Guid? parent = null) => new() { Pixels = Raster.Solid(w, h, color), X = x, Y = y, ParentId = parent };
        // A tone ramp under a style folder holding the new effects, a label in it, and room for a mover.
        static (Document Document, Layer Folder) Scene()
        {
            var document = new Document { Width = 64, Height = 40 };
            var ramp = new Raster(64, 40);
            for (int y = 0; y < 40; y++) for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4; byte v = Imaging.Byte(30 + 200 * x / 63.0 - 20 * Math.Sin(y / 4.0));
                ramp.Data[i] = v; ramp.Data[i + 1] = Imaging.Byte(v * .9 + 20); ramp.Data[i + 2] = Imaging.Byte(255 - v * .6); ramp.Data[i + 3] = 255;
            }
            document.Add(new Layer { Name = "바탕", Pixels = ramp, Locked = true });
            var folder = new Layer { Kind = LayerKind.Group, Name = "스타일", PassThrough = true, Pixels = new Raster(64, 40), Style = new StyleTag(DesignStyles.ScreentonePlan, 2, [], [], []) };
            document.Add(folder);
            foreach (var spec in new[]
            {
                new AdjustmentSpec { Kind = AdjustmentKind.GradientMap, DarkColor = 0xFF101830, LightColor = 0xFFF4EEE0 },
                new AdjustmentSpec { Kind = AdjustmentKind.Threshold, Threshold = new ThresholdSpec { Level = 150, Smoothness = 24 } },
                new AdjustmentSpec { Kind = AdjustmentKind.Halftone, Halftone = new HalftoneSpec { CellSize = 5, Angle = 30, InkArgb = 0xFF14204A, PaperArgb = 0xFFF1D9DB } },
                new AdjustmentSpec { Kind = AdjustmentKind.PaperTexture, Paper = new PaperTextureSpec { Scale = 1, Grain = .6, Toner = .5, Streaks = .4, Edges = .5, EdgeWidth = .1 } }
            })
            {
                var adjustment = DocumentFeatures.CreateAdjustment(document, spec); adjustment.ParentId = folder.Id; document.Add(adjustment);
            }
            document.Add(Solid(10, 6, Color.FromRgb(230, 60, 40), 40, 4, folder.Id));
            return (document, folder);
        }

        test("move preview keeps a pass-through style folder exact around the dragged object or falls back", () =>
        {
            // Under the folder: its adjustments read the moving pixels, so three planes cannot show it.
            var (document, folder) = Scene();
            var under = Solid(8, 8, Colors.Yellow, 6, 20); document.Add(under);
            document.Layers.Remove(under); document.Layers.Insert(1, under);
            Check(!CanPreviewLayerMove(document, under, 1), "An object under a style folder's adjustments was split into planes");

            // Over the folder: the folder stays whole in the plane below, still reading the layers under it.
            (document, folder) = Scene();
            var over = Solid(8, 8, Colors.Yellow, 6, 20); document.Add(over);
            Check(!Expected(document, over.Id, 7, 3, passThrough: false).Data.AsSpan().SequenceEqual(Expected(document, over.Id, 7, 3).Data),
                "The scene does not exercise the pass-through folder; the check is not meaningful");
            Exact(document, over, "Object over a style folder");
            var (below, above) = CreateLayerMovePreviewStacks(document, over.Id);
            Check(below.Layers.Any(l => l.Id == folder.Id && l.PassThrough) && below.Layers.Count(l => l.ParentId == folder.Id) == 5 && above.Layers.Count == 0,
                "The style folder was not kept whole below the object");
            // A folder shown at 60% through a mask mixes its result with what was there: still one kept folder.
            folder.Opacity = .6; folder.Mask = Enumerable.Range(0, 64 * 40).Select(i => (byte)(i % 64 < 32 ? 255 : 90)).ToArray();
            Exact(document, over, "Object over a translucent, masked style folder");

            // Inside the folder above its adjustments (a text or label of the style): exact; under one of them: fallback.
            (document, folder) = Scene();
            var inside = Solid(8, 8, Colors.Yellow, 6, 20, folder.Id); document.Add(inside);
            document.Add(Solid(6, 6, Color.FromRgb(20, 140, 90), 50, 28, folder.Id));
            Exact(document, inside, "Object inside a style folder above its adjustments");
            folder.Opacity = .6;
            Check(!CanPreviewLayerMove(document, inside, 1), "A translucent style folder holding the object was split into planes");
            folder.Opacity = 1;
            var grain = DocumentFeatures.CreateAdjustment(document, new AdjustmentSpec { Kind = AdjustmentKind.PaperTexture }); grain.ParentId = folder.Id; document.Add(grain);
            Check(!CanPreviewLayerMove(document, inside, 1), "An object under an adjustment inside its style folder was split into planes");
        });

        test("a dirty-rectangle render equals a fresh render inside the rectangle with design style folders", () =>
        {
            var services = DesignStyleTests.Services;
            var plan = SyntheticPlan.Create(1200, 800); DesignStyleEngine.Apply(plan, new StyleRequest(DesignStyles.ScreentonePlan), services);
            var section = SyntheticPlan.Create(1200, 800); DesignStyleEngine.Apply(section, new StyleRequest(DesignStyles.DarkSection), services);
            var halftone = SyntheticPhoto.Create(960, 600); DesignStyleEngine.Apply(halftone, new StyleRequest(DesignStyles.NeoBrutalistPoster), services);
            var bitmap = SyntheticPhoto.Create(960, 600); DesignStyleEngine.Apply(bitmap, new StyleRequest(DesignStyles.NeoBrutalistPoster, new Dictionary<string, double> { ["photo"] = 1, ["title"] = 1 }), services);
            var editorial = SyntheticPhoto.Create(960, 600); DesignStyleEngine.Apply(editorial, new StyleRequest(DesignStyles.TranslucentEditorial, new Dictionary<string, double> { ["glow"] = 0 }), services);
            foreach (var (document, label) in new[] { (plan, "screentone plan"), (section, "dark section"), (halftone, "halftone poster"), (bitmap, "bitmap poster"), (editorial, "editorial") })
            {
                Check(!StyleEffects.Spreads(document), label + ": the scene must not spread light (those drops render the whole view)");
                foreach (var (zoom, x, y) in new[] { (.5371, 11.3, 6.9), (1.0, 0, 0), (1.9137, 120.7, 64.3) })
                {
                    var area = new Rect(x, y, 520 / zoom, 360 / zoom); area.Intersect(new Rect(0, 0, document.Width, document.Height));
                    int width = (int)Math.Ceiling(area.Width * zoom), height = (int)Math.Ceiling(area.Height * zoom);
                    var fresh = DesignRenderer.Render(document, area, width, height);
                    var random = new Random(17);
                    for (int k = 0; k < 3; k++)
                    {
                        var dirty = new Int32Rect(random.Next(width - 110), random.Next(height - 80), 30 + random.Next(80), 20 + random.Next(60));
                        var part = DesignRenderer.RenderDirty(document, area, width, height, dirty);
                        for (int row = dirty.Y; row < dirty.Y + dirty.Height; row++)
                            Check(part.Data.AsSpan((row * width + dirty.X) * 4, dirty.Width * 4).SequenceEqual(fresh.Data.AsSpan((row * width + dirty.X) * 4, dirty.Width * 4)),
                                $"{label}: the dirty render differs from the fresh render at zoom {zoom}, row {row}");
                    }
                }
            }
        });

        test("a drop under, over or inside a design style folder ends on a frame equal to a fresh crisp render", () =>
        {
            foreach (var (styleId, values) in new (string, Dictionary<string, double>?)[] { (DesignStyles.ScreentonePlan, null), (DesignStyles.TranslucentEditorial, new() { ["glow"] = 60 }) })
            {
                var window = new MainWindow(null) { headlessTesting = true };
                try
                {
                    var document = SyntheticDrawing.Create(groups: 3, objectsPerGroup: 60, width: 800, height: 560, materials: 1);
                    var styled = DesignStyleEngine.Apply(document, new StyleRequest(styleId, values), DesignStyleTests.Services);
                    var note = DocumentFeatures.CreateText(new TextSpec { Content = "A-101", FontSize = 28, ColorArgb = 0xFFC0392B }, 120, 90); document.Add(note);
                    bool spreads = StyleEffects.Spreads(document);
                    window.AddTab(document, null); window.SetWorkspaceMode(true); window.SetTool(Tool.Move);
                    var content = (FrameworkElement)window.Content; var size = new Size(1280, 860);
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    var canvas = window.canvas; canvas.Fit(); canvas.ZoomAt(1.23, new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
                    void Frame() { canvas.InvalidateVisual(); content.UpdateLayout(); }
                    bool Crisp() => PumpDispatcherUntil(() => { Frame(); return canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null; }) && canvas.IsDesignPreviewReady;
                    bool Fresh()
                    {
                        var (area, width, height) = canvas.DesignViewport!.Value;
                        return Raster.FromBitmap(canvas.DesignImage!).Data.AsSpan().SequenceEqual(DesignRenderer.Render(window.doc, area, width, height).Data);
                    }
                    void Drop(Layer layer, double dx, double dy, bool fast, string what)
                    {
                        window.doc.ActiveId = layer.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(layer.Id);
                        window.RememberCurrentComposite(Imaging.Render(window.doc)); canvas.Composite = window.composite!.Bitmap();
                        Check(Crisp(), what + ": the crisp view did not settle before the drag: " + canvas.DesignPreviewError);
                        window.beforeGesture = window.doc.Snapshot(); window.dragging = true; window.moveStarted = false; window.CaptureMoveInterimSource();
                        // Spreading light reaches beyond the object's footprints: those drops never keep the frame to patch it.
                        Check(canvas.HoldsMoveBase != spreads, what + ": the crisp frame was kept (or dropped) against the glow rule");
                        window.moveStarted = true; layer.X += dx; layer.Y += dy;
                        Check(window.TryPreviewTextMove() == fast, what + (fast ? ": the fast preview was not used" : ": the fast preview was used across the style folder's adjustments"));
                        if (fast) { Check(PumpDispatcherUntil(() => canvas.MovePreviewLayer != null), what + ": the drag never showed the object moving"); Frame(); }
                        int patches = canvas.DesignPatchesCompleted;
                        window.dragging = false; window.CommitPointerGesture(canvas.ToDocument(new Point(400, 300)));
                        Check(Crisp(), what + ": no crisp frame after the drop: " + canvas.DesignPreviewError);
                        Check(fast && !spreads ? canvas.DesignPatchesCompleted == patches + 1 : canvas.DesignPatchesCompleted == patches,
                            what + ": the drop " + (fast && !spreads ? "rendered the whole view instead of the object's footprints" : "patched a frame it could not patch exactly"));
                        Check(Fresh(), what + ": the settled crisp frame differs from a fresh crisp render");
                        // Then the full composite of the same state arrives and the frame stays as it is.
                        window.composite = Imaging.Render(window.doc); canvas.Composite = window.composite.Bitmap();
                        window.ClearTextMovePreview(); canvas.AdoptComposite(); Frame();
                        Check(PumpDispatcherUntil(() => { Frame(); return canvas.PendingDesignToken == null; }, 20000) && Crisp() && Fresh(), what + ": the frame after the full composite differs from a fresh crisp render");
                    }
                    var objects = window.doc.Layers.Where(l => l.Kind == LayerKind.Vector && !l.Locked).ToArray();
                    Drop(objects[objects.Length / 2], 29, -17, false, $"{styleId}: object under the style folder");
                    Drop(window.doc.Layers.Single(l => l.Id == note.Id), -41, 23, true, $"{styleId}: text over the style folder");
                    // A layer the style made, under the folder's last adjustment: back to the full compositor.
                    var label = window.doc.Layers.First(l => l.ParentId == styled.GroupId && l.Kind is LayerKind.Text or LayerKind.Shape or LayerKind.Material);
                    Drop(label, 13, 9, false, $"{styleId}: a layer inside the style folder");
                }
                finally { window.StopRenderingForShutdown(); }
            }
        });
    }
}
