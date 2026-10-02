using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Drag start must not wait for a full flatten: planes prepared while an object is
// selected (or kept from its last drag) show at once, an edit to any other layer
// discards them, and otherwise the composite on screen stands in until they are
// ready. These checks cover reuse, invalidation, cancellation and the exact
// pixels of the interim frame, never wall-clock thresholds.
public sealed partial class MainWindow
{
    // Runs dispatcher work (async continuations) until the condition holds.
    internal static bool PumpDispatcherUntil(Func<bool> done, int timeoutMilliseconds = 30000)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        void Check()
        {
            if (done() || clock.ElapsedMilliseconds > timeoutMilliseconds) frame.Continue = false;
            else dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Check);
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Check);
        Dispatcher.PushFrame(frame);
        return done();
    }

    internal static void RunMovePlaneCacheTests(Action<string, Action> run)
    {
        // Async preview work resumes on the dispatcher thread, as it does inside the running app.
        void test(string name, Action body) => run(name, () =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static MainWindow Open(Document document, Guid moverId)
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.AddTab(document, null); window.SetTool(Tool.Move);
            window.doc.ActiveId = moverId; window.selectedLayers.Clear(); window.selectedLayers.Add(moverId);
            return window;
        }
        static void BeginDrag(MainWindow window)
        {
            window.beforeGesture = window.doc.Snapshot(); window.dragging = true; window.moveStarted = true;
            window.CaptureMoveInterimSource();
        }
        static void EndDrag(MainWindow window)
        {
            window.dragging = false; window.moveStarted = false; window.beforeGesture = null;
            // What the full render does when it replaces the preview after a drop.
            window.ClearTextMovePreview();
        }
        static MovePlanes Ready(MainWindow window)
        {
            var planes = window.PrepareMovePlanes() ?? throw new InvalidOperationException("The object was not eligible for move planes");
            Check(PumpDispatcherUntil(() => planes.Ready || planes.Failed) && planes.Ready, "Move planes did not finish");
            return planes;
        }
        static Document Drawing() => SyntheticDrawing.Create(groups: 3, objectsPerGroup: 40, width: 600, height: 420, materials: 2);
        static Layer TopObject(Document document) => document.Layers.Last(l => l.Kind == LayerKind.Vector && !l.Locked);
        static Layer MiddleObject(Document document)
        {
            var objects = document.Layers.Where(l => l.Kind == LayerKind.Vector && !l.Locked).ToArray();
            return objects[objects.Length / 2];
        }

        test("a drag starts from planes prepared while the object was selected and reuses them after the drop", () =>
        {
            var source = Drawing(); var window = Open(source, TopObject(source).Id);
            try
            {
                var mover = window.doc.Active!; var planes = Ready(window); int builds = window.MovePlaneBuilds;
                BeginDrag(window); mover.X += 9;
                Check(window.TryPreviewTextMove(), "An eligible drag did not use the fast move preview");
                // Shown within the same pointer event: no flatten, no waiting.
                Check(ReferenceEquals(window.canvas.MovePreviewBackground, planes.Background) && window.canvas.MovePreviewLayer != null &&
                    window.canvas.MovePreviewForegroundBounds == null && window.canvas.MovePreviewMatrix == mover.Matrix && !window.textPreviewInterim,
                    "Prepared planes were not shown at drag start");
                Check(window.MovePlaneBuilds == builds, "Drag start flattened the document again");
                mover.Y -= 4; window.TryPreviewTextMove();
                Check(window.canvas.MovePreviewMatrix == mover.Matrix, "A later pointer event did not move the object");
                EndDrag(window);
                Check(window.canvas.MovePreviewBackground == null && window.canvas.MovePreviewLayer == null && window.canvas.MovePreviewForeground == null &&
                    window.canvas.MovePreviewForegroundBounds == null, "The settled frame kept a preview plane over the full composite");
                Check(ReferenceEquals(window.movePlanes, planes) && window.MovePlanesCurrent(planes, window.doc, mover.Id), "The dropped object's planes were not kept");
                window.Refresh(false);
                Check(ReferenceEquals(window.movePlanes, planes), "A refresh after the drop discarded planes that are still current");
                BeginDrag(window); mover.X -= 20;
                window.TryPreviewTextMove();
                Check(ReferenceEquals(window.canvas.MovePreviewBackground, planes.Background) && window.MovePlaneBuilds == builds,
                    "The second drag of the same object flattened the document again");
                // Later pointer events skip the stack check but still see a distortion of the object.
                mover.Warp = new(new Point(0, 0), new Point(mover.Pixels.Width, 0), new Point(mover.Pixels.Width * .8, mover.Pixels.Height), new Point(0, mover.Pixels.Height));
                Check(!window.TryPreviewTextMove() && window.canvas.MovePreviewBackground == null, "A distorted object kept the affine move preview");
                mover.Warp = null;
                EndDrag(window);
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("move planes are discarded when another layer, the materials, the object or the document changes", () =>
        {
            var source = Drawing(); var window = Open(source, MiddleObject(source).Id);
            try
            {
                var mover = window.doc.Active!; var below = window.doc.Layers.First(l => l.Kind == LayerKind.Vector && l.Id != mover.Id);
                var above = TopObject(window.doc);
                void Discarded(string what, Action edit, Action undo)
                {
                    var planes = Ready(window);
                    edit();
                    Check(!window.MovePlanesCurrent(planes, window.doc, mover.Id), what + " left the planes current");
                    window.Refresh(false);
                    Check(window.movePlanes == null && planes.Cancel.IsCancellationRequested, what + " did not release the planes");
                    undo();
                }
                // The object's own changes never invalidate them.
                var kept = Ready(window);
                mover.X += 30; mover.Opacity = .4; mover.Visible = false; mover.Pixels = mover.Pixels.Clone();
                Check(window.MovePlanesCurrent(kept, window.doc, mover.Id), "Moving or restyling the dragged object discarded its planes");
                mover.Visible = true; mover.Opacity = 1;
                Discarded("Moving a layer below", () => below.X += 3, () => below.X -= 3);
                Discarded("Hiding a layer above", () => above.Visible = false, () => above.Visible = true);
                int at = window.doc.Layers.IndexOf(above);
                Discarded("Reordering the stack", () => { window.doc.Layers.Remove(above); window.doc.Layers.Insert(1, above); },
                    () => { window.doc.Layers.Remove(above); window.doc.Layers.Insert(at, above); });
                Discarded("Changing the material library", () => window.doc.Materials.Add(MaterialPresets.Create(MaterialKind.Concrete)), () => window.doc.Materials.RemoveAt(window.doc.Materials.Count - 1));
                var selected = Ready(window);
                window.doc.ActiveId = below.Id; window.Refresh(false);
                Check(window.movePlanes == null && selected.Cancel.IsCancellationRequested, "Selecting another object kept the previous object's planes");
                window.doc.ActiveId = mover.Id;
                var planes = Ready(window);
                window.AddTab(Drawing(), null);
                Check(window.movePlanes == null && planes.Cancel.IsCancellationRequested && !window.movePlaneScene.Holds(source),
                    "Another document kept the previous document's planes");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("a move plane build made stale while it runs is canceled and never shown", () =>
        {
            var source = Drawing(); var window = Open(source, TopObject(source).Id);
            try
            {
                var hold = window.movePlanesHold = new TaskCompletionSource();
                var planes = window.PrepareMovePlanes()!;
                window.doc.Layers.First(l => l.Kind == LayerKind.Vector).Y += 2; window.Refresh(false);
                Check(planes.Cancel.IsCancellationRequested && window.movePlanes == null, "An edit did not cancel the planes being flattened");
                hold.SetResult(); window.movePlanesHold = null;
                Check(PumpDispatcherUntil(() => planes.Build.IsCompleted), "The canceled build did not stop");
                Check(!planes.Ready && window.movePlanes == null && window.canvas.MovePreviewBackground == null, "A canceled build published planes");
                // Shutdown cancels a build in flight too.
                var running = window.PrepareMovePlanes()!; window.StopRenderingForShutdown();
                Check(running.Cancel.IsCancellationRequested && window.movePlanes == null, "Shutdown left a move plane build running");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("the first drag frame reuses the composite and re-renders only the object's footprint, exactly", () =>
        {
            // Opaque photo, a translucent layer below, the dragged object, a layer above
            // overlapping its footprint and one elsewhere: the plain pixel renderer.
            var document = new Document { Width = 64, Height = 48 };
            var photo = new Raster(64, 48);
            for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
            { int i = (y * 64 + x) * 4; photo.Data[i] = (byte)(x * 4); photo.Data[i + 1] = (byte)(y * 5); photo.Data[i + 2] = (byte)(x * y % 255); photo.Data[i + 3] = 255; }
            document.Add(new Layer { Name = "photo", Pixels = photo, Locked = true });
            document.Add(new Layer { Name = "tint", Pixels = Raster.Solid(30, 20, Color.FromArgb(110, 30, 200, 90)), X = 12, Y = 10 });
            var moving = Raster.Solid(10, 8, Color.FromArgb(210, 230, 20, 20)); moving.Data[3] = 40;
            var mover = new Layer { Name = "mover", Pixels = moving, X = 20, Y = 15 }; document.Add(mover);
            document.Add(new Layer { Name = "post", Pixels = Raster.Solid(4, 20, Color.FromArgb(180, 20, 160, 40)), X = 24, Y = 10 });
            document.Add(new Layer { Name = "far", Pixels = Raster.Solid(6, 6, Colors.Navy), X = 50, Y = 36 });
            var window = Open(document, mover.Id);
            try
            {
                mover = window.doc.Active!;
                var full = Imaging.Render(window.doc); window.RememberCurrentComposite(full);
                var withoutMover = window.doc.Snapshot(); withoutMover.Layers.RemoveAll(l => l.Id == mover.Id);
                var expectedOutside = Imaging.Render(withoutMover);
                var (belowStack, aboveStack) = CreateLayerMovePreviewStacks(window.doc, mover.Id);
                var expectedBelow = Imaging.Render(belowStack); var expectedAbove = Imaging.Render(aboveStack);
                var hold = window.movePlanesHold = new TaskCompletionSource();
                BeginDrag(window);
                Check(window.moveInterimSource is { } captured && captured.Footprint == MoveFootprint(window.doc, mover) && !captured.Design,
                    "A current composite was not captured for the first frames");
                var footprint = window.moveInterimSource!.Value.Footprint;
                mover.X += 11; mover.Y += 3;
                window.TryPreviewTextMove();
                var planes = window.textPreviewPlanes!;
                Check(PumpDispatcherUntil(() => window.canvas.MovePreviewBackground != null), "No interim frame appeared while the planes were held");
                Check(window.textPreviewInterim && !planes.Ready && window.canvas.MovePreviewLayer != null && window.canvas.MovePreviewMatrix == mover.Matrix,
                    "The first frame did not show the moving object over the interim background");
                Check(window.canvas.MovePreviewForegroundBounds == new Rect(footprint.X, footprint.Y, footprint.Width, footprint.Height),
                    "The interim foreground does not cover exactly the original footprint");
                var background = Raster.FromBitmap(window.canvas.MovePreviewBackground!);
                var foreground = Raster.FromBitmap(window.canvas.MovePreviewForeground!);
                for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
                {
                    int i = (y * 64 + x) * 4; bool inside = footprint.X <= x && x < footprint.X + footprint.Width && footprint.Y <= y && y < footprint.Y + footprint.Height;
                    var expected = inside ? expectedBelow : expectedOutside;
                    Check(background.Data.AsSpan(i, 4).SequenceEqual(expected.Data.AsSpan(i, 4)), $"Interim background differs at {x},{y} ({(inside ? "footprint" : "outside")})");
                    if (inside)
                    {
                        int j = ((y - footprint.Y) * footprint.Width + x - footprint.X) * 4;
                        Check(foreground.Data.AsSpan(j, 4).SequenceEqual(expectedAbove.Data.AsSpan(i, 4)), $"Interim foreground differs at {x},{y}");
                    }
                }
                hold.SetResult(); window.movePlanesHold = null;
                Check(PumpDispatcherUntil(() => planes.Ready) && ReferenceEquals(window.canvas.MovePreviewBackground, planes.Background) &&
                    window.canvas.MovePreviewForegroundBounds == null && !window.textPreviewInterim, "The full planes did not replace the interim frame");
                Check(Raster.FromBitmap(planes.Background!).Data.AsSpan().SequenceEqual(expectedBelow.Data), "The full background plane differs from the flattened stack below");
                EndDrag(window);
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("the drawing interim footprint matches the vector renderer of the whole drawing", () =>
        {
            // Without material fills the footprint render equals the whole-page render
            // (tiled hatches are not shift-invariant to the last level).
            var source = SyntheticDrawing.Create(groups: 3, objectsPerGroup: 60, width: 600, height: 420, materials: 0);
            var window = Open(source, MiddleObject(source).Id);
            try
            {
                var mover = window.doc.Active!;
                var full = Imaging.Render(window.doc); window.RememberCurrentComposite(full);
                BeginDrag(window);
                Check(window.moveInterimSource is { Design: true } captured, "The drawing's composite was not captured");
                var footprint = window.moveInterimSource!.Value.Footprint;
                var (below, above) = CreateLayerMovePreviewStacks(window.doc, mover.Id);
                var (background, foreground) = ComposeMoveInterim(full, below, above, footprint, true, default);
                var withoutMover = window.doc.Snapshot(); withoutMover.Layers.RemoveAll(l => l.Id == mover.Id);
                var expected = Imaging.Render(withoutMover);
                var belowFull = DesignRenderer.Render(below, new Rect(0, 0, source.Width, source.Height), source.Width, source.Height);
                Check(background.Data.AsSpan().SequenceEqual(ComposeExpected(expected, belowFull, footprint).Data), "The drawing interim background differs from the drawing without the object");
                var aboveFull = DesignRenderer.Render(above, new Rect(0, 0, source.Width, source.Height), source.Width, source.Height);
                var region = new Raster(footprint.Width, footprint.Height);
                for (int row = 0; row < footprint.Height; row++)
                    Buffer.BlockCopy(aboveFull.Data, ((footprint.Y + row) * source.Width + footprint.X) * 4, region.Data, row * footprint.Width * 4, footprint.Width * 4);
                Check(foreground == null ? region.Data.All(b => b == 0) : foreground.Data.AsSpan().SequenceEqual(region.Data), "The drawing interim foreground differs from the layers above");
                EndDrag(window);
            }
            finally { window.StopRenderingForShutdown(); }

            static Raster ComposeExpected(Raster outside, Raster inside, Int32Rect area)
            {
                var result = outside.Clone();
                for (int row = 0; row < area.Height; row++)
                    Buffer.BlockCopy(inside.Data, ((area.Y + row) * outside.Width + area.X) * 4, result.Data, ((area.Y + row) * outside.Width + area.X) * 4, area.Width * 4);
                return result;
            }
        });

        test("a stale or replaced composite never stands in for the first drag frames", () =>
        {
            var source = Drawing(); var window = Open(source, TopObject(source).Id);
            try
            {
                var full = Imaging.Render(window.doc); window.RememberCurrentComposite(full);
                BeginDrag(window); Check(window.moveInterimSource != null, "A current composite was not used; the check is not meaningful"); EndDrag(window);
                var other = window.doc.Layers.First(l => l.Kind == LayerKind.Vector); other.X += 1;
                BeginDrag(window); Check(window.moveInterimSource == null, "A composite of an earlier state stood in for the first frame"); EndDrag(window);
                other.X -= 1; window.RememberCurrentComposite(full);
                window.composite = full.Clone();
                BeginDrag(window); Check(window.moveInterimSource == null, "A composite set elsewhere stood in for the first frame"); EndDrag(window);
                window.RememberCurrentComposite(full); window.moveInterimEnabled = false;
                BeginDrag(window); Check(window.moveInterimSource == null, "The interim frame ignored its switch"); EndDrag(window);
                // Without a stand-in, the drag keeps the previous frame until the planes are ready.
                window.moveInterimEnabled = true; window.composite = null;
                BeginDrag(window); window.doc.Active!.X += 5; window.TryPreviewTextMove();
                Check(window.canvas.MovePreviewBackground == null, "A drag without a stand-in showed a frame before its planes");
                Check(PumpDispatcherUntil(() => window.canvas.MovePreviewBackground != null) && !window.textPreviewInterim, "The planes never replaced the previous frame");
                EndDrag(window);
                // Another document releases the record of this one's composite.
                window.RememberCurrentComposite(full); var shown = window.doc;
                window.AddTab(Drawing(), null);
                Check(window.compositeSource == null && !window.compositeScene.Holds(shown), "Another document kept the previous document's composite record");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("selected objects get move planes ahead of a drag only in slow documents within the memory budget", () =>
        {
            var source = Drawing(); var window = Open(source, TopObject(source).Id);
            try
            {
                window.lastCompositeMilliseconds = 10;
                Check(!window.WantsMovePlanes(), "A fast document flattened ahead of a drag");
                window.lastCompositeMilliseconds = 500;
                Check(window.WantsMovePlanes(), "A slow document did not prepare planes for its selected object");
                window.SetTool(Tool.Brush);
                Check(!window.WantsMovePlanes(), "Planes were prepared outside the move tool");
                window.SetTool(Tool.Move); window.doc.ActiveId = TopObject(window.doc).Id; window.selectedLayers.Add(window.doc.ActiveId);
                Ready(window);
                Check(!window.WantsMovePlanes(), "Current planes were scheduled again");
                window.doc.ActiveId = window.doc.Layers.First(l => l.Kind == LayerKind.Material).Id;
                Check(!window.WantsMovePlanes(), "A layer the fast preview cannot move got planes");
                var large = new Document { Width = 5200, Height = 5000 };
                var dot = new Layer { Pixels = Raster.Solid(4, 4, Colors.Red) }; large.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.White) }); large.Add(dot);
                window.AddTab(large, null); window.doc.ActiveId = dot.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(dot.Id);
                window.lastCompositeMilliseconds = 500;
                Check(CanPreviewLayerMove(window.doc, dot, 1) && !window.WantsMovePlanes(), "Planes beyond the speculative memory budget were prepared");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("scene state can ignore one object while tracking every other layer", () =>
        {
            var document = Drawing(); var mover = TopObject(document); var other = document.Layers.First(l => l.Kind == LayerKind.Vector);
            var state = new SceneState(); long version = state.Update(document, mover.Id);
            mover.X += 5; mover.Opacity = .3; mover.Pixels = mover.Pixels.Clone();
            Check(state.Update(document, mover.Id) == version, "Changes to the ignored object were reported");
            Check(state.Update(document) != version, "Switching to full tracking was not reported");
            version = state.Update(document, mover.Id);
            other.Y += 1;
            Check(state.Update(document, mover.Id) != version, "A change to another layer was missed");
            version = state.Update(document, mover.Id);
            mover.ParentId = null;
            Check(state.Update(document, mover.Id) != version, "Moving the ignored object to another parent was missed");
        });
    }
}
