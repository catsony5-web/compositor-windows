using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class CanvasView
{
    public bool DesignMode { get; set; }
    public bool DesignProof { get; set; }
    public string? DesignProofProfile { get; set; }
    public bool IsDesignPreviewReady => designImage != null && completedDesign == requestedDesign;
    public string? DesignPreviewError { get; private set; }
    readonly SemaphoreSlim designGate = new(1, 1);
    CancellationTokenSource? designCancellation;
    record Request(Document Document, Guid Revision, int State, Rect Area, int Width, int Height, bool Proof, string? Profile);
    Request? requestedDesign, completedDesign;
    BitmapSource? designImage;
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
        var hash = new HashCode(); hash.Add(Composite);
        foreach (var l in Document.Layers)
        {
            hash.Add(l.Id); hash.Add(l.Kind); hash.Add(l.Visible); hash.Add(l.Pixels); hash.Add(l.Mask); hash.Add(l.Vector); hash.Add(l.Text); hash.Add(l.Shape);
            hash.Add(l.ParentId); hash.Add(l.Clipped); hash.Add(l.Opacity); hash.Add(l.Blend); hash.Add(l.Matrix); hash.Add(l.Warp); hash.Add(l.Adjustment);
        }
        var request = new Request(Document, Document.Revision, hash.ToHashCode(), visible, width, height, DesignProof, DesignProofProfile);
        if (requestedDesign != request) QueueDesignPreview(request);
        if (completedDesign != request || designImage == null) return false;
        dc.DrawImage(designImage, new Rect(origin.X + visible.X * Zoom, origin.Y + visible.Y * Zoom, visible.Width * Zoom, visible.Height * Zoom));
        return true;
    }
    async void QueueDesignPreview(Request request)
    {
        designCancellation?.Cancel(); var cts = designCancellation = new CancellationTokenSource();
        requestedDesign = request; DesignPreviewError = null;
        bool entered = false;
        try
        {
            await Task.Delay(75, cts.Token);
            // Capture on the UI thread after coalescing wheel/pan events. Pixel gestures
            // own only the active mutable buffer; detach that buffer before the worker.
            var snapshot = await Dispatcher.InvokeAsync(() =>
            {
                var copy = request.Document.Snapshot();
                if (copy.Active is { } active) { if (active.Kind == LayerKind.Raster) active.Pixels = active.Pixels.Clone(); if (active.Mask != null) active.Mask = (byte[])active.Mask.Clone(); }
                return copy;
            });
            await designGate.WaitAsync(cts.Token); entered = true;
            var raster = await CompatibilityImport.OnSta(() =>
            {
                var rgb = DesignRenderer.Render(snapshot, request.Area, request.Width, request.Height, cts.Token);
                return request.Proof ? CmykExport.Preview(rgb, request.Profile) : rgb;
            }, cts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || requestedDesign != request || !DesignMode || !ReferenceEquals(Document, request.Document)) return;
                designImage = raster.Bitmap(); completedDesign = request; ToolTip = null; InvalidateVisual();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (requestedDesign == request) { DesignPreviewError = e.Message; ToolTip = "벡터 미리보기 실패: " + e.Message; } }); }
        finally { if (entered) designGate.Release(); if (ReferenceEquals(designCancellation, cts)) designCancellation = null; cts.Dispose(); }
    }
    public void CancelDesignPreview()
    {
        designCancellation?.Cancel(); requestedDesign = null; completedDesign = null; designImage = null;
    }
}
