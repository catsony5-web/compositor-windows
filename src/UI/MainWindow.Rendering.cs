using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    long renderGeneration;
    CancellationTokenSource? renderCts;
    readonly DispatcherTimer gestureRenderTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    bool rendering, pendingGestureRender, pendingFullRender;
    CancellationTokenSource? textPreviewCts;
    Guid? textPreviewLayerId;
    Document? textPreviewDocument;
    BitmapSource? textPreviewBitmap;
    long textPreviewGeneration;
    bool textPreviewFailed;
    bool renderShutdown;

    void StopRenderingForShutdown()
    {
        renderShutdown = true;
        canvas.CancelDesignPreview();
        pendingFullRender = false; pendingGestureRender = false;
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        ++renderGeneration;
        ClearTextMovePreview(); DropMovePlanes();
        renderCts?.Cancel(); jobCts?.Cancel();
    }

    // Full renders requested (diagnostics for checks; counted in headless tests too).
    internal int FullRenderRequests { get; private set; }
    void QueueRender(bool gesture = false)
    {
        if (!gesture) FullRenderRequests++;
        if (headlessTesting || renderShutdown || !HasDocument) return;
        if (gesture && TryPreviewTextMove()) return;
        if (gesture)
        {
            pendingGestureRender = true;
            if (!rendering && !gestureRenderTimer.IsEnabled)
            {
                gestureRenderTimer.Tick += OnGestureRenderTick;
                gestureRenderTimer.Start();
            }
            return;
        }
        // On mouse-up the valid preview stays visible until its full-quality
        // replacement is ready. A canceled drag or document switch clears it.
        if (!ReferenceEquals(textPreviewDocument, doc) || canvas.MovePreviewBackground == null || canvas.MovePreviewLayer == null)
            ClearTextMovePreview();
        pendingFullRender = true; pendingGestureRender = false;
        ++renderGeneration; renderCts?.Cancel();
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        if (!rendering) StartQueuedRender();
    }
    void OnGestureRenderTick(object? sender, EventArgs e)
    {
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        if (!renderShutdown && !rendering) StartQueuedRender();
    }
    async void StartQueuedRender()
    {
        if (renderShutdown || !HasDocument || rendering || (!pendingFullRender && !pendingGestureRender)) return;
        bool gesture = !pendingFullRender;
        pendingFullRender = false; pendingGestureRender = false;
        rendering = true;
        long generation = renderGeneration;
        var document = doc; int tab = activeTab; bool proof = cmykProof; string? profile = proofProfile;
        canvas.DesignProof = proof; canvas.DesignProofProfile = profile;
        var cts = renderCts = new CancellationTokenSource();
        try
        {
            // Capture only when this frame is actually due. A brush owns a
            // mutable buffer, so detach just the active layer before workers read it.
            var snapshot = document.Snapshot();
            if (gesture && (stroke != null || IsRetouch(tool)) && snapshot.Active is { } active)
            { active.Pixels = active.Pixels.Clone(); if (active.Mask != null) active.Mask = (byte[])active.Mask.Clone(); }
            // Record which state a full composite shows; a drag can start from it.
            long scene = gesture ? 0 : compositeScene.Update(document);
            if (gesture) compositeSource = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() =>
            {
                var rgb = Imaging.Render(snapshot, cts.Token); cts.Token.ThrowIfCancellationRequested();
                var display = proof ? CmykExport.Preview(rgb, profile) : rgb;
                cts.Token.ThrowIfCancellationRequested(); return (Rgb: rgb, Display: display);
            }, cts.Token);
            var raster = result.Rgb;
            if (renderShutdown || cts.IsCancellationRequested || generation != renderGeneration || !ReferenceEquals(document, doc) || tab != activeTab) return;
            composite = raster; canvas.Composite = result.Display.Bitmap();
            if (!gesture)
            {
                RememberComposite(document, raster, scene); lastCompositeMilliseconds = watch.Elapsed.TotalMilliseconds;
                // Planes kept after a drop stay for the next drag only within the speculative budget.
                if (movePlanes is { Bytes: > SpeculativeMovePlaneBytes }) DropMovePlanes();
            }
            if (!gesture) { ClearTextMovePreview(); histogram.Update(result.Display); histogramInfo.Text = $"{doc.Width:N0} × {doc.Height:N0} px · {doc.Dpi:0.#} DPI · {(proof ? "CMYK 미리보기" : "RGB / 8 bit")}"; }
            canvas.InvalidateVisual();
            if (status.Text.StartsWith("CMYK 인쇄색을 준비합니다", StringComparison.Ordinal))
                status.Text = proof ? "CMYK 인쇄색 미리보기 · RGB 원본 유지 · 상단 RGB 버튼으로 복귀" : "RGB 편집 화면";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!renderShutdown && generation == renderGeneration)
            {
                if (!gesture) ClearTextMovePreview();
                if (proof) { cmykProof = false; UpdateProofButtons(); pendingFullRender = true; }
                status.Text = (proof ? "CMYK 미리보기 실패 · RGB 화면으로 복귀: " : "화면 렌더링 실패: ") + e.Message;
            }
        }
        finally
        {
            cts.Dispose(); if (ReferenceEquals(renderCts, cts)) renderCts = null;
            rendering = false;
            if (!renderShutdown)
            {
                if (pendingFullRender) StartQueuedRender();
                else if (pendingGestureRender && !gestureRenderTimer.IsEnabled)
                { gestureRenderTimer.Tick += OnGestureRenderTick; gestureRenderTimer.Start(); }
            }
        }
    }
    bool TryPreviewTextMove()
    {
        // The stack around the object cannot change during its drag; later pointer
        // events re-check only the object itself (a distortion handle adds a warp).
        bool previewing = doc.Active is { } shown && ReferenceEquals(textPreviewDocument, doc) && textPreviewLayerId == shown.Id;
        if (renderShutdown || cmykProof || !dragging || !moveStarted || tool != Tool.Move || doc.Active is not { } layer ||
            !(previewing ? CanPreviewMover(doc, layer) : CanPreviewLayerMove(doc, layer, MovableSelectedLayers().Count())))
        {
            // A distortion, mode change or canceled gesture must not leave a
            // stale fast preview covering the accurate composite underneath.
            if (textPreviewDocument != null) ClearTextMovePreview();
            return false;
        }
        if (textPreviewFailed) return false;
        if (!ReferenceEquals(textPreviewDocument, doc) || textPreviewLayerId != layer.Id)
        {
            ClearTextMovePreview();
            // Prepare fixed layers on either side of the moving object once.
            // Pointer events only update its matrix, even while caches load.
            ++renderGeneration; renderCts?.Cancel(); pendingGestureRender = false; pendingFullRender = false;
            gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick; movePlaneTimer.Stop();
            textPreviewDocument = doc; textPreviewLayerId = layer.Id;
            canvas.MovePreviewOpacity = layer.Opacity;
            // Planes made while the object was selected, or kept from its last
            // drag, show at once. Otherwise the composite on screen stands in.
            var planes = textPreviewPlanes = EnsureMovePlanes(doc, layer, false);
            textPreviewBitmap = layer.Pixels.Bitmap();
            if (planes.Ready) ShowMovePlanes(planes);
            else if (moveInterimSource is { } source && ReferenceEquals(source.Document, doc) && source.LayerId == layer.Id)
            {
                var cts = textPreviewCts = new CancellationTokenSource();
                planes.Interim = RenderMoveInterim(planes, source, textPreviewGeneration, cts);
            }
            moveInterimSource = null;
        }
        canvas.MovePreviewMatrix = layer.Matrix;
        canvas.InvalidateVisual();
        return true;
    }
    internal static bool CanPreviewTextMove(Document document, Layer layer, int movingCount) =>
        movingCount == 1 && layer.Kind == LayerKind.Text && layer.Visible &&
        layer.ParentId == null && !layer.Clipped && layer.Mask == null && layer.Warp == null &&
        layer.Blend == BlendMode.Normal && document.Layers.LastOrDefault(l => l.ParentId == null) == layer;

    internal static bool CanPreviewLayerMove(Document document, Layer layer, int movingCount)
    {
        if (movingCount != 1 || !CanPreviewMover(document, layer) || !document.Layers.Contains(layer)) return false;
        var lookup = document.Layers.ToDictionary(item => item.Id);
        var parent = layer.ParentId; int depth = 0;
        while (parent is { } id)
        {
            if (++depth > 16 || !lookup.TryGetValue(id, out var group) || group.Kind != LayerKind.Group || group.Locked) return false;
            parent = group.ParentId;
        }
        return MovePreviewPlan(document, layer.Id) != null;
    }

    // What the object itself must satisfy to move as one bitmap over fixed planes.
    static bool CanPreviewMover(Document document, Layer layer)
    {
        if (!layer.Visible || layer.Opacity <= 0 || layer.Locked ||
            layer.Kind is not (LayerKind.Raster or LayerKind.Text or LayerKind.Shape or LayerKind.Vector) ||
            layer.Clipped || layer.Mask != null || layer.Warp != null || layer.Blend != BlendMode.Normal) return false;
        // Bound additional raster/WIC copies without limiting document editing.
        // Large or interdependent stacks retain the full compositor path.
        long estimatedBytes = (long)document.Width * document.Height * 16 + layer.Pixels.Data.LongLength;
        return estimatedBytes <= 512L * 1024 * 1024;
    }

    static bool IsMovePreviewContainer(Document document, Layer group) =>
        group.Visible && group.Opacity == 1 && group.Mask == null && group.Warp == null && group.Matrix.IsIdentity &&
        group.Pixels.Width == document.Width && group.Pixels.Height == document.Height;

    // The preview draws three planes: everything below the mover, the mover, then everything
    // above with normal source-over. This walks the layers in paint order and decides, for each
    // one, which plane it belongs to and under which parent, or returns null when three planes
    // cannot reproduce the compositor.
    // - Normal source-over is associative, so a layer that reads its backdrop (Multiply hatch,
    //   clipping, adjustment) is only a problem ABOVE the mover, where its backdrop would include
    //   the moving pixels. Below the mover it is drawn in paint order by the same renderer — the
    //   common CAD case: hatch materials multiply under the linework the user drags.
    // - Identity folders whose children all paint with normal source-over are flattened, as the
    //   compositor itself does. A folder the compositor renders as an isolated surface (a Multiply,
    //   clipped or adjustment child, its own blend, a clipped layer on top of it) keeps that surface
    //   when it is below the mover. A folder holding the mover is flattened only while nothing has
    //   been painted before it (an isolated surface over transparency equals its children);
    //   otherwise its part below the mover stays one isolated folder and its part above is plain
    //   source-over.
    internal static List<(Layer Layer, Guid? Parent, bool Above)>? MovePreviewPlan(Document document, Guid movingId)
    {
        var children = document.Layers.ToLookup(item => item.ParentId);
        var lookup = new Dictionary<Guid, Layer>(document.Layers.Count);
        foreach (var item in document.Layers) lookup[item.Id] = item;
        if (!lookup.TryGetValue(movingId, out var mover)) return null;
        var holders = new HashSet<Guid>();
        for (var up = mover.ParentId; up is { } id; up = lookup[id].ParentId)
            if (holders.Count >= 16 || !lookup.ContainsKey(id) || !holders.Add(id)) return null;
        var direct = new Dictionary<Guid, bool>();
        bool Direct(Layer group, int depth)
        {
            if (direct.TryGetValue(group.Id, out var known)) return known;
            bool result = depth <= 16 && group.Blend == BlendMode.Normal && !group.Clipped && IsMovePreviewContainer(document, group) &&
                children[group.Id].All(child => !child.Clipped && child.Blend == BlendMode.Normal && child.Kind != LayerKind.Adjustment &&
                    (child.Kind != LayerKind.Group || Direct(child, depth + 1)));
            return direct[group.Id] = result;
        }
        var plan = new List<(Layer Layer, Guid? Parent, bool Above)>(document.Layers.Count);
        bool painted = false, above = false, failed = false;
        void Keep(Layer item, Guid? output, int depth)
        {
            if (depth > 16) { failed = true; return; }
            plan.Add((item, output, false));
            if (item.Kind == LayerKind.Group) foreach (var child in children[item.Id]) Keep(child, item.Id, depth + 1);
        }
        void Visit(Guid? parentId, Guid? output, int depth)
        {
            if (depth > 16) { failed = true; return; }
            var items = children[parentId].ToArray();
            for (int i = 0; i < items.Length && !failed; i++)
            {
                var item = items[i];
                if (item.Id == movingId) { plan.Add((item, output, false)); above = true; painted = true; continue; }
                if (above && (item.Clipped || item.Kind == LayerKind.Adjustment || item.Blend != BlendMode.Normal)) { failed = true; return; }
                if (item.Kind != LayerKind.Group)
                {
                    plan.Add((item, above ? null : output, above));
                    painted |= item.Visible && item.Opacity > 0;
                    continue;
                }
                // A clipped layer right after a folder clips to the whole folder surface.
                bool clippedOnTop = i + 1 < items.Length && items[i + 1].Clipped;
                bool holds = holders.Contains(item.Id);
                if (Direct(item, depth) && !clippedOnTop) { Visit(item.Id, above ? null : output, depth + 1); continue; }
                if (above || holds && (clippedOnTop || !IsMovePreviewContainer(document, item) || item.Clipped)) { failed = true; return; }
                if (!holds) { Keep(item, output, depth); painted |= item.Visible && item.Opacity > 0; continue; }
                if (!painted) { Visit(item.Id, output, depth + 1); continue; }
                if (item.Blend != BlendMode.Normal) { failed = true; return; }
                plan.Add((item, output, false)); Visit(item.Id, item.Id, depth + 1);
            }
        }
        Visit(null, null, 0);
        return failed || !above ? null : plan;
    }

    internal static (Document Below, Document Above) CreateLayerMovePreviewStacks(Document document, Guid movingId)
    {
        // Work on a snapshot: its layers are copies whose parents may be rewritten.
        var source = document.Snapshot();
        if (!source.Layers.Any(layer => layer.Id == movingId)) throw new ArgumentException("이동할 레이어를 찾을 수 없습니다.", nameof(movingId));
        var plan = MovePreviewPlan(source, movingId) ?? throw new InvalidOperationException("그룹 변형에는 전체 합성 미리보기가 필요합니다.");
        var below = new List<Layer>(); var above = new List<Layer>();
        foreach (var (item, parent, upper) in plan)
        {
            if (item.Id == movingId) continue;
            item.ParentId = parent; (upper ? above : below).Add(item);
        }
        var aboveDocument = source.Snapshot(); aboveDocument.Layers = above;
        source.Layers = below;
        source.ActiveId = Guid.Empty; aboveDocument.ActiveId = Guid.Empty;
        return (source, aboveDocument);
    }

    void ClearTextMovePreview()
    {
        ++textPreviewGeneration; textPreviewCts?.Cancel(); textPreviewCts = null;
        textPreviewDocument = null; textPreviewLayerId = null; textPreviewBitmap = null;
        textPreviewPlanes = null; textPreviewInterim = false; textPreviewFailed = false;
        // The planes themselves stay cached for the next drag of the same object.
        canvas.MovePreviewBackground = null; canvas.MovePreviewLayer = null; canvas.MovePreviewForeground = null;
        canvas.MovePreviewForegroundBounds = null;
    }
}
