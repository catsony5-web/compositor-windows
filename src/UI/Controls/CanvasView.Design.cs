using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class CanvasView
{
    public bool DesignMode { get; set; }
    public bool DesignProof { get; set; }
    public string? DesignProofProfile { get; set; }
    public bool IsDesignPreviewReady => designCache.Count > 0 && designCache[0].Request == requestedDesign;
    public string? DesignPreviewError { get; private set; }
    // A wheel burst or pan drag changes the viewport on every event. Until input
    // pauses, the last crisp render is moved and scaled with the view, and the new
    // viewport renders once afterwards. Content edits keep the shorter delay.
    static readonly TimeSpan ViewportSettleDelay = TimeSpan.FromMilliseconds(120);
    static readonly TimeSpan ContentSettleDelay = TimeSpan.FromMilliseconds(75);
    // Diagnostics for checks: viewports queued, renders begun and published,
    // renders stopped by a newer view, frames drawn from an earlier render, and
    // views shown again from the cache without rendering.
    internal int DesignRequests { get; private set; }
    internal int DesignRendersStarted { get; private set; }
    internal int DesignRendersCompleted { get; private set; }
    internal int DesignRendersCanceled { get; private set; }
    internal int DesignReusedFrames { get; private set; }
    internal int DesignCacheHits { get; private set; }
    internal BitmapSource? DesignImage => designCache.Count > 0 ? designCache[0].Image : null;
    internal (Rect Area, int Width, int Height)? DesignViewport => designCache.Count > 0 ? (designCache[0].Request.Area, designCache[0].Request.Width, designCache[0].Request.Height) : null;
    internal CancellationToken? PendingDesignToken => designCancellation?.Token;
    // Whether the design preview still references a document (scene state,
    // cached viewports or a queued request).
    internal bool HoldsDesignScene(Document doc) => designState.Holds(doc) || ReferenceEquals(requestedDesign?.Document, doc) ||
        designCache.Exists(entry => ReferenceEquals(entry.Request.Document, doc));
    readonly SemaphoreSlim designGate = new(1, 1);
    // Detects content changes without hashing every object on every frame.
    readonly SceneState designState = new();
    CancellationTokenSource? designCancellation;
    record Request(Document Document, Guid Revision, long State, BitmapSource? Composite, Rect Area, int Width, int Height, bool Proof, string? Profile)
    {
        public bool SameContent(Request other) => ReferenceEquals(Document, other.Document) && Revision == other.Revision && State == other.State &&
            ReferenceEquals(Composite, other.Composite) && Proof == other.Proof && Profile == other.Profile;
    }
    Request? requestedDesign;
    // Viewports rendered for the current content, newest first. A view shown
    // again (fit, 100%, a fixed zoom step) is reused without rendering, and an
    // interaction frame draws the newest one over one that covers the view.
    const int DesignCacheSize = 3;
    readonly List<(Request Request, BitmapSource Image)> designCache = [];
    // The crisp viewport this frame needs: visible document area and its physical pixel size.
    Request? CurrentDesignRequest()
    {
        if (Document == null) return null;
        var origin = Origin;
        var visible = new Rect(-origin.X / Zoom, -origin.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);
        visible.Intersect(new Rect(0, 0, Document.Width, Document.Height));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(visible.Width * Zoom * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(visible.Height * Zoom * dpi.DpiScaleY));
        double cap = Math.Min(1, Math.Sqrt(16_777_216d / ((double)width * height)));
        width = Math.Clamp((int)(width * cap), 1, 8192); height = Math.Clamp((int)(height * cap), 1, 8192);
        return new Request(Document, Document.Revision, designState.Update(Document), Composite, visible, width, height, DesignProof, DesignProofProfile);
    }
    bool TryDrawDesign(DrawingContext dc)
    {
        // A drag shows its move preview planes. After the drop (settled) the crisp view
        // comes back as soon as it is ready, over the planes still shown meanwhile.
        if (!DesignMode || Document == null || MovePreviewBackground != null && !(MovePreviewSettled && MoveSettleEnabled) || !DesignRenderer.HasRetainedContent(Document))
        { CancelDesignPreview(MovePreviewBackground != null && DesignMode && Document != null); return false; }
        if (CurrentDesignRequest() is not { } request) return false;
        var origin = Origin; var visible = request.Area;
        Rect Screen(Rect area) => new(origin.X + area.X * Zoom, origin.Y + area.Y * Zoom, area.Width * Zoom, area.Height * Zoom);
        int exact = designCache.FindIndex(entry => entry.Request == request);
        if (exact >= 0)
        {
            var entry = designCache[exact];
            if (exact > 0) { designCache.RemoveAt(exact); designCache.Insert(0, entry); }
            // A view shown before needs no new render; drop any queued one.
            if (requestedDesign != request) { designCancellation?.Cancel(); requestedDesign = request; DesignCacheHits++; }
            dc.DrawImage(entry.Image, Screen(request.Area));
            return true;
        }
        if (requestedDesign != request)
        {
            // The first view after a drop, at the viewport the drag began in: redraw
            // only the object's old and new footprints into the frame kept from then.
            if (pendingSettle is { } settle && moveBase is { } kept && ReferenceEquals(settle.Document, Document) && ReferenceEquals(kept.Request.Document, Document) &&
                kept.Request.Area == request.Area && kept.Request.Width == request.Width && kept.Request.Height == request.Height &&
                !request.Proof && !kept.Request.Proof)
            { pendingSettle = null; moveBase = null; QueueDesignPreview(request, TimeSpan.Zero, (kept.Image, settle.Old, settle.New)); }
            else QueueDesignPreview(request, requestedDesign != null && requestedDesign.SameContent(request) ? ViewportSettleDelay : ContentSettleDelay);
        }
        // Interaction frame: same content, different view. The newest render is
        // moved and scaled with the view; edges it does not cover show an older
        // render that does, or the document composite, until the view settles.
        int top = designCache.FindIndex(entry => entry.Request.SameContent(request));
        if (top < 0) return false;
        var (shown, image) = designCache[top];
        DesignReusedFrames++;
        if (!shown.Area.Contains(visible))
        {
            int under = designCache.FindIndex(top + 1, entry => entry.Request.SameContent(request) && entry.Request.Area.Contains(visible));
            if (under >= 0) dc.DrawImage(designCache[under].Image, Screen(designCache[under].Request.Area));
            else if (Composite is { } composite) dc.DrawImage(composite, Screen(new Rect(0, 0, Document.Width, Document.Height)));
        }
        dc.DrawImage(image, Screen(shown.Area));
        return true;
    }
    async void QueueDesignPreview(Request request, TimeSpan delay, (BitmapSource Image, Rect Old, Rect New)? patch = null)
    {
        designCancellation?.Cancel(); var cts = designCancellation = new CancellationTokenSource();
        requestedDesign = request; DesignPreviewError = null; DesignRequests++;
        if (patch != null) patchRendering = true;
        bool entered = false;
        try
        {
            await Task.Delay(delay, cts.Token);
            // Capture on the UI thread after coalescing wheel/pan events. Pixel gestures
            // own only the active mutable buffer; detach that buffer before the worker.
            var snapshot = await Dispatcher.InvokeAsync(() =>
            {
                var copy = request.Document.Snapshot();
                if (copy.Active is { } active) { if (active.Kind == LayerKind.Raster) active.Pixels = active.Pixels.Clone(); if (active.Mask != null) active.Mask = (byte[])active.Mask.Clone(); }
                return copy;
            });
            await designGate.WaitAsync(cts.Token); entered = true; DesignRendersStarted++;
            var raster = await CompatibilityImport.OnSta(() =>
            {
                if (patch is { } p) return PatchDesignFrame(snapshot, request.Area, request.Width, request.Height, p.Image, p.Old, p.New, cts.Token);
                var rgb = DesignRenderer.Render(snapshot, request.Area, request.Width, request.Height, cts.Token);
                return request.Proof ? CmykExport.Preview(rgb, request.Profile) : rgb;
            }, cts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || requestedDesign != request || !DesignMode || !ReferenceEquals(Document, request.Document)) return;
                // Renders of earlier content are never shown again; release them.
                designCache.RemoveAll(entry => !entry.Request.SameContent(request) || entry.Request == request);
                designCache.Insert(0, (request, raster.Bitmap()));
                if (designCache.Count > DesignCacheSize) designCache.RemoveRange(DesignCacheSize, designCache.Count - DesignCacheSize);
                if (patch != null) { settledRequest = request; DesignPatchesCompleted++; }
                ToolTip = null; DesignRendersCompleted++; InvalidateVisual();
            });
        }
        catch (OperationCanceledException) { if (entered) DesignRendersCanceled++; }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (requestedDesign == request) { DesignPreviewError = e.Message; ToolTip = "벡터 미리보기 실패: " + e.Message; } }); }
        finally { if (entered) designGate.Release(); if (ReferenceEquals(designCancellation, cts)) designCancellation = null; cts.Dispose(); if (patch != null) patchRendering = false; }
    }
    // Also runs whenever the canvas stops drawing a design (photo mode, a photo
    // document, no document). The scene state references every layer of the
    // last drawing compared, so it is released here too; the guard keeps the
    // per-frame early return from advancing its version on every frame.
    public void CancelDesignPreview() => CancelDesignPreview(false);
    // A drag keeps the crisp frame it began from (see CaptureMoveBase).
    void CancelDesignPreview(bool keepMoveBase)
    {
        designCancellation?.Cancel(); requestedDesign = null; designCache.Clear(); settledRequest = null;
        if (!keepMoveBase) { moveBase = null; pendingSettle = null; }
        if (designState.HoldsScene) designState.Reset();
    }

    // Settling a drop. At pointer down the crisp frame on screen is kept when it shows
    // exactly the current document and view. After the drop only the dragged object
    // moved, so its old and new footprints are the only pixels that can differ; they are
    // drawn with the same transforms and surfaces as a fresh render (RenderDirty), which
    // makes the patched frame equal to one.
    (Request Request, BitmapSource Image)? moveBase;
    record MoveSettle(Document Document, Rect Old, Rect New);
    MoveSettle? pendingSettle;
    Request? settledRequest;
    public bool MovePreviewSettled { get; set; }
    internal bool MoveSettleEnabled { get; set; } = true;
    internal int DesignPatchesCompleted { get; private set; }
    internal bool HoldsMoveBase => moveBase != null;
    bool patchRendering;
    // A drop's footprint redraw is queued or rendering.
    internal bool MoveSettlePending => pendingSettle != null || patchRendering;

    internal void CaptureMoveBase()
    {
        moveBase = null; pendingSettle = null;
        if (!MoveSettleEnabled || !DesignMode || Document == null || MovePreviewBackground != null && !MovePreviewSettled || designCache.Count == 0 ||
            !DesignRenderer.HasRetainedContent(Document) || CurrentDesignRequest() is not { } request) return;
        int exact = designCache.FindIndex(entry => entry.Request == request);
        if (exact >= 0 && !request.Proof) moveBase = designCache[exact];
    }
    // Old and new document bounds of the object; the caller guarantees nothing else changed.
    internal void SettleMove(Rect oldBounds, Rect newBounds)
    {
        pendingSettle = moveBase != null && Document != null && MoveSettleEnabled ? new MoveSettle(Document, oldBounds, newBounds) : null;
        if (pendingSettle == null) moveBase = null;
    }
    internal void ForgetMoveBase() { moveBase = null; pendingSettle = null; }
    // The full composite of the settled document arrived: a patched frame made for the same
    // document state stays current instead of being rendered again.
    internal void AdoptComposite()
    {
        if (settledRequest is not { } settled || designCache.Count == 0 || designCache[0].Request != settled || Document == null ||
            !ReferenceEquals(settled.Document, Document) || settled.Revision != Document.Revision || designState.Update(Document) != settled.State) return;
        var adopted = settled with { Composite = Composite };
        designCache[0] = (adopted, designCache[0].Image);
        if (requestedDesign == settled) requestedDesign = adopted;
        settledRequest = null;
    }

    internal static Raster PatchDesignFrame(Document snapshot, Rect area, int width, int height, BitmapSource image, Rect oldBounds, Rect newBounds, CancellationToken token)
    {
        var frame = Raster.FromBitmap(image);
        var map = new Matrix(width / area.Width, 0, 0, height / area.Height, -area.X * width / area.Width, -area.Y * height / area.Height);
        oldBounds.Transform(map); newBounds.Transform(map); oldBounds.Union(newBounds);
        int left = (int)Math.Clamp(Math.Floor(oldBounds.Left) - 2, 0, width), top = (int)Math.Clamp(Math.Floor(oldBounds.Top) - 2, 0, height);
        int right = (int)Math.Clamp(Math.Ceiling(oldBounds.Right) + 2, 0, width), bottom = (int)Math.Clamp(Math.Ceiling(oldBounds.Bottom) + 2, 0, height);
        if (right <= left || bottom <= top) return frame;
        var dirty = new Int32Rect(left, top, right - left, bottom - top);
        var fresh = DesignRenderer.RenderDirty(snapshot, area, width, height, dirty, token);
        for (int row = dirty.Y; row < dirty.Y + dirty.Height; row++)
            Buffer.BlockCopy(fresh.Data, (row * width + dirty.X) * 4, frame.Data, (row * width + dirty.X) * 4, dirty.Width * 4);
        return frame;
    }
}
