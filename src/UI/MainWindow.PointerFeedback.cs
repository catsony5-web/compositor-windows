using System.Windows;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    MagneticSnapSession? moveSnapSession;
    Point? lastPointerScreen;
    Point? hoverProbe;
    Document? hoverDocument;
    Guid hoverRevision;
    double hoverZoom;

    Layer? PickMoveTarget(Point point) => LayerPicking.PickNear(doc, point, canvas.Zoom);

    void ClearPointerHover()
    {
        hoverProbe = null; hoverDocument = null;
        if (canvas.HoveredLayerId == null) return;
        canvas.HoveredLayerId = null; canvas.InvalidateVisual();
    }

    void ResetPointerFeedback()
    {
        ClearPointerHover(); moveSnapSession = null;
        if (canvas.SnapGuides.Count != 0) { canvas.SnapGuides = []; canvas.InvalidateVisual(); }
    }

    // Preselection changes only the overlay, never the selected layer, inspector or history.
    void UpdatePointerHover(Point point, bool panModifier = false)
    {
        if (!HasDocument || tool != Tool.Move || autoSelectToggle.IsChecked != true || dragging || panning ||
            resizingBrush || jobCts != null || panModifier)
        { ClearPointerHover(); return; }
        if (doc.Active is { } active && CanUseTransformHandles(active) && TransformHandles.HitTest(doc, active, point, canvas.Zoom) >= 0)
        { ClearPointerHover(); return; }
        if (ReferenceEquals(hoverDocument, doc) && hoverRevision == doc.Revision && hoverZoom == canvas.Zoom &&
            hoverProbe is { } previous && (point - previous).Length * canvas.Zoom < .25) return;
        hoverDocument = doc; hoverRevision = doc.Revision; hoverZoom = canvas.Zoom; hoverProbe = point;
        var picked = PickMoveTarget(point);
        Guid? id = picked != null && !IsLockedWithParents(picked) ? picked.Id : null;
        if (canvas.HoveredLayerId == id) return;
        canvas.HoveredLayerId = id; canvas.InvalidateVisual();
    }

    void UpdatePointerModifiers()
    {
        if (lastPointerScreen is not { } screen) return;
        var point = canvas.ToDocument(screen);
        if (dragging && tool == Tool.Move && handleGesture == null) ContinueMove(point, screen);
        else UpdatePointerHover(point, Keyboard.IsKeyDown(Key.Space));
        Mouse.UpdateCursor();
    }
}
