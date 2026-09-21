using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    Guid cloneAnchorLayer;
    Document? cloneAnchorDocument;
    Point? cloneAnchorLocal;

    void ResetInteractionTransient()
    {
        polygonInProgress = false; lassoPoints.Clear(); canvas.GesturePoints = null;
        canvas.GestureBounds = null; cloneSnapshot = null; moveStarted = false;
    }

    void ChangeInteractionTool(Tool next)
    {
        CancelGesture(); ResetInteractionTransient(); tool = next;
        foreach (var pair in toolButtons)
        {
            pair.Value.Background = pair.Key == tool ? Theme.Brush("#31564A") : Theme.Panel;
            pair.Value.BorderBrush = pair.Key == tool ? Theme.Accent : Theme.Panel;
        }
        canvas.ShowLayerBounds = tool == Tool.Move;
        canvas.Cursor = tool == Tool.Hand ? Cursors.Hand : tool == Tool.Move ? Cursors.SizeAll : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        canvas.BrushPoint = null; canvas.BrushRadius = brushSize / 2;
        autoSelectToggle.IsEnabled = tool == Tool.Move; Refresh(false); ShowInteractionHint();
    }

    void ShowInteractionHint()
    {
        string? hint = tool switch
        {
            Tool.Lasso => "드래그: 올가미 · Shift: 선택 추가 · Alt: 빼기 · Shift+Alt: 교차",
            Tool.PolygonLasso => "클릭: 꼭짓점 · 더블클릭 / Enter: 완성 · Backspace: 마지막 점 삭제 · Esc: 취소",
            Tool.MagicWand => $"마술봉 오차 {wandTolerance:0} · 클릭: 연결된 색 · Ctrl+클릭: 전체 같은 색 · Shift/Alt: 추가/빼기 · 더블클릭: 오차 설정",
            Tool.CloneStamp => "Alt+클릭: 복제할 원본 위치 · 드래그: 복제 도장 · [ ]: 크기",
            Tool.Heal => "Alt+클릭: 참조 위치 · 드래그: 주변 색에 맞춰 질감 복구 · [ ]: 크기",
            Tool.Smudge => "드래그: 픽셀을 문질러 이동 · 농도: 강도 · [ ]: 크기",
            Tool.Liquify => "드래그: 국소 변형 · 농도: 강도 · [ ]: 크기",
            Tool.BlurBrush => "드래그: 선택 영역 안에서 국소 흐림 · [ ]: 크기",
            Tool.Text => "텍스트 클릭: 내용·서식 편집 · 빈 곳 클릭: 새 텍스트 · Alt+클릭: 항상 새 텍스트",
            _ => null
        };
        if (hint != null) status.Text = hint + "    |    Space+드래그: 화면 이동";
    }

    void EditTextAt(Point point)
    {
        var picked = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? null : LayerPicking.Pick(doc, point);
        if (picked?.Kind == LayerKind.Text)
        {
            if (IsLockedWithParents(picked)) { status.Text = "잠긴 텍스트 레이어입니다."; return; }
            SelectLayer(picked.Id); EditTextLayer(picked);
        }
        else { maskEditing = false; EditTextLayer(null, point); }
    }

    static SelectionCombine CurrentSelectionMode()
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        return shift && alt ? SelectionCombine.Intersect : shift ? SelectionCombine.Add : alt ? SelectionCombine.Subtract : SelectionCombine.Replace;
    }

    void InteractionDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            canvas.Focus(); var screen = e.GetPosition(canvas); var point = canvas.ToDocument(screen);
            if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && (tool == Tool.Hand || Keyboard.IsKeyDown(Key.Space)))
            { panning = true; screenStart = screen; initialPan = canvas.Pan; canvas.CaptureMouse(); e.Handled = true; return; }
            if (e.ChangedButton == MouseButton.Right && tool == Tool.PolygonLasso && polygonInProgress)
            { FinishPolygon(); e.Handled = true; return; }
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            if (tool == Tool.MagicWand && e.ClickCount > 1) { ConfigureWand(); return; }
            if (jobCts != null) { status.Text = "처리 중입니다. Esc로 취소한 뒤 편집하세요."; return; }
            if (tool == Tool.Move && TryBeginTransformHandle(point, screen)) return;
            bool inside = point.X >= 0 && point.Y >= 0 && point.X < doc.Width && point.Y < doc.Height;
            if (!inside)
            {
                if (tool == Tool.Move && autoSelectToggle.IsChecked == true) SelectLayer(Guid.Empty);
                return;
            }
            if (tool == Tool.Eyedropper) { PickForeground(point); return; }
            if (tool == Tool.Text) { EditTextAt(point); return; }
            if (tool == Tool.PolygonLasso) { AddPolygonVertex(point, e.ClickCount > 1); return; }
            if (tool == Tool.MagicWand)
            {
                BeginWandSelection(point, !Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                return;
            }
            if (tool == Tool.Move && autoSelectToggle.IsChecked == true)
            {
                var picked = LayerPicking.Pick(doc, point);
                if (picked == null) { SelectLayer(Guid.Empty); return; }
                // Preserve a multi-selection when dragging one of its members.
                if (selectedLayers.Contains(picked.Id) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                { doc.ActiveId = picked.Id; maskEditing = false; Refresh(false); }
                else SelectLayer(picked.Id);
            }
            bool pixelTool = tool is Tool.Brush or Tool.Eraser || IsRetouch(tool);
            if ((pixelTool || tool == Tool.Move) && (doc.Active == null || IsLockedWithParents(doc.Active)))
            { status.Text = "편집할 레이어를 선택하거나 레이어·그룹 잠금을 해제하세요."; return; }
            if (pixelTool && !CanPaintActiveLayer()) return;
            if (tool is Tool.CloneStamp or Tool.Heal)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    var local = doc.Active!.Local(point);
                    if (local.X < 0 || local.Y < 0 || local.X >= doc.Active.Pixels.Width || local.Y >= doc.Active.Pixels.Height)
                    { status.Text = "선택한 레이어의 이미지 안에서 원본 위치를 지정하세요."; return; }
                    cloneAnchorLayer = doc.Active.Id; cloneAnchorDocument = doc; cloneAnchorLocal = local; cloneSource = point;
                    status.Text = $"참조 위치 지정됨 ({point.X:0}, {point.Y:0}) · 드래그하여 적용"; return;
                }
                if (cloneAnchorLayer != doc.Active!.Id || !ReferenceEquals(cloneAnchorDocument, doc) || cloneAnchorLocal == null)
                { status.Text = "먼저 선택한 레이어 위에서 Alt+클릭으로 참조 위치를 지정하세요."; return; }
                cloneSource = doc.Active.Document(cloneAnchorLocal.Value);
            }
            beforeGesture = doc.Snapshot(); start = point; screenStart = screen; dragging = true; moveStarted = false;
            selectionMode = CurrentSelectionMode(); canvas.CaptureMouse();
            if (tool is Tool.Brush or Tool.Eraser)
            {
                if (!maskEditing) DocumentFeatures.Rasterize(doc.Active!);
                stroke = new BrushStroke(doc.Active!, selection, foreground, brushSize, hardness, brushOpacity, tool == Tool.Eraser, maskEditing);
                stroke.Point(point); RenderGesture();
            }
            else if (IsRetouch(tool))
            {
                DocumentFeatures.Rasterize(doc.Active!); cloneSnapshot = doc.Active!.Pixels; lastRetouch = point;
                if (tool is Tool.CloneStamp or Tool.Heal or Tool.BlurBrush) RetouchAt(point);
                RenderGesture();
            }
            else if (tool == Tool.Lasso)
            { lassoPoints.Clear(); lassoPoints.Add(point); canvas.GesturePoints = lassoPoints.ToArray(); canvas.InvalidateVisual(); }
            else if (tool != Tool.Move)
            { canvas.GestureBounds = new Rect(point, point); canvas.EllipseGesture = tool is Tool.Ellipse or Tool.EllipseSelect; canvas.InvalidateVisual(); }
        }
        catch (Exception error) { CancelGesture(); status.Text = "도구를 시작하지 못했습니다: " + error.Message; }
    }

    void InteractionMove(object sender, MouseEventArgs e)
    {
        try
        {
            var screen = e.GetPosition(canvas); var point = canvas.ToDocument(screen);
            canvas.BrushPoint = tool is Tool.Brush or Tool.Eraser || IsRetouch(tool) ? point : null;
            canvas.BrushRadius = brushSize / 2;
            if (panning) { canvas.Pan = initialPan + (screen - screenStart); canvas.InvalidateVisual(); return; }
            if (polygonInProgress && tool == Tool.PolygonLasso)
            { canvas.GesturePoints = lassoPoints.Append(ClampToCanvas(point)).ToArray(); canvas.InvalidateVisual(); return; }
            if (!dragging) { canvas.InvalidateVisual(); return; }
            if (MoveTransformHandle(point)) return;
            if (stroke != null) { stroke.Point(point); RenderGesture(); return; }
            if (IsRetouch(tool)) { ContinueRetouch(point); return; }
            if (tool == Tool.Move) { ContinueMove(point, screen); return; }
            point = ClampToCanvas(point);
            if (tool == Tool.Lasso)
            {
                if (lassoPoints.Count == 0 || (point - lassoPoints[^1]).Length * canvas.Zoom >= 1.5) lassoPoints.Add(point);
                canvas.GesturePoints = lassoPoints.ToArray(); canvas.InvalidateVisual(); return;
            }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && tool is Tool.Rectangle or Tool.Ellipse)
            {
                double length = Math.Max(Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y));
                point = ClampToCanvas(new Point(start.X + Math.Sign(point.X - start.X) * length, start.Y + Math.Sign(point.Y - start.Y) * length));
            }
            canvas.GestureBounds = Between(start, point); canvas.EllipseGesture = tool is Tool.Ellipse or Tool.EllipseSelect; canvas.InvalidateVisual();
        }
        catch (Exception error) { CancelGesture(); status.Text = "편집을 취소했습니다: " + error.Message; }
    }

    void InteractionUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (panning) { panning = false; canvas.ReleaseMouseCapture(); e.Handled = true; return; }
            if (e.ChangedButton != MouseButton.Left || !dragging) return;
            e.Handled = true;
            MoveTransformHandle(canvas.ToDocument(e.GetPosition(canvas)));
            if (EndTransformHandle()) return;
            var point = canvas.ToDocument(e.GetPosition(canvas));
            if (stroke != null) stroke.Point(point);
            else if (IsRetouch(tool)) ContinueRetouch(point);
            else if (tool == Tool.Move) ContinueMove(point, e.GetPosition(canvas));
            var bounds = canvas.GestureBounds;
            dragging = false; canvas.ReleaseMouseCapture(); canvas.GestureBounds = null; canvas.GesturePoints = null;
            if (stroke != null || IsRetouch(tool) || tool == Tool.Move)
            {
                if (beforeGesture != null)
                {
                    // Merely touching an empty area must not rasterize editable
                    // text or erase redo history when no pixel actually changed.
                    if (tool != Tool.Move && beforeGesture.Active is { } original && doc.Active is { } current &&
                        original.Pixels.Data.AsSpan().SequenceEqual(current.Pixels.Data) &&
                        (ReferenceEquals(original.Mask, current.Mask) || original.Mask != null && current.Mask != null && original.Mask.AsSpan().SequenceEqual(current.Mask)))
                    { doc.Layers[doc.Layers.IndexOf(current)] = original.Snapshot(); }
                    doc.Validate(); history.Commit(tool == Tool.Move ? "레이어 이동" : ToolLabel(tool), beforeGesture, doc);
                }
                beforeGesture = null; stroke = null; cloneSnapshot = null; Refresh(); ShowInteractionHint(); return;
            }
            beforeGesture = null;
            if (tool == Tool.Lasso)
            {
                lassoPoints.Add(ClampToCanvas(point));
                if (lassoPoints.Count >= 3) ApplySelection(SelectionTools.Polygon(doc.Width, doc.Height, lassoPoints));
                lassoPoints.Clear(); canvas.InvalidateVisual(); return;
            }
            point = ClampToCanvas(point);
            if (tool == Tool.Gradient)
            {
                if ((point - start).Length >= 1) AddGradient(start, point); else canvas.InvalidateVisual(); return;
            }
            if (bounds == null) bounds = Between(start, point);
            if (bounds.Value.Width < 1 || bounds.Value.Height < 1)
            {
                if (tool is Tool.RectangleSelect or Tool.EllipseSelect && selectionMode == SelectionCombine.Replace) { selection = null; Refresh(false); }
                canvas.InvalidateVisual(); return;
            }
            if (tool is Tool.RectangleSelect or Tool.EllipseSelect)
                ApplySelection(new Selection(bounds.Value, tool == Tool.EllipseSelect));
            else if (tool == Tool.Crop) { selection = new Selection(bounds.Value); CropSelection(); }
            else if (tool is Tool.Rectangle or Tool.Ellipse) AddShape(bounds.Value, tool == Tool.Ellipse);
        }
        catch (Exception error)
        {
            if (beforeGesture != null) doc = beforeGesture;
            beforeGesture = null; stroke = null; dragging = false; canvas.ReleaseMouseCapture(); ResetInteractionTransient(); Refresh(); status.Text = "편집을 취소했습니다: " + error.Message;
        }
    }

    void ContinueRetouch(Point point)
    {
        if (doc.Active is not { } layer || beforeGesture?.Active == null) return;
        var from = lastRetouch; var delta = point - from;
        if (delta.Length * canvas.Zoom < .25) return;
        // One immutable full-size result per pointer event; intermediate dabs
        // reuse that working raster and allocate only local footprint patches.
        int count = Math.Clamp((int)Math.Ceiling(delta.Length / Math.Max(1, brushSize * .24)), 1, 64);
        var samples = Enumerable.Range(1, count).Select(i => from + delta * (i / (double)count)).ToArray();
        var kind = tool switch { Tool.CloneStamp => RetouchKind.Clone, Tool.Heal => RetouchKind.Heal, Tool.Smudge => RetouchKind.Smudge, Tool.Liquify => RetouchKind.Liquify, _ => RetouchKind.Blur };
        layer.Pixels = RetouchTools.ApplyStroke(layer, kind, cloneSnapshot ?? beforeGesture.Active.Pixels, cloneSource, start, from, samples, brushSize / 2, hardness, brushOpacity, selection);
        lastRetouch = point;
        RenderGesture();
    }

    void ContinueMove(Point point, Point screen)
    {
        if (doc.Active == null || beforeGesture?.Active == null) return;
        var screenDelta = screen - screenStart;
        if (!moveStarted && Math.Abs(screenDelta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(screenDelta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        moveStarted = true; var delta = point - start;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) delta.Y = 0; else delta.X = 0; }
        var active = beforeGesture.Active;
        if (!HasTransformedParent(active))
        { delta.X = Snap(active.X + delta.X, true) - active.X; delta.Y = Snap(active.Y + delta.Y, false) - active.Y; }
        foreach (var current in MovableSelectedLayers())
        {
            var original = beforeGesture.Layers.Single(l => l.Id == current.Id);
            var localDelta = ParentPoint(beforeGesture, original, start + delta) - ParentPoint(beforeGesture, original, start);
            current.X = Math.Clamp(original.X + localDelta.X, -100_000, 100_000); current.Y = Math.Clamp(original.Y + localDelta.Y, -100_000, 100_000);
        }
        RenderGesture();
    }

    IEnumerable<Layer> MovableSelectedLayers()
    {
        var ids = selectedLayers.Contains(doc.ActiveId) ? selectedLayers : new HashSet<Guid> { doc.ActiveId };
        return doc.Layers.Where(l => ids.Contains(l.Id) && !IsLockedWithParents(l) && !Parents(doc, l).Any(parent => ids.Contains(parent.Id))).ToArray();
    }
    static IEnumerable<Layer> Parents(Document document, Layer layer)
    {
        var parent = layer.ParentId; var seen = new HashSet<Guid>();
        while (parent is { } id && seen.Add(id)) { var group = document.Layers.Find(l => l.Id == id); if (group == null) yield break; yield return group; parent = group.ParentId; }
    }
    static Point ParentPoint(Document document, Layer layer, Point point)
    { foreach (var group in Parents(document, layer).Reverse()) point = group.Local(point); return point; }
    bool IsLockedWithParents(Layer layer) => layer.Locked || Parents(doc, layer).Any(parent => parent.Locked);
    bool HasTransformedParent(Layer layer) => Parents(doc, layer).Any(parent => parent.Warp != null || !parent.Matrix.IsIdentity);
    bool CanPaintActiveLayer()
    {
        if (doc.Active is not { } layer) return false;
        if (HasTransformedParent(layer)) { status.Text = "변형된 그룹 안에서는 직접 픽셀 편집이 제한됩니다. 레이어를 최상위로 옮긴 뒤 편집하세요."; return false; }
        if (maskEditing)
        {
            if (layer.Mask == null) { maskEditing = false; }
            else if (IsRetouch(tool)) { status.Text = "복구 도구는 이미지에서 사용합니다. 먼저 이미지 편집으로 전환하세요."; return false; }
            else return true;
        }
        if (layer.Kind is LayerKind.Group or LayerKind.Adjustment) { status.Text = "픽셀 또는 텍스트 레이어를 선택하세요. 그룹·조정 레이어는 마스크 브러시만 지원합니다."; return false; }
        if (!layer.Visible || Parents(doc, layer).Any(parent => !parent.Visible)) { status.Text = "숨겨진 레이어입니다. 레이어와 부모 그룹을 표시한 뒤 편집하세요."; return false; }
        return true;
    }

    void AddPolygonVertex(Point point, bool complete)
    {
        if (!polygonInProgress) { selectionMode = CurrentSelectionMode(); lassoPoints.Clear(); polygonInProgress = true; }
        if (lassoPoints.Count >= 3 && (complete || (point - lassoPoints[0]).Length * canvas.Zoom <= 7)) { FinishPolygon(); return; }
        if (lassoPoints.Count == 0 || (point - lassoPoints[^1]).Length > .1) lassoPoints.Add(point);
        if (complete && lassoPoints.Count >= 3) { FinishPolygon(); return; }
        canvas.GesturePoints = lassoPoints.ToArray(); canvas.InvalidateVisual(); ShowInteractionHint();
    }
    void ConfigureWand()
    {
        jobCts?.Cancel(); var fields = Dialogs.Fields(this, "마술봉 허용 오차", ("색상 차이 (0~255)", wandTolerance.ToString("0")));
        if (fields != null) wandTolerance = Dialogs.Number(fields[0], 0, 255); ShowInteractionHint();
    }
    async void BeginWandSelection(Point point, bool contiguous)
    {
        var document = doc; var revision = doc.Revision; var previous = selection; var mode = CurrentSelectionMode();
        jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource(); var snapshot = doc.Snapshot(); double tolerance = wandTolerance;
        status.Text = "마술봉 계산 중… Esc: 취소";
        try
        {
            var result = await Task.Run(() => SelectionTools.MagicWand(Imaging.Render(snapshot), point, tolerance, contiguous, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(document, doc) || revision != doc.Revision || !ReferenceEquals(selection, previous)) return;
            selection = SelectionTools.Combine(previous, result, doc.Width, doc.Height, mode); Refresh(false); ShowInteractionHint();
        }
        catch (OperationCanceledException) { status.Text = "선택 계산을 취소했습니다."; }
        catch (Exception error) { status.Text = "마술봉 선택 실패: " + error.Message; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
    Point ClampToCanvas(Point point) => new(Math.Clamp(point.X, 0, doc.Width), Math.Clamp(point.Y, 0, doc.Height));
    void PickForeground(Point point)
    {
        var raster = composite;
        if (raster == null || raster.Width != doc.Width || raster.Height != doc.Height) raster = Imaging.Render(doc);
        int i = ((int)point.Y * raster.Width + (int)point.X) * 4;
        foreground = Color.FromArgb(raster.Data[i + 3], raster.Data[i + 2], raster.Data[i + 1], raster.Data[i]); UpdateColor();
    }
    static string ToolLabel(Tool selected) => selected switch
    {
        Tool.Eraser => "지우개", Tool.CloneStamp => "복제 도장", Tool.Heal => "복구 브러시", Tool.Smudge => "스머지", Tool.Liquify => "액화", Tool.BlurBrush => "흐림 브러시", _ => "브러시"
    };
    void AddShape(Rect bounds, bool ellipse)
    {
        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var pixels = Imaging.Draw(w, h, dc =>
        {
            var brush = new SolidColorBrush(foreground);
            if (ellipse) dc.DrawEllipse(brush, null, new Point(w / 2.0, h / 2.0), w / 2.0, h / 2.0); else dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        });
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels.Data[(y * w + x) * 4 + 3] = Imaging.Byte(pixels.Data[(y * w + x) * 4 + 3] * brushOpacity * (selection?.Weight(bounds.X + x + .5, bounds.Y + y + .5) ?? 1));
        Edit("도형 추가", () => { doc.Add(new Layer { Name = ellipse ? "타원" : "사각형", Pixels = pixels, X = bounds.X, Y = bounds.Y }); maskEditing = false; });
    }
    void AddGradient(Point from, Point to)
    {
        Edit("그라데이션", () =>
        {
            var pixels = new Raster(doc.Width, doc.Height); var delta = to - from; double length = delta.LengthSquared;
            for (int y = 0; y < doc.Height; y++) for (int x = 0; x < doc.Width; x++)
            {
                double weight = selection?.Weight(x + .5, y + .5) ?? 1; if (weight <= 0) continue;
                double t = Math.Clamp(Vector.Multiply(new Point(x + .5, y + .5) - from, delta) / length, 0, 1); int i = (y * doc.Width + x) * 4;
                pixels.Data[i] = foreground.B; pixels.Data[i + 1] = foreground.G; pixels.Data[i + 2] = foreground.R; pixels.Data[i + 3] = Imaging.Byte((1 - t) * foreground.A * brushOpacity * weight);
            }
            doc.Add(new Layer { Name = "그라데이션", Pixels = pixels }); maskEditing = false;
        });
    }

    void InteractionKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && (jobCts != null || dragging || panning || polygonInProgress))
        { jobCts?.Cancel(); CancelGesture(); ResetInteractionTransient(); Refresh(false); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or System.Windows.Controls.Slider) return;
        if (polygonInProgress && key is Key.Enter or Key.Back)
        {
            if (key == Key.Enter) Guard(FinishPolygon);
            else if (lassoPoints.Count > 0) { lassoPoints.RemoveAt(lassoPoints.Count - 1); canvas.GesturePoints = lassoPoints.ToArray(); canvas.InvalidateVisual(); }
            e.Handled = true; return;
        }
        Action? action;
        if (ctrl) action = key switch
        {
            Key.N => NewDocument, Key.O => shift ? Import : Open, Key.S => () => Save(shift), Key.W => CloseTab,
            Key.E => shift ? Export : MergeDown, Key.Z => shift ? Redo : Undo, Key.Y => Redo, Key.J => Duplicate,
            Key.T => Transform, Key.L => Levels, Key.U => () => ShowAdjustment(AdjustmentKind.HueSaturation),
            Key.G => alt ? ToggleClipping : shift ? UngroupSelected : GroupSelected,
            Key.I => shift ? InvertSelection : () => Adjust("invert"), Key.D => () => { selection = null; Refresh(false); },
            Key.A => () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); },
            Key.C => CopyMerged, Key.V => Paste,
            Key.D0 or Key.NumPad0 => () => { canvas.Fit(); UpdateStatus(); },
            Key.D1 or Key.NumPad1 => () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); },
            Key.Tab => () => SwitchTab((activeTab + (shift ? tabs.Count - 1 : 1)) % tabs.Count), _ => null
        };
        else if (alt && key == Key.Back) action = Fill;
        else action = key switch
        {
            Key.V => () => SetTool(Tool.Move), Key.B => () => SetTool(Tool.Brush), Key.E => () => SetTool(Tool.Eraser),
            Key.M => () => SetTool(shift ? Tool.EllipseSelect : Tool.RectangleSelect), Key.C => () => SetTool(Tool.Crop),
            Key.U => () => SetTool(shift ? Tool.Ellipse : Tool.Rectangle), Key.G => () => SetTool(Tool.Gradient),
            Key.T => () => SetTool(Tool.Text), Key.I => () => SetTool(Tool.Eyedropper), Key.H => () => SetTool(Tool.Hand),
            Key.L => () => SetTool(shift ? Tool.PolygonLasso : Tool.Lasso), Key.W => () => SetTool(Tool.MagicWand),
            Key.S => () => SetTool(Tool.CloneStamp), Key.J => () => SetTool(Tool.Heal), Key.R => () => SetTool(shift ? Tool.Liquify : Tool.Smudge), Key.K => () => SetTool(Tool.BlurBrush),
            Key.F5 when shift => ContentFill, Key.Delete => ClearPixels,
            Key.Escape => () => { CancelGesture(); ResetInteractionTransient(); selection = null; Refresh(false); },
            Key.OemOpenBrackets => () => { brushSize = Math.Max(1, brushSize - 5); UpdateBrushLabel(); canvas.BrushRadius = brushSize / 2; canvas.InvalidateVisual(); },
            Key.OemCloseBrackets => () => { brushSize = Math.Min(300, brushSize + 5); UpdateBrushLabel(); canvas.BrushRadius = brushSize / 2; canvas.InvalidateVisual(); },
            Key.D => () => { foreground = Colors.Black; UpdateColor(); }, Key.X => () => { foreground = foreground == Colors.Black ? Colors.White : Colors.Black; UpdateColor(); },
            Key.Left => () => NudgeSelected(new Vector(shift ? -10 : -1, 0)), Key.Right => () => NudgeSelected(new Vector(shift ? 10 : 1, 0)),
            Key.Up => () => NudgeSelected(new Vector(0, shift ? -10 : -1)), Key.Down => () => NudgeSelected(new Vector(0, shift ? 10 : 1)),
            Key.F1 => Help, _ => null
        };
        if (action != null) { e.Handled = true; Guard(action); }
    }
    void NudgeSelected(Vector delta)
    {
        if (doc.Active == null) return;
        Edit("레이어 이동", () =>
        {
            foreach (var layer in MovableSelectedLayers())
            {
                var local = ParentPoint(doc, layer, new Point(delta.X, delta.Y)) - ParentPoint(doc, layer, new Point(0, 0));
                layer.X = Math.Clamp(layer.X + local.X, -100_000, 100_000); layer.Y = Math.Clamp(layer.Y + local.Y, -100_000, 100_000);
            }
        });
    }
}
