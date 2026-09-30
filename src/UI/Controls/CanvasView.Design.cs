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
    bool TryDrawDesign(DrawingContext dc)
    {
        if (!DesignMode || Document == null || MovePreviewBackground != null || !DesignRenderer.HasRetainedContent(Document))
        { CancelDesignPreview(); return false; }
        var origin = Origin;
        var visible = new Rect(-origin.X / Zoom, -origin.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);
        visible.Intersect(new Rect(0, 0, Document.Width, Document.Height));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(visible.Width * Zoom * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(visible.Height * Zoom * dpi.DpiScaleY));
        double cap = Math.Min(1, Math.Sqrt(16_777_216d / ((double)width * height)));
        width = Math.Clamp((int)(width * cap), 1, 8192); height = Math.Clamp((int)(height * cap), 1, 8192);
        var request = new Request(Document, Document.Revision, designState.Update(Document), Composite, visible, width, height, DesignProof, DesignProofProfile);
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
            QueueDesignPreview(request, requestedDesign != null && requestedDesign.SameContent(request) ? ViewportSettleDelay : ContentSettleDelay);
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
    async void QueueDesignPreview(Request request, TimeSpan delay)
    {
        designCancellation?.Cancel(); var cts = designCancellation = new CancellationTokenSource();
        requestedDesign = request; DesignPreviewError = null; DesignRequests++;
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
                ToolTip = null; DesignRendersCompleted++; InvalidateVisual();
            });
        }
        catch (OperationCanceledException) { if (entered) DesignRendersCanceled++; }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (requestedDesign == request) { DesignPreviewError = e.Message; ToolTip = "벡터 미리보기 실패: " + e.Message; } }); }
        finally { if (entered) designGate.Release(); if (ReferenceEquals(designCancellation, cts)) designCancellation = null; cts.Dispose(); }
    }
    // Also runs whenever the canvas stops drawing a design (photo mode, a photo
    // document, no document). The scene state references every layer of the
    // last drawing compared, so it is released here too; the guard keeps the
    // per-frame early return from advancing its version on every frame.
    public void CancelDesignPreview()
    {
        designCancellation?.Cancel(); requestedDesign = null; designCache.Clear();
        if (designState.HoldsScene) designState.Reset();
    }
}
