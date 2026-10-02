using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                    window.canvas.MovePreviewForegroundTiles == null && window.canvas.MovePreviewMatrix == mover.Matrix && !window.textPreviewInterim,
                    "Prepared planes were not shown at drag start");
                Check(window.MovePlaneBuilds == builds, "Drag start flattened the document again");
                mover.Y -= 4; window.TryPreviewTextMove();
                Check(window.canvas.MovePreviewMatrix == mover.Matrix, "A later pointer event did not move the object");
                EndDrag(window);
                Check(window.canvas.MovePreviewBackground == null && window.canvas.MovePreviewLayer == null && window.canvas.MovePreviewForeground == null &&
                    window.canvas.MovePreviewForegroundTiles == null, "The settled frame kept a preview plane over the full composite");
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

        foreach (var opaquePage in new[] { true, false })
        test($"the interim drag frame is exact where it was redrawn and keeps the object under the layers above it ({(opaquePage ? "opaque page: tiles over the composite" : "transparent page: written copy")})", () =>
        {
            // A photo (or nothing), a translucent layer below, the dragged object, a short layer above
            // its footprint, a long line above elsewhere and one more far away: the pixel renderer.
            int W = 600, H = 400;
            var document = new Document { Width = W, Height = H };
            var photo = new Raster(W, H);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            { int i = (y * W + x) * 4; photo.Data[i] = (byte)(x * 3); photo.Data[i + 1] = (byte)(y * 5); photo.Data[i + 2] = (byte)(x * y % 255); photo.Data[i + 3] = 255; }
            if (opaquePage) document.Add(new Layer { Name = "photo", Pixels = photo, Locked = true });
            document.Add(new Layer { Name = "tint", Pixels = Raster.Solid(60, 40, Color.FromArgb(110, 30, 200, 90)), X = 80, Y = 40 });
            var moving = Raster.Solid(10, 8, Color.FromArgb(255, 230, 20, 20)); moving.Data[3] = 40;
            var mover = new Layer { Name = "mover", Pixels = moving, X = 122, Y = 60 }; document.Add(mover);
            document.Add(new Layer { Name = "post", Pixels = Raster.Solid(4, 20, Color.FromArgb(180, 20, 160, 40)), X = 126, Y = 55 });
            document.Add(new Layer { Name = "line", Pixels = Raster.Solid(3, H, Color.FromRgb(20, 220, 60)), X = 240, Y = 0 });
            document.Add(new Layer { Name = "far", Pixels = Raster.Solid(6, 6, Colors.Navy), X = 570, Y = 370 });
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
                // Drop the object onto the line above it, away from its original tiles.
                mover.X = 236; mover.Y = 60;
                window.TryPreviewTextMove();
                var planes = window.textPreviewPlanes!; var interim = window.moveInterim!;
                Check(PumpDispatcherUntil(() => window.canvas.MovePreviewLayer != null), "No interim frame appeared while the planes were held");
                Check(window.textPreviewInterim && !planes.Ready && window.canvas.MovePreviewMatrix == mover.Matrix && window.canvas.MovePreviewForeground == null,
                    "The first frame did not show the moving object over the interim background");
                bool Ready(int x, int y) => interim.Ready.Contains((x / MoveInterim.Tile, y / MoveInterim.Tile));
                var background = Raster.FromBitmap(window.canvas.MovePreviewBackground!);
                Check(opaquePage ? window.canvas.MovePreviewBackground is not WriteableBitmap && window.canvas.MovePreviewBackgroundTiles is { Count: > 0 }
                    : window.canvas.MovePreviewBackground is WriteableBitmap && window.canvas.MovePreviewBackgroundTiles is { Count: 0 }, "The interim frame used the wrong background mode");
                // Opaque tiles drawn over the composite replace it.
                foreach (var (image, bounds) in window.canvas.MovePreviewBackgroundTiles!)
                {
                    var tile = Raster.FromBitmap(image);
                    for (int row = 0; row < tile.Height; row++)
                        Buffer.BlockCopy(tile.Data, row * tile.Width * 4, background.Data, (((int)bounds.Y + row) * W + (int)bounds.X) * 4, tile.Width * 4);
                }
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4; var expected = Ready(x, y) ? expectedBelow : expectedOutside;
                    Check(background.Data.AsSpan(i, 4).SequenceEqual(expected.Data.AsSpan(i, 4)), $"Interim background differs at {x},{y} ({(Ready(x, y) ? "redrawn" : "composite")})");
                }
                Check(window.canvas.MovePreviewForegroundTiles is { Count: > 0 } tiles, "The layers above were not drawn over the object");
                foreach (var (image, bounds) in window.canvas.MovePreviewForegroundTiles!)
                {
                    var tile = Raster.FromBitmap(image);
                    for (int y = 0; y < tile.Height; y++) for (int x = 0; x < tile.Width; x++)
                    {
                        int i = (y * tile.Width + x) * 4, j = (((int)bounds.Y + y) * W + (int)bounds.X + x) * 4;
                        Check(tile.Data.AsSpan(i, 4).SequenceEqual(expectedAbove.Data.AsSpan(j, 4)), $"Interim foreground tile differs at {bounds.X + x},{bounds.Y + y}");
                    }
                }
                // What the canvas draws: the line covers the object, the object shows beside it,
                // and its original place shows what is under it.
                var view = new CanvasView
                {
                    Document = window.doc, Composite = full.Bitmap(), Zoom = 2,
                    MovePreviewBackground = window.canvas.MovePreviewBackground, MovePreviewBackgroundTiles = window.canvas.MovePreviewBackgroundTiles, MovePreviewLayer = window.canvas.MovePreviewLayer,
                    MovePreviewMatrix = window.canvas.MovePreviewMatrix, MovePreviewForegroundTiles = window.canvas.MovePreviewForegroundTiles
                };
                view.Measure(new Size(1300, 900)); view.Arrange(new Rect(0, 0, 1300, 900));
                var surface = new RenderTargetBitmap(1300, 900, 96, 96, PixelFormats.Pbgra32); surface.Render(view);
                var screen = new byte[1300 * 900 * 4]; surface.CopyPixels(screen, 5200, 0);
                (byte R, byte G, byte B) Probe(double x, double y)
                {
                    int i = ((int)(view.Origin.Y + y * view.Zoom) * 1300 + (int)(view.Origin.X + x * view.Zoom)) * 4;
                    return (screen[i + 2], screen[i + 1], screen[i]);
                }
                var onLine = Probe(241.5, 64.5); var beside = Probe(237.5, 64.5); var original = Probe(123.5, 64.5);
                Check(onLine.G > 200 && onLine.R < 40, $"The moving object was drawn over the line above it: {onLine}");
                Check(beside.R > 220 && beside.G < 40, $"The moving object did not show beside the line: {beside}");
                Check(!(original.R > 200 && original.G < 40 && original.B < 40), $"The original place kept a ghost of the object: {original}");
                // The seam between two redrawn tiles under the original place shows no trace of the object either.
                var seam = Probe(MoveInterim.Tile - .5, 64.5); var seamRight = Probe(MoveInterim.Tile + .5, 64.5);
                Check(!(seam.R > 200 && seam.G < 40) && !(seamRight.R > 200 && seamRight.G < 40), $"A tile seam showed the object: {seam} {seamRight}");

                // A jump to tiles not rendered yet: the object waits there, then follows.
                var shown = window.canvas.MovePreviewMatrix;
                mover.X = 20; mover.Y = 360; window.TryPreviewTextMove();
                Check(window.canvas.MovePreviewMatrix == shown, "The object moved into tiles that were not ready");
                Check(PumpDispatcherUntil(() => window.canvas.MovePreviewMatrix == mover.Matrix), "The object never followed into newly rendered tiles");
                hold.SetResult(); window.movePlanesHold = null;
                Check(PumpDispatcherUntil(() => planes.Ready) && ReferenceEquals(window.canvas.MovePreviewBackground, planes.Background) &&
                    window.canvas.MovePreviewForegroundTiles == null && window.canvas.MovePreviewBackgroundTiles == null && window.moveInterim == null && !window.textPreviewInterim, "The full planes did not replace the interim frame");
                Check(Raster.FromBitmap(planes.Background!).Data.AsSpan().SequenceEqual(expectedBelow.Data), "The full background plane differs from the flattened stack below");
                EndDrag(window);
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("drawing interim tiles match the vector renderer of the whole page", () =>
        {
            // Without material fills a tile equals the same pixels of a whole-page render
            // (tiled hatches are not shift-invariant to the last level).
            var source = SyntheticDrawing.Create(groups: 3, objectsPerGroup: 80, width: 600, height: 420, materials: 0);
            var window = Open(source, MiddleObject(source).Id);
            try
            {
                var mover = window.doc.Active!;
                var full = Imaging.Render(window.doc); window.RememberCurrentComposite(full);
                BeginDrag(window);
                Check(window.moveInterimSource is { Design: true }, "The drawing's composite was not captured");
                var (below, above) = CreateLayerMovePreviewStacks(window.doc, mover.Id);
                var belowFull = DesignRenderer.Render(below, new Rect(0, 0, source.Width, source.Height), source.Width, source.Height);
                var aboveFull = DesignRenderer.Render(above, new Rect(0, 0, source.Width, source.Height), source.Width, source.Height);
                var withoutMover = window.doc.Snapshot(); withoutMover.Layers.RemoveAll(l => l.Id == mover.Id);
                var outside = Imaging.Render(withoutMover);
                var belowIndex = new MoveTileIndex(below); var aboveIndex = new MoveTileIndex(above);
                // The first tiles of a drag render from the whole stacks, later ones from the index.
                foreach (var indexed in new[] { false, true })
                foreach (var tile in MoveTiles(window.doc, MoveFootprint(window.doc, mover)).Concat([(0, 0), (4, 3)]))
                {
                    var area = TileArea(window.doc, tile);
                    var (b, a) = indexed ? RenderMoveTile(belowIndex.For(area), aboveIndex.For(area), area, true, default) : RenderMoveTile(below, above, area, true, default);
                    for (int y = 0; y < area.Height; y++) for (int x = 0; x < area.Width; x++)
                    {
                        int i = (y * area.Width + x) * 4, j = ((area.Y + y) * source.Width + area.X + x) * 4;
                        Check(b.Data.AsSpan(i, 4).SequenceEqual(belowFull.Data.AsSpan(j, 4)), $"Tile {tile} below differs at {area.X + x},{area.Y + y}");
                        Check(a == null ? aboveFull.Data[j + 3] == 0 : a.Data.AsSpan(i, 4).SequenceEqual(aboveFull.Data.AsSpan(j, 4)), $"Tile {tile} above differs at {area.X + x},{area.Y + y}");
                    }
                }
                // Outside the original footprint the composite already equals the drawing without the object.
                var footprint = MoveFootprint(window.doc, mover);
                for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
                {
                    if (footprint.X <= x && x < footprint.X + footprint.Width && footprint.Y <= y && y < footprint.Y + footprint.Height) continue;
                    int i = (y * source.Width + x) * 4;
                    Check(full.Data.AsSpan(i, 4).SequenceEqual(outside.Data.AsSpan(i, 4)), $"The composite outside the footprint differs at {x},{y}");
                }
                EndDrag(window);
            }
            finally { window.StopRenderingForShutdown(); }
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
