using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 선 · 곡선 (P, Shift+P) and 지시선 (N): click points (double-click, Enter or a right click finishes, a click
// on the first point closes the line, Shift keeps 15° steps, a drag draws one straight line), drag from
// a target to the label for a callout, and edit the points of the selected line or callout afterwards
// with the 이동, 선 or 지시선 tool: drag a handle, double-click the line to add a point, Alt+click or
// Delete removes one. Every finished edit is one undo step.
public sealed partial class MainWindow
{
    // Options for new lines and callouts (tool options bar).
    internal bool lineCurve;
    internal StrokeDash lineDash = StrokeDash.Solid, calloutDash = StrokeDash.Solid;
    internal double lineWidth = 2;
    internal LineMark lineStart = LineMark.None, lineEnd = LineMark.None, calloutMark = LineMark.Dot;
    internal CalloutLeader calloutLeader = CalloutLeader.Elbow;

    // The line being drawn, in document pixels.
    readonly List<Point> lineDraft = [];
    Point? lineHover;
    bool lineDrawing, linePressed, lineDragged;
    Point linePressScreen;
    // The callout being dragged out: target point and current label position.
    Point? calloutAnchor;
    Point calloutCurrent, calloutPressScreen;
    bool calloutDragged;

    sealed record PointDrag(Guid LayerId, int Index, ShapeSpec Original, bool WasVisible, Point StartScreen);
    PointDrag? pointDrag;
    ShapeSpec? pointDragSpec;
    bool pointDragMoved;
    (Guid Layer, int Index)? selectedShapePoint;
    int hotShapePoint = -1;
    TextPropertiesPanel? calloutLabelPanel;

    static bool DiagramTool(Tool tool) => tool is Tool.Line or Tool.Callout;
    internal bool LineDrawing => lineDrawing;
    internal IReadOnlyList<Point> LineDraft => lineDraft;

    double DefaultMarkSize(double width) => Math.Round(Math.Max(10, width * 5), 1);
    double CalloutFontSize() => Math.Clamp(Math.Round(Math.Min(doc.Width, doc.Height) / 40.0), 12, 160);

    void ResetDiagramTransient()
    {
        lineDraft.Clear(); lineHover = null; lineDrawing = linePressed = lineDragged = false;
        calloutAnchor = null; calloutDragged = false;
        pointDrag = null; pointDragSpec = null; pointDragMoved = false; hotShapePoint = -1;
        canvas.DiagramPreview = null; canvas.DiagramCloseTarget = null;
    }

    // ------------------------------------------------------------------ specs for new objects

    ShapeSpec DraftLineSpec(IEnumerable<Point> points) => new()
    {
        Kind = ShapeKind.Line, Points = new ShapePoints(points), Smooth = lineCurve, StrokeEnabled = true, StrokeArgb = VectorShapes.Argb(foreground),
        StrokeWidth = lineWidth, Dash = lineDash, StartMark = lineStart, EndMark = lineEnd, MarkSize = DefaultMarkSize(lineWidth),
        FillEnabled = false, FillArgb = VectorShapes.Argb(backgroundColor), Width = 1, Height = 1
    };

    internal ShapeSpec CalloutSpec(Point anchor, Point label) => new()
    {
        Kind = ShapeKind.Callout, Leader = calloutLeader,
        Points = new ShapePoints([anchor, calloutLeader == CalloutLeader.Elbow ? DiagramEditing.DefaultElbow(anchor, label) : anchor + (label - anchor) / 2, label]),
        StrokeEnabled = true, StrokeArgb = VectorShapes.Argb(foreground), StrokeWidth = lineWidth, Dash = calloutDash, StartMark = calloutMark,
        MarkSize = DefaultMarkSize(lineWidth), FillEnabled = false, FillArgb = 0xFFFFFFFF, CornerRadius = 4, Width = 1, Height = 1,
        Label = new TextSpec { Content = Loc.T("라벨"), FontFamily = "Malgun Gothic", FontSize = CalloutFontSize(), ColorArgb = VectorShapes.Argb(foreground) }
    };

    // ------------------------------------------------------------------ selected line or callout

    /// <summary>The active line or callout whose points can be edited now, or null.</summary>
    Layer? EditablePathLayer()
    {
        if (!HasDocument || !(tool == Tool.Move || DiagramTool(tool)) || selectedLayers.Count > 1 || lineDrawing || calloutAnchor != null) return null;
        if (doc.Active is not { Shape.HasPoints: true } layer || layer.Warp != null || IsLockedWithParents(layer)) return null;
        return layer.Visible || pointDrag?.LayerId == layer.Id ? layer : null;
    }

    Matrix LayerToDocument(Layer layer)
    {
        var matrix = layer.Matrix;
        foreach (var group in Parents(doc, layer)) matrix.Append(group.Matrix);
        return matrix;
    }
    Point ToLayerLocal(Layer layer, Point documentPoint) => layer.Local(ParentPoint(doc, layer, documentPoint));

    Point[] HandlePositions(Layer layer, ShapeSpec spec) => spec.Points!.Select(p => DocumentFeatures.ToDocumentSpace(doc, layer, p)).ToArray();

    int HitShapeHandle(Layer layer, Point documentPoint)
    {
        var spec = pointDrag?.LayerId == layer.Id && pointDragSpec != null ? pointDragSpec : layer.Shape!;
        var positions = HandlePositions(layer, spec); double tolerance = 7 / Math.Max(.01, canvas.Zoom);
        // The label end sits next to its text and wins over the elbow when they meet.
        for (int i = positions.Length - 1; i >= 0; i--) if ((positions[i] - documentPoint).Length <= tolerance) return i;
        return -1;
    }

    void UpdateDiagramOverlay()
    {
        canvas.DiagramHandles = null; canvas.DiagramPreview = null; canvas.DiagramPreviewTransform = Matrix.Identity; canvas.DiagramCloseTarget = null;
        if (!HasDocument) { canvas.InvalidateVisual(); return; }
        var handles = new List<DiagramHandle>();
        if (tool == Tool.Line && lineDrawing && lineDraft.Count > 0)
        {
            var points = lineHover is { } hover ? lineDraft.Append(hover).ToArray() : lineDraft.ToArray();
            if (points.Length > 1) canvas.DiagramPreview = ShapeGeometry.Drawing(DraftLineSpec(points));
            handles.AddRange(lineDraft.Select(p => new DiagramHandle(p, DiagramHandleKind.Draft, false, false)));
            if (CanCloseDraft(lineHover)) canvas.DiagramCloseTarget = lineDraft[0];
        }
        else if (tool == Tool.Callout && calloutAnchor is { } anchor && calloutDragged)
            canvas.DiagramPreview = ShapeGeometry.Drawing(CalloutSpec(anchor, calloutCurrent));
        if (pointDrag != null && pointDragSpec != null && doc.Layers.Find(l => l.Id == pointDrag.LayerId) is { } dragged && pointDragMoved)
        { canvas.DiagramPreview = ShapeGeometry.Drawing(pointDragSpec); canvas.DiagramPreviewTransform = LayerToDocument(dragged); }
        if (EditablePathLayer() is { } layer)
        {
            var spec = pointDrag?.LayerId == layer.Id && pointDragSpec != null ? pointDragSpec : layer.Shape!;
            var positions = HandlePositions(layer, spec);
            for (int i = 0; i < positions.Length; i++)
            {
                var kind = spec.Kind != ShapeKind.Callout ? DiagramHandleKind.Point : i switch { DiagramEditing.Anchor => DiagramHandleKind.Anchor, DiagramEditing.Elbow => DiagramHandleKind.Elbow, _ => DiagramHandleKind.Label };
                // A straight leader has no bend to drag.
                if (spec.Kind == ShapeKind.Callout && i == DiagramEditing.Elbow && spec.Leader == CalloutLeader.Straight) continue;
                bool selected = selectedShapePoint is { } chosen && chosen.Layer == layer.Id && chosen.Index == i;
                handles.Add(new DiagramHandle(positions[i], kind, selected, i == hotShapePoint));
            }
        }
        canvas.DiagramHandles = handles.Count > 0 ? handles : null;
        canvas.InvalidateVisual();
    }

    bool CanCloseDraft(Point? hover) => lineDraft.Count >= 3 && hover is { } p && (p - lineDraft[0]).Length * canvas.Zoom <= 8;

    // ------------------------------------------------------------------ pointer

    // Called before the move tool's own handling. True when the diagram tools took the press.
    bool DiagramDown(Point point, Point screen, MouseButtonEventArgs e)
    {
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (TryBeginPointDrag(point, screen, alt)) return true;
        // A press anywhere else lets go of the chosen point, so Delete deletes objects again.
        if (selectedShapePoint != null) { selectedShapePoint = null; UpdateDiagramOverlay(); }
        if (tool == Tool.Move && e.ClickCount == 2 && doc.Active is { Shape.HasPoints: true } active && EditablePathLayer() == active)
        {
            if (active.Shape!.Kind == ShapeKind.Callout && ShapeGeometry.LabelRect(active.Shape).Contains(ToLayerLocal(active, point))) { FocusCalloutLabel(); return true; }
            if (TryInsertShapePoint(active, point)) return true;
        }
        if (tool == Tool.Line) { LineDown(ClampToCanvas(point), screen, e.ClickCount); return true; }
        if (tool == Tool.Callout) { CalloutDown(ClampToCanvas(point), screen); return true; }
        return false;
    }

    // True when the pointer move belongs to a diagram gesture.
    bool DiagramMove(Point point, Point screen)
    {
        if (pointDrag != null) { ContinuePointDrag(point, screen); return true; }
        if (tool == Tool.Line && lineDrawing)
        {
            var clamped = ClampToCanvas(point);
            if (linePressed && !lineDragged && lineDraft.Count == 1 && Dragged(screen - linePressScreen)) lineDragged = true;
            lineHover = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && lineDraft.Count > 0 ? ClampToCanvas(DiagramEditing.SnapAngle(lineDraft[^1], clamped)) : clamped;
            UpdateDiagramOverlay(); return true;
        }
        if (tool == Tool.Callout && calloutAnchor != null)
        {
            calloutCurrent = ClampToCanvas(point);
            if (!calloutDragged && Dragged(screen - calloutPressScreen)) calloutDragged = true;
            UpdateDiagramOverlay(); return true;
        }
        if (!dragging && EditablePathLayer() is { } layer)
        {
            int hot = HitShapeHandle(layer, point);
            if (hot != hotShapePoint) { hotShapePoint = hot; UpdateDiagramOverlay(); }
            if (hot >= 0 && tool == Tool.Move) { ClearPointerHover(); return true; }
        }
        else if (hotShapePoint >= 0) { hotShapePoint = -1; UpdateDiagramOverlay(); }
        return false;
    }

    // True when the release finished a diagram gesture.
    bool DiagramUp(Point point, Point screen)
    {
        if (pointDrag != null) { ContinuePointDrag(point, screen); EndPointDrag(); return true; }
        if (tool == Tool.Line && linePressed)
        {
            linePressed = false; canvas.ReleaseMouseCapture();
            if (lineDragged && lineDraft.Count == 1)
            {
                var end = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ClampToCanvas(DiagramEditing.SnapAngle(lineDraft[0], ClampToCanvas(point))) : ClampToCanvas(point);
                lineDraft.Add(end); FinishLine(false);
            }
            return true;
        }
        if (tool == Tool.Callout && calloutAnchor is { } anchor)
        {
            canvas.ReleaseMouseCapture();
            var label = calloutDragged ? ClampToCanvas(point) : ClampToCanvas(anchor + new Vector(90, -60) / Math.Max(.01, canvas.Zoom));
            calloutAnchor = null; calloutDragged = false;
            FinishCallout(anchor, label); return true;
        }
        return false;
    }

    static bool Dragged(Vector screenDelta) => Math.Abs(screenDelta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(screenDelta.Y) >= SystemParameters.MinimumVerticalDragDistance;

    void LineDown(Point point, Point screen, int clicks)
    {
        if (!lineDrawing)
        {
            if (clicks > 1) return;
            lineDraft.Clear(); lineDraft.Add(point); lineDrawing = true; lineHover = point;
            selectedShapePoint = null;
        }
        else if (clicks > 1) { FinishLine(false); return; }
        else if (CanCloseDraft(point)) { FinishLine(true); return; }
        else
        {
            var next = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ClampToCanvas(DiagramEditing.SnapAngle(lineDraft[^1], point)) : point;
            if ((next - lineDraft[^1]).Length * canvas.Zoom >= .5) lineDraft.Add(next);
        }
        linePressed = true; lineDragged = false; linePressScreen = screen; canvas.CaptureMouse();
        UpdateDiagramOverlay(); ShowInteractionHint();
    }

    void CalloutDown(Point point, Point screen)
    {
        calloutAnchor = point; calloutCurrent = point; calloutPressScreen = screen; calloutDragged = false; selectedShapePoint = null;
        canvas.CaptureMouse(); UpdateDiagramOverlay();
    }

    /// <summary>Creates the drawn line (closed: the last point joins the first). Fewer than two distinct points cancel.</summary>
    internal void FinishLine(bool closed)
    {
        var points = new List<Point>();
        foreach (var p in lineDraft) if (points.Count == 0 || (p - points[^1]).Length > 1e-6) points.Add(p);
        bool curve = lineCurve;
        ResetDiagramTransient(); canvas.ReleaseMouseCapture();
        if (points.Count < 2 || closed && points.Count < 3) { status.Text = "선을 그리려면 서로 다른 두 점 이상을 클릭하세요."; UpdateDiagramOverlay(); return; }
        var spec = DraftLineSpec(points) with { Closed = closed };
        Layer layer;
        try { layer = VectorShapes.Create(spec); }
        catch (System.IO.InvalidDataException error) { status.Text = error.Message; UpdateDiagramOverlay(); return; }
        layer.Name = Loc.T(layer.Name);
        Edit(curve ? "곡선 그리기" : "선 그리기", () => { doc.Add(layer); selectedLayers.Clear(); selectedLayers.Add(layer.Id); maskEditing = false; });
        ShowStudioPage(1);
    }

    internal void FinishCallout(Point anchor, Point label)
    {
        Layer layer;
        try { layer = VectorShapes.Create(CalloutSpec(anchor, label)); }
        catch (System.IO.InvalidDataException error) { status.Text = error.Message; UpdateDiagramOverlay(); return; }
        layer.Name = Loc.T(layer.Name);
        Edit("지시선 추가", () => { doc.Add(layer); selectedLayers.Clear(); selectedLayers.Add(layer.Id); maskEditing = false; });
        ShowStudioPage(1); FocusCalloutLabel();
    }

    void FocusCalloutLabel()
    {
        if (doc.Active is not { Shape.Kind: ShapeKind.Callout }) return;
        ShowStudioPage(1);
        var panel = calloutLabelPanel;
        if (!headlessTesting && panel != null) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (ReferenceEquals(calloutLabelPanel, panel)) panel.FocusContent(); }));
    }

    // ------------------------------------------------------------------ point editing

    bool TryBeginPointDrag(Point point, Point screen, bool alt)
    {
        if (jobCts != null || EditablePathLayer() is not { } layer) return false;
        int index = HitShapeHandle(layer, point); if (index < 0) return false;
        if (alt) { DeleteShapePoint(layer, index); return true; }
        selectedShapePoint = (layer.Id, index);
        beforeGesture = doc.Snapshot(); dragging = true; moveStarted = false; start = point; screenStart = screen;
        pointDrag = new PointDrag(layer.Id, index, layer.Shape!, layer.Visible, screen); pointDragSpec = layer.Shape; pointDragMoved = false;
        canvas.CaptureMouse(); UpdateDiagramOverlay();
        status.Text = layer.Shape!.Kind == ShapeKind.Callout ? "지시선 손잡이 이동 · Shift: 15° 단위 · Esc: 취소" : "점 이동 · Shift: 이웃 점 기준 15° 단위 · Esc: 취소";
        return true;
    }

    void ContinuePointDrag(Point point, Point screen)
    {
        if (pointDrag is not { } drag || doc.Layers.Find(l => l.Id == drag.LayerId) is not { } layer) return;
        if (!pointDragMoved)
        {
            if (!Dragged(screen - drag.StartScreen)) return;
            // The layer hides while its edited copy is drawn over the canvas; the real one is updated once on release.
            pointDragMoved = true; layer.Visible = false; RenderGesture();
        }
        var spec = drag.Original; var points = spec.Points!;
        var target = point;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int neighbor = spec.Kind == ShapeKind.Callout ? (drag.Index == DiagramEditing.Anchor ? DiagramEditing.Elbow : drag.Index - 1) : drag.Index > 0 ? drag.Index - 1 : 1;
            if (spec.Kind == ShapeKind.Callout && spec.Leader == CalloutLeader.Straight) neighbor = drag.Index == DiagramEditing.Anchor ? DiagramEditing.LabelPoint : DiagramEditing.Anchor;
            if (neighbor >= 0 && neighbor < points.Count) target = DiagramEditing.SnapAngle(DocumentFeatures.ToDocumentSpace(doc, layer, points[neighbor]), point);
        }
        pointDragSpec = DiagramEditing.MovePoint(spec, drag.Index, ToLayerLocal(layer, target));
        UpdateDiagramOverlay();
    }

    void EndPointDrag()
    {
        if (pointDrag is not { } drag) return;
        var spec = pointDragSpec; bool moved = pointDragMoved;
        pointDrag = null; pointDragSpec = null; pointDragMoved = false; dragging = false; canvas.ReleaseMouseCapture();
        var before = beforeGesture; beforeGesture = null;
        if (!moved || spec == null || before == null || doc.Layers.Find(l => l.Id == drag.LayerId) is not { } layer) { Refresh(false); return; }
        layer.Visible = drag.WasVisible;
        try
        {
            VectorShapes.Update(layer, spec); doc.Validate();
            history.Commit(spec.Kind == ShapeKind.Callout ? "지시선 편집" : "선 점 이동", before, doc);
        }
        catch (Exception error) when (error is System.IO.InvalidDataException or InvalidOperationException or ArgumentException)
        { doc = before; status.Text = error.Message; }
        Refresh();
    }

    bool TryInsertShapePoint(Layer layer, Point point)
    {
        var spec = layer.Shape!; if (spec.Kind != ShapeKind.Line) return false;
        var local = ToLayerLocal(layer, point);
        if (ShapeGeometry.Nearest(spec, local) is not { } near) return false;
        double scale = Math.Max(.01, Math.Abs(layer.Scale * Math.Min(layer.ScaleX, layer.ScaleY)));
        if (near.Distance > spec.StrokeWidth / 2 + 6 / Math.Max(.01, canvas.Zoom * scale)) return false;
        try
        {
            var inserted = DiagramEditing.InsertPoint(spec, near.InsertIndex, near.At);
            Edit("선 점 추가", () => VectorShapes.Update(layer, inserted));
            selectedShapePoint = (layer.Id, near.InsertIndex); UpdateDiagramOverlay();
        }
        catch (InvalidOperationException error) { status.Text = error.Message; }
        return true;
    }

    void DeleteShapePoint(Layer layer, int index)
    {
        try
        {
            var removed = DiagramEditing.DeletePoint(layer.Shape!, index);
            selectedShapePoint = null;
            Edit("선 점 삭제", () => VectorShapes.Update(layer, removed));
        }
        catch (InvalidOperationException error) { status.Text = error.Message; }
    }

    // Enter / Backspace / Esc while drawing; Delete or Backspace removes the selected point.
    bool DiagramKey(Key key)
    {
        if (tool == Tool.Line && lineDrawing)
        {
            if (key == Key.Enter) { FinishLine(false); return true; }
            if (key == Key.Back)
            {
                if (lineDraft.Count > 1) { lineDraft.RemoveAt(lineDraft.Count - 1); UpdateDiagramOverlay(); }
                else { ResetDiagramTransient(); UpdateDiagramOverlay(); }
                return true;
            }
        }
        if (key is Key.Delete or Key.Back && !dragging && selectedShapePoint is { } chosen && EditablePathLayer() is { } layer && layer.Id == chosen.Layer && layer.Shape!.Kind == ShapeKind.Line
            && chosen.Index < layer.Shape.Points!.Count)
        { DeleteShapePoint(layer, chosen.Index); return true; }
        return false;
    }

    Cursor? DiagramCursor(Point documentPoint)
    {
        if (pointDrag != null) return Cursors.SizeAll;
        if (tool == Tool.Line && lineDrawing) return CanCloseDraft(documentPoint) ? Cursors.Hand : Cursors.Cross;
        if (!dragging && EditablePathLayer() is { } layer && HitShapeHandle(layer, documentPoint) >= 0)
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && layer.Shape!.Kind == ShapeKind.Line ? Cursors.No : Cursors.SizeAll;
        return DiagramTool(tool) ? Cursors.Cross : null;
    }

    string? DiagramHint() => tool switch
    {
        Tool.Line => lineCurve
            ? "곡선: 클릭한 점을 모두 지나는 매끄러운 선 · 더블클릭 / Enter: 완성 · 첫 점 클릭: 닫기 · Backspace: 마지막 점 삭제 · Esc: 취소"
            : "클릭: 점 추가 · 드래그: 직선 하나 · 더블클릭 / Enter: 완성 · 첫 점 클릭: 닫기 · Shift: 15° 단위 · Backspace: 마지막 점 삭제",
        Tool.Callout => "대상에서 라벨 쪽으로 드래그 · 클릭: 기본 위치에 라벨 · 손잡이로 대상·꺾임·라벨을 따로 옮기기",
        _ => null
    };

    // Shift+P: the line tool in curve mode. P: straight segments.
    void SelectLineTool(bool curve)
    {
        lineCurve = curve; SetTool(Tool.Line); SyncDiagramOptions();
    }
}
