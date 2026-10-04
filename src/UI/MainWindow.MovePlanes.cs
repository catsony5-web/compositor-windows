using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Compositor.Windows;

// The fast move preview draws three planes: everything below the dragged object
// flattened, the object at its live matrix, and everything above it flattened.
// Flattening a large drawing takes most of a second, so drag start must not wait
// for it:
// - the planes do not depend on the dragged object, so they are kept after a
//   drop and reused by the next drag of the same object until any other layer
//   changes (SceneState ignoring that one object);
// - a selected object in a slow document gets its planes in the background
//   before the drag begins;
// - otherwise the first frames reuse the composite already on screen, with only
//   the object's original footprint re-rendered without it, until the planes
//   are ready. The settled frame after the drop is still the full render.
public sealed partial class MainWindow
{
    internal sealed class MovePlanes(Document document, Guid layerId, long scene, MaterialAsset[] materials, MaterialRegion[] regions, bool speculative)
    {
        public Document Document { get; } = document;
        public Guid LayerId { get; } = layerId;
        public long Scene { get; } = scene;
        public MaterialAsset[] Materials { get; } = materials;
        public MaterialRegion[] Regions { get; } = regions;
        public bool Speculative { get; } = speculative;
        public long Bytes { get; } = (long)document.Width * document.Height * 8;
        public CancellationTokenSource Cancel { get; } = new();
        public Task<(Document Below, Document Above)> Stacks { get; set; } = null!;
        // Paint-order runs of both stacks with their bounds, for interim tiles (built right after the split).
        public Task<(MoveTileIndex Below, MoveTileIndex Above)> Index { get; set; } = null!;
        // An interim frame for a drag that has just started renders before the
        // flatten, which waits for it; it is a small part of that work.
        public Task Interim { get; set; } = Task.CompletedTask;
        // Planes started at pointer down split the stacks at once but flatten only once the
        // gesture is known (a drag has shown its first frame, or the click ended).
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Build { get; set; } = Task.CompletedTask;
        public BitmapSource? Background { get; set; }
        public BitmapSource? Foreground { get; set; }
        public bool Ready => Background != null;
        public bool Failed { get; set; }
    }
    readonly record struct CompositeSource(Document Document, Raster Raster, long Scene, MaterialAsset[] Materials, MaterialRegion[] Regions);
    internal readonly record struct MoveInterimSource(Document Document, Guid LayerId, Raster Composite, Int32Rect Footprint, bool Design);

    // A selected object gets planes ahead of its drag only when the last full
    // render was slow enough to be felt, and only while both planes stay small.
    const double SpeculativeMovePlaneMilliseconds = 60;
    const long SpeculativeMovePlaneBytes = 160L * 1024 * 1024;
    // The interim frame copies the composite once (raster and bitmap).
    const long MoveInterimBytes = 192L * 1024 * 1024;
    const double MoveInterimMaxArea = .25;
    const int MovePlaneTimerRetries = 40;

    MovePlanes? movePlanes;
    readonly SceneState movePlaneScene = new();
    readonly SceneState compositeScene = new();
    CompositeSource? compositeSource;
    MoveInterimSource? moveInterimSource;
    double lastCompositeMilliseconds;
    MovePlanes? textPreviewPlanes;
    bool textPreviewInterim;
    readonly DispatcherTimer movePlaneTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    int movePlaneTimerRetries;
    // Diagnostics and hooks for checks and the drawing benchmark.
    internal int MovePlaneBuilds { get; private set; }
    internal bool moveInterimEnabled = true;
    internal TaskCompletionSource? movePlanesHold;

    static bool SameMaterials(Document document, MaterialAsset[] materials, MaterialRegion[] regions) =>
        document.Materials.SequenceEqual(materials) && document.MaterialRegions.SequenceEqual(regions);

    // Imaging.Render draws a document through the vector renderer when it has
    // drawing folders or materials; a footprint patch must use the same one.
    static bool UsesDesignOutput(Document document) => document.Layers.Any(l => DrawingLayers.IsContainer(l) || l.Kind == LayerKind.Material);

