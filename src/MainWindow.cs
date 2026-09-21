using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public enum Tool { Move, Brush, Eraser, RectangleSelect, EllipseSelect, Crop, Rectangle, Ellipse, Gradient, Text, Eyedropper, Hand }

public sealed class MainWindow : Window
{
    Document doc = Demo.Create();
    readonly History history = new();
    readonly CanvasView canvas = new();
    readonly StackPanel properties = new(), layerList = new();
    readonly TextBlock status = Theme.Label("준비", 11), documentTitle = Theme.Label("", 12), zoomLabel = Theme.Label("", 11);
    readonly Dictionary<Tool, Button> toolButtons = [];
    readonly Button colorButton;
    readonly Slider sizeSlider;
    readonly TextBlock brushLabel = Theme.Label("", 11, Theme.Muted);
    Tool tool = Tool.Move;
    Color foreground = Color.FromRgb(162, 232, 205);
    double brushSize = 42, hardness = .8, brushOpacity = 1;
    bool maskEditing, dragging, panning;
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
        Title = "Compositor for Windows"; Width = 1400; Height = 940; MinWidth = 1080; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Theme.Panel; Foreground = Theme.Text;
        FontFamily = new FontFamily("Malgun Gothic"); FontSize = 12; UseLayoutRounding = true;
        var root = new Grid(); Content = root;
        foreach (double h in new[] { 52.0, 34, 52, -1, 32 }) root.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });
        var header = new DockPanel { Background = Theme.Brush("#1C1F26"), LastChildFill = true, Margin = new Thickness(0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0) };
        brand.Children.Add(new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(6), Width = 28, Height = 28, Child = new TextBlock { Text = "C", FontWeight = FontWeights.Bold, FontSize = 20, Foreground = Theme.Brush("#18352D"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        brand.Children.Add(new TextBlock { Text = "  Compositor", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Theme.Label("  WINDOWS / 0.1", 10, Theme.Muted)); DockPanel.SetDock(brand, Dock.Left); header.Children.Add(brand);
        var headActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 12, 4) };
        headActions.Children.Add(Theme.Button("이미지 가져오기", () => Guard(Import), "Ctrl+Shift+O · 현재 작업에 이미지 레이어 추가"));
        headActions.Children.Add(Theme.Button("작업 저장", () => Save(false), "Ctrl+S · 레이어를 보존하는 .cwproj 파일"));
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

        var body = new Grid(); Grid.SetRow(body, 3); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(282) });
        var tools = new StackPanel { Margin = new Thickness(5, 8, 5, 8) };
        var toolDefs = new (Tool Tool, string Icon, string Name, string Key)[] { (Tool.Move, "↖", "이동", "V"), (Tool.RectangleSelect, "▣", "사각 선택", "M"), (Tool.EllipseSelect, "◌", "타원 선택", "Shift+M"), (Tool.Crop, "⌗", "자르기", "C"), (Tool.Brush, "B", "브러시", "B"), (Tool.Eraser, "E", "지우개", "E"), (Tool.Rectangle, "□", "사각형", "U"), (Tool.Ellipse, "○", "타원", "Shift+U"), (Tool.Gradient, "▧", "그라데이션", "G"), (Tool.Text, "T", "텍스트", "T"), (Tool.Eyedropper, "I", "색상 추출", "I"), (Tool.Hand, "✥", "손 도구", "H") };
        foreach (var def in toolDefs)
        {
            var b = Theme.Button(def.Icon, () => SetTool(def.Tool), $"{def.Name} ({def.Key})"); b.Height = 38; b.FontSize = 19; b.Padding = new Thickness(4); b.Margin = new Thickness(4, 2, 4, 2); toolButtons[def.Tool] = b; tools.Children.Add(b);
        }
        body.Children.Add(new ScrollViewer { Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var workspace = new Grid(); workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); workspace.RowDefinitions.Add(new RowDefinition()); Grid.SetColumn(workspace, 1); body.Children.Add(workspace);
        var tab = new DockPanel { Background = Theme.Brush("#242832"), Margin = new Thickness(0, 0, 0, 1) };
        documentTitle.Margin = new Thickness(14, 0, 0, 0); tab.Children.Add(documentTitle); workspace.Children.Add(tab);
        Grid.SetRow(canvas, 1); workspace.Children.Add(canvas); canvas.Document = doc;
        var right = new Grid { Background = Theme.Panel }; right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(270) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) }); right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); Grid.SetColumn(right, 2); body.Children.Add(right);
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
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Guard(() => { foreach (var file in files) { if (Path.GetExtension(file).Equals(".cwproj", StringComparison.OrdinalIgnoreCase)) { if (ConfirmDiscard()) OpenProject(file); } else ImportFiles([file]); } }); };
        Closing += (_, e) => { CancelGesture(); if (!ConfirmDiscard()) e.Cancel = true; };
        Loaded += (_, _) => { history.Reset(doc); UpdateColor(); UpdateBrushLabel(); Refresh(); canvas.Fit(); SetTool(Tool.Move); if (startupPath != null && File.Exists(startupPath)) Guard(() => { if (Path.GetExtension(startupPath).Equals(".cwproj", StringComparison.OrdinalIgnoreCase)) OpenProject(startupPath); else OpenImage(startupPath); }); };
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
        Add("파일", ("새 캔버스…", "Ctrl+N", NewDocument), ("열기…", "Ctrl+O", Open), ("레이어로 가져오기…", "Ctrl+Shift+O", Import), ("저장", "Ctrl+S", () => Save(false)), ("다른 이름으로 저장…", "Ctrl+Shift+S", () => Save(true)), ("PNG / JPEG 내보내기…", "Ctrl+Shift+E", Export), ("샘플 작업 열기", "", () => { if (ConfirmDiscard()) { doc = Demo.Create(); projectPath = null; selection = null; history.Reset(doc); Refresh(); canvas.Fit(); } }));
        Add("편집", ("실행 취소", "Ctrl+Z", Undo), ("다시 실행", "Ctrl+Shift+Z", Redo), ("합성 이미지 복사", "Ctrl+C", CopyMerged), ("이미지 붙여넣기", "Ctrl+V", Paste), ("선택 픽셀 지우기", "Delete", ClearPixels), ("전경색으로 채우기", "Alt+Backspace", Fill));
        Add("이미지", ("캔버스 크기…", "", CanvasSize), ("이미지 크기…", "", ImageSize), ("선택 영역으로 자르기", "", CropSelection));
        Add("레이어", ("레이어 복제", "Ctrl+J", Duplicate), ("이름 변경…", "", Rename), ("변형 값 입력…", "Ctrl+T", Transform), ("가로 뒤집기", "", () => EditLayer("가로 뒤집기", l => l.FlipX = !l.FlipX)), ("세로 뒤집기", "", () => EditLayer("세로 뒤집기", l => l.FlipY = !l.FlipY)), ("마스크 추가", "", AddMask), ("마스크 반전", "", InvertMask), ("마스크 제거", "", () => EditLayer("마스크 제거", l => { l.Mask = null; maskEditing = false; })), ("모든 레이어 병합", "", Flatten), ("레이어 삭제", "", DeleteLayer));
        Add("선택", ("전체 선택", "Ctrl+A", () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); }), ("선택 해제", "Ctrl+D", () => { selection = null; Refresh(false); }));
        Add("보정", ("레벨…", "Ctrl+L", Levels), ("노출…", "", Exposure), ("채도…", "", Saturation), ("흑백", "", () => Adjust("grayscale")), ("색상 반전", "Ctrl+I", () => Adjust("invert")), ("가우시안 흐림…", "", Blur));
        Add("보기", ("화면에 맞춤", "Ctrl+0", () => { canvas.Fit(); UpdateStatus(); }), ("실제 크기", "Ctrl+1", () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); }), ("도움말 / 지원 범위", "F1", Help));
        return menu;
    }
    void Guard(Action action) { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "Compositor", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    void Edit(string label, Action action)
    {
        CancelGesture(); var before = doc.Snapshot();
        try { action(); doc.Validate(); if (!SameDocument(before, doc)) history.Commit(label, before, doc); Refresh(); }
        catch (Exception e) { doc = before; Refresh(); MessageBox.Show(this, e.Message, "Compositor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    void EditLayer(string label, Action<Layer> action)
    {
        if (doc.Active == null) return;
        if (doc.Active.Locked) { status.Text = "잠긴 레이어입니다. 레이어의 잠금을 먼저 해제하세요."; return; }
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
            if (x.Id != y.Id || x.Name != y.Name || x.Visible != y.Visible || x.Locked != y.Locked || x.Opacity != y.Opacity || x.Blend != y.Blend || x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.FlipX != y.FlipX || x.FlipY != y.FlipY || !ReferenceEquals(x.Pixels, y.Pixels) || !ReferenceEquals(x.Mask, y.Mask)) return false;
        }
        return true;
    }
    void SetTool(Tool next)
    {
        CancelGesture(); tool = next;
        foreach (var pair in toolButtons) { pair.Value.Background = pair.Key == tool ? Theme.Brush("#31564A") : Theme.Panel; pair.Value.BorderBrush = pair.Key == tool ? Theme.Accent : Theme.Panel; }
        canvas.ShowLayerBounds = tool == Tool.Move; canvas.Cursor = tool == Tool.Hand ? Cursors.Hand : tool == Tool.Move ? Cursors.SizeAll : Cursors.Cross;
        Refresh(false);
    }
    void Refresh(bool render = true)
    {
        canvas.Document = doc; canvas.Selection = selection;
        if (render) { composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); }
        canvas.InvalidateVisual();
        documentTitle.Text = $"{(history.Dirty(doc) ? "●  " : "")}{doc.Name}   ·   {doc.Width} × {doc.Height} px";
        Title = $"{(history.Dirty(doc) ? "* " : "")}{doc.Name} — Compositor for Windows";
        BuildProperties(); BuildLayers(); UpdateStatus();
    }
    void RenderGesture()
    {
        composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); canvas.InvalidateVisual();
    }
    void UpdateStatus()
    {
        var hint = tool switch { Tool.Move => "선택 레이어를 드래그하여 이동 · Ctrl+T 변형", Tool.Brush => "드래그하여 그리기 · [ ] 크기 조절", Tool.Eraser => "드래그하여 지우기", Tool.Crop => "드래그한 영역으로 캔버스 자르기", Tool.Text => "캔버스를 클릭하여 텍스트 추가", Tool.Gradient => "전경색 → 투명 그라데이션 · 드래그", Tool.Hand => "드래그하여 화면 이동", _ => "캔버스에서 드래그 · Esc 취소" };
        status.Text = (maskEditing ? "마스크 편집 · " : "") + hint + "    |    휠: 확대/축소 · Space+드래그: 화면 이동";
        zoomLabel.Text = $"{doc.Layers.Count} 레이어    {canvas.Zoom * 100:0}%";
    }
    void BuildProperties()
    {
        properties.Children.Clear(); properties.Children.Add(Theme.Label("속성", 13));
        var l = doc.Active;
        if (l == null) { properties.Children.Add(Theme.Label("레이어를 추가하세요.", 12, Theme.Muted)); return; }
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
    }
    void BuildLayers()
    {
        layerList.Children.Clear();
        foreach (var l in doc.Layers.AsEnumerable().Reverse())
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = l.Id == doc.ActiveId ? Theme.Brush("#35483F") : Theme.Brush("#272C35"), Height = 61 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var visible = new CheckBox { IsChecked = l.Visible, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "레이어 표시" };
            visible.Click += (_, _) => Edit("레이어 표시", () => l.Visible = visible.IsChecked == true); grid.Children.Add(visible);
            var thumb = new Image { Source = l.Pixels.Bitmap(), Width = 40, Height = 38, Stretch = Stretch.Uniform, Margin = new Thickness(3) }; Grid.SetColumn(thumb, 1); grid.Children.Add(thumb);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = l.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 3, 3), FontSize = 11 }); text.Children.Add(new TextBlock { Text = $"{l.Blend} · {l.Opacity * 100:0}%{(l.Mask != null ? " · 마스크" : "")}", Foreground = Theme.Muted, FontSize = 9, Margin = new Thickness(7, 0, 0, 0) }); Grid.SetColumn(text, 2); grid.Children.Add(text);
            var locked = Theme.Button(l.Locked ? "●" : "○", () => Edit("잠금", () => l.Locked = !l.Locked), "레이어 잠금 / 해제"); locked.FontSize = 13; locked.Padding = new Thickness(0); locked.Margin = new Thickness(2, 12, 3, 12); Grid.SetColumn(locked, 3); grid.Children.Add(locked);
            grid.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not CheckBox && e.OriginalSource is not Button) { doc.ActiveId = l.Id; maskEditing = false; Refresh(false); e.Handled = true; } };
            layerList.Children.Add(grid);
        }
    }

    void NewDocument()
    {
        var f = Dialogs.Fields(this, "새 캔버스", ("이름", "제목 없음"), ("너비 (px)", "1280"), ("높이 (px)", "800"), ("배경 (투명 / 흰색)", "투명"));
        if (f == null) return; int w = (int)Dialogs.Number(f[1], 1, 8192), h = (int)Dialogs.Number(f[2], 1, 8192); Raster.ValidateSize(w, h);
        if (!ConfirmDiscard()) return;
        var next = new Document { Width = w, Height = h, Name = string.IsNullOrWhiteSpace(f[0]) ? "제목 없음" : f[0] };
        next.Add(new Layer { Name = "레이어 1", Pixels = f[3].Trim() == "흰색" ? Raster.Solid(w, h, Colors.White) : new Raster(w, h) });
        doc = next; projectPath = null; selection = null; maskEditing = false; history.Reset(doc); Refresh(); canvas.Fit();
    }
    bool ConfirmDiscard()
    {
        if (!history.Dirty(doc)) return true;
        var result = MessageBox.Show(this, "변경한 작업을 저장할까요?", "저장하지 않은 작업", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && Save(false);
    }
    const string ImageFilter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|모든 파일|*.*";
    void Open()
    {
        var dialog = new OpenFileDialog { Filter = "이미지 또는 Compositor 작업|*.cwproj;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|모든 파일|*.*" };
        if (dialog.ShowDialog(this) != true || !ConfirmDiscard()) return;
        if (Path.GetExtension(dialog.FileName).Equals(".cwproj", StringComparison.OrdinalIgnoreCase)) OpenProject(dialog.FileName); else OpenImage(dialog.FileName);
    }
    void OpenProject(string path)
    {
        var loaded = ProjectStore.Load(path); doc = loaded; projectPath = path; selection = null; maskEditing = false; history.Reset(doc); Refresh(); canvas.Fit();
    }
    void OpenImage(string path)
    {
        var pixels = Raster.Load(path); var next = new Document { Width = pixels.Width, Height = pixels.Height, Name = Path.GetFileNameWithoutExtension(path) };
        next.Add(new Layer { Name = "원본", Pixels = pixels }); doc = next; projectPath = null; selection = null; maskEditing = false; history.Reset(doc); Refresh(); canvas.Fit();
    }
    void Import()
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }
    void ImportFiles(string[] paths)
    {
        var layers = paths.Select(p => new Layer { Name = Path.GetFileNameWithoutExtension(p), Pixels = Raster.Load(p) }).ToList();
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
                var d = new SaveFileDialog { Filter = "Compositor Windows 작업|*.cwproj", DefaultExt = ".cwproj", FileName = doc.Name };
                if (d.ShowDialog(this) != true) return false; path = d.FileName;
            }
            ProjectStore.Save(doc, path); projectPath = path; history.MarkSaved(doc); Refresh(false); status.Text = "작업 저장 완료 · " + Path.GetFileName(path); return true;
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    void Export()
    {
        var d = new SaveFileDialog { Filter = "PNG (투명 배경 지원)|*.png|JPEG (흰 배경)|*.jpg", FileName = doc.Name, DefaultExt = ".png" };
        if (d.ShowDialog(this) == true) { ProjectStore.Export(doc, d.FileName); status.Text = "이미지 내보내기 완료 · " + Path.GetFileName(d.FileName); }
    }
    void Undo() { CancelGesture(); if (!history.CanUndo) return; doc = history.Undo(doc); maskEditing = false; selection = null; Refresh(); }
    void Redo() { CancelGesture(); if (!history.CanRedo) return; doc = history.Redo(doc); maskEditing = false; selection = null; Refresh(); }
    void Duplicate() => EditLayer("레이어 복제", l => { var copy = l.Snapshot(); copy.Id = Guid.NewGuid(); copy.Name += " 복사"; doc.Add(copy); });
    void DeleteLayer() => EditLayer("레이어 삭제", l => { doc.Layers.Remove(l); doc.ActiveId = doc.Layers.LastOrDefault()?.Id ?? Guid.Empty; maskEditing = false; });
    void Reorder(int delta)
    {
        if (doc.Active == null) return; int old = doc.Layers.IndexOf(doc.Active), next = old + delta;
        if (next < 0 || next >= doc.Layers.Count) return;
        Edit("레이어 순서", () => { var layer = doc.Layers[old]; doc.Layers.RemoveAt(old); doc.Layers.Insert(next, layer); });
    }
    void Rename()
    {
        if (doc.Active == null) return; var f = Dialogs.Fields(this, "레이어 이름", ("이름", doc.Active.Name));
        if (f != null && !string.IsNullOrWhiteSpace(f[0])) EditLayer("레이어 이름", l => l.Name = f[0]);
    }
    void Transform()
    {
        if (doc.Active is not { } l) return;
        var f = Dialogs.Fields(this, "레이어 변형", ("X 위치 (px)", l.X.ToString(CultureInfo.InvariantCulture)), ("Y 위치 (px)", l.Y.ToString(CultureInfo.InvariantCulture)), ("크기 (%)", (l.Scale * 100).ToString(CultureInfo.InvariantCulture)), ("시계방향 회전 (°)", l.Rotation.ToString(CultureInfo.InvariantCulture)));
        if (f == null) return;
        double x = Dialogs.Number(f[0], -100000, 100000), y = Dialogs.Number(f[1], -100000, 100000), scale = Dialogs.Number(f[2], 1, 2000) / 100, rotation = Dialogs.Number(f[3], -36000, 36000);
        EditLayer("레이어 변형", active => { active.X = x; active.Y = y; active.Scale = scale; active.Rotation = rotation; });
    }
    void AddMask() => EditLayer("마스크 추가", l => { if (l.Mask != null) return; l.Mask = new byte[l.Pixels.Width * l.Pixels.Height]; var map = l.Matrix; for (int y = 0; y < l.Pixels.Height; y++) for (int x = 0; x < l.Pixels.Width; x++) { var p = map.Transform(new Point(x + .5, y + .5)); l.Mask[y * l.Pixels.Width + x] = selection == null || selection.Contains(p.X, p.Y) ? (byte)255 : (byte)0; } maskEditing = true; });
    void InvertMask() => EditLayer("마스크 반전", l => { if (l.Mask != null) l.Mask = l.Mask.Select(v => (byte)(255 - v)).ToArray(); });
    void Flatten() => Edit("모든 레이어 병합", () => { var rendered = Imaging.Render(doc); doc.Layers.Clear(); doc.Add(new Layer { Name = "합성 이미지", Pixels = rendered }); maskEditing = false; });
    void Adjust(string kind, double a = 0, double b = 255, double c = 1) => EditLayer("색상 보정", l => l.Pixels = Imaging.Adjust(l, selection, kind, a, b, c));
    void Levels()
    {
        var f = Dialogs.Fields(this, "레벨 보정", ("입력 검정 (0~254)", "0"), ("입력 흰색 (1~255)", "255"), ("감마 (0.1~9.99)", "1"));
        if (f == null) return; double black = Dialogs.Number(f[0], 0, 254), white = Dialogs.Number(f[1], black + 1, 255), gamma = Dialogs.Number(f[2], .1, 9.99); Adjust("levels", black, white, gamma);
    }
    void Exposure() { var f = Dialogs.Fields(this, "노출 보정", ("노출 EV (-5~5)", "0.5")); if (f != null) Adjust("exposure", Dialogs.Number(f[0], -5, 5)); }
    void Saturation() { var f = Dialogs.Fields(this, "채도 보정", ("채도 (-100~100)", "20")); if (f != null) Adjust("saturation", Dialogs.Number(f[0], -100, 100)); }
    void Blur() { var f = Dialogs.Fields(this, "가우시안 흐림", ("반경 (1~30 px)", "5")); if (f != null) { int r = (int)Dialogs.Number(f[0], 1, 30); EditLayer("가우시안 흐림", l => l.Pixels = Imaging.Blur(l, r, selection)); } }
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
        if (doc.Layers.Any(l => l.Scale * factor < .01 || l.Scale * factor > 20)) throw new ArgumentException("변경 후 레이어 배율이 지원 범위를 벗어납니다.");
        Edit("이미지 크기", () => { doc.Width = w; doc.Height = h; foreach (var l in doc.Layers) { l.Scale *= factor; l.X *= factor; l.Y *= factor; } selection = null; }); canvas.Fit();
    }
    void CropSelection()
    {
        if (selection == null) { status.Text = "먼저 선택 도구로 자를 영역을 지정하세요."; return; }
        var r = Rect.Intersect(selection.Bounds, new Rect(0, 0, doc.Width, doc.Height)); if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y), w = (int)Math.Ceiling(r.Right) - x, h = (int)Math.Ceiling(r.Bottom) - y;
        Edit("자르기", () => { foreach (var l in doc.Layers) { l.X -= x; l.Y -= y; } doc.Width = w; doc.Height = h; selection = null; }); canvas.Fit();
    }
    void ClearPixels() => PixelFill(true);
    void Fill() => PixelFill(false);
    void PixelFill(bool erase) => EditLayer(erase ? "픽셀 지우기" : "전경색 채우기", l =>
    {
        var pixels = l.Pixels.Clone(); var map = l.Matrix; var mask = maskEditing && l.Mask != null ? (byte[])l.Mask.Clone() : null;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
        {
            var p = map.Transform(new Point(x + .5, y + .5)); if (selection != null && !selection.Contains(p.X, p.Y)) continue;
            int i = (y * pixels.Width + x) * 4;
            if (mask != null) mask[y * pixels.Width + x] = erase ? (byte)0 : Imaging.Byte(.2126 * foreground.R + .7152 * foreground.G + .0722 * foreground.B);
            else if (erase) pixels.Data[i + 3] = 0;
            else Imaging.Over(pixels.Data, i, foreground.B / 255.0, foreground.G / 255.0, foreground.R / 255.0, 1);
        }
        if (mask != null) l.Mask = mask; else l.Pixels = pixels;
    });
    void CopyMerged()
    {
        var raster = Imaging.Render(doc);
        if (selection != null)
        {
            var r = Rect.Intersect(selection.Bounds, new Rect(0, 0, doc.Width, doc.Height)); if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
            int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height; var cropped = new Raster(w, h);
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) if (selection.Contains(xx + x + .5, yy + y + .5)) Array.Copy(raster.Data, ((yy + y) * raster.Width + xx + x) * 4, cropped.Data, (yy * w + xx) * 4, 4);
            raster = cropped;
        }
        Clipboard.SetImage(raster.Bitmap()); status.Text = "합성 이미지를 클립보드에 복사했습니다.";
    }
    void Paste()
    {
        if (!Clipboard.ContainsImage()) { status.Text = "클립보드에 이미지가 없습니다."; return; }
        var raster = Raster.FromBitmap(Clipboard.GetImage()); Edit("붙여넣기", () => { doc.Add(new Layer { Name = "붙여넣은 이미지", Pixels = raster }); maskEditing = false; });
    }
    void TextAt(Point p)
    {
        var f = Dialogs.Fields(this, "텍스트 레이어", ("텍스트", "새로운 시선"), ("글꼴", "Malgun Gothic"), ("크기 (px)", "64")); if (f == null || string.IsNullOrEmpty(f[0])) return;
        double size = Dialogs.Number(f[2], 6, 600);
        var formatted = new FormattedText(f[0], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(f[1]), size, new SolidColorBrush(foreground), 1);
        int w = Math.Max(1, (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + 12)), h = Math.Max(1, (int)Math.Ceiling(formatted.Height + 8)); Raster.ValidateSize(w, h);
        var pixels = Imaging.Draw(w, h, dc => dc.DrawText(formatted, new Point(4, 0)));
        Edit("텍스트 추가", () => { doc.Add(new Layer { Name = f[0], Pixels = pixels, X = p.X, Y = p.Y }); maskEditing = false; }); SetTool(Tool.Move);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        canvas.Focus(); var screen = e.GetPosition(canvas); var p = canvas.ToDocument(screen);
        if (e.ChangedButton == MouseButton.Middle || tool == Tool.Hand || Keyboard.IsKeyDown(Key.Space))
        { panning = true; screenStart = screen; initialPan = canvas.Pan; canvas.CaptureMouse(); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left || p.X < 0 || p.Y < 0 || p.X >= doc.Width || p.Y >= doc.Height) return;
        if (tool == Tool.Eyedropper)
        { if (composite != null) { int i = ((int)p.Y * doc.Width + (int)p.X) * 4; foreground = Color.FromRgb(composite.Data[i + 2], composite.Data[i + 1], composite.Data[i]); UpdateColor(); } return; }
        if (tool == Tool.Text) { Guard(() => TextAt(p)); return; }
        if (tool is Tool.Brush or Tool.Eraser or Tool.Move && (doc.Active == null || doc.Active.Locked)) { status.Text = "편집할 레이어를 선택하거나 잠금을 해제하세요."; return; }
        beforeGesture = doc.Snapshot(); start = p; dragging = true;
        if (tool is Tool.Brush or Tool.Eraser)
        { stroke = new BrushStroke(doc.Active!, selection, foreground, brushSize, hardness, brushOpacity, tool == Tool.Eraser, maskEditing); stroke.Point(p); RenderGesture(); }
        canvas.CaptureMouse(); e.Handled = true;
    }
    static Rect Between(Point a, Point b) => new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    void OnMove(object sender, MouseEventArgs e)
    {
        if (panning) { canvas.Pan = initialPan + (e.GetPosition(canvas) - screenStart); canvas.InvalidateVisual(); return; }
        if (!dragging) return;
        var p = canvas.ToDocument(e.GetPosition(canvas));
        if (stroke != null) { stroke.Point(p); RenderGesture(); return; }
        if (tool == Tool.Move && doc.Active != null && beforeGesture?.Active != null)
        { doc.Active.X = Math.Clamp(beforeGesture.Active.X + p.X - start.X, -100000, 100000); doc.Active.Y = Math.Clamp(beforeGesture.Active.Y + p.Y - start.Y, -100000, 100000); RenderGesture(); return; }
        canvas.GestureBounds = Between(start, new Point(Math.Clamp(p.X, 0, doc.Width), Math.Clamp(p.Y, 0, doc.Height))); canvas.EllipseGesture = tool is Tool.Ellipse or Tool.EllipseSelect; canvas.InvalidateVisual();
    }
    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (panning) { panning = false; canvas.ReleaseMouseCapture(); return; }
        if (!dragging) return;
        var p = canvas.ToDocument(e.GetPosition(canvas)); var bounds = canvas.GestureBounds;
        dragging = false; canvas.ReleaseMouseCapture(); canvas.GestureBounds = null;
        if (stroke != null || tool == Tool.Move)
        {
            if (beforeGesture != null && !SameDocument(beforeGesture, doc)) history.Commit(tool == Tool.Move ? "레이어 이동" : "브러시", beforeGesture, doc);
            beforeGesture = null; stroke = null; Refresh(); return;
        }
        beforeGesture = null;
        if (tool == Tool.Gradient ? (p - start).Length < 1 : bounds == null || bounds.Value.Width < 1 || bounds.Value.Height < 1) { canvas.InvalidateVisual(); return; }
        if (tool is Tool.RectangleSelect or Tool.EllipseSelect or Tool.Crop)
        { selection = new Selection(bounds!.Value, tool == Tool.EllipseSelect); if (tool == Tool.Crop) CropSelection(); else Refresh(false); }
        else if (tool is Tool.Rectangle or Tool.Ellipse)
        {
            var r = bounds!.Value; int w = Math.Max(1, (int)Math.Ceiling(r.Width)), h = Math.Max(1, (int)Math.Ceiling(r.Height));
            var pixels = Imaging.Draw(w, h, dc => { if (tool == Tool.Ellipse) dc.DrawEllipse(new SolidColorBrush(foreground), null, new Point(w / 2.0, h / 2.0), w / 2.0, h / 2.0); else dc.DrawRectangle(new SolidColorBrush(foreground), null, new Rect(0, 0, w, h)); });
            Edit("도형 추가", () => { doc.Add(new Layer { Name = tool == Tool.Ellipse ? "타원" : "사각형", Pixels = pixels, X = r.X, Y = r.Y }); maskEditing = false; });
        }
        else if (tool == Tool.Gradient)
        {
            Edit("그라데이션", () =>
            {
                var pixels = new Raster(doc.Width, doc.Height); Vector delta = p - start; double length = delta.LengthSquared;
                for (int y = 0; y < doc.Height; y++) for (int x = 0; x < doc.Width; x++)
                {
                    if (selection != null && !selection.Contains(x + .5, y + .5)) continue;
                    double t = Math.Clamp(Vector.Multiply(new Point(x + .5, y + .5) - start, delta) / Math.Max(1, length), 0, 1); int i = (y * doc.Width + x) * 4;
                    pixels.Data[i] = foreground.B; pixels.Data[i + 1] = foreground.G; pixels.Data[i + 2] = foreground.R; pixels.Data[i + 3] = Imaging.Byte((1 - t) * 255 * brushOpacity);
                }
                doc.Add(new Layer { Name = "그라데이션", Pixels = pixels }); maskEditing = false;
            });
        }
    }
    void CancelGesture()
    {
        if (!dragging && !panning) return;
        dragging = false; panning = false;
        if (beforeGesture != null) doc = beforeGesture;
        beforeGesture = null; stroke = null; canvas.GestureBounds = null; canvas.ReleaseMouseCapture(); canvas.Document = doc; RenderGesture();
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Action? action = null;
        if (ctrl) action = key switch
        {
            Key.N => NewDocument, Key.O => shift ? Import : Open, Key.S => () => Save(shift), Key.E when shift => Export,
            Key.Z => shift ? Redo : Undo, Key.Y => Redo, Key.J => Duplicate, Key.T => Transform, Key.L => Levels,
            Key.I => () => Adjust("invert"), Key.D => () => { selection = null; Refresh(false); },
            Key.A => () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); },
            Key.C => CopyMerged, Key.V => Paste, Key.D0 => () => { canvas.Fit(); UpdateStatus(); },
            Key.D1 => () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); }, _ => null
        };
        else if (alt && key == Key.Back) action = Fill;
        else action = key switch
        {
            Key.V => () => SetTool(Tool.Move), Key.B => () => SetTool(Tool.Brush), Key.E => () => SetTool(Tool.Eraser),
            Key.M => () => SetTool(shift ? Tool.EllipseSelect : Tool.RectangleSelect), Key.C => () => SetTool(Tool.Crop),
            Key.U => () => SetTool(shift ? Tool.Ellipse : Tool.Rectangle), Key.G => () => SetTool(Tool.Gradient),
            Key.T => () => SetTool(Tool.Text), Key.I => () => SetTool(Tool.Eyedropper), Key.H => () => SetTool(Tool.Hand),
            Key.Delete => ClearPixels, Key.Escape => () => { CancelGesture(); selection = null; Refresh(false); },
            Key.OemOpenBrackets => () => { brushSize = Math.Max(1, brushSize - 5); UpdateBrushLabel(); },
            Key.OemCloseBrackets => () => { brushSize = Math.Min(300, brushSize + 5); UpdateBrushLabel(); },
            Key.D => () => { foreground = Colors.Black; UpdateColor(); }, Key.X => () => { foreground = foreground == Colors.Black ? Colors.White : Colors.Black; UpdateColor(); },
            Key.Left => () => EditLayer("레이어 이동", l => l.X -= shift ? 10 : 1), Key.Right => () => EditLayer("레이어 이동", l => l.X += shift ? 10 : 1),
            Key.Up => () => EditLayer("레이어 이동", l => l.Y -= shift ? 10 : 1), Key.Down => () => EditLayer("레이어 이동", l => l.Y += shift ? 10 : 1),
            Key.F1 => Help, _ => null
        };
        if (action != null) { e.Handled = true; Guard(action); }
    }
    void Help() => MessageBox.Show(this,
        "Compositor for Windows 0.1 — 비공식 Windows 재구현\n\n" +
        "PNG/JPEG/BMP/TIFF/GIF 열기 · 레이어/마스크/혼합 · 브러시/지우개 · 선택/자르기 · 도형/텍스트 · 색상 보정\n\n" +
        "Ctrl+S: 레이어를 보존한 .cwproj 저장\nCtrl+Shift+E: PNG/JPEG 내보내기\nCtrl+T: 위치·배율·회전 / Ctrl+Z: 실행 취소\n마스크: 흰색은 표시, 검정은 숨김 (D/X로 전환)\n\n" +
        "현재 제한: PSD 및 Mac 작업 파일 호환, AI 배경 제거, 복구/복제 도장, 그룹, 곡선, 다중 문서 탭은 미지원. 텍스트는 추가 시 래스터화됩니다.\n" +
        "최대 32 레이어, 한 변 8192px, 전체 16.7MP. EXIF 회전/ICC 색상 관리 및 인쇄용 CMYK 미지원.\n\n" +
        "원본: github.com/robbietilton/Compositor\nCopyright © 2026 Wonder Assembly LLC · MIT License\n인터넷 연결 없이 로컬에서 편집합니다.", "도움말", MessageBoxButton.OK, MessageBoxImage.Information);
}
