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
        // An interim frame for a drag that has just started renders before the
        // flatten, which waits for it; it is a small part of that work.
        public Task Interim { get; set; } = Task.CompletedTask;
        public Task Build { get; set; } = Task.CompletedTask;
        public BitmapSource? Background { get; set; }
        public BitmapSource? Foreground { get; set; }
        public bool Ready => Background != null;
        public bool Failed { get; set; }
    }
    readonly record struct CompositeSource(Document Document, Raster Raster, long Scene, MaterialAsset[] Materials, MaterialRegion[] Regions);
    readonly record struct MoveInterimSource(Document Document, Guid LayerId, Raster Composite, Int32Rect Footprint, bool Design);

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
    MovePlanes EnsureMovePlanes(Document document, Layer layer, bool speculative)
    {
        if (movePlanes is { } current && MovePlanesCurrent(current, document, layer.Id)) return current;
        DropMovePlanes();
        long scene = movePlaneScene.Update(document, layer.Id);
        var source = document.Snapshot(); var id = layer.Id;
        var planes = new MovePlanes(document, id, scene, document.Materials.ToArray(), document.MaterialRegions.ToArray(), speculative);
        movePlanes = planes; MovePlaneBuilds++;
        planes.Stacks = Task.Run(() => CreateLayerMovePreviewStacks(source, id), planes.Cancel.Token);
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
        canvas.MovePreviewBackground = planes.Background; canvas.MovePreviewLayer = textPreviewBitmap;
        canvas.MovePreviewForeground = planes.Foreground; canvas.MovePreviewForegroundBounds = null;
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

    async Task RenderMoveInterim(MovePlanes planes, MoveInterimSource source, long generation, CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var (below, above) = await planes.Stacks;
            token.ThrowIfCancellationRequested();
            var frame = await Task.Run(() =>
            {
                var (background, foreground) = ComposeMoveInterim(source.Composite, below, above, source.Footprint, source.Design, token);
                return (Background: background.Bitmap(), Foreground: foreground?.Bitmap());
            }, token);
            if (renderShutdown || token.IsCancellationRequested || generation != textPreviewGeneration || !ReferenceEquals(textPreviewPlanes, planes) ||
                !ReferenceEquals(doc, planes.Document) || planes.Ready) return;
            textPreviewInterim = true;
            canvas.MovePreviewBackground = frame.Background; canvas.MovePreviewLayer = textPreviewBitmap;
            canvas.MovePreviewForeground = frame.Foreground;
            canvas.MovePreviewForegroundBounds = frame.Foreground == null ? null : new Rect(source.Footprint.X, source.Footprint.Y, source.Footprint.Width, source.Footprint.Height);
            canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        // The planes still follow; until then the previous frame stays.
        catch (Exception) { }
        finally { if (ReferenceEquals(textPreviewCts, cts)) textPreviewCts = null; cts.Dispose(); }
    }

    // The composite already shows the document with the object at its original
    // place. Outside that footprint it equals everything but the object; inside
    // it, the layers below are rendered again for that rectangle only, and the
    // layers above come back as a small foreground over the moving object.
    // Only content above the object outside its footprint is drawn under the
    // object until the full planes replace this frame.
    internal static (Raster Background, Raster? Foreground) ComposeMoveInterim(Raster composite, Document below, Document above, Int32Rect footprint, bool design, CancellationToken token)
    {
        var patch = RenderMoveRegion(below, footprint, design, token);
        var background = composite.Clone();
        for (int row = 0; row < footprint.Height; row++)
            Buffer.BlockCopy(patch.Data, row * footprint.Width * 4, background.Data, ((footprint.Y + row) * composite.Width + footprint.X) * 4, footprint.Width * 4);
        token.ThrowIfCancellationRequested();
        if (above.Layers.Count == 0) return (background, null);
        var foreground = RenderMoveRegion(above, footprint, design, token);
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