    bool MovePlanesCurrent(MovePlanes planes, Document document, Guid layerId) =>
        !planes.Failed && ReferenceEquals(planes.Document, document) && planes.LayerId == layerId &&
        movePlaneScene.Update(document, layerId) == planes.Scene && SameMaterials(document, planes.Materials, planes.Regions);

    // Planes that are current for this object (ready or still flattening), or a new build.
    MovePlanes EnsureMovePlanes(Document document, Layer layer, bool speculative, bool holdFlatten = false)
    {
        if (movePlanes is { } current && MovePlanesCurrent(current, document, layer.Id)) { if (!holdFlatten) current.Gate.TrySetResult(); return current; }
        DropMovePlanes();
        long scene = movePlaneScene.Update(document, layer.Id);
        var source = document.Snapshot(); var id = layer.Id;
        var planes = new MovePlanes(document, id, scene, document.Materials.ToArray(), document.MaterialRegions.ToArray(), speculative);
        movePlanes = planes; MovePlaneBuilds++;
        planes.Stacks = Task.Run(() => CreateLayerMovePreviewStacks(source, id, owned: true), planes.Cancel.Token);
        var stacks = planes.Stacks;
        planes.Index = Task.Run(async () => { var (below, above) = await stacks; return (new MoveTileIndex(below), new MoveTileIndex(above)); }, planes.Cancel.Token);
        if (!holdFlatten) planes.Gate.TrySetResult();
        planes.Build = BuildMovePlanes(planes);
        return planes;
    }

    internal MovePlanes? PrepareMovePlanes() =>
        HasDocument && doc.Active is { } layer && CanPreviewLayerMove(doc, layer, MovableSelectedLayers().Count()) ? EnsureMovePlanes(doc, layer, true) : null;

