using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Properties of a selected line or callout (and the dash row of rectangles and ellipses). Every
// change is one undo step; switches and choices apply at once, numbers with Enter or on leaving.
public sealed partial class MainWindow
{
    void AddDiagramProperties(Layer layer, ShapeSpec shape, StackPanel content)
    {
        var boundDocument = doc; long version = inspectorVersion; bool locked = IsLockedWithParents(layer);
        bool Current() => ReferenceEquals(doc, boundDocument) && inspectorVersion == version && doc.ActiveId == layer.Id && !IsLockedWithParents(layer);
        void Change(string label, Func<ShapeSpec, ShapeSpec> change)
        {
            if (!Current()) return;
            EditLayer(label, l => { if (l.Shape is { HasPoints: true } s) VectorShapes.Update(l, change(s)); });
        }
        T Enabled<T>(T element) where T : UIElement { element.IsEnabled = !locked; return element; }
        bool callout = shape.Kind == ShapeKind.Callout;

        content.Children.Add(Theme.Section(callout ? "지시선" : "선 · 곡선"));
        if (callout)
        {
            var leader = Enabled(new SegmentedChoice<CalloutLeader>([(CalloutLeader.Elbow, "꺾은 지시선"), (CalloutLeader.Straight, "곧은 지시선")], shape.Leader) { Margin = new Thickness(2, 0, 2, 8) });
            System.Windows.Automation.AutomationProperties.SetName(leader, "지시선 모양");
            leader.Changed += value => Change("지시선 모양", s =>
            {
                var points = s.Points!;
                // A new bend starts level with the label, straight up or down from the target.
                return value == CalloutLeader.Elbow
                    ? s with { Leader = value, Points = points.With(DiagramEditing.Elbow, DiagramEditing.DefaultElbow(points[DiagramEditing.Anchor], points[DiagramEditing.LabelPoint])) }
                    : s with { Leader = value };
            });
            content.Children.Add(leader);
        }
        else
        {
            var mode = Enabled(new SegmentedChoice<bool>([(false, "직선"), (true, "매끄러운 곡선")], shape.Smooth) { Margin = new Thickness(2, 0, 2, 8), ToolTip = "곡선: 모든 점을 지나는 매끄러운 선" });
            System.Windows.Automation.AutomationProperties.SetName(mode, "선 종류");
            mode.Changed += smooth => Change(smooth ? "곡선으로 바꾸기" : "직선으로 바꾸기", s => s with { Smooth = smooth });
            content.Children.Add(mode);
        }

        // Stroke: switch and colour on one row, then width and the dash pattern.
        var strokeRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        strokeRow.ColumnDefinitions.Add(new ColumnDefinition()); strokeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120 });
        var strokeToggle = Enabled(new CheckBox { Content = "선", IsChecked = shape.StrokeEnabled, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center });
        strokeToggle.Click += (_, _) => Change("선", s => s with { StrokeEnabled = strokeToggle.IsChecked == true });
        strokeRow.Children.Add(strokeToggle);
        var strokeChip = Enabled(PropertyRows.ColorChip(VectorShapes.Color(shape.StrokeArgb), $"#{shape.StrokeArgb & 0xFFFFFF:X6}", () =>
        {
            if (!Current()) return;
            if (Dialogs.ColorPicker(this, VectorShapes.Color(shape.StrokeArgb), "선 색상") is { } color) Change("선 색상", s => s with { StrokeArgb = VectorShapes.Argb(color) });
        }, "선 색상 변경"));
        strokeChip.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(strokeChip, 1); strokeRow.Children.Add(strokeChip);
        content.Children.Add(strokeRow);
        content.Children.Add(TransformRow(layer,
            ("선 두께 · px", shape.StrokeWidth, 0, 512, "선 두께", (l, v) => { if (l.Shape is { } s) VectorShapes.Update(l, s with { StrokeWidth = v }); }),
            ("끝 모양 크기 · px", shape.MarkSize, 1, ShapeSpec.MaxMarkSize, "끝 모양 크기", (l, v) => { if (l.Shape is { } s) VectorShapes.Update(l, s with { MarkSize = v }); })));
        AddDashRows(layer, shape, content, Change, Enabled);

        // Line ends: at the first and last point, or at the target and the label.
        var startMark = Enabled(ChoiceBox(MarkChoices, shape.StartMark, callout ? "대상 쪽 끝 모양" : "시작점 모양", mark => Change("끝 모양", s => s with { StartMark = mark })));
        var endMark = Enabled(ChoiceBox(MarkChoices, shape.EndMark, callout ? "라벨 쪽 끝 모양" : "끝점 모양", mark => Change("끝 모양", s => s with { EndMark = mark })));
        bool closed = shape.Kind == ShapeKind.Line && shape.Closed;
        startMark.IsEnabled = endMark.IsEnabled = !locked && !closed;
        content.Children.Add(PropertyRows.Pair(callout ? "대상 쪽 끝" : "시작점", startMark, callout ? "라벨 쪽 끝" : "끝점", endMark));
        if (closed) content.Children[^1].SetValue(ToolTipProperty, "닫힌 선에는 끝 모양이 없습니다.");

        if (callout) { AddEditingNote(content, "이동 도구에서 원은 대상, 마름모는 꺾임, 사각형은 라벨 위치입니다. 손잡이를 끌어 따로 옮깁니다."); AddCalloutLabelProperties(layer, shape, content, Current, Change, Enabled); }
        else
        {
            // Closing joins the last point to the first; the fill paints inside a closed line.
            var closeRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
            closeRow.ColumnDefinitions.Add(new ColumnDefinition()); closeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120 });
            var closeToggle = Enabled(new CheckBox { Content = "닫힌 선 · 채우기", IsChecked = shape.Closed && shape.FillEnabled, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "마지막 점을 첫 점과 잇고 안쪽을 채웁니다 · 재료 맵핑의 경계로도 쓸 수 있습니다" });
            closeToggle.IsEnabled = !locked && (shape.Points!.Count >= 3);
            closeToggle.Click += (_, _) => Change("닫힌 선", s => s with { Closed = closeToggle.IsChecked == true, FillEnabled = closeToggle.IsChecked == true });
            closeRow.Children.Add(closeToggle);
            var fillChip = Enabled(PropertyRows.ColorChip(VectorShapes.Color(shape.FillArgb), $"#{shape.FillArgb & 0xFFFFFF:X6}", () =>
            {
                if (!Current()) return;
                if (Dialogs.ColorPicker(this, VectorShapes.Color(shape.FillArgb), "채우기 색상") is { } color) Change("채우기 색상", s => s with { FillArgb = VectorShapes.Argb(color), Closed = true, FillEnabled = true });
            }, "채우기 색상 변경"));
            fillChip.IsEnabled = closeToggle.IsEnabled; fillChip.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(fillChip, 1); closeRow.Children.Add(fillChip);
            content.Children.Add(closeRow);
            var reverse = InspectorCommand(Theme.Glyphs.Swap, "방향 바꾸기", "선 방향 바꾸기", () => Change("선 방향 바꾸기", DiagramEditing.Reverse), "시작과 끝을 바꿉니다. 화살표가 반대쪽을 가리킵니다.", layer);
            reverse.IsEnabled = !locked && !closed;
            content.Children.Add(QuickActions.Grid(2, [reverse], QuickActions.CommandWidth));
            AddEditingNote(content, $"점 {shape.Points!.Count}개 · 손잡이를 끌어 옮기고, 이동 도구로 선을 두 번 클릭해 점을 더하고, Alt+클릭 또는 Delete로 지웁니다.");
        }
    }

    static void AddEditingNote(StackPanel content, string text)
    {
        var note = Theme.Label(text, Theme.CaptionSize, Theme.Subtle);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(2, 0, 2, 10);
        content.Children.Add(Density.Mark(note, DensityRole.Description));
    }

    // 선 모양 and its spacing; shared by lines, callouts, rectangles and ellipses.
    void AddDashRows(Layer layer, ShapeSpec shape, StackPanel content, Action<string, Func<ShapeSpec, ShapeSpec>> change, Func<UIElement, UIElement> enabled)
    {
        var dash = (SegmentedChoice<StrokeDash>)enabled(new SegmentedChoice<StrokeDash>(DashChoices, shape.Dash) { Margin = new Thickness(0) });
        System.Windows.Automation.AutomationProperties.SetName(dash, "선 모양");
        dash.ToolTip = "실선 · 점선 · 파선 · 일점쇄선 · 간격은 선 두께를 따릅니다";
        dash.Changed += value => change("선 모양", s => s with { Dash = value });
        content.Children.Add(PropertyRows.Field("선 모양", dash));
        var cap = (ComboBox)enabled(ChoiceBox(CapChoices, shape.Cap, "선 끝", value => change("선 끝", s => s with { Cap = value })));
        cap.ToolTip = "둥근 끝: 점선이 둥근 점 · 각진 끝: 네모 점 · 평평한 끝: 선이 점에서 딱 끝남";
        var scale = InspectorNumberBox(layer, shape.DashScale * 100, ShapeSpec.MinDashScale * 100, ShapeSpec.MaxDashScale * 100, "점선 간격", (l, v) => { if (l.Shape is { } s) VectorShapes.Update(l, s with { DashScale = v / 100 }); });
        scale.IsEnabled = !IsLockedWithParents(layer) && shape.Dash != StrokeDash.Solid;
        content.Children.Add(PropertyRows.Pair("점선 간격 · %", scale, "선 끝", cap));
    }

    void AddCalloutLabelProperties(Layer layer, ShapeSpec shape, StackPanel content, Func<bool> current, Action<string, Func<ShapeSpec, ShapeSpec>> change, Func<UIElement, UIElement> enabled)
    {
        // The label box (라벨 바탕) is the callout's fill: a padded, rounded box behind the text.
        var boxRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        boxRow.ColumnDefinitions.Add(new ColumnDefinition()); boxRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120 });
        var boxToggle = (CheckBox)enabled(new CheckBox { Content = "라벨 바탕", IsChecked = shape.FillEnabled, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, ToolTip = "글자 뒤에 둥근 상자를 깔아 복잡한 그림 위에서도 읽히게 합니다" });
        boxToggle.Click += (_, _) => change("라벨 바탕", s => s with { FillEnabled = boxToggle.IsChecked == true });
        boxRow.Children.Add(boxToggle);
        var boxChip = (Button)enabled(PropertyRows.ColorChip(VectorShapes.Color(shape.FillArgb), $"#{shape.FillArgb & 0xFFFFFF:X6}", () =>
        {
            if (!current()) return;
            if (Dialogs.ColorPicker(this, VectorShapes.Color(shape.FillArgb), "라벨 바탕 색상") is { } color) change("라벨 바탕 색상", s => s with { FillArgb = VectorShapes.Argb(color), FillEnabled = true });
        }, "라벨 바탕 색상 변경"));
        boxChip.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(boxChip, 1); boxRow.Children.Add(boxChip);
        content.Children.Add(boxRow);
        var corner = InspectorNumberBox(layer, shape.CornerRadius, 0, 4096, "라벨 바탕 모서리", (l, v) => { if (l.Shape is { } s) VectorShapes.Update(l, s with { CornerRadius = v }); });
        corner.IsEnabled = !IsLockedWithParents(layer) && shape.FillEnabled;
        content.Children.Add(PropertyRows.Inline("바탕 모서리 · px", corner));

        // The label text uses the text panel (content, font, outline, paragraph box) on the callout's label.
        Guid layerId = layer.Id; var boundDocument = doc; long version = inspectorVersion;
        string? Apply(TextSpec spec)
        {
            if (!ReferenceEquals(doc, boundDocument) || inspectorVersion != version || doc.ActiveId != layerId) return "선택한 레이어가 바뀌었습니다. 현재 속성에서 편집하세요.";
            if (IsLockedWithParents(layer)) return "레이어 또는 부모 그룹의 잠금을 먼저 해제하세요.";
            if (layer.Shape is not { Label: { } previous } s || previous == spec) return null;
            Layer prepared;
            try { prepared = layer.Snapshot(); VectorShapes.Update(prepared, s with { Label = spec }); }
            catch (Exception error) when (error is System.IO.InvalidDataException or ArgumentException) { return error.Message; }
            EditLayer(spec.SameExceptOutline(previous) ? "글자 외곽선" : "지시선 라벨", active =>
            {
                active.Pixels = prepared.Pixels; active.Mask = prepared.Mask; active.Shape = prepared.Shape; active.X = prepared.X; active.Y = prepared.Y;
            });
            return null;
        }
        var panel = new TextPropertiesPanel(shape.Label!, Apply, color => Dialogs.ColorPicker(this, color), null);
        panel.IsEnabled = !IsLockedWithParents(layer);
        Action pending = panel.CommitPending;
        panel.EditingStarted += () => pendingInspectorCommit = pending;
        content.Children.Add(panel); calloutLabelPanel = panel;
    }
}
