using System.Windows;

namespace Compositor.Windows;

// After a drop in a drawing, the crisp view used to render the whole viewport again
// (after the full composite). The canvas keeps the crisp frame the drag began from;
// when only the dragged object changed, it redraws just the object's old and new
// footprints into that frame, which equals a fresh crisp render.
public sealed partial class MainWindow
{
    readonly SceneState settleScene = new();
    Document? settleDocument;
    Guid settleLayerId;
    long settleVersion;
    Rect settleBounds;
    MaterialAsset[] settleMaterials = [];
    MaterialRegion[] settleRegions = [];

    // Pointer down, before anything moved.
    void CaptureMoveSettle()
    {
        settleDocument = null;
        canvas.CaptureMoveBase();
        if (!canvas.HoldsMoveBase || !HasDocument || cmykProof || doc.Active is not { } layer) return;
        settleDocument = doc; settleLayerId = layer.Id; settleVersion = settleScene.Update(doc, layer.Id);
        settleMaterials = doc.Materials.ToArray(); settleRegions = doc.MaterialRegions.ToArray();
        settleBounds = DocumentBounds(doc, layer);
    }

    // The drop of a fast-previewed move. Everything except the dragged object must be
    // as it was at pointer down, or the canvas renders the whole view as before.
    void SettleMovePreview()
    {
        var document = settleDocument; settleDocument = null;
        if (!ReferenceEquals(textPreviewDocument, doc) || canvas.MovePreviewBackground == null || canvas.MovePreviewLayer == null) { canvas.ForgetMoveBase(); return; }
        // The preview stays until the full composite replaces it; the crisp view may replace it sooner.
        canvas.MovePreviewSettled = true;
        if (ReferenceEquals(document, doc) && doc.Active is { } layer && layer.Id == settleLayerId && textPreviewLayerId == layer.Id &&
            settleScene.Update(doc, layer.Id) == settleVersion && SameMaterials(doc, settleMaterials, settleRegions))
            canvas.SettleMove(settleBounds, DocumentBounds(doc, layer));
        else canvas.ForgetMoveBase();
        settleScene.Reset();
    }

    readonly System.Windows.Threading.DispatcherTimer settleRenderTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly System.Diagnostics.Stopwatch settleRenderWait = new();
    const int SettleRenderWaitMilliseconds = 400;

    void DeferFullRenderForSettle()
    {
        settleRenderWait.Restart();
        settleRenderTimer.Tick -= OnSettleRenderTick; settleRenderTimer.Tick += OnSettleRenderTick; settleRenderTimer.Start();
    }
    void OnSettleRenderTick(object? sender, EventArgs e)
    {
        if (canvas.MoveSettlePending && settleRenderWait.ElapsedMilliseconds < SettleRenderWaitMilliseconds && !renderShutdown) return;
        settleRenderTimer.Stop(); settleRenderTimer.Tick -= OnSettleRenderTick;
        if (!renderShutdown && !rendering && pendingFullRender) StartQueuedRender();
    }

    static Rect DocumentBounds(Document document, Layer layer)
    {
        var bounds = Rect.Empty;
        foreach (var corner in new[] { new Point(0, 0), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) })
            bounds.Union(DocumentFeatures.ToDocumentSpace(document, layer, corner));
        return bounds;
    }
}
