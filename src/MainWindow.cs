using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public enum Tool { Move, Brush, Eraser, RectangleSelect, EllipseSelect, Crop, Rectangle, Ellipse, Gradient, Text, Eyedropper, Hand, Lasso, PolygonLasso, MagicWand, CloneStamp, Heal, Smudge, Liquify, BlurBrush }

public sealed partial class MainWindow : Window
{
    Document doc = Demo.Create();
    History history = new();
    readonly CanvasView canvas = new();
    readonly StackPanel properties = new(), layerList = new();
    readonly TextBlock status = Theme.Label("준비", 11), documentTitle = Theme.Label("", 12), zoomLabel = Theme.Label("", 11);
    readonly Dictionary<Tool, Button> toolButtons = [];
    readonly Button colorButton;
    readonly Slider sizeSlider;
    readonly CheckBox autoSelectToggle;
    readonly TextBlock brushLabel = Theme.Label("", 11, Theme.Muted);
    Tool tool = Tool.Move;
    Color foreground = Color.FromRgb(162, 232, 205);
    double brushSize = 42, hardness = .8, brushOpacity = 1;
    bool maskEditing, dragging, panning, moveStarted;
    Point start, screenStart;
    Vector initialPan;
    Document? beforeGesture;
    BrushStroke? stroke;
    Selection? selection;
    string? projectPath;
    Raster? composite;
    readonly string? startupPath;

