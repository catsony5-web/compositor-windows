using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public enum Tool { Move, Brush, Eraser, RectangleSelect, EllipseSelect, Crop, Rectangle, Ellipse, Gradient, Text, Eyedropper, Hand, Lasso, PolygonLasso, MagicWand, CloneStamp, Heal, Smudge, Liquify, BlurBrush, Bucket }

public sealed partial class MainWindow : Window
{
    // An unregistered, empty model keeps editing helpers non-null; it is never
    // displayed, rendered or saved while the workspace has no document tab.
    Document doc = new() { Width = 1, Height = 1 };
    History history = new();
    readonly CanvasView canvas = new();
    readonly StackPanel properties = new();
    readonly LayerList layerList = new();
    readonly TextBlock status = Theme.Label("준비", 11), documentTitle = Theme.Label("", 12), zoomLabel = Theme.Label("", 11);
    readonly Dictionary<Tool, Button> toolButtons = [];
    readonly ColorSwatches colorSwatches;
    readonly StackPanel brushOptions = new() { Orientation = Orientation.Horizontal }, opacityOptions = new() { Orientation = Orientation.Horizontal }, gradientOptions = new() { Orientation = Orientation.Horizontal };
    readonly Slider sizeSlider, hardnessSlider;
    readonly CheckBox autoSelectToggle;
    readonly TextBlock brushLabel = Theme.Label("", 11, Theme.Muted);
    Tool tool = Tool.Move;
    Color foreground = Color.FromRgb(188, 217, 250);
    Color backgroundColor = Colors.White;
    bool gradientToBackground;
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
        Title = "Morupixel · 모루픽셀"; Icon = Theme.BrandIcon; Width = 1480; Height = 980; MinWidth = 1200; MinHeight = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Theme.Panel; Foreground = Theme.Text;
        FontFamily = Theme.UiFont; FontSize = Theme.BodySize; UseLayoutRounding = true;
        var root = new Grid { Background = Theme.Header }; Content = root;
        foreach (double h in new[] { 48.0, 30, 46, -1, 28 }) root.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });
        root.Children.Add(BuildHeader());

        var menu = BuildMenu(); Grid.SetRow(menu, 1); root.Children.Add(menu);
        var optionHost = new DockPanel { Background = Theme.Panel, Margin = new Thickness(12, 4, 12, 4), LastChildFill = true };
        Grid.SetRow(optionHost, 2); root.Children.Add(optionHost);
        var viewport = BuildViewportActions(); DockPanel.SetDock(viewport, Dock.Right); optionHost.Children.Add(viewport);
        var options = new StackPanel { Orientation = Orientation.Horizontal };
        optionHost.Children.Add(DocumentControl(new ScrollViewer { Content = options, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }));
        options.Children.Add(toolCaption);
        BuildBucketOptions(); options.Children.Add(bucketOptions);
        brushOptions.Children.Add(Theme.Label("크기"));
        sizeSlider = Slider(1, MaxBrushSize, brushSize, 115, v => { brushSize = v; UpdateBrushLabel(); }); brushOptions.Children.Add(sizeSlider);
        brushLabel.Width = 50; brushOptions.Children.Add(brushLabel); brushOptions.Children.Add(Theme.Label("경도"));
        hardnessSlider = Slider(0, 1, hardness, 75, v => { hardness = v; studioHardness?.SetValue(v * 100); }); brushOptions.Children.Add(hardnessSlider); options.Children.Add(brushOptions);
        opacityOptions.Children.Add(Theme.Label("농도")); opacityOptions.Children.Add(Slider(.01, 1, brushOpacity, 75, v => brushOpacity = v)); options.Children.Add(opacityOptions);
        var gradientMode = new ComboBox { ItemsSource = new[] { "전경색 → 투명", "전경색 → 배경색" }, SelectedIndex = 0, Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5) };
        gradientMode.SelectionChanged += (_, _) => { gradientToBackground = gradientMode.SelectedIndex == 1; UpdateStatus(); }; gradientOptions.Children.Add(gradientMode); options.Children.Add(gradientOptions);
        autoSelectToggle = new CheckBox { Content = "자동 선택", IsChecked = true, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), ToolTip = "이동 도구(V): 클릭한 대상의 레이어 선택. 끄면 목록에서 선택한 레이어만 이동합니다." };
        System.Windows.Automation.AutomationProperties.SetName(autoSelectToggle, "캔버스 레이어 자동 선택");
        options.Children.Add(autoSelectToggle);
        autoSelectToggle.Unchecked += (_, _) => ClearPointerHover();

        var body = new Grid { Margin = new Thickness(10, 6, 10, 8) }; Grid.SetRow(body, 3); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); body.ColumnDefinitions.Add(leftPanelColumn); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(rightPanelColumn);
        var tools = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 8, 4, 8) };
        var toolDefs = new (Tool Tool, string Icon, string Name, string Key)[] { (Tool.Move, "↖", "이동", "V"), (Tool.RectangleSelect, "▣", "사각 선택", "M"), (Tool.EllipseSelect, "◌", "타원 선택", "Shift+M"), (Tool.Crop, "⌗", "자르기", "C"), (Tool.Brush, "B", "브러시", "B"), (Tool.Eraser, "E", "지우개", "E"), (Tool.Rectangle, "□", "사각형", "U"), (Tool.Ellipse, "○", "타원", "Shift+U"), (Tool.Bucket, "▰", "버킷 채우기", "G"), (Tool.Gradient, "▧", "그라데이션", "Shift+G"), (Tool.Text, "T", "텍스트", "T"), (Tool.Eyedropper, "I", "색상 추출", "I"), (Tool.Hand, "✥", "손 도구", "H") };
        foreach (var def in toolDefs)
        {
            var b = Theme.Button(def.Icon, () => SetTool(def.Tool), $"{def.Name} ({def.Key})"); b.Content = ToolIcons.Create(def.Tool); System.Windows.Automation.AutomationProperties.SetName(b, def.Name); b.Height = 38; b.FontSize = 19; b.Padding = new Thickness(2); b.Margin = new Thickness(2); toolButtons[def.Tool] = b; tools.Children.Add(b);
        }
        foreach (var def in AdvancedToolDefinitions())
        {
            var b = Theme.Button(def.Icon, () => SetTool(def.Tool), $"{def.Name} ({def.Key})"); b.Content = ToolIcons.Create(def.Tool); System.Windows.Automation.AutomationProperties.SetName(b, def.Name); b.Height = 40; b.FontSize = 15; b.Padding = new Thickness(1); b.Margin = new Thickness(2); toolButtons[def.Tool] = b; tools.Children.Add(b);
        }
        var toolColumn = new StackPanel(); toolColumn.Children.Add(tools);
        workspaceTools = tools; photoToolOrder = toolButtons.Keys.ToArray();
        toolColumn.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(14, 0, 14, 0) });
        colorSwatches = new ColorSwatches(() => ChooseColor(false), () => ChooseColor(true), SwapColors, ResetColors); toolColumn.Children.Add(colorSwatches);
        body.Children.Add(new GlassPanel { Margin = new Thickness(0, 0, 7, 0), Child = new ScrollViewer { Content = toolColumn, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var workspace = new Grid(); workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) }); workspace.RowDefinitions.Add(new RowDefinition()); Grid.SetColumn(workspace, 2); body.Children.Add(workspace);
        workspace.Children.Add(BuildDocumentStrip());
        Grid.SetRow(canvas, 1); workspace.Children.Add(canvas);
        emptyWorkspace = BuildEmptyWorkspace(); Grid.SetRow(emptyWorkspace, 1); workspace.Children.Add(emptyWorkspace);
        var left = new ScrollViewer { Content = leftPanels, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(left, 1); body.Children.Add(left);
        var right = BuildInspectorPanel(); Grid.SetColumn(right, 3); body.Children.Add(right);
        var bottom = new DockPanel { Background = Theme.Header }; status.Margin = new Thickness(12, 0, 8, 0); DockPanel.SetDock(zoomLabel, Dock.Right); zoomLabel.Margin = new Thickness(8, 0, 16, 0); bottom.Children.Add(zoomLabel); bottom.Children.Add(status); Grid.SetRow(bottom, 4); root.Children.Add(bottom);

        canvas.MouseDown += OnDown; canvas.MouseMove += OnMove; canvas.MouseUp += OnUp;
        Loaded += (_, _) => LoadCustomBrushTips();
        canvas.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            e.Cursor = HasDocument ? CanvasCursorAt(canvas.ToDocument(e.GetPosition(canvas)), Keyboard.IsKeyDown(Key.Space)) : Cursors.Arrow;
            e.Handled = true;
        }), handledEventsToo: true);
        canvas.LostMouseCapture += (_, _) => { if (dragging || resizingBrush) CancelGesture(); };
        canvas.MouseWheel += (_, e) => { ClearPointerHover(); canvas.ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(canvas)); UpdateStatus(); e.Handled = true; };
        PreviewKeyDown += OnKey; AllowDrop = true;
        PreviewKeyUp += (_, e) => { var key = e.Key == Key.System ? e.SystemKey : e.Key; if (suppressAltMenu && key is Key.LeftAlt or Key.RightAlt) { suppressAltMenu = false; e.Handled = true; } if (key is Key.Space or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) UpdatePointerModifiers(); };
        Deactivated += (_, _) => { if (resizingBrush) EndBrushResize(true); suppressAltMenu = false; ClearPointerHover(); };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Guard(() => { foreach (var file in files) { var ext = Path.GetExtension(file).ToLowerInvariant(); if (ext is ".moruproj" or ".cwproj" or ".comp" || CompatibilityImport.Supports(file)) OpenPath(file); else ImportFiles([file]); } }); };
        Closing += (_, e) => { CancelGesture(); if (!ConfirmAllTabs()) e.Cancel = true; else { CloseFloatingPanels(); StopRenderingForShutdown(); } };
        Loaded += (_, _) => InitializeStartup();
        canvas.MouseLeave += (_, _) => { if (!resizingBrush) canvas.BrushPoint = null; ClearPointerHover(); if (!dragging && !panning) lastPointerScreen = null; canvas.InvalidateVisual(); };
        history.Reset(doc); UpdateColor(); UpdateBrushLabel(); UpdateToolOptions(); UpdateDocumentAvailability(); UpdateStatus();
        SizeChanged += (_, _) => studioScroll.Height = PreferredStudioHeight(ActualHeight);
    }

    static Slider Slider(double min, double max, double value, double width, Action<double> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5), ToolTip = "드래그하여 조절" };
        slider.ValueChanged += (_, _) => changed(slider.Value); return slider;
    }
    void Guard(Action action) { try { action(); } catch (Exception e) { if (headlessTesting) throw; MessageBox.Show(this, e.Message, "Morupixel", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    void Edit(string label, Action action)
    {
        if (!HasDocument) return;
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
    void UpdateColor() { colorSwatches.SetColors(foreground, backgroundColor); UpdateStudioColor(); }
    void UpdateBrushLabel()
    {
        string label = $"{brushSize:0}px";
        if (brushLabel.Text != label) brushLabel.Text = label;
        if (sizeSlider != null && Math.Abs(sizeSlider.Value - brushSize) > .001) sizeSlider.Value = brushSize;
        studioDiameter?.SetValue(brushSize);
        if (hardnessSlider != null && Math.Abs(hardnessSlider.Value - hardness) > .001) hardnessSlider.Value = hardness;
        double radius = brushSize / 2;
        if (canvas.BrushRadius != radius) { canvas.BrushRadius = radius; canvas.InvalidateVisual(); }
    }
    // UI-only changes and no-op commands must not discard the redo stack or dirty the file.
    static bool SameDocument(Document a, Document b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Dpi != b.Dpi || a.Name != b.Name || a.Layers.Count != b.Layers.Count) return false;
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var x = a.Layers[i]; var y = b.Layers[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Visible != y.Visible || x.Locked != y.Locked || x.Opacity != y.Opacity || x.Blend != y.Blend || x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.FlipX != y.FlipX || x.FlipY != y.FlipY || x.ScaleX != y.ScaleX || x.ScaleY != y.ScaleY || x.Kind != y.Kind || x.ParentId != y.ParentId || x.Clipped != y.Clipped || x.Warp != y.Warp || x.Shape != y.Shape || x.Text != y.Text || !DocumentFeatures.SameAdjustment(x.Adjustment, y.Adjustment) || !ReferenceEquals(x.Pixels.Data, y.Pixels.Data) || !ReferenceEquals(x.Mask, y.Mask)) return false;
        }
        return true;
    }
    void SetTool(Tool next) => ChangeInteractionTool(next);
    void Refresh(bool render = true)
    {
        ClearPointerHover();
        selectedLayers.RemoveWhere(id => !doc.Layers.Any(l => l.Id == id));
        canvas.Document = HasDocument ? doc : null; canvas.Selection = HasDocument ? selection : null;
        UpdateDocumentAvailability();
        if (render && HasDocument) QueueRender();
        else if (!dragging) ClearTextMovePreview();
        canvas.InvalidateVisual();
        documentTitle.Text = HasDocument ? $"{(history.Dirty(doc) ? "●  " : "")}{doc.Name}   ·   {doc.Width} × {doc.Height} px" : "";
        Title = HasDocument ? $"{(history.Dirty(doc) ? "* " : "")}{doc.Name} — Morupixel" : "Morupixel · 모루픽셀";
        BuildProperties(); BuildLayers(); UpdateStatus(); RebuildTabs();
    }
    void RenderGesture()
    {
        QueueRender(dragging); canvas.InvalidateVisual();
    }
    void UpdateStatus()
    {
        if (!HasDocument) { status.Text = ""; status.ToolTip = null; zoomLabel.Text = ""; return; }
        var hint = tool switch { Tool.Move => "클릭: 레이어 선택 · 드래그: 이동 · 자동 선택을 끄면 선택한 레이어 유지 · Ctrl+T 변형", Tool.Brush => "드래그하여 그리기 · Alt+좌우 드래그 / [ ] 크기 조절", Tool.Eraser => "드래그하여 지우기 · Alt+좌우 드래그: 크기", Tool.Crop => "드래그한 영역으로 캔버스 자르기", Tool.Text => "캔버스를 클릭하여 텍스트 추가", Tool.Bucket => "클릭: 전경색으로 영역 채우기 · 허용 오차·연결 영역 조절 · Esc 취소", Tool.Gradient => gradientToBackground ? "전경색 → 배경색 그라데이션 · 드래그" : "전경색 → 투명 그라데이션 · 드래그", Tool.Hand => "드래그하여 화면 이동", _ => "캔버스에서 드래그 · Esc 취소" };
        status.Text = ToolDisplayName(tool) + (maskEditing ? " · 마스크" : "");
        status.ToolTip = hint + (tool == Tool.Move ? "\n자석 정렬 · Alt: 스냅 잠시 해제 · Shift: 가로/세로 고정" : "") + "\n휠: 확대/축소 · Space+드래그: 화면 이동";
        zoomLabel.Text = $"{doc.Layers.Count} 레이어    {canvas.Zoom * 100:0.#}%";
    }
    void SelectLayer(Guid id)
    {
        // Row buttons are not focusable; commit the current inspector value before
        // changing ActiveId so its blur handler cannot silently discard the edit.
        CommitFocusedInspectorField();
        CancelGesture(); doc.ActiveId = id; maskEditing = false;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) selectedLayers.Clear();
        if (id != Guid.Empty) selectedLayers.Add(id);
        RevealLayerSelection(id);
        Refresh(false); canvas.Focus();
    }

    void NewDocument()
    {
        var dialog = new NewDocumentDialog(this);
        if (dialog.ShowDialog() == true && dialog.Result is { } document) AddTab(document, null);
    }
    bool ConfirmDiscard(Func<string, DocumentCloseChoice>? choose = null, Func<bool>? save = null)
    {
        if (!HasDocument) return true;
        if (!history.Dirty(doc)) return true;
        var choice = choose != null ? choose(doc.Name) : AskToSaveChanges();
        // The document stays open if saving is cancelled or fails. Dismissing
        // the dialog never becomes an implicit discard decision.
        return choice == DocumentCloseChoice.Discard || choice == DocumentCloseChoice.Save && (save?.Invoke() ?? Save(false));
    }
    DocumentCloseChoice AskToSaveChanges()
    {
        var dialog = new SaveChangesDialog(this, doc.Name);
        dialog.ShowDialog(); return dialog.Choice;
    }
    const string ImageFilter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.heic;*.heif|모든 파일|*.*";
    void Open()
    {
        var dialog = new OpenFileDialog { Filter = CompatibilityImport.Filter, Multiselect = true };
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
        var dialog = new OpenFileDialog { Filter = CompatibilityImport.Filter.Replace("*.moruproj;*.cwproj;", ""), Multiselect = true };
        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }
    void ImportFiles(string[] paths)
    {
        if (!HasDocument) { foreach (var path in paths) OpenPath(path); return; }
        var layers = new List<Layer>();
        foreach (var path in paths)
        {
            if (CompatibilityImport.Supports(path))
            {
                var imported = ReadCompatibilityDocument(path, placeAsLayer: true); if (imported == null) return;
                layers.AddRange(CompatibilityImport.PlacementLayers(imported, doc.Width, doc.Height));
            }
            else
            {
                var layer = new Layer { Name = Path.GetFileNameWithoutExtension(path), Pixels = ImportExport.LoadImage(path) };
                layer.Scale = Math.Min(1, Math.Min(doc.Width / (double)layer.Pixels.Width, doc.Height / (double)layer.Pixels.Height));
                layer.X = (doc.Width - layer.Pixels.Width * layer.Scale) / 2; layer.Y = (doc.Height - layer.Pixels.Height * layer.Scale) / 2; layers.Add(layer);
            }
        }
        var candidate = doc.Snapshot(); foreach (var layer in layers) candidate.Add(layer); candidate.Validate();
        Edit("이미지 가져오기", () => { doc = candidate; maskEditing = false; });
    }
    bool Save(bool saveAs)
    {
        if (!HasDocument) return false;
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
        if (!HasDocument) return;
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
    void Levels() => QuickAdjustment("levels");
    void Exposure() => QuickAdjustment("exposure");
    void Saturation() => QuickAdjustment("saturation");
    void Blur() => QuickAdjustment("blur");
    void CanvasSize()
    {
        var f = Dialogs.Fields(this, "캔버스 크기 · 좌측 상단 기준", ("너비 (px)", doc.Width.ToString()), ("높이 (px)", doc.Height.ToString())); if (f == null) return;
        int w = (int)Dialogs.Number(f[0], 1, Raster.MaxDimension), h = (int)Dialogs.Number(f[1], 1, Raster.MaxDimension); Raster.ValidateSize(w, h);
        Edit("캔버스 크기", () => { doc.Width = w; doc.Height = h; selection = null; }); canvas.Fit();
    }
    void ImageSize()
    {
        var f = Dialogs.Fields(this, "이미지 크기 · 비율 유지", ("새 너비 (px)", doc.Width.ToString())); if (f == null) return;
        int w = (int)Dialogs.Number(f[0], 1, Raster.MaxDimension); double factor = (double)w / doc.Width;
        // Preserve the validation failure for an oversized proportional height without overflowing int.
        int h = (int)Math.Clamp(Math.Round(doc.Height * factor), 1, (double)Raster.MaxDimension + 1); Raster.ValidateSize(w, h);
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
    void CopyMerged()
    {
        if (!HasDocument) return;
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
        PasteRaster(Raster.FromBitmap(Clipboard.GetImage()));
    }
    void PasteRaster(Raster raster)
    {
        if (!HasDocument)
        {
            var document = new Document { Width = raster.Width, Height = raster.Height, Name = "붙여넣은 이미지" };
            document.Add(new Layer { Name = "붙여넣은 이미지", Pixels = raster }); AddTab(document, null); return;
        }
        Edit("붙여넣기", () => { doc.Add(new Layer { Name = "붙여넣은 이미지", Pixels = raster }); maskEditing = false; });
    }
    void TextAt(Point p) => EditTextAt(p);
    void OnDown(object sender, MouseButtonEventArgs e) => InteractionDown(sender, e);
    static Rect Between(Point a, Point b) => new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    void OnMove(object sender, MouseEventArgs e) => InteractionMove(sender, e);
    void OnUp(object sender, MouseButtonEventArgs e) => InteractionUp(sender, e);
    void CancelGesture()
    {
        if (resizingBrush) EndBrushResize(true);
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
        "Ctrl+S: .moruproj 저장 / Ctrl+Shift+E: 내보내기 미리보기\nCtrl+T: 변형 값 입력 / 모서리: 크기 / Ctrl+모서리: 원근 / 원형 핸들: 회전\nShift+레이어 클릭: 다중 선택 / Ctrl+G: 그룹\nAlt+클릭: 복제·복구 원본 지정 / Shift·Alt: 선택 추가·빼기\n마스크: 흰색 표시·검정 숨김 / D: 검정·흰색 초기화 / X: 전경·배경 교환\nAlt+좌우 드래그: 브러시 크기 (1~1000px) / Esc: 크기 변경 취소\nAlt+Delete: 전경색 채우기 / Ctrl+Delete: 배경색 채우기 (Backspace도 가능)\nG: 버킷 채우기 / Shift+G: 그라데이션\n텍스트 속성: Enter 줄바꿈 / Ctrl+Enter 적용 / 숫자·글꼴 입력 Enter 적용\n\n" +
        $"8개 문서 탭 · 최대 {Document.MaxNodes:N0}개 객체·그룹 (이미지·조정 {Document.MaxLayers}개) · 한 변 {Raster.MaxDimension:N0}px · {Raster.MaxPixels / 1_000_000.0:N1}MP · 레이어 메모리 {Document.MaxLayerBytes / (1024.0 * 1024 * 1024):0.#}GiB\n실제 작업 가능 크기는 사용 가능한 메모리와 편집 작업에 따라 달라집니다.\nICC 입력은 sRGB로 변환합니다. HEIC는 Windows 코덱이 필요합니다.\nCompositor .comp 파일은 지원하는 속성만 호환됩니다. 자세한 범위는 배포본 docs/PORTING.md를 확인하세요.\n\n" +
        "Compositor 참고: github.com/robbietilton/Compositor\nCopyright © 2026 Wonder Assembly LLC · MIT License\nAI 모델: U²-NetP · Apache-2.0 · 모든 편집은 로컬에서 처리됩니다.", "Morupixel 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
}
