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
        if (renderShutdown || cmykProof || !dragging || !moveStarted || tool != Tool.Move || doc.Active is not { } layer ||
            !CanPreviewLayerMove(doc, layer, MovableSelectedLayers().Count()))
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
            gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
            textPreviewDocument = doc; textPreviewLayerId = layer.Id;
            canvas.MovePreviewOpacity = layer.Opacity;
            var (below, above) = CreateLayerMovePreviewStacks(doc, layer.Id);
            var cts = textPreviewCts = new CancellationTokenSource();
            long generation = textPreviewGeneration;
            _ = RenderTextMoveBackground(below, above, layer.Pixels, doc, activeTab, generation, cts);
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
        if (movingCount != 1 || !layer.Visible || layer.Opacity <= 0 || layer.Locked ||
            layer.Kind is not (LayerKind.Raster or LayerKind.Text or LayerKind.Shape or LayerKind.Vector) ||
            layer.Clipped || layer.Mask != null || layer.Warp != null ||
            layer.Blend != BlendMode.Normal || !document.Layers.Contains(layer)) return false;
        // Bound additional raster/WIC copies without limiting document editing.
        // Large or interdependent stacks retain the full compositor path.
        long estimatedBytes = (long)document.Width * document.Height * 16 + layer.Pixels.Data.LongLength;
        if (estimatedBytes > 512L * 1024 * 1024) return false;
        if (!document.Layers.All(item => !item.Clipped && item.Kind != LayerKind.Adjustment && item.Blend == BlendMode.Normal &&
            (item.Kind != LayerKind.Group || IsMovePreviewContainer(document, item)))) return false;
        var lookup = document.Layers.ToDictionary(item => item.Id);
        var parent = layer.ParentId; int depth = 0;
        while (parent is { } id)
        {
            if (++depth > 16 || !lookup.TryGetValue(id, out var group) || group.Kind != LayerKind.Group || group.Locked) return false;
            parent = group.ParentId;
        }
        return true;
    }

    static bool IsMovePreviewContainer(Document document, Layer group) =>
        group.Visible && group.Opacity == 1 && group.Mask == null && group.Warp == null && group.Matrix.IsIdentity &&
        group.Pixels.Width == document.Width && group.Pixels.Height == document.Height;

    internal static (Document Below, Document Above) CreateLayerMovePreviewStacks(Document document, Guid movingId)
    {
        // Source-layer folders from CAD import do not transform or crop their
        // children. Flatten only these preview snapshots, in actual paint order.
        var source = document.Snapshot();
        if (source.Layers.Any(layer => layer.Kind == LayerKind.Group))
        {
            if (source.Layers.Any(layer => layer.Kind == LayerKind.Group && !IsMovePreviewContainer(document, layer)))
                throw new InvalidOperationException("그룹 변형에는 전체 합성 미리보기가 필요합니다.");
            var children = source.Layers.ToLookup(layer => layer.ParentId);
            var flattened = new List<Layer>();
            void Append(Guid? parent, int depth)
            {
                if (depth > 16) throw new InvalidOperationException("그룹 계층이 너무 깊습니다.");
                foreach (var item in children[parent])
                {
                    if (item.Kind == LayerKind.Group) Append(item.Id, depth + 1);
                    else { item.ParentId = null; flattened.Add(item); }
                }
            }
            Append(null, 0); source.Layers = flattened;
        }
        int index = source.Layers.FindIndex(layer => layer.Id == movingId);
        if (index < 0) throw new ArgumentException("이동할 레이어를 찾을 수 없습니다.", nameof(movingId));
        var below = source; var above = source.Snapshot();
        below.Layers.RemoveRange(index, below.Layers.Count - index);
        above.Layers.RemoveRange(0, index + 1);
        below.ActiveId = Guid.Empty; above.ActiveId = Guid.Empty;
        return (below, above);
    }

    async Task RenderTextMoveBackground(Document below, Document above, Raster moving, Document document, int tab, long generation, CancellationTokenSource cts)
    {
        try
        {
            var cache = await Task.Run(() =>
            {
                var background = Imaging.Render(below, cts.Token).Bitmap();
                cts.Token.ThrowIfCancellationRequested();
                var foreground = above.Layers.Count == 0 ? null : Imaging.Render(above, cts.Token).Bitmap();
                cts.Token.ThrowIfCancellationRequested();
                var bitmap = moving.Bitmap();
                cts.Token.ThrowIfCancellationRequested();
                return (Background: background, Foreground: foreground, Layer: bitmap);
            }, cts.Token);
            if (renderShutdown || cts.IsCancellationRequested || generation != textPreviewGeneration || !ReferenceEquals(doc, document) || activeTab != tab) return;
            // Publish the complete stack together on the dispatcher. Until now,
            // the old composite stayed visible, with no partial-layer flashes.
            textPreviewBitmap = cache.Layer;
            canvas.MovePreviewBackground = cache.Background;
            canvas.MovePreviewLayer = cache.Layer;
            canvas.MovePreviewForeground = cache.Foreground;
            canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!renderShutdown && generation == textPreviewGeneration)
            {
                ClearTextMovePreview(); textPreviewFailed = true;
                status.Text = "이동 미리보기 실패: " + e.Message;
                // Resume the exact renderer even if the pointer has stopped;
                // do not repeatedly retry a failing cache during this gesture.
                QueueRender(dragging);
            }
        }
        finally { cts.Dispose(); if (ReferenceEquals(textPreviewCts, cts)) textPreviewCts = null; }
    }
    void ClearTextMovePreview()
    {
        ++textPreviewGeneration; textPreviewCts?.Cancel(); textPreviewCts = null;
        textPreviewDocument = null; textPreviewLayerId = null; textPreviewBitmap = null;
        textPreviewFailed = false;
        canvas.MovePreviewBackground = null; canvas.MovePreviewLayer = null; canvas.MovePreviewForeground = null;
    }
}