    public MainWindow(string? path)
    {
        startupPath = path;
        Title = "Morupixel · 모루픽셀"; Width = 1480; Height = 980; MinWidth = 1200; MinHeight = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Theme.Panel; Foreground = Theme.Text;
        FontFamily = new FontFamily("Malgun Gothic"); FontSize = 12; UseLayoutRounding = true;
        var root = new Grid { Background = Theme.Panel }; Content = root;
        foreach (double h in new[] { 52.0, 34, 52, -1, 32 }) root.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });
        var header = new DockPanel { Background = Theme.Brush("#1C1F26"), LastChildFill = false, Margin = new Thickness(0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0) };
        brand.Children.Add(new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(6), Width = 30, Height = 30, Child = new TextBlock { Text = "M", FontWeight = FontWeights.Bold, FontSize = 21, Foreground = Theme.Brush("#18352D"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        brand.Children.Add(new TextBlock { Text = "  Morupixel", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Theme.Label("   IMAGE STUDIO  /  0.2", 10, Theme.Muted)); DockPanel.SetDock(brand, Dock.Left); header.Children.Add(brand);
        var headActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 12, 4) };
        headActions.Children.Add(Theme.Button("이미지 가져오기", () => Guard(Import), "Ctrl+Shift+O · 현재 작업에 이미지 레이어 추가"));
        headActions.Children.Add(Theme.Button("작업 저장", () => Save(false), "Ctrl+S · 레이어를 보존하는 .moruproj 파일"));
        var export = Theme.Button("내보내기  ↗", () => Guard(Export)); export.Background = Theme.Brush("#285848"); headActions.Children.Add(export);
        DockPanel.SetDock(headActions, Dock.Right); header.Children.Add(headActions); root.Children.Add(header);

        var menu = BuildMenu(); Grid.SetRow(menu, 1); root.Children.Add(menu);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 10, 5) }; Grid.SetRow(options, 2); root.Children.Add(options);
        colorButton = Theme.Button("전경색", () => { var c = Dialogs.ColorPicker(this, foreground); if (c != null) { foreground = c.Value; UpdateColor(); } });
        colorButton.Width = 78; options.Children.Add(colorButton);
        options.Children.Add(Theme.Label("크기"));
        sizeSlider = Slider(1, 300, brushSize, 115, v => { brushSize = v; UpdateBrushLabel(); }); options.Children.Add(sizeSlider);
        options.Children.Add(brushLabel); options.Children.Add(Theme.Label("경도"));
        options.Children.Add(Slider(0, 1, hardness, 75, v => hardness = v)); options.Children.Add(Theme.Label("농도"));
        options.Children.Add(Slider(.01, 1, brushOpacity, 75, v => brushOpacity = v));
        options.Children.Add(Theme.Button("↶", Undo, "실행 취소 · Ctrl+Z")); options.Children.Add(Theme.Button("↷", Redo, "다시 실행 · Ctrl+Shift+Z"));
        options.Children.Add(Theme.Button("화면 맞춤", () => { canvas.Fit(); UpdateStatus(); }, "Ctrl+0"));
        options.Children.Add(Theme.Button("100%", () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); }));
        autoSelectToggle = new CheckBox { Content = "자동 선택", IsChecked = true, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), ToolTip = "이동 도구(V): 클릭한 대상의 레이어 선택. 끄면 목록에서 선택한 레이어만 이동합니다." };
        System.Windows.Automation.AutomationProperties.SetName(autoSelectToggle, "캔버스 레이어 자동 선택");
        options.Children.Add(autoSelectToggle);

        var body = new Grid(); Grid.SetRow(body, 3); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(302) });
        var tools = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 8, 4, 8) };
        var toolDefs = new (Tool Tool, string Icon, string Name, string Key)[] { (Tool.Move, "↖", "이동", "V"), (Tool.RectangleSelect, "▣", "사각 선택", "M"), (Tool.EllipseSelect, "◌", "타원 선택", "Shift+M"), (Tool.Crop, "⌗", "자르기", "C"), (Tool.Brush, "B", "브러시", "B"), (Tool.Eraser, "E", "지우개", "E"), (Tool.Rectangle, "□", "사각형", "U"), (Tool.Ellipse, "○", "타원", "Shift+U"), (Tool.Gradient, "▧", "그라데이션", "G"), (Tool.Text, "T", "텍스트", "T"), (Tool.Eyedropper, "I", "색상 추출", "I"), (Tool.Hand, "✥", "손 도구", "H") };
        foreach (var def in toolDefs)
        {
            var b = Theme.Button(def.Icon, () => SetTool(def.Tool), $"{def.Name} ({def.Key})"); b.Height = 38; b.FontSize = 19; b.Padding = new Thickness(4); b.Margin = new Thickness(4, 2, 4, 2); toolButtons[def.Tool] = b; tools.Children.Add(b);
        }
        foreach (var def in AdvancedToolDefinitions())
        {
            var b = Theme.Button(def.Icon, () => SetTool(def.Tool), $"{def.Name} ({def.Key})"); b.Height = 40; b.FontSize = 15; b.Padding = new Thickness(1); b.Margin = new Thickness(2); toolButtons[def.Tool] = b; tools.Children.Add(b);
        }
        body.Children.Add(new ScrollViewer { Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var workspace = new Grid(); workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); workspace.RowDefinitions.Add(new RowDefinition()); Grid.SetColumn(workspace, 1); body.Children.Add(workspace);
        var tab = new DockPanel { Background = Theme.Brush("#242832"), Margin = new Thickness(0, 0, 0, 1) };
        tab.Children.Add(new ScrollViewer { Content = tabsBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }); workspace.Children.Add(tab);
        Grid.SetRow(canvas, 1); workspace.Children.Add(canvas); canvas.Document = doc;
        var right = new Grid { Background = Theme.Panel }; right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(340) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) }); right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); Grid.SetColumn(right, 2); body.Children.Add(right);
        right.Children.Add(new ScrollViewer { Content = properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 8, 12, 8) });
        var layerHeading = Theme.Label("레이어", 13); layerHeading.FontWeight = FontWeights.SemiBold; layerHeading.Margin = new Thickness(16, 7, 0, 7); Grid.SetRow(layerHeading, 1); right.Children.Add(layerHeading);
        var layerScroll = new ScrollViewer { Content = layerList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(7, 0, 7, 0) }; Grid.SetRow(layerScroll, 2); right.Children.Add(layerScroll);
        var layerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        layerActions.Children.Add(Theme.Button("＋", () => Edit("새 레이어", () => doc.Add(new Layer { Name = $"레이어 {doc.Layers.Count + 1}", Pixels = new Raster(doc.Width, doc.Height) })), "새 투명 레이어"));
        layerActions.Children.Add(Theme.Button("복제", Duplicate)); layerActions.Children.Add(Theme.Button("↑", () => Reorder(1))); layerActions.Children.Add(Theme.Button("↓", () => Reorder(-1))); layerActions.Children.Add(Theme.Button("삭제", DeleteLayer));
        Grid.SetRow(layerActions, 3); right.Children.Add(layerActions);
        var bottom = new DockPanel { Background = Theme.Brush("#1C1F26") }; status.Margin = new Thickness(12, 0, 8, 0); DockPanel.SetDock(zoomLabel, Dock.Right); zoomLabel.Margin = new Thickness(8, 0, 16, 0); bottom.Children.Add(zoomLabel); bottom.Children.Add(status); Grid.SetRow(bottom, 4); root.Children.Add(bottom);

        canvas.MouseDown += OnDown; canvas.MouseMove += OnMove; canvas.MouseUp += OnUp;
        canvas.LostMouseCapture += (_, _) => { if (dragging) CancelGesture(); };
        canvas.MouseWheel += (_, e) => { canvas.ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(canvas)); UpdateStatus(); e.Handled = true; };
        PreviewKeyDown += OnKey; AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Guard(() => { foreach (var file in files) { var ext = Path.GetExtension(file).ToLowerInvariant(); if (ext is ".moruproj" or ".cwproj" or ".comp") OpenPath(file); else ImportFiles([file]); } }); };
        Closing += (_, e) => { CancelGesture(); if (!ConfirmAllTabs()) e.Cancel = true; else { renderCts?.Cancel(); jobCts?.Cancel(); } };
        Loaded += (_, _) => { history.Reset(doc); InitializeWorkspace(); UpdateColor(); UpdateBrushLabel(); Refresh(); canvas.Fit(); SetTool(Tool.Move); if (startupPath != null && (File.Exists(startupPath) || Directory.Exists(startupPath))) Guard(() => OpenPath(startupPath)); };
        canvas.MouseLeave += (_, _) => { canvas.BrushPoint = null; canvas.InvalidateVisual(); };
    }

    static Slider Slider(double min, double max, double value, double width, Action<double> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5), ToolTip = "드래그하여 조절" };
        slider.ValueChanged += (_, _) => changed(slider.Value); return slider;
    }
    Menu BuildMenu()
    {
        var menu = new Menu { Background = Theme.Brush("#262A33"), Foreground = Theme.Text, Padding = new Thickness(10, 2, 0, 2) };
        void Add(string name, params (string Label, string Shortcut, Action Run)[] actions)
        {
            var top = new MenuItem { Header = name, Foreground = Theme.Text };
            foreach (var a in actions) { var item = new MenuItem { Header = a.Label, InputGestureText = a.Shortcut, Foreground = Brushes.Black }; item.Click += (_, _) => Guard(a.Run); top.Items.Add(item); }
            menu.Items.Add(top);
        }
        Add("파일", ("새 캔버스…", "Ctrl+N", NewDocument), ("열기…", "Ctrl+O", Open), ("레이어로 가져오기…", "Ctrl+Shift+O", Import), ("저장", "Ctrl+S", () => Save(false)), ("다른 이름으로 저장…", "Ctrl+Shift+S", () => Save(true)), ("내보내기 미리보기…", "Ctrl+Shift+E", Export), ("Compositor .comp 가져오기…", "", ImportCompositor), ("Compositor .comp 내보내기…", "", ExportCompositor), ("현재 문서 닫기", "Ctrl+W", CloseTab), ("샘플 작업 열기", "", () => AddTab(Demo.Create(), null)));
        Add("편집", ("실행 취소", "Ctrl+Z", Undo), ("다시 실행", "Ctrl+Shift+Z", Redo), ("합성 이미지 복사", "Ctrl+C", CopyMerged), ("이미지 붙여넣기", "Ctrl+V", Paste), ("선택 픽셀 지우기", "Delete", ClearPixels), ("전경색으로 채우기", "Alt+Backspace", Fill));
        Add("이미지", ("캔버스 크기…", "", CanvasSize), ("이미지 크기…", "", ImageSize), ("선택 영역으로 자르기", "", CropSelection));
        Add("레이어", ("레이어 복제", "Ctrl+J", Duplicate), ("이름 변경…", "", Rename), ("변형 값 입력…", "Ctrl+T", Transform), ("가로 뒤집기", "", () => EditLayer("가로 뒤집기", l => l.FlipX = !l.FlipX)), ("세로 뒤집기", "", () => EditLayer("세로 뒤집기", l => l.FlipY = !l.FlipY)), ("마스크 추가", "", AddMask), ("마스크 반전", "", InvertMask), ("마스크 제거", "", () => EditLayer("마스크 제거", l => { l.Mask = null; maskEditing = false; })), ("모든 레이어 병합", "", Flatten), ("레이어 삭제", "", DeleteLayer));
        Add("선택", ("전체 선택", "Ctrl+A", () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); }), ("선택 해제", "Ctrl+D", () => { selection = null; Refresh(false); }), ("선택 반전", "Ctrl+Shift+I", InvertSelection), ("페더…", "", () => ModifySelection("feather")), ("확장…", "", () => ModifySelection("expand")), ("축소…", "", () => ModifySelection("contract")), ("레이어의 불투명 픽셀 선택", "", SelectAlpha), ("선택 픽셀을 새 레이어로", "", ExtractSelection), ("선택 윤곽 이동…", "", MoveSelectionOutline));
        Add("보정", ("레벨…", "Ctrl+L", Levels), ("노출…", "", Exposure), ("채도…", "", Saturation), ("흑백", "", () => Adjust("grayscale")), ("색상 반전", "Ctrl+I", () => Adjust("invert")), ("가우시안 흐림…", "", Blur));
        Add("합성", ("선택 레이어 그룹화", "Ctrl+G", GroupSelected), ("그룹 해제", "Ctrl+Shift+G", UngroupSelected), ("그룹으로 이동…", "", MoveToGroup), ("클리핑 마스크 전환", "Ctrl+Alt+G", ToggleClipping), ("아래 레이어와 병합", "Ctrl+E", MergeDown), ("텍스트 내용·서식 편집…", "", EditText), ("픽셀 레이어로 변환", "", RasterizeActive), ("다른 문서로 레이어 복사…", "", CopyLayerToTab));
        Add("조정 레이어", ("레벨…", "", () => ShowAdjustment(AdjustmentKind.Levels)), ("곡선…", "", () => ShowAdjustment(AdjustmentKind.Curves)), ("색조 / 채도…", "Ctrl+U", () => ShowAdjustment(AdjustmentKind.HueSaturation)), ("노출…", "", () => ShowAdjustment(AdjustmentKind.Exposure)), ("그라데이션 맵…", "", () => ShowAdjustment(AdjustmentKind.GradientMap)), ("그레인…", "", () => ShowAdjustment(AdjustmentKind.Grain)), ("선택 조정 레이어 편집…", "", EditAdjustment));
        Add("필터", ("모션 블러…", "", MotionBlur), ("노이즈…", "", Noise), ("렌즈 왜곡 보정…", "", Lens), ("내용 인식 채우기", "Shift+F5", ContentFill), ("배경색 제거…", "", RemoveColorBackground), ("AI 피사체 배경 제거…", "", RemoveAiBackground), ("마스크 페더…", "", FeatherMask));
        Add("인쇄", ("ICC / CMYK 내보내기…", "", () => new CmykExportDialog(this, doc).ShowDialog()));
        Add("보기", ("화면에 맞춤", "Ctrl+0", () => { canvas.Fit(); UpdateStatus(); }), ("실제 크기", "Ctrl+1", () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); }), ("가이드 추가…", "", AddGuide), ("가이드 지우기", "", () => { canvas.Guides.Clear(); canvas.InvalidateVisual(); }), ("스냅 전환", "", () => { snapping = !snapping; status.Text = snapping ? "스냅 켜짐" : "스냅 꺼짐"; }), ("픽셀 격자 전환", "", () => { canvas.PixelGrid = !canvas.PixelGrid; canvas.InvalidateVisual(); }), ("도움말 / 지원 범위", "F1", Help));
        return menu;
    }
    void Guard(Action action) { try { action(); } catch (Exception e) { if (headlessTesting) throw; MessageBox.Show(this, e.Message, "Morupixel", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    void Edit(string label, Action action)
    {
        CancelGesture(); var before = doc.Snapshot();
        try { action(); doc.Validate(); history.Commit(label, before, doc); Refresh(); }
        catch (Exception e) { doc = before; Refresh(); if (headlessTesting) throw; MessageBox.Show(this, e.Message, "Morupixel", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    void EditLayer(string label, Action<Layer> action)
    {
        if (doc.Active == null) return;
        if (IsLockedWithParents(doc.Active)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        Edit(label, () => action(doc.Active!));
    }
    void UpdateColor() { colorButton.Background = new SolidColorBrush(foreground); colorButton.Foreground = foreground.R * .299 + foreground.G * .587 + foreground.B * .114 > 145 ? Brushes.Black : Brushes.White; }
    void UpdateBrushLabel()
    {
        brushLabel.Text = $"{brushSize:0}px";
        if (sizeSlider != null && Math.Abs(sizeSlider.Value - brushSize) > .001) sizeSlider.Value = brushSize;
    }
    // UI-only changes and no-op commands must not discard the redo stack or dirty the file.
    static bool SameDocument(Document a, Document b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Name != b.Name || a.Layers.Count != b.Layers.Count) return false;
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var x = a.Layers[i]; var y = b.Layers[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Visible != y.Visible || x.Locked != y.Locked || x.Opacity != y.Opacity || x.Blend != y.Blend || x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.FlipX != y.FlipX || x.FlipY != y.FlipY || x.ScaleX != y.ScaleX || x.ScaleY != y.ScaleY || x.Kind != y.Kind || x.ParentId != y.ParentId || x.Clipped != y.Clipped || x.Warp != y.Warp || x.Text != y.Text || !DocumentFeatures.SameAdjustment(x.Adjustment, y.Adjustment) || !ReferenceEquals(x.Pixels.Data, y.Pixels.Data) || !ReferenceEquals(x.Mask, y.Mask)) return false;
        }
        return true;
    }
    void SetTool(Tool next) => ChangeInteractionTool(next);
    void Refresh(bool render = true)
    {
        selectedLayers.RemoveWhere(id => !doc.Layers.Any(l => l.Id == id));
        canvas.Document = doc; canvas.Selection = selection;
        if (render) QueueRender();
        canvas.InvalidateVisual();
        documentTitle.Text = $"{(history.Dirty(doc) ? "●  " : "")}{doc.Name}   ·   {doc.Width} × {doc.Height} px";
        Title = $"{(history.Dirty(doc) ? "* " : "")}{doc.Name} — Morupixel";
        BuildProperties(); BuildLayers(); UpdateStatus(); RebuildTabs();
    }
    void RenderGesture()
    {
        QueueRender(true); canvas.InvalidateVisual();
    }
    void UpdateStatus()
    {
        var hint = tool switch { Tool.Move => "클릭: 레이어 선택 · 드래그: 이동 · 자동 선택을 끄면 선택한 레이어 유지 · Ctrl+T 변형", Tool.Brush => "드래그하여 그리기 · [ ] 크기 조절", Tool.Eraser => "드래그하여 지우기", Tool.Crop => "드래그한 영역으로 캔버스 자르기", Tool.Text => "캔버스를 클릭하여 텍스트 추가", Tool.Gradient => "전경색 → 투명 그라데이션 · 드래그", Tool.Hand => "드래그하여 화면 이동", _ => "캔버스에서 드래그 · Esc 취소" };
        status.Text = (maskEditing ? "마스크 편집 · " : "") + hint + "    |    휠: 확대/축소 · Space+드래그: 화면 이동";
        zoomLabel.Text = $"{doc.Layers.Count} 레이어    {canvas.Zoom * 100:0}%";
    }
    void BuildProperties()
    {
        properties.Children.Clear(); properties.Children.Add(Theme.Label("속성", 13));
        var l = doc.Active;
        if (l == null) { properties.Children.Add(Theme.Label("캔버스 또는 목록에서 레이어를 선택하세요.", 12, Theme.Muted)); return; }
        properties.Children.Add(Theme.Label(l.Name.Length > 25 ? l.Name[..25] + "…" : l.Name, 12, Theme.Muted));
        var blend = new ComboBox { ItemsSource = Enum.GetValues<BlendMode>(), SelectedItem = l.Blend, Margin = new Thickness(3), Padding = new Thickness(5) };
        blend.SelectionChanged += (_, _) => { if (blend.SelectedItem is BlendMode b && b != doc.Active?.Blend) EditLayer("혼합 모드", active => active.Blend = b); }; properties.Children.Add(blend);
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal };
        opacityRow.Children.Add(Theme.Label("불투명도 %", 11, Theme.Muted));
        var opacity = new TextBox { Text = (l.Opacity * 100).ToString("0", CultureInfo.InvariantCulture), Width = 64 };
        opacityRow.Children.Add(opacity); opacityRow.Children.Add(Theme.Button("적용", () => Guard(() => { var value = Dialogs.Number(opacity.Text, 0, 100) / 100; if (value != l.Opacity) EditLayer("불투명도", active => active.Opacity = value); }))); properties.Children.Add(opacityRow);
        properties.Children.Add(Theme.Label($"X {l.X:0}   Y {l.Y:0}   ·   {l.Scale * 100:0}%   ·   {l.Rotation:0}°", 11, Theme.Muted));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Theme.Button("변형…", () => Guard(Transform))); row.Children.Add(Theme.Button("레벨…", () => Guard(Levels))); row.Children.Add(Theme.Button("이름…", () => Guard(Rename))); properties.Children.Add(row);
        var mask = Theme.Button(l.Mask == null ? "＋ 레이어 마스크" : maskEditing ? "마스크 편집 중 → 이미지로 전환" : "마스크 편집으로 전환", () => { if (l.Mask == null) AddMask(); else { maskEditing = !maskEditing; if (maskEditing) SetTool(Tool.Brush); Refresh(false); } });
        mask.FontSize = 11; properties.Children.Add(mask);
        AddAdvancedProperties(l);
    }
    void BuildLayers()
    {
        layerList.Children.Clear();
        foreach (var l in LayerDisplayOrder(null))
        {
            var id = l.Id;
            var row = new LayerRow(l, selectedLayers.Contains(id) || id == doc.ActiveId,
                () => SelectLayer(id),
                visible => Edit("레이어 표시", () => doc.Layers.Single(item => item.Id == id).Visible = visible),
                () => Edit("잠금", () => { var layer = doc.Layers.Single(item => item.Id == id); layer.Locked = !layer.Locked; }),
                !collapsedGroups.Contains(id), () => { if (!collapsedGroups.Add(id)) collapsedGroups.Remove(id); BuildLayers(); });
            row.Margin = new Thickness(LayerDepth(l) * 12, 2, 0, 2); row.AllowDrop = true;
            row.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) DragDrop.DoDragDrop(row, id.ToString(), DragDropEffects.Move); };
            row.Drop += (_, e) => { if (e.Data.GetData(DataFormats.StringFormat) is string text && Guid.TryParse(text, out var moving)) ReorderDrop(moving, id); e.Handled = true; };
            layerList.Children.Add(row);
        }
    }
    void SelectLayer(Guid id)
    {
        CancelGesture(); doc.ActiveId = id; maskEditing = false;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) selectedLayers.Clear();
        if (id != Guid.Empty) selectedLayers.Add(id);
        Refresh(false); canvas.Focus();
    }

    void NewDocument()
    {
        var f = Dialogs.Fields(this, "새 캔버스", ("이름", "제목 없음"), ("너비 (px)", "1280"), ("높이 (px)", "800"), ("배경 (투명 / 흰색)", "투명"));
        if (f == null) return; int w = (int)Dialogs.Number(f[1], 1, 8192), h = (int)Dialogs.Number(f[2], 1, 8192); Raster.ValidateSize(w, h);
        var next = new Document { Width = w, Height = h, Name = string.IsNullOrWhiteSpace(f[0]) ? "제목 없음" : f[0] };
        next.Add(new Layer { Name = "레이어 1", Pixels = f[3].Trim() == "흰색" ? Raster.Solid(w, h, Colors.White) : new Raster(w, h) });
        AddTab(next, null);
    }
    bool ConfirmDiscard()
    {
        if (!history.Dirty(doc)) return true;
        var result = MessageBox.Show(this, "변경한 작업을 저장할까요?", "저장하지 않은 작업", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && Save(false);
    }
    const string ImageFilter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.heic;*.heif|모든 파일|*.*";
    void Open()
    {
        var dialog = new OpenFileDialog { Filter = "이미지 또는 Morupixel 작업|*.moruproj;*.cwproj;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.heic;*.heif|모든 파일|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) OpenPath(path);
    }
    void OpenProject(string path)
    {
        int existing = FindPathTab(path); if (existing >= 0) { SwitchTab(existing); status.Text = "이미 열려 있는 작업으로 이동했습니다."; return; }
        AddTab(ProjectStore.Load(path), path);
    }
    void OpenImage(string path)
    {
        var pixels = ImportExport.LoadImage(path); var next = new Document { Width = pixels.Width, Height = pixels.Height, Name = Path.GetFileNameWithoutExtension(path) };
        next.Add(new Layer { Name = "원본", Pixels = pixels }); AddTab(next, null);
    }
    void Import()
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }
    void ImportFiles(string[] paths)
    {
        var layers = paths.Select(p => new Layer { Name = Path.GetFileNameWithoutExtension(p), Pixels = ImportExport.LoadImage(p) }).ToList();
        Edit("이미지 가져오기", () => { foreach (var l in layers) { l.Scale = Math.Min(1, Math.Min(doc.Width / (double)l.Pixels.Width, doc.Height / (double)l.Pixels.Height)); l.X = (doc.Width - l.Pixels.Width * l.Scale) / 2; l.Y = (doc.Height - l.Pixels.Height * l.Scale) / 2; doc.Add(l); } maskEditing = false; });
    }
    bool Save(bool saveAs)
    {
        CancelGesture();
        try
        {
            string? path = projectPath;
            if (path == null || saveAs)
            {
                var d = new SaveFileDialog { Filter = "Morupixel 작업|*.moruproj", DefaultExt = ".moruproj", FileName = doc.Name };
                if (d.ShowDialog(this) != true) return false; path = d.FileName;
            }
            EnsureSavePathAvailable(path);
            ProjectStore.Save(doc, path); projectPath = Path.GetFullPath(path); history.MarkSaved(doc); Refresh(false); status.Text = "작업 저장 완료 · " + Path.GetFileName(path); return true;
        }
        catch (Exception e) { if (headlessTesting) throw; MessageBox.Show(this, e.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    void Export()
    {
        ExportDialog.Show(this, doc);
    }
    void Undo() { CancelGesture(); if (!history.CanUndo) return; doc = history.Undo(doc); maskEditing = false; selection = null; Refresh(); }
    void Redo() { CancelGesture(); if (!history.CanRedo) return; doc = history.Redo(doc); maskEditing = false; selection = null; Refresh(); }
    void Duplicate() => EditLayer("레이어 복제", l =>
    {
        var subtree = new HashSet<Guid> { l.Id };
        bool changed; do { changed = false; foreach (var child in doc.Layers) if (child.ParentId is { } p && subtree.Contains(p)) changed |= subtree.Add(child.Id); } while (changed);
        var originals = doc.Layers.Where(x => subtree.Contains(x.Id)).ToArray(); var map = originals.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        foreach (var original in originals) { var copy = original.Snapshot(); copy.Id = map[original.Id]; if (copy.ParentId is { } parent && map.TryGetValue(parent, out var mapped)) copy.ParentId = mapped; if (original.Id == l.Id) copy.Name += " 복사"; doc.Add(copy); }
        doc.ActiveId = map[l.Id]; selectedLayers.Clear(); selectedLayers.Add(doc.ActiveId);
    });
    void DeleteLayer() => EditLayer("레이어 삭제", l => { DocumentFeatures.Remove(doc, l.Id); maskEditing = false; selectedLayers.Clear(); });
    void Reorder(int delta)
    {
        if (doc.Active is not { } active) return;
        var siblings = doc.Layers.Where(l => l.ParentId == active.ParentId).ToList(); int next = siblings.IndexOf(active) + delta;
        if (next < 0 || next >= siblings.Count) return;
        Edit("레이어 순서", () => { int target = doc.Layers.IndexOf(siblings[next]); doc.Layers.Remove(active); doc.Layers.Insert(Math.Min(target, doc.Layers.Count), active); });
    }
    void Rename()
    {
        if (doc.Active == null) return; var f = Dialogs.Fields(this, "레이어 이름", ("이름", doc.Active.Name));
        if (f != null && !string.IsNullOrWhiteSpace(f[0])) EditLayer("레이어 이름", l => l.Name = f[0]);
    }
    void Transform()
    {
        if (doc.Active is not { } l) return;
        var f = Dialogs.Fields(this, "레이어 변형", ("X 위치 (px)", l.X.ToString(CultureInfo.InvariantCulture)), ("Y 위치 (px)", l.Y.ToString(CultureInfo.InvariantCulture)), ("공통 크기 (%)", (l.Scale * 100).ToString(CultureInfo.InvariantCulture)), ("시계방향 회전 (°)", l.Rotation.ToString(CultureInfo.InvariantCulture)), ("가로 배율 (%)", (l.ScaleX * 100).ToString(CultureInfo.InvariantCulture)), ("세로 배율 (%)", (l.ScaleY * 100).ToString(CultureInfo.InvariantCulture)));
        if (f == null) return;
        double x = Dialogs.Number(f[0], -100000, 100000), y = Dialogs.Number(f[1], -100000, 100000), scale = Dialogs.Number(f[2], 1, 2000) / 100, rotation = Dialogs.Number(f[3], -36000, 36000);
        double sx = Dialogs.Number(f[4], 1, 2000) / 100, sy = Dialogs.Number(f[5], 1, 2000) / 100;
        EditLayer("레이어 변형", active => { active.X = x; active.Y = y; active.Scale = scale; active.ScaleX = sx; active.ScaleY = sy; active.Rotation = rotation; });
    }
    void AddMask() => EditLayer("마스크 추가", l => { if (l.Mask != null) return; l.Mask = new byte[l.Pixels.Width * l.Pixels.Height]; for (int y = 0; y < l.Pixels.Height; y++) for (int x = 0; x < l.Pixels.Width; x++) { var p = DocumentFeatures.ToDocumentSpace(doc, l, new Point(x + .5, y + .5)); l.Mask[y * l.Pixels.Width + x] = Imaging.Byte((selection?.Weight(p.X, p.Y) ?? 1) * 255); } maskEditing = true; });
    void InvertMask() => EditLayer("마스크 반전", l => { if (l.Mask != null) l.Mask = l.Mask.Select(v => (byte)(255 - v)).ToArray(); });
    void Flatten() => Edit("모든 레이어 병합", () => { var rendered = Imaging.Render(doc); doc.Layers.Clear(); doc.Add(new Layer { Name = "합성 이미지", Pixels = rendered }); maskEditing = false; });
    void Adjust(string kind, double a = 0, double b = 255, double c = 1) => RunRasterJob("색상 보정", (l, s, ct) => Imaging.Adjust(l, s, kind, a, b, c));
    void Levels()
    {
        var f = Dialogs.Fields(this, "레벨 보정", ("입력 검정 (0~254)", "0"), ("입력 흰색 (1~255)", "255"), ("감마 (0.1~9.99)", "1"));
        if (f == null) return; double black = Dialogs.Number(f[0], 0, 254), white = Dialogs.Number(f[1], black + 1, 255), gamma = Dialogs.Number(f[2], .1, 9.99); Adjust("levels", black, white, gamma);
    }
    void Exposure() { var f = Dialogs.Fields(this, "노출 보정", ("노출 EV (-5~5)", "0.5")); if (f != null) Adjust("exposure", Dialogs.Number(f[0], -5, 5)); }
    void Saturation() { var f = Dialogs.Fields(this, "채도 보정", ("채도 (-100~100)", "20")); if (f != null) Adjust("saturation", Dialogs.Number(f[0], -100, 100)); }
    void Blur() { var f = Dialogs.Fields(this, "가우시안 흐림", ("반경 (1~30 px)", "5")); if (f != null) { int r = (int)Dialogs.Number(f[0], 1, 30); RunRasterJob("가우시안 흐림", (l, s, ct) => Imaging.Blur(l, r, s)); } }
    void CanvasSize()
    {
        var f = Dialogs.Fields(this, "캔버스 크기 · 좌측 상단 기준", ("너비 (px)", doc.Width.ToString()), ("높이 (px)", doc.Height.ToString())); if (f == null) return;
        int w = (int)Dialogs.Number(f[0], 1, 8192), h = (int)Dialogs.Number(f[1], 1, 8192); Raster.ValidateSize(w, h);
        Edit("캔버스 크기", () => { doc.Width = w; doc.Height = h; selection = null; }); canvas.Fit();
    }
    void ImageSize()
    {
        var f = Dialogs.Fields(this, "이미지 크기 · 비율 유지", ("새 너비 (px)", doc.Width.ToString())); if (f == null) return;
        int w = (int)Dialogs.Number(f[0], 1, 8192); double factor = (double)w / doc.Width; int h = Math.Max(1, (int)Math.Round(doc.Height * factor)); Raster.ValidateSize(w, h);
        if (doc.Layers.Where(l => l.ParentId == null).Any(l => l.Scale * factor < .01 || l.Scale * factor > 20)) throw new ArgumentException("변경 후 레이어 배율이 지원 범위를 벗어납니다.");
        Edit("이미지 크기", () => { doc.Width = w; doc.Height = h; foreach (var l in doc.Layers.Where(l => l.ParentId == null)) { l.Scale *= factor; l.X *= factor; l.Y *= factor; } selection = null; }); canvas.Fit();
    }
    void CropSelection()
    {
        if (selection == null) { status.Text = "먼저 선택 도구로 자를 영역을 지정하세요."; return; }
        var r = Rect.Intersect(selection.Bounds, new Rect(0, 0, doc.Width, doc.Height)); if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y), w = (int)Math.Ceiling(r.Right) - x, h = (int)Math.Ceiling(r.Bottom) - y;
        Edit("자르기", () => { foreach (var l in doc.Layers.Where(l => l.ParentId == null)) { l.X -= x; l.Y -= y; } doc.Width = w; doc.Height = h; selection = null; }); canvas.Fit();
    }
    void ClearPixels() => PixelFill(true);
    void Fill() => PixelFill(false);
    void PixelFill(bool erase) => EditLayer(erase ? "픽셀 지우기" : "전경색 채우기", l =>
    {
        if (!maskEditing && l.Kind is LayerKind.Group or LayerKind.Adjustment) throw new InvalidOperationException("픽셀 레이어 또는 편집할 마스크를 선택하세요.");
        var pixels = l.Pixels.Clone(); var mask = maskEditing && l.Mask != null ? (byte[])l.Mask.Clone() : null;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
        {
            var p = DocumentFeatures.ToDocumentSpace(doc, l, new Point(x + .5, y + .5)); double weight = selection?.Weight(p.X, p.Y) ?? 1; if (weight <= 0) continue;
            int i = (y * pixels.Width + x) * 4;
            if (mask != null) { int index = y * pixels.Width + x; double target = erase ? 0 : .2126 * foreground.R + .7152 * foreground.G + .0722 * foreground.B; mask[index] = Imaging.Byte(mask[index] * (1 - weight) + target * weight); }
            else if (erase) pixels.Data[i + 3] = Imaging.Byte(pixels.Data[i + 3] * (1 - weight));
            else Imaging.Over(pixels.Data, i, foreground.B / 255.0, foreground.G / 255.0, foreground.R / 255.0, weight * foreground.A / 255.0);
        }
        if (mask != null) l.Mask = mask; else { DocumentFeatures.Rasterize(l); l.Pixels = pixels; }
    });
    void CopyMerged()
    {
        var raster = Imaging.Render(doc);
        if (selection != null)
        {
            var r = Rect.Intersect(selection.Bounds, new Rect(0, 0, doc.Width, doc.Height)); if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
            int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height; var cropped = new Raster(w, h);
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) { int dest = (yy * w + xx) * 4; Array.Copy(raster.Data, ((yy + y) * raster.Width + xx + x) * 4, cropped.Data, dest, 4); cropped.Data[dest + 3] = Imaging.Byte(cropped.Data[dest + 3] * selection.Weight(xx + x + .5, yy + y + .5)); }
            raster = cropped;
        }
        Clipboard.SetImage(raster.Bitmap()); status.Text = "합성 이미지를 클립보드에 복사했습니다.";
    }
    void Paste()
    {
        if (!Clipboard.ContainsImage()) { status.Text = "클립보드에 이미지가 없습니다."; return; }
        var raster = Raster.FromBitmap(Clipboard.GetImage()); Edit("붙여넣기", () => { doc.Add(new Layer { Name = "붙여넣은 이미지", Pixels = raster }); maskEditing = false; });
    }
    void TextAt(Point p) => EditTextAt(p);
    void OnDown(object sender, MouseButtonEventArgs e) => InteractionDown(sender, e);
    static Rect Between(Point a, Point b) => new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    void OnMove(object sender, MouseEventArgs e) => InteractionMove(sender, e);
    void OnUp(object sender, MouseButtonEventArgs e) => InteractionUp(sender, e);
    void CancelGesture()
    {
        ResetInteractionTransient();
        if (CancelTransformHandle()) return;
        if (!dragging && !panning) return;
        dragging = false; panning = false;
        if (beforeGesture != null) doc = beforeGesture;
        beforeGesture = null; stroke = null; canvas.GestureBounds = null; canvas.ReleaseMouseCapture(); canvas.Document = doc; RenderGesture();
    }
    void OnKey(object sender, KeyEventArgs e) => InteractionKey(sender, e);
    void Help() => MessageBox.Show(this,
        "Morupixel · 모루픽셀 0.2 Preview\n독립적인 Windows 이미지 편집기\n\n" +
        "레이어 그룹·클리핑·14 혼합 모드·마스크·6종 조정 레이어·편집 가능한 텍스트\n올가미·마술봉·페더·복제·복구·스머지·액화·내용 인식 채우기·AI 배경 제거\n\n" +
        "Ctrl+S: .moruproj 저장 / Ctrl+Shift+E: 내보내기 미리보기\nCtrl+T: 변형 값 입력 / 모서리: 크기 / Ctrl+모서리: 원근 / 원형 핸들: 회전\nShift+레이어 클릭: 다중 선택 / Ctrl+G: 그룹\nAlt+클릭: 복제·복구 원본 지정 / Shift·Alt: 선택 추가·빼기\n마스크: 흰색 표시·검정 숨김 / D·X: 색상 전환\n\n" +
        "8개 문서 탭 · 최대 128 레이어 · 한 변 8192px · 16.7MP · 레이어 메모리 384MB\nICC 입력은 sRGB로 변환합니다. HEIC는 Windows 코덱이 필요합니다.\nCompositor .comp 파일은 지원하는 속성만 호환됩니다. 자세한 범위는 배포본 docs/PORTING.md를 확인하세요.\n\n" +
        "Compositor 참고: github.com/robbietilton/Compositor\nCopyright © 2026 Wonder Assembly LLC · MIT License\nAI 모델: U²-NetP · Apache-2.0 · 모든 편집은 로컬에서 처리됩니다.", "Morupixel 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
}
