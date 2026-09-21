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
        pendingFullRender = false; pendingGestureRender = false;
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        ++renderGeneration;
        ClearTextMovePreview();
        renderCts?.Cancel(); jobCts?.Cancel();
    }

    void QueueRender(bool gesture = false)
    {
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
        var cts = renderCts = new CancellationTokenSource();
        try
        {
            // Capture only when this frame is actually due. A brush owns a
            // mutable buffer, so detach just the active layer before workers read it.
            var snapshot = document.Snapshot();
            if (gesture && (stroke != null || IsRetouch(tool)) && snapshot.Active is { } active)
            { active.Pixels = active.Pixels.Clone(); if (active.Mask != null) active.Mask = (byte[])active.Mask.Clone(); }
            var result = await Task.Run(() =>
            {
                var rgb = Imaging.Render(snapshot, cts.Token); cts.Token.ThrowIfCancellationRequested();
                var display = proof ? CmykExport.Preview(rgb, profile) : rgb;
                cts.Token.ThrowIfCancellationRequested(); return (Rgb: rgb, Display: display);
            }, cts.Token);
            var raster = result.Rgb;
            if (renderShutdown || cts.IsCancellationRequested || generation != renderGeneration || !ReferenceEquals(document, doc) || tab != activeTab) return;
            composite = raster; canvas.Composite = result.Display.Bitmap();
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
        if (renderShutdown || cmykProof || textPreviewFailed || !dragging || !moveStarted || tool != Tool.Move || doc.Active is not { } layer ||
            !CanPreviewTextMove(doc, layer, MovableSelectedLayers().Count())) return false;
        if (!ReferenceEquals(textPreviewDocument, doc) || textPreviewLayerId != layer.Id)
        {
            ClearTextMovePreview();
            // The cached background replaces any in-flight whole-document frame.
            ++renderGeneration; renderCts?.Cancel(); pendingGestureRender = false; pendingFullRender = false;
            gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
            textPreviewDocument = doc; textPreviewLayerId = layer.Id;
            textPreviewBitmap = layer.Pixels.Bitmap();
            canvas.MovePreviewOpacity = layer.Opacity;
            var snapshot = doc.Snapshot();
            snapshot.Layers.RemoveAll(l => l.Id == layer.Id);
            var cts = textPreviewCts = new CancellationTokenSource();
            long generation = textPreviewGeneration;
            _ = RenderTextMoveBackground(snapshot, doc, activeTab, generation, cts);
        }
        canvas.MovePreviewMatrix = layer.Matrix;
        canvas.InvalidateVisual();
        return true;
    }
    internal static bool CanPreviewTextMove(Document document, Layer layer, int movingCount) =>
        movingCount == 1 && layer.Kind == LayerKind.Text && layer.Visible &&
        layer.ParentId == null && !layer.Clipped && layer.Mask == null && layer.Warp == null &&
        layer.Blend == BlendMode.Normal && document.Layers.LastOrDefault(l => l.ParentId == null) == layer;
    async Task RenderTextMoveBackground(Document snapshot, Document document, int tab, long generation, CancellationTokenSource cts)
    {
        try
        {
            var raster = await Task.Run(() => Imaging.Render(snapshot, cts.Token), cts.Token);
            if (renderShutdown || cts.IsCancellationRequested || generation != textPreviewGeneration || !ReferenceEquals(doc, document) || activeTab != tab) return;
            canvas.MovePreviewBackground = raster.Bitmap();
            canvas.MovePreviewLayer = textPreviewBitmap;
            canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!renderShutdown && generation == textPreviewGeneration)
            { textPreviewFailed = true; status.Text = "이동 미리보기 실패: " + e.Message; }
        }
        finally { cts.Dispose(); if (ReferenceEquals(textPreviewCts, cts)) textPreviewCts = null; }
    }
    void ClearTextMovePreview()
    {
        ++textPreviewGeneration; textPreviewCts?.Cancel(); textPreviewCts = null;
        textPreviewDocument = null; textPreviewLayerId = null; textPreviewBitmap = null;
        textPreviewFailed = false;
        canvas.MovePreviewBackground = null; canvas.MovePreviewLayer = null;
    }
}