    async Task BuildMovePlanes(MovePlanes planes)
    {
        var token = planes.Cancel.Token;
        try
        {
            // Resume after the caller's turn, which may attach an interim frame.
            await Task.Yield();
            var (below, above) = await planes.Stacks;
            await planes.Gate.Task;
            await planes.Interim;
            if (movePlanesHold is { } hold) await hold.Task;
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() =>
            {
                var background = Imaging.Render(below, token).Bitmap();
                token.ThrowIfCancellationRequested();
                var foreground = above.Layers.Count == 0 ? null : Imaging.Render(above, token).Bitmap();
                token.ThrowIfCancellationRequested();
                return (Background: background, Foreground: foreground);
            }, token);
            if (renderShutdown || token.IsCancellationRequested || !ReferenceEquals(movePlanes, planes)) return;
            planes.Background = result.Background; planes.Foreground = result.Foreground;
            // A drag waiting on these planes switches from its interim frame now.
            if (ReferenceEquals(textPreviewPlanes, planes) && ReferenceEquals(doc, planes.Document)) ShowMovePlanes(planes);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            planes.Failed = true;
            if (ReferenceEquals(movePlanes, planes)) DropMovePlanes();
            if (!renderShutdown && ReferenceEquals(textPreviewPlanes, planes))
            {
                ClearTextMovePreview(); textPreviewFailed = true;
                status.Text = "이동 미리보기 실패: " + e.Message;
                // Resume the exact renderer even if the pointer has stopped;
                // do not repeatedly retry a failing cache during this gesture.
                QueueRender(dragging);
            }
        }
    }

    void ShowMovePlanes(MovePlanes planes)
    {
        textPreviewInterim = false;
        if (moveInterim is { } interim) { interim.Cancel.Cancel(); moveInterim = null; }
        canvas.MovePreviewBackground = planes.Background; canvas.MovePreviewLayer = textPreviewBitmap;
        canvas.MovePreviewForeground = planes.Foreground; canvas.MovePreviewForegroundTiles = null; canvas.MovePreviewBackgroundTiles = null;
        // The object may have run ahead of the interim frame; show it where it is.
        if (doc.Layers.Find(l => l.Id == planes.LayerId) is { } layer) canvas.MovePreviewMatrix = layer.Matrix;
        canvas.InvalidateVisual();
    }

    void DropMovePlanes()
    {
        movePlaneTimer.Stop();
        if (movePlanes is { } planes) { planes.Cancel.Cancel(); movePlanes = null; }
        movePlaneScene.Reset();
    }

    // Called after every refresh: release planes of another document, object or
    // tool, or planes made stale by an edit; then plan planes for the selection.
    void MaintainMovePlanes()
    {
        if (dragging) return;
        // No drag is under way, so a stand-in captured earlier is stale; a composite
        // record of another document (or of a replaced composite) is released too.
        moveInterimSource = null;
        if (compositeSource is { } shown && (!ReferenceEquals(shown.Document, doc) || !ReferenceEquals(shown.Raster, composite)))
        { compositeSource = null; compositeScene.Reset(); }
        if (movePlanes is { } planes && (!HasDocument || tool != Tool.Move || doc.Active?.Id != planes.LayerId || !MovePlanesCurrent(planes, doc, planes.LayerId)))
            DropMovePlanes();
        ScheduleMovePlanes();
    }

    void ScheduleMovePlanes()
    {
        movePlaneTimer.Stop(); movePlaneTimerRetries = 0;
        if (headlessTesting || renderShutdown || !WantsMovePlanes()) return;
        movePlaneTimer.Tick -= OnMovePlaneTick; movePlaneTimer.Tick += OnMovePlaneTick; movePlaneTimer.Start();
    }

    void OnMovePlaneTick(object? sender, EventArgs e)
    {
        movePlaneTimer.Stop();
        if (!dragging) movePlanes?.Gate.TrySetResult();
        if (renderShutdown || headlessTesting || !WantsMovePlanes()) return;
        // Let the composite and the crisp drawing view finish first; all of
        // them compete for the same cores.
        bool busy = rendering || pendingFullRender || pendingGestureRender ||
            canvas.DesignMode && DesignRenderer.HasRetainedContent(doc) && !canvas.IsDesignPreviewReady && canvas.DesignPreviewError == null;
        if (busy) { if (++movePlaneTimerRetries <= MovePlaneTimerRetries) movePlaneTimer.Start(); return; }
        EnsureMovePlanes(doc, doc.Active!, true);
    }

    internal bool WantsMovePlanes() =>
        HasDocument && tool == Tool.Move && !dragging && !cmykProof && doc.Active is { } layer &&
        lastCompositeMilliseconds >= SpeculativeMovePlaneMilliseconds && (long)doc.Width * doc.Height * 8 <= SpeculativeMovePlaneBytes &&
        !(movePlanes is { } planes && MovePlanesCurrent(planes, doc, layer.Id)) &&
        CanPreviewLayerMove(doc, layer, MovableSelectedLayers().Count());

    // The renderer publishes which document state its composite shows.
    internal void RememberComposite(Document document, Raster raster, long scene) =>
        compositeSource = new CompositeSource(document, raster, scene, document.Materials.ToArray(), document.MaterialRegions.ToArray());
    internal void RememberCurrentComposite(Raster raster) { composite = raster; RememberComposite(doc, raster, compositeScene.Update(doc)); }

    // At pointer down, while nothing has moved yet: when the composite shows
    // exactly this document, the first drag frames can be built from it.
    internal void CaptureMoveInterimSource()
    {
        CaptureMoveSettle();
        moveInterimSource = null;
        if (!moveInterimEnabled || !HasDocument || tool != Tool.Move || cmykProof || doc.Active is not { } layer || composite is not { } raster ||
            compositeSource is not { } source || !ReferenceEquals(source.Raster, raster) || !ReferenceEquals(source.Document, doc) ||
            raster.Width != doc.Width || raster.Height != doc.Height || (long)doc.Width * doc.Height * 8 > MoveInterimBytes ||
            layer.Warp != null || !CanPreviewLayerMove(doc, layer, MovableSelectedLayers().Count()) ||
            // Adjustments may read neighboring pixels; a footprint render would not match.
            doc.Layers.Any(l => l.Kind == LayerKind.Adjustment) ||
            compositeScene.Update(doc) != source.Scene || !SameMaterials(doc, source.Materials, source.Regions)) return;
        var footprint = MoveFootprint(doc, layer);
        if (footprint.Width <= 0 || footprint.Height <= 0 || (double)footprint.Width * footprint.Height > (double)doc.Width * doc.Height * MoveInterimMaxArea) return;
        moveInterimSource = new MoveInterimSource(doc, layer.Id, raster, footprint, UsesDesignOutput(doc));
        // In a slow drawing, split the stacks now: the drag threshold is usually passed a few
        // tens of milliseconds later, and the first frame then only waits for its tiles.
        if (lastCompositeMilliseconds >= SpeculativeMovePlaneMilliseconds) EnsureMovePlanes(doc, layer, true, holdFlatten: true);
    }

    // Pixels the object can touch at its current matrix, with an antialiasing margin.
    internal static Int32Rect MoveFootprint(Document document, Layer layer)
    {
        var bounds = new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height); bounds.Transform(layer.Matrix);
        if (bounds.IsEmpty || !double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height)) return default;
        int left = (int)Math.Clamp(Math.Floor(bounds.Left) - 2, 0, document.Width), top = (int)Math.Clamp(Math.Floor(bounds.Top) - 2, 0, document.Height);
        int right = (int)Math.Clamp(Math.Ceiling(bounds.Right) + 2, 0, document.Width), bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom) + 2, 0, document.Height);
        return right <= left || bottom <= top ? default : new Int32Rect(left, top, right - left, bottom - top);
    }

    // The interim frame, built from the composite on screen while the planes flatten.
    // The composite shows the object at its original place and every other layer exactly,
    // so a tile of it can be replaced by the layers below the object rendered for that tile
    // only, with the layers above drawn back as a tile over the moving object. Tiles are
    // rendered where the object was and wherever it goes; the object is shown only at
    // positions whose tiles are ready, so it is never drawn over a layer above it, never
    // twice, and its original place never shows a ghost.
    internal sealed class MoveInterim(MovePlanes planes, MoveInterimSource source, Layer mover, CancellationTokenSource cancel)
    {
        public const int Tile = 128;
        public MovePlanes Planes { get; } = planes;
        public MoveInterimSource Source { get; } = source;
        public Layer Mover { get; } = mover;
        public CancellationTokenSource Cancel { get; } = cancel;
        public MoveTileIndex? Below, Above;
        public Document? BelowStack, AboveStack;
        public readonly HashSet<(int X, int Y)> Ready = [], Queued = [];
        public readonly LinkedList<(int X, int Y)> Queue = new();
        // The composite as a frozen bitmap, made off the UI thread; tiles of the layers below
        // the object are drawn over it while they are opaque (they then replace it exactly).
        public BitmapSource? Image;
        public readonly List<(BitmapSource Image, Rect Bounds)> Under = [];
        public readonly List<Raster> UnderPixels = [];
        // A tile with transparency cannot be drawn over the composite; then a copy of the
        // composite is written instead.
        public WriteableBitmap? Background;
        public readonly List<(BitmapSource Image, Rect Bounds)> Foreground = [];
        public bool Working, Shown;
        public int TilesRendered;
        // Diagnostics: milliseconds from drag start to each preparation step and to the first frame.
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public double StacksAt, IndexAt, BackgroundAt, FirstFrameAt;
        public readonly TaskCompletionSource FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    MoveInterim? moveInterim;

    // Tiles covering a document rectangle (clamped to the page).
    static IEnumerable<(int X, int Y)> MoveTiles(Document document, Int32Rect area)
    {
        int left = Math.Max(0, area.X), top = Math.Max(0, area.Y);
        int right = Math.Min(document.Width, area.X + area.Width), bottom = Math.Min(document.Height, area.Y + area.Height);
        if (right <= left || bottom <= top) yield break;
        for (int y = top / MoveInterim.Tile; y <= (bottom - 1) / MoveInterim.Tile; y++)
            for (int x = left / MoveInterim.Tile; x <= (right - 1) / MoveInterim.Tile; x++) yield return (x, y);
    }
    static Int32Rect TileArea(Document document, (int X, int Y) tile)
    {
        int x = tile.X * MoveInterim.Tile, y = tile.Y * MoveInterim.Tile;
        return new Int32Rect(x, y, Math.Min(MoveInterim.Tile, document.Width - x), Math.Min(MoveInterim.Tile, document.Height - y));
    }

    void StartMoveInterim(MovePlanes planes, MoveInterimSource source, Layer layer)
    {
        var cts = textPreviewCts = new CancellationTokenSource();
        var interim = moveInterim = new MoveInterim(planes, source, layer, cts);
        // Ghost removal first, then where the object is now, then around it.
        RequestMoveTiles(interim, MoveFootprint(doc, layer), true);
        RequestMoveTiles(interim, source.Footprint, true);
        planes.Interim = interim.FirstFrame.Task;
        _ = RunMoveInterim(interim);
    }

    void RequestMoveTiles(MoveInterim interim, Int32Rect footprint, bool urgent)
    {
        var page = interim.Planes.Document;
        foreach (var tile in MoveTiles(page, footprint))
        {
            if (interim.Ready.Contains(tile)) continue;
            if (!interim.Queued.Add(tile)) { if (!urgent) continue; interim.Queue.Remove(tile); }
            if (urgent) interim.Queue.AddFirst(tile); else interim.Queue.AddLast(tile);
        }
        if (urgent)
        {
            // Prefetch a ring around the object so ordinary motion does not wait.
            var around = new Int32Rect(footprint.X - MoveInterim.Tile / 2, footprint.Y - MoveInterim.Tile / 2, footprint.Width + MoveInterim.Tile, footprint.Height + MoveInterim.Tile);
            RequestMoveTiles(interim, around, false);
        }
        if (!interim.Working && interim.Image != null) _ = RunMoveInterim(interim);
    }

    static bool MoveTilesReady(MoveInterim interim, Int32Rect footprint) => MoveTiles(interim.Planes.Document, footprint).All(interim.Ready.Contains);

    // Shows the object at its current matrix when the frame covers it there.
    void AdvanceMoveInterim(MoveInterim interim)
    {
        if (!ReferenceEquals(moveInterim, interim) || interim.Planes.Ready || !ReferenceEquals(doc, interim.Planes.Document) || renderShutdown) return;
        if (!MoveTilesReady(interim, interim.Source.Footprint) || !MoveTilesReady(interim, MoveFootprint(doc, interim.Mover))) return;
        if (!interim.Shown)
        {
            interim.Shown = true; textPreviewInterim = true; interim.FirstFrameAt = interim.Clock.Elapsed.TotalMilliseconds;
            canvas.MovePreviewBackground = (BitmapSource?)interim.Background ?? interim.Image; canvas.MovePreviewBackgroundTiles = interim.Under;
            canvas.MovePreviewLayer = textPreviewBitmap;
            canvas.MovePreviewForeground = null; canvas.MovePreviewForegroundTiles = interim.Foreground;
            interim.FirstFrame.TrySetResult();
        }
        canvas.MovePreviewMatrix = interim.Mover.Matrix;
        canvas.InvalidateVisual();
    }

    // Each pointer event while the interim frame is in use: move the object only where
    // the frame is ready, otherwise ask for those tiles first.
    void FollowMoveInterim(MoveInterim interim)
    {
        if (interim.Planes.Ready) return;
        var footprint = MoveFootprint(doc, interim.Mover);
        if (interim.Shown && MoveTilesReady(interim, footprint)) canvas.MovePreviewMatrix = interim.Mover.Matrix;
        else RequestMoveTiles(interim, footprint, true);
    }

    async Task RunMoveInterim(MoveInterim interim)
    {
        if (interim.Working) return;
        interim.Working = true;
        var token = interim.Cancel.Token;
        try
        {
            if (interim.Image == null)
            {
                // The composite bitmap is made on a worker while the stacks are split.
                var composite = interim.Source.Composite;
                var image = Task.Run(() => composite.Bitmap(), token);
                var (below, above) = await interim.Planes.Stacks;
                token.ThrowIfCancellationRequested(); interim.StacksAt = interim.Clock.Elapsed.TotalMilliseconds;
                (interim.Below, interim.Above) = await interim.Planes.Index;
                token.ThrowIfCancellationRequested(); interim.IndexAt = interim.Clock.Elapsed.TotalMilliseconds;
                var bitmap = await image;
                token.ThrowIfCancellationRequested(); interim.BackgroundAt = interim.Clock.Elapsed.TotalMilliseconds;
                interim.BelowStack = below; interim.AboveStack = above; interim.Image = bitmap;
            }
            while (interim.Queue.Count > 0 && !token.IsCancellationRequested && ReferenceEquals(moveInterim, interim) && !interim.Planes.Ready)
            {
                // The tiles the next frame needs (the original place until it is shown, and where the
                // object is now) go first and alone; prefetched ones follow, spread over the cores.
                var page0 = interim.Planes.Document;
                var needed = MoveTiles(page0, MoveFootprint(page0, interim.Mover));
                if (!interim.Shown) needed = needed.Concat(MoveTiles(page0, interim.Source.Footprint));
                var batch = needed.Distinct().Where(interim.Queued.Contains).ToList();
                foreach (var tile in batch) interim.Queue.Remove(tile);
                if (batch.Count == 0)
                    while (interim.Queue.Count > 0 && batch.Count < Math.Max(2, Environment.ProcessorCount / 2))
                    { batch.Add(interim.Queue.First!.Value); interim.Queue.RemoveFirst(); }
                var page = interim.Planes.Document; bool design = interim.Source.Design;
                var below = interim.Below; var above = interim.Above; var belowStack = interim.BelowStack!; var aboveStack = interim.AboveStack!;
                var rendered = await Task.Run(() =>
                {
                    var results = new (Int32Rect Area, Raster Below, Raster? Above, BitmapSource? Opaque)[batch.Count];
                    Parallel.For(0, batch.Count, new ParallelOptions { CancellationToken = token }, i =>
                    {
                        var area = TileArea(page, batch[i]);
                        var (b, a) = RenderMoveTile(below?.For(area) ?? belowStack, above?.For(area) ?? aboveStack, area, design, token);
                        bool opaque = true;
                        for (int k = 3; k < b.Data.Length && opaque; k += 4) opaque = b.Data[k] == 255;
                        results[i] = (area, b, a, opaque ? b.Bitmap() : null);
                    });
                    return results;
                }, token);
                if (token.IsCancellationRequested || !ReferenceEquals(moveInterim, interim)) return;
                for (int i = 0; i < batch.Count; i++)
                {
                    var (area, b, a, opaque) = rendered[i];
                    if (interim.Background == null && opaque != null) { interim.Under.Add((opaque, new Rect(area.X, area.Y, area.Width, area.Height))); interim.UnderPixels.Add(b); }
                    else
                    {
                        if (interim.Background == null) WriteMoveInterimBackground(interim);
                        interim.Background!.WritePixels(area, b.Data, area.Width * 4, 0);
                    }
                    if (a != null) interim.Foreground.Add((a.Bitmap(), new Rect(area.X, area.Y, area.Width, area.Height)));
                    interim.Ready.Add(batch[i]); interim.Queued.Remove(batch[i]); interim.TilesRendered++;
                }
                AdvanceMoveInterim(interim);
            }
        }
        catch (OperationCanceledException) { }
        // The planes still follow; until then the previous frame stays.
        catch (Exception) { }
        finally { interim.Working = false; interim.FirstFrame.TrySetResult(); }
    }

    // A stack split into paint-order runs (a layer with the clipped layers on it, or a kept
    // folder with its children) and the document rectangle each run can touch.
    internal sealed class MoveTileIndex
    {
        readonly Document stack;
        readonly List<(Layer[] Members, Rect Bounds)> runs = [];
        public MoveTileIndex(Document stack)
        {
            this.stack = stack;
            var everywhere = new Rect(-1e9, -1e9, 2e9, 2e9);
            foreach (var layer in stack.Layers.Where(l => l.ParentId == null))
            {
                if (layer.Clipped && runs.Count > 0) { var last = runs[^1]; runs[^1] = ([.. last.Members, layer], last.Bounds); continue; }
                if (layer.Kind == LayerKind.Group)
                {
                    var ids = new HashSet<Guid> { layer.Id }; var members = new List<Layer> { layer };
                    foreach (var item in stack.Layers) if (item.ParentId is { } parent && ids.Contains(parent)) { ids.Add(item.Id); members.Add(item); }
                    runs.Add(([.. members], everywhere)); continue;
                }
                if (layer.Warp != null) { runs.Add(([layer], everywhere)); continue; }
                var bounds = new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height); bounds.Transform(layer.Matrix); bounds.Inflate(2, 2);
                runs.Add(([layer], bounds));
            }
        }
        public bool IsEmpty => runs.Count == 0;
        // The stack without the runs that cannot reach this rectangle: the same pixels inside it.
        public Document For(Int32Rect area)
        {
            var region = new Rect(area.X, area.Y, area.Width, area.Height);
            return new Document
            {
                Width = stack.Width, Height = stack.Height, Materials = stack.Materials, MaterialRegions = stack.MaterialRegions,
                Layers = runs.Where(run => run.Bounds.IntersectsWith(region)).SelectMany(run => run.Members).ToList()
            };
        }
    }

    // Switch from tiles drawn over the composite to a written copy of it.
    void WriteMoveInterimBackground(MoveInterim interim)
    {
        var composite = interim.Source.Composite;
        var background = new WriteableBitmap(composite.Width, composite.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        background.WritePixels(new Int32Rect(0, 0, composite.Width, composite.Height), composite.Data, composite.Width * 4, 0);
        for (int i = 0; i < interim.Under.Count; i++)
        {
            var bounds = interim.Under[i].Bounds;
            background.WritePixels(new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height), interim.UnderPixels[i].Data, (int)bounds.Width * 4, 0);
        }
        interim.Under.Clear(); interim.UnderPixels.Clear(); interim.Background = background;
        if (interim.Shown) canvas.MovePreviewBackground = background;
    }

    // One tile of the stacks below and above the object (or of their parts that reach it).
    internal static (Raster Below, Raster? Above) RenderMoveTile(Document below, Document above, Int32Rect area, bool design, CancellationToken token)
    {
        var background = RenderMoveRegion(below, area, design, token);
        token.ThrowIfCancellationRequested();
        if (above.Layers.Count == 0) return (background, null);
        var foreground = RenderMoveRegion(above, area, design, token);
        bool ink = false;
        for (int i = 3; i < foreground.Data.Length && !ink; i += 4) ink = foreground.Data[i] != 0;
        return (background, ink ? foreground : null);
    }

    // One rectangle of a flattened stack at document resolution, drawn the way
    // Imaging.Render draws the whole document.
    internal static Raster RenderMoveRegion(Document stack, Int32Rect area, bool design, CancellationToken token)
    {
        if (stack.Layers.Count == 0) return new Raster(area.Width, area.Height);
        if (design) return DesignRenderer.Render(stack, new Rect(area.X, area.Y, area.Width, area.Height), area.Width, area.Height, token);
        var shifted = new Document { Width = area.Width, Height = area.Height, Materials = stack.Materials.ToList(), MaterialRegions = stack.MaterialRegions.ToList() };
        shifted.Layers = stack.Layers.Select(layer =>
        {
            var copy = layer.Snapshot();
            if (copy.ParentId == null) { copy.X -= area.X; copy.Y -= area.Y; }
            return copy;
        }).ToList();
        return Imaging.Render(shifted, token);
    }
}
