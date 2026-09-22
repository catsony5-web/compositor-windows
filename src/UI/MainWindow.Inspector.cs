using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly TextBlock layerCountLabel = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    static readonly Brush inspectorInvalid = Theme.Brush("#D67A79");
    long inspectorVersion;
    Action? pendingInspectorCommit;
    Document? layerPanelDocument;
    Guid layerPanelActive;
    Guid? pendingLayerReveal;

    void CommitFocusedInspectorField()
    {
        var commit = pendingInspectorCommit;
        pendingInspectorCommit = null;
        commit?.Invoke();
    }

    FrameworkElement BuildInspectorPanel()
    {
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        host.ColumnDefinitions.Add(new ColumnDefinition());
        var widthGrip = new System.Windows.Controls.Primitives.Thumb
        {
            Cursor = Cursors.SizeWE, Focusable = true, Background = Brushes.Transparent,
            ToolTip = "패널 너비 조절 · 두 번 클릭하면 기본 너비"
        };
        System.Windows.Automation.AutomationProperties.SetName(widthGrip, "오른쪽 패널 너비 조절");
        var widthTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var gripSurface = new FrameworkElementFactory(typeof(Border));
        gripSurface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var gripLine = new FrameworkElementFactory(typeof(Border));
        gripLine.SetValue(WidthProperty, 1d);
        gripLine.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        gripLine.SetValue(Border.BackgroundProperty, Theme.Line);
        gripSurface.AppendChild(gripLine); widthTemplate.VisualTree = gripSurface; widthGrip.Template = widthTemplate;
        void ResizePanel(double change) => rightPanelColumn.Width = new GridLength(Math.Clamp(
            (rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value) + change,
            rightPanelColumn.MinWidth, rightPanelColumn.MaxWidth));
        widthGrip.DragDelta += (_, e) => ResizePanel(-e.HorizontalChange);
        widthGrip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            rightPanelColumn.Width = new GridLength(396); e.Handled = true;
        };
        widthGrip.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            ResizePanel(e.Key == Key.Left ? 8 : -8); e.Handled = true;
        };
        host.Children.Add(widthGrip);
        var panel = new Grid(); Grid.SetColumn(panel, 1); host.Children.Add(panel);
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(BuildStudioTop());
        var splitter = new System.Windows.Controls.Primitives.Thumb { Height = 6, Cursor = Cursors.SizeNS, Background = Theme.Line };
        var splitterTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var splitterBorder = new FrameworkElementFactory(typeof(Border)); splitterBorder.SetValue(Border.BackgroundProperty, Theme.Line); splitterBorder.SetValue(HeightProperty, 2d); splitterTemplate.VisualTree = splitterBorder; splitter.Template = splitterTemplate;
        splitter.DragDelta += (_, e) => studioScroll.Height = Math.Clamp(studioScroll.Height + e.VerticalChange, 100, Math.Max(120, ActualHeight - 430));
        Grid.SetRow(splitter, 1); panel.Children.Add(splitter);
        layersPane = CreatePane("레이어", -1, BuildLayersPanel()); layersSlot.Child = layersPane;
        Grid.SetRow(layersSlot, 2); panel.Children.Add(layersSlot); return host;
    }

    FrameworkElement BuildLayersPanel()
    {
        var panel = new Grid { Background = Brushes.Transparent };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        panel.Children.Add(BuildLayerCategoryTabs());
        var divider = new Border { Background = Theme.Line };
        Grid.SetRow(divider, 1); panel.Children.Add(divider);

        var heading = new DockPanel { Margin = new Thickness(13, 4, 13, 2) };
        layerCountLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(layerCountLabel, Dock.Right); heading.Children.Add(layerCountLabel);
        layerList.ToolTip = "레이어를 드래그하여 순서 변경";
        Grid.SetRow(heading, 2); panel.Children.Add(heading);

        layerList.CreateRow = CreateLayerRow;
        layerList.Margin = new Thickness(7, 0, 7, 0);
        Grid.SetRow(layerList, 3); panel.Children.Add(layerList);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Button ActionButton(string glyph, string tip, Action action)
        {
            var button = Theme.Button(glyph, () => Guard(action), tip);
            button.Width = 43; button.Height = 32; button.FontSize = 17; button.Padding = new Thickness(0);
            button.Margin = new Thickness(2, 0, 2, 0);
            return button;
        }
        actions.Children.Add(ActionButton("＋", "새 투명 레이어", () => Edit("새 레이어", () => doc.Add(new Layer { Name = $"레이어 {doc.Layers.Count + 1}", Pixels = new Raster(doc.Width, doc.Height) }))));
        actions.Children.Add(ActionButton("▣", "선택 레이어 복제 · Ctrl+J", Duplicate));
        actions.Children.Add(ActionButton("↑", "선택 레이어를 위로", () => Reorder(1)));
        actions.Children.Add(ActionButton("↓", "선택 레이어를 아래로", () => Reorder(-1)));
        actions.Children.Add(ActionButton("×", "선택 레이어 삭제", DeleteLayer));
        Grid.SetRow(actions, 4); panel.Children.Add(actions);
        return DocumentControl(panel);
    }

    void BuildProperties()
    {
        inspectorVersion++;
        properties.Children.Clear();
        if (!HasDocument) return;
        if (tool == Tool.Artboard) { BuildArtboardProperties(); return; }
        if (doc.Active is not { } layer)
        {
            var empty = Theme.Label("레이어를 선택하세요", Theme.BodySize, Theme.Muted);
            empty.Margin = new Thickness(2, 0, 2, 6); properties.Children.Add(empty);
            return;
        }

        var name = Theme.Label(layer.Name, Theme.HeadingSize); name.FontWeight = FontWeights.SemiBold;
        name.TextWrapping = TextWrapping.Wrap;
        name.ToolTip = layer.Name; name.Margin = new Thickness(2, 1, 2, 3);
        properties.Children.Add(name);
        var kind = Theme.Label(layer.Kind switch
        {
            LayerKind.Shape => "벡터 도형",
            LayerKind.Text => "텍스트 레이어",
            LayerKind.Adjustment => "조정 레이어",
            LayerKind.Group => "그룹",
            _ => "이미지 레이어"
        }, Theme.CaptionSize, Theme.Muted);
        kind.Margin = new Thickness(2, 0, 2, 5); properties.Children.Add(kind);
        AddTextProperties(layer);
        AddShapeProperties(layer);

        properties.Children.Add(Theme.Section("외형"));

        var blendLabel = Theme.Label("혼합 모드", Theme.CaptionSize, Theme.Muted); blendLabel.Margin = new Thickness(2, 0, 2, 3);
        properties.Children.Add(blendLabel);
        var blend = new ComboBox { MinHeight = 34, Margin = new Thickness(2, 0, 2, 8), Padding = new Thickness(8, 5, 8, 5) };
        foreach (var mode in Enum.GetValues<BlendMode>())
        {
            var item = new ComboBoxItem { Content = BlendLabel(mode), Tag = mode };
            blend.Items.Add(item);
            if (mode == layer.Blend) blend.SelectedItem = item;
        }
        blend.IsEnabled = !IsLockedWithParents(layer);
        var layerId = layer.Id;
        blend.SelectionChanged += (_, _) =>
        {
            if (doc.ActiveId != layerId || blend.SelectedItem is not ComboBoxItem { Tag: BlendMode mode } || doc.Active?.Blend == mode) return;
            EditLayer("혼합 모드", active => active.Blend = mode);
        };
        properties.Children.Add(blend);

        var opacity = new Grid { Margin = new Thickness(2, 0, 2, 4) };
        opacity.ColumnDefinitions.Add(new ColumnDefinition());
        opacity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        var opacityLabel = Theme.Label("불투명도 · %", Theme.BodySize); opacity.Children.Add(opacityLabel);
        var opacityBox = InspectorNumberBox(layer, layer.Opacity * 100, 0, 100, "불투명도", (l, v) => l.Opacity = v / 100);
        opacityBox.ToolTip = "0~100% · Enter 또는 포커스를 벗어나면 적용";
        Grid.SetColumn(opacityBox, 1); opacity.Children.Add(opacityBox);
        properties.Children.Add(opacity);
        if (layer.Kind == LayerKind.Raster)
            properties.Children.Add(InspectorAction("레벨 보정", Levels, "선택한 이미지 레이어의 검정·흰색·감마 값을 보정합니다.", layer));

        properties.Children.Add(Theme.Section("위치와 변형"));
        properties.Children.Add(TransformRow(layer, ("X", layer.X, -100000, 100000, "X 위치", (l, v) => l.X = v),
            ("Y", layer.Y, -100000, 100000, "Y 위치", (l, v) => l.Y = v)));
        var scaleRow = TransformRow(layer, ("크기 %", layer.Scale * 100, 1, 2000, "크기", (l, v) => l.Scale = v / 100),
            ("회전 °", layer.Rotation, -36000, 36000, "회전", (l, v) => l.Rotation = v));
        scaleRow.Margin = new Thickness(2, 2, 2, 5); properties.Children.Add(scaleRow);
        properties.Children.Add(InspectorAction("상세 변형 설정", Transform, "위치·회전과 가로·세로 배율을 조절합니다.", layer));

        properties.Children.Add(Theme.Section("레이어 작업"));
        properties.Children.Add(InspectorAction("레이어 이름 변경", Rename, "선택한 레이어의 이름을 변경합니다.", layer));
        var mask = InspectorAction(layer.Mask == null ? "레이어 마스크 추가" : maskEditing ? "레이어 이미지 편집" : "레이어 마스크 편집", () =>
        {
            if (layer.Mask == null) AddMask();
            else { maskEditing = !maskEditing; if (maskEditing) SetTool(Tool.Brush); Refresh(false); }
        }, layer.Mask == null ? "선택 영역으로 마스크를 추가합니다. 선택 영역이 없으면 레이어 전체를 표시합니다." : "이미지와 마스크 사이에서 편집 대상을 전환합니다.", layer);
        properties.Children.Add(mask);
        AddAdvancedProperties(layer);
    }

    Button InspectorAction(string label, Action action, string tooltip, Layer layer)
    {
        var button = Theme.ActionRow(label, () => Guard(() =>
        {
            CommitFocusedInspectorField();
            action();
        }), tooltip);
        button.IsEnabled = !IsLockedWithParents(layer);
        return button;
    }

    static string BlendLabel(BlendMode mode) => mode switch
    {
        BlendMode.Normal => "보통", BlendMode.Multiply => "곱하기", BlendMode.Screen => "스크린",
        BlendMode.Overlay => "오버레이", BlendMode.SoftLight => "소프트 라이트",
        BlendMode.Darken => "어둡게", BlendMode.Lighten => "밝게", BlendMode.Difference => "차이",
        BlendMode.ColorDodge => "색상 닷지", BlendMode.ColorBurn => "색상 번",
        BlendMode.Hue => "색조", BlendMode.Saturation => "채도", BlendMode.Color => "색상",
        BlendMode.Luminosity => "명도", _ => mode.ToString()
    };

    static Button CompactButton(string label, Action action, string tip)
    {
        var button = Theme.Button(label, action, tip);
        button.MinHeight = 34; button.Padding = new Thickness(9, 5, 9, 5);
        button.Margin = new Thickness(2, 2, 2, 4); button.FontSize = Theme.BodySize;
        return button;
    }

    Grid TransformRow(Layer layer,
        (string Label, double Value, double Min, double Max, string History, Action<Layer, double> Apply) left,
        (string Label, double Value, double Min, double Max, string History, Action<Layer, double> Apply) right)
    {
        var row = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var leftLabel = Theme.Label(left.Label, Theme.CaptionSize, Theme.Muted); var rightLabel = Theme.Label(right.Label, Theme.CaptionSize, Theme.Muted);
        var leftBox = InspectorNumberBox(layer, left.Value, left.Min, left.Max, left.History, left.Apply);
        var rightBox = InspectorNumberBox(layer, right.Value, right.Min, right.Max, right.History, right.Apply);
        Grid.SetColumn(rightLabel, 1); Grid.SetColumn(rightBox, 1); Grid.SetRow(leftBox, 1); Grid.SetRow(rightBox, 1);
        row.Children.Add(leftLabel); row.Children.Add(leftBox); row.Children.Add(rightLabel); row.Children.Add(rightBox);
        return row;
    }

    TextBox InspectorNumberBox(Layer layer, double initial, double min, double max, string label, Action<Layer, double> apply)
    {
        string originalText = initial.ToString("0.##", CultureInfo.InvariantCulture);
        string normalTip = $"{min:0}~{max:0} · Enter 또는 포커스를 벗어나면 적용";
        var boundDocument = doc;
        long boundVersion = inspectorVersion;
        var box = new TextBox
        {
            Text = originalText,
            MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(2, 2, 2, 4), Padding = new Thickness(8, 5, 8, 5),
            ToolTip = normalTip,
            IsEnabled = !IsLockedWithParents(layer)
        };
        string? lastAttempt = null;
        void ClearInvalid()
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ToolTip = normalTip;
        }
        void Commit()
        {
            if (!ReferenceEquals(doc, boundDocument) || inspectorVersion != boundVersion || doc.ActiveId != layer.Id || IsLockedWithParents(layer)) return;
            if (box.Text == originalText || box.Text == lastAttempt) return;
            lastAttempt = box.Text;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < min || value > max)
            {
                string message = $"{label}: {min:0}~{max:0} 사이의 숫자를 입력하세요.";
                box.BorderBrush = inspectorInvalid; box.ToolTip = message; status.Text = message;
                return;
            }
            ClearInvalid();
            if (Math.Abs(value - initial) < 0.0000001) return;
            EditLayer(label, active => apply(active, value));
        }
        Action commit = Commit;
        box.GotFocus += (_, _) => pendingInspectorCommit = commit;
        box.LostFocus += (_, _) =>
        {
            if (ReferenceEquals(pendingInspectorCommit, commit)) pendingInspectorCommit = null;
            Commit();
        };
        box.TextChanged += (_, _) => ClearInvalid();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                box.Text = originalText; ClearInvalid(); lastAttempt = null;
                e.Handled = true;
            }
        };
        return box;
    }

    void RevealLayerSelection(Guid id)
    {
        if (id == Guid.Empty) return;
        var byId = doc.Layers.ToDictionary(layer => layer.Id);
        if (!byId.TryGetValue(id, out var selected)) return;
        var parent = selected.ParentId;
        bool keepDrawingCollapsed = DrawingLayers.Categories(doc).GetValueOrDefault(id) == LayerCategory.Drawing;
        Guid reveal = id;
        for (int depth = 0; parent is { } parentId && depth < 16; depth++)
        {
            if (keepDrawingCollapsed) { if (collapsedGroups.Contains(parentId)) reveal = parentId; }
            else collapsedGroups.Remove(parentId);
            knownLayerGroups.Add(parentId);
            parent = byId.TryGetValue(parentId, out var group) ? group.ParentId : null;
        }
        pendingLayerReveal = reveal;
    }

    void BuildLayers()
    {
        foreach (var group in doc.Layers.Where(layer => layer.Kind == LayerKind.Group))
            if (knownLayerGroups.Add(group.Id)) collapsedGroups.Add(group.Id);
        // Canvas multiselection can change ActiveId without calling SelectLayer.
        if (ReferenceEquals(layerPanelDocument, doc) && layerPanelActive != doc.ActiveId)
            RevealLayerSelection(doc.ActiveId);
        var categories = DrawingLayers.Categories(doc);
        if ((!ReferenceEquals(layerPanelDocument, doc) || layerPanelActive != doc.ActiveId) && categories.TryGetValue(doc.ActiveId, out var activeCategory)) layerCategory = activeCategory;
        layerPanelDocument = doc; layerPanelActive = doc.ActiveId;
        foreach (var (category, button) in layerCategoryButtons)
        { button.BorderBrush = category == layerCategory ? Theme.Accent : Theme.Line; button.Background = category == layerCategory ? Theme.Selected : Theme.Panel; }
        int roots = doc.Layers.Count(l => l.ParentId == null && categories[l.Id] == layerCategory);
        int objects = doc.Layers.Count(l => l.Kind != LayerKind.Group && categories[l.Id] == layerCategory);
        layerCountLabel.Text = !HasDocument ? "" : layerCategory == LayerCategory.Drawing ? $"{roots:N0}개 레이어 · {objects:N0}개 객체" : $"{objects:N0}개 레이어";
        var entries = HasDocument ? DrawingLayerEntries(categories) : [];
        if (pendingLayerReveal is { } reveal && !entries.Any(e => e.Layer.Id == reveal))
            pendingLayerReveal = entries.FirstOrDefault(e => e.GroupMembers?.Contains(reveal) == true)?.Layer.Id;
        layerList.SetEntries(entries, pendingLayerReveal); pendingLayerReveal = null;
    }

    LayerRow CreateLayerRow(LayerListEntry entry)
    {
        var id = entry.Layer.Id;
        if (entry.GroupMembers is { } members)
        {
            var grouped = new LayerRow(entry.Layer, entry.Selected, () => SelectSourceLayer(members),
                visible => ToggleSourceLayer(members, visible), () => ToggleSourceLayer(members), entry.Expanded,
                () => { foreach (var member in members) if (entry.Expanded) collapsedGroups.Add(member); else collapsedGroups.Remove(member); BuildLayers(); }, entry.Description);
            grouped.Margin = new Thickness(Math.Min(4, entry.Depth) * 10, 1, 0, 1);
            return grouped;
        }
        var row = new LayerRow(entry.Layer, entry.Selected,
            () => SelectLayer(id),
            visible => Edit("레이어 표시", () => doc.Layers.Single(item => item.Id == id).Visible = visible),
            () => Edit("잠금", () => { var active = doc.Layers.Single(item => item.Id == id); active.Locked = !active.Locked; }),
            entry.Expanded, () => { if (!collapsedGroups.Add(id)) collapsedGroups.Remove(id); BuildLayers(); }, entry.Description);
        row.Margin = new Thickness(Math.Min(4, entry.Depth) * 10, 1, 0, 1); row.AllowDrop = true;
        EnableLayerDrag(row, id);
        return row;
    }
}
