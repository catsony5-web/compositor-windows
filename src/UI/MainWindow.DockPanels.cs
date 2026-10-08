using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// The panels only the 간결한 화면 dock shows. Each reads the editor's own state and runs the
// editor's own commands: color and swatches are ColorPalettePanel parts, patterns the material
// palette, adjustments the same adjustment-layer commands as the 보정 tab, history the undo
// stack. Panels refresh only while their tab is open (UpdateDockPanels after edits,
// UpdateDockAfterRender after a render).
public sealed partial class MainWindow
{
    ColorPalettePanel? dockPicker, dockSwatchPanel;
    ColorSwatches? dockColorChips;
    TextBlock? dockRgb;
    StackPanel? dockGradientList, dockPatternHost, dockArtboardList, dockHistoryList;
    object? dockPatternKey;
    internal NavigatorView? navigator;
    Slider? navigatorZoom;
    TextBlock? navigatorZoomText;
    bool syncingNavigatorZoom;
    internal HistogramView? dockHistogram;
    internal TextBlock? dockHistogramStats;
    internal TextBlock? infoPosition, infoColor, infoDocument, infoSelection, infoZoom;
    Border? infoSwatch;
    Point? infoPointer;
    Color[] dockRecentShown = [];
    static readonly DrawingBrush TransparencyChecker = CreateChecker();

    static DrawingBrush CreateChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze(); return brush;
    }

    static ScrollViewer DockScroll(UIElement content) => Density.Mark(new ScrollViewer
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8, 6, 8, 8), Focusable = false
    }, DensityRole.Keep);

    static TextBlock DockCaption(string text)
    {
        var caption = Theme.Label(text, Theme.CaptionSize, Theme.Muted); caption.Margin = new Thickness(0, 0, 0, 4);
        return caption;
    }

    // ---- 색상 · 견본 · 그라데이션 · 패턴 ----------------------------------------------------

    UIElement BuildDockColor()
    {
        var panel = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new ColumnDefinition());
        dockColorChips = new ColorSwatches(() => ChooseColor(false), () => ChooseColor(true), SwapColors, ResetColors) { VerticalAlignment = VerticalAlignment.Top };
        dockColorChips.SetCompact(true); dockColorChips.Margin = new Thickness(0, 6, 8, 0);
        top.Children.Add(dockColorChips);
        dockPicker = new ColorPalettePanel(ColorPaletteParts.Picker) { PadHeight = 112, Margin = new Thickness(0), ShowValues = false };
        dockPicker.ColorChanged += color => { foreground = color; UpdateColor(); };
        Grid.SetColumn(dockPicker, 1); top.Children.Add(dockPicker);
        panel.Children.Add(top);
        dockRgb = Theme.Label("", Theme.CaptionSize, Theme.Muted); dockRgb.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetName(dockRgb, "전경색 RGB 값");
        panel.Children.Add(dockRgb);
        UpdateDockColor();
        // The pad takes the group's free height, like a picker that fills its panel.
        var scroll = DockScroll(panel);
        scroll.SizeChanged += (_, e) => { if (e.HeightChanged) dockPicker.PadHeight = Math.Clamp(e.NewSize.Height - 96, 64, 240); };
        return scroll;
    }

    UIElement BuildDockSwatches()
    {
        dockSwatchPanel = new ColorPalettePanel(ColorPaletteParts.Recent | ColorPaletteParts.Swatches | ColorPaletteParts.Tones | ColorPaletteParts.Harmony) { Margin = new Thickness(0) };
        dockSwatchPanel.ColorChanged += color => { foreground = color; UpdateColor(); };
        dockSwatchPanel.SetColor(foreground); dockRecentShown = ColorPalettePanel.RecentColors.ToArray();
        return DockScroll(dockSwatchPanel);
    }

    void UpdateDockColor()
    {
        dockColorChips?.SetColors(foreground, backgroundColor);
        dockPicker?.SetColor(foreground);
        dockSwatchPanel?.SetColor(foreground);
        if (dockRgb != null) dockRgb.Text = $"R {foreground.R}  G {foreground.G}  B {foreground.B}";
        RefreshDockRecent();
        if (dockGradientList != null) UpdateDockGradients();
    }

    // The recent row is shared by every palette; rebuild the dock's copy only when it changed.
    void RefreshDockRecent()
    {
        if (dockSwatchPanel == null || ColorPalettePanel.RecentColors.SequenceEqual(dockRecentShown)) return;
        dockRecentShown = ColorPalettePanel.RecentColors.ToArray();
        dockSwatchPanel.RefreshRecent();
    }

    UIElement BuildDockGradients()
    {
        var panel = new StackPanel();
        panel.Children.Add(DockCaption("그라데이션 도구가 칠할 색 흐름"));
        dockGradientList = new StackPanel(); panel.Children.Add(dockGradientList);
        panel.Children.Add(Theme.ActionRow("그라데이션 맵 조정 레이어", Run(() => ShowAdjustment(AdjustmentKind.GradientMap)), "명암에 따라 색을 입히는 조정 레이어 추가", Theme.Glyphs.GradientMap));
        panel.Children.Add(Theme.ActionRow("점 · 스크린톤 그라데이션 패턴", Run(() => ShowDockTab("patterns")), "선택 영역을 농도가 변하는 망점·스크린톤으로 채우기 · 패턴 탭", Theme.Glyphs.Screentone));
        if (gradientModeBox != null) gradientModeBox.SelectionChanged += (_, _) => UpdateDockGradients();
        UpdateDockGradients();
        return DockScroll(panel);
    }

    void UpdateDockGradients()
    {
        if (dockGradientList == null) return;
        dockGradientList.Children.Clear();
        int mode = gradientModeBox?.SelectedIndex ?? (gradientToBackground ? 1 : 0);
        foreach (var (index, label, end) in new[] { (0, "전경색 → 투명", Color.FromArgb(0, foreground.R, foreground.G, foreground.B)), (1, "전경색 → 배경색", backgroundColor) })
        {
            bool on = mode == index;
            var ramp = new Grid { Height = 18 };
            ramp.Children.Add(new Border { Background = TransparencyChecker, CornerRadius = new CornerRadius(2) });
            ramp.Children.Add(new Border { Background = new LinearGradientBrush(foreground, end, 0), BorderBrush = on ? Theme.Accent : Theme.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) });
            var content = new StackPanel(); content.Children.Add(ramp);
            var name = Theme.Label(label, Theme.CaptionSize, on ? Theme.Text : Theme.Muted); name.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(name);
            int choice = index;
            var button = Theme.Button("", () => Guard(() => ChooseGradient(choice)), $"{label} · 그라데이션 도구로 칠하기");
            button.Content = content; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 5, 6, 4); button.Margin = new Thickness(0, 0, 0, 4);
            button.Background = on ? Theme.Selected : Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
            AutomationProperties.SetName(button, $"{label} 그라데이션"); AutomationProperties.SetItemStatus(button, on ? "선택됨" : "");
            dockGradientList.Children.Add(button);
        }
    }

    internal void ChooseGradient(int mode)
    {
        if (gradientModeBox != null) gradientModeBox.SelectedIndex = mode; else gradientToBackground = mode == 1;
        if (HasDocument) SetTool(Tool.Gradient);
        UpdateDockGradients();
    }

    UIElement BuildDockPatterns()
    {
        dockPatternHost = new StackPanel(); dockPatternKey = null;
        RebuildDockPatterns();
        return DockScroll(dockPatternHost);
    }

    // The material palette, opened on its patterns: it fills the selection, or swaps the
    // pattern of the selected material layer, through the same commands as the 속성 panel.
    void RebuildDockPatterns()
    {
        if (dockPatternHost == null) return;
        var layer = HasDocument && selection == null && doc.Active is { Material: not null } active && !IsLockedWithParents(active) ? active : null;
        var target = HasDocument && selection != null ? SelectionMaterialLayer() : layer;
        var key = (HasDocument ? doc : null, selection, target?.Id, target?.Material?.Asset.Id, patternFavorites.Count, string.Join(",", patternFavorites));
        if (Equals(key, dockPatternKey)) return;
        dockPatternKey = key;
        dockPatternHost.Children.Clear();
        string hint = !HasDocument ? "문서를 열면 패턴을 고를 수 있습니다."
            : selection != null ? (target != null ? "누르면 방금 만든 재질 레이어의 패턴을 바꿉니다." : "누르면 선택 영역을 이 패턴으로 채웁니다.")
            : layer != null ? "누르면 선택한 재질 레이어의 패턴을 바꿉니다." : "선택 영역을 만들거나 재질 레이어를 고르면 패턴으로 채울 수 있습니다.";
        dockPatternHost.Children.Add(DockCaption(hint));
        if (!HasDocument) return;
        var suggestion = selection != null ? SelectionMaterialSuggestion() : new MaterialSuggestion(SurfaceHint.General, null, SelectionMaterials.Order(SurfaceHint.General));
        dockPatternHost.Children.Add(MaterialPalette(MaterialPaletteTab.Patterns, target?.Material?.Asset.Id, target != null, ApplyDockPattern,
            SelectionMaterialChoices(suggestion), SelectionMaterials.PatternOrder(suggestion.Surface)));
    }

    internal void ApplyDockPattern(MaterialAsset asset, string name)
    {
        if (!HasDocument) return;
        if (selection != null) ApplySelectionMaterial(asset, name);
        else if (doc.Active is { Material: not null } layer && !IsLockedWithParents(layer)) SwapLayerMaterial(asset, name);
        else { status.Text = "먼저 패턴을 채울 영역을 선택하세요. 마술봉으로 방을 누르면 빠릅니다."; return; }
        dockPatternKey = null; RebuildDockPatterns();
    }

    // ---- 조정 ---------------------------------------------------------------------------

    UIElement BuildDockAdjustments()
    {
        var panel = new StackPanel();
        panel.Children.Add(DockCaption("조정 레이어 추가"));
        var grid = new WrapPanel { Margin = new Thickness(-1, 0, -1, 6) };
        AutomationProperties.SetName(grid, "조정 레이어 추가");
        foreach (var tile in AdjustmentTiles.Concat(StyleEffectTiles))
        {
            var kind = tile.Kind;
            var button = Theme.IconButton(tile.Glyph, Run(() => ShowAdjustment(kind)), $"{tile.Label} · {tile.Tip}", 30, 16);
            button.Margin = new Thickness(1); Density.Mark(button, DensityRole.Keep);
            AutomationProperties.SetName(button, $"{tile.Label} 조정 레이어");
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        panel.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Theme.ActionRow("사진 현상", Run(() => ShowAdjustment(AdjustmentKind.PhotoDevelop)), "화이트 밸런스·톤·질감을 한 번에 보정 · 수정 가능한 조정 레이어로 적용 · Ctrl+Shift+A", Theme.Glyphs.Camera));
        panel.Children.Add(Theme.ActionRow("디자인 스타일", Run(ShowDesignStyles), "미리보기를 보며 디자인 스타일을 고르고 편집할 수 있는 레이어로 적용", Theme.Glyphs.Style));
        panel.Children.Add(Theme.ActionRow("선택한 조정 레이어 편집", Run(EditAdjustment), "선택한 조정 레이어의 값을 다시 편집", Theme.Glyphs.Sliders));
        return DockScroll(panel);
    }

    // ---- 내비게이터 · 히스토그램 · 정보 -------------------------------------------------------

    UIElement BuildDockNavigator()
    {
        var grid = new Grid { Margin = new Thickness(6, 6, 6, 4) };
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigator = new NavigatorView();
        navigator.Navigate += point => Guard(() => NavigateTo(point));
        grid.Children.Add(navigator);
        var zoom = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var zoomOut = Theme.IconButton("M5 12H19", () => Guard(() => StepZoom(-1)), "축소 · Ctrl+-", 22, 12);
        var zoomIn = Theme.IconButton(Theme.Glyphs.Plus, () => Guard(() => StepZoom(1)), "확대 · Ctrl++", 22, 12);
        navigatorZoomText = new TextBlock { FontSize = Theme.CaptionSize, Foreground = Theme.Muted, MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(zoomOut, Dock.Left); DockPanel.SetDock(navigatorZoomText, Dock.Right); DockPanel.SetDock(zoomIn, Dock.Right);
        zoom.Children.Add(zoomOut); zoom.Children.Add(navigatorZoomText); zoom.Children.Add(zoomIn);
        navigatorZoom = new Slider { Minimum = Math.Log2(.01), Maximum = Math.Log2(16), SmallChange = .1, LargeChange = .5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), ToolTip = "화면 배율 · 끌어서 확대/축소" };
        AutomationProperties.SetName(navigatorZoom, "내비게이터 배율");
        navigatorZoom.ValueChanged += (_, e) => { if (!syncingNavigatorZoom && HasDocument) Guard(() => SetZoom(Math.Pow(2, e.NewValue))); };
        zoom.Children.Add(navigatorZoom);
        Grid.SetRow(zoom, 1); grid.Children.Add(zoom);
        canvas.ZoomChanged += SyncNavigatorZoom;
        UpdateNavigatorDocument(); SyncNavigatorZoom();
        return grid;
    }

    /// <summary>Centers the canvas view on a document point (내비게이터 click or drag).</summary>
    internal void NavigateTo(Point point)
    {
        if (!HasDocument) return;
        ClearPointerHover();
        canvas.Pan = new Vector((doc.Width / 2.0 - point.X) * canvas.Zoom, (doc.Height / 2.0 - point.Y) * canvas.Zoom);
        canvas.InvalidateVisual(); UpdateNavigatorViewport();
    }

    void SyncNavigatorZoom()
    {
        if (navigatorZoom == null || navigatorZoomText == null) return;
        syncingNavigatorZoom = true;
        try
        {
            navigatorZoom.Maximum = Math.Log2(MaxZoom);
            navigatorZoom.Value = Math.Clamp(Math.Log2(Math.Max(.0001, canvas.Zoom)), navigatorZoom.Minimum, navigatorZoom.Maximum);
            navigatorZoomText.Text = HasDocument ? $"{canvas.Zoom * 100:0.#}%" : "";
        }
        finally { syncingNavigatorZoom = false; }
        UpdateNavigatorViewport();
    }

    void UpdateNavigatorDocument()
    {
        if (navigator == null) return;
        navigator.SetDocument(HasDocument && composite != null && composite.Width == doc.Width && composite.Height == doc.Height ? composite.Thumbnail(320) : null,
            HasDocument ? new Size(doc.Width, doc.Height) : Size.Empty);
        UpdateNavigatorViewport();
    }

    void UpdateNavigatorViewport()
    {
        if (navigator == null) return;
        var view = HasDocument && canvas.ActualWidth > 0 ? canvas.VisibleDocumentRect : Rect.Empty;
        navigator.SetViewport(view);
    }

    UIElement BuildDockHistogram()
    {
        var panel = new StackPanel();
        dockHistogram = new HistogramView { ShowLuminance = true, Height = 80, MinWidth = 0, ToolTip = "합성 이미지의 명도(회색)와 RGB 분포 · 투명 픽셀 제외" };
        AutomationProperties.SetName(dockHistogram, "히스토그램");
        panel.Children.Add(dockHistogram);
        dockHistogramStats = Theme.Label("", Theme.CaptionSize, Theme.Muted); dockHistogramStats.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(dockHistogramStats);
        UpdateDockHistogram();
        var scroll = DockScroll(panel);
        scroll.SizeChanged += (_, e) => { if (e.HeightChanged && dockHistogram != null) dockHistogram.Height = Math.Clamp(e.NewSize.Height - 50, 40, 200); };
        return scroll;
    }

    void UpdateDockHistogram()
    {
        if (dockHistogram == null || dockHistogramStats == null) return;
        dockHistogram.Show(histogram.Bins);
        var (mean, weight) = HistogramView.LuminanceStats(histogram.Bins);
        if (!HasDocument || weight <= 0 || double.IsNaN(mean)) { dockHistogramStats.Text = HasDocument ? "불투명한 픽셀이 없습니다." : ""; return; }
        double half = weight / 2, seen = 0; int median = 0;
        for (int v = 0; v < 256; v++) { seen += histogram.Bins[3][v]; if (seen >= half) { median = v; break; } }
        dockHistogramStats.Text = $"명도 평균 {mean:0} · 중간값 {median}";
        dockHistogramStats.ToolTip = $"{(cmykProof ? "CMYK 미리보기" : "RGB / 8 bit")} · 투명 픽셀 제외 · 회색: 명도, 선: 빨강·초록·파랑";
    }

    UIElement BuildDockInfo()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        TextBlock Row(string caption, FrameworkElement? lead = null)
        {
            int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Theme.Label(caption, Theme.CaptionSize, Theme.Muted); label.Margin = new Thickness(0, 2, 12, 2); label.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(label, row); grid.Children.Add(label);
            var value = Theme.Label("", Theme.CaptionSize); value.Margin = new Thickness(0, 2, 0, 2);
            AutomationProperties.SetName(value, caption);
            FrameworkElement cell = value;
            if (lead != null) { var line = new StackPanel { Orientation = Orientation.Horizontal }; line.Children.Add(lead); line.Children.Add(value); cell = line; }
            Grid.SetRow(cell, row); Grid.SetColumn(cell, 1); grid.Children.Add(cell);
            return value;
        }
        infoPosition = Row("위치");
        infoSwatch = new Border { Width = 12, Height = 12, BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = TransparencyChecker };
        infoColor = Row("색상", infoSwatch);
        infoDocument = Row("문서");
        infoSelection = Row("선택");
        infoZoom = Row("배율");
        UpdateDockInfo();
        return DockScroll(grid);
    }

    /// <summary>Pointer position (document pixels) and the composite color there, document and selection size.</summary>
    internal void UpdateDockInfo(Point? pointer = null, bool clearPointer = false)
    {
        if (pointer is { } point) infoPointer = point;
        if (clearPointer) infoPointer = null;
        if (infoPosition == null || infoColor == null || infoDocument == null || infoSelection == null || infoZoom == null || infoSwatch == null) return;
        if (!HasDocument) { infoPosition.Text = infoColor.Text = infoDocument.Text = infoSelection.Text = infoZoom.Text = ""; return; }
        if (infoPointer is { } at)
        {
            int x = (int)Math.Floor(at.X), y = (int)Math.Floor(at.Y);
            infoPosition.Text = $"X {x:N0} · Y {y:N0} px";
            if (x >= 0 && y >= 0 && x < doc.Width && y < doc.Height && composite is { } image && image.Width == doc.Width && image.Height == doc.Height)
            {
                int i = (y * image.Width + x) * 4; byte b = image.Data[i], g = image.Data[i + 1], r = image.Data[i + 2], a = image.Data[i + 3];
                if (a == 0) { infoColor.Text = "투명"; infoSwatch.Background = TransparencyChecker; }
                else
                {
                    infoColor.Text = $"#{r:X2}{g:X2}{b:X2} · R {r} G {g} B {b}" + (a < 255 ? $" · A {a * 100 / 255}%" : "");
                    infoSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                }
            }
            else { infoColor.Text = "문서 밖"; infoSwatch.Background = TransparencyChecker; }
        }
        else { infoPosition.Text = "캔버스 위에 포인터를 올리세요"; infoColor.Text = "—"; infoSwatch.Background = TransparencyChecker; }
        infoDocument.Text = $"{doc.Width:N0} × {doc.Height:N0} px · {doc.Dpi:0.#} DPI";
        var area = selection == null ? Rect.Empty : Rect.Intersect(selection.Bounds, new Rect(0, 0, doc.Width, doc.Height));
        infoSelection.Text = area.IsEmpty ? "없음" : $"{area.Width:N0} × {area.Height:N0} px · X {area.X:N0} Y {area.Y:N0}";
        infoZoom.Text = $"{canvas.Zoom * 100:0.#}%";
    }

    // ---- 대지 · 기록 ------------------------------------------------------------------------

    UIElement BuildDockArtboards()
    {
        var panel = new DockPanel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        var footerHost = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        Button Action(string glyph, string tip, Action run) { var button = Theme.IconButton(glyph, () => Guard(() => { if (HasDocument) run(); }), tip, 24, 15); Density.Mark(button, DensityRole.Keep); footer.Children.Add(button); return button; }
        Action(Theme.Glyphs.Plus, "새 대지 · 오른쪽에 같은 크기로 추가", AddArtboard);
        Action(ToolIcons.PathData(Tool.Artboard), "대지 편집 도구 · 끌어서 만들기와 크기 조절 · Shift+O", () => SetTool(Tool.Artboard));
        Action(Theme.Glyphs.Export, "선택한 대지 내보내기", () => ExportDialog.Show(this, ArtboardEditing.ExportDocument(doc, CurrentArtboard.Id)));
        Action(Theme.Glyphs.Delete, "선택한 대지 삭제", RemoveArtboard);
        DockPanel.SetDock(footerHost, Dock.Bottom); panel.Children.Add(footerHost);
        dockArtboardList = new StackPanel();
        AutomationProperties.SetName(dockArtboardList, "대지 목록");
        panel.Children.Add(DockScroll(dockArtboardList));
        RebuildDockArtboards();
        return panel;
    }

    void RebuildDockArtboards()
    {
        if (dockArtboardList == null) return;
        dockArtboardList.Children.Clear();
        if (!HasDocument) return;
        var current = CurrentArtboard.Id;
        foreach (var board in ArtboardEditing.Visible(doc))
        {
            var row = new DockPanel();
            var icon = Theme.Glyph(ToolIcons.PathData(Tool.Artboard), 14, Theme.Muted); icon.Margin = new Thickness(0, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var size = new TextBlock { Text = $"{board.Width:N0} × {board.Height:N0}", FontSize = Theme.CaptionSize, Foreground = Theme.Subtle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(size, Dock.Right); row.Children.Add(size);
            row.Children.Add(Loc.Keep(new TextBlock { Text = board.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = board.Name }));
            var chosen = board;
            var button = Theme.Button("", () => Guard(() => SelectArtboard(chosen.Id)), $"{board.Name} · {board.Width:N0} × {board.Height:N0} px");
            button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 2, 6, 2); button.Margin = new Thickness(0, 0, 0, 1); button.MinHeight = 24;
            bool on = board.Id == current;
            button.Background = on ? Theme.Selected : Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
            Density.Mark(button, DensityRole.Keep);
            AutomationProperties.SetName(button, "대지 선택: " + board.Name); AutomationProperties.SetItemStatus(button, on ? "선택됨" : "");
            dockArtboardList.Children.Add(button);
        }
    }

    // Selects an artboard (for its properties and export) and centers it in the view.
    internal void SelectArtboard(Guid id)
    {
        if (!HasDocument || ArtboardEditing.Visible(doc).FirstOrDefault(b => b.Id == id) is not { } board) return;
        selectedArtboard = id; canvas.SelectedArtboardId = id;
        NavigateTo(new Point(board.X + board.Width / 2, board.Y + board.Height / 2));
        Refresh(false);
    }

    UIElement BuildDockHistory()
    {
        dockHistoryList = new StackPanel();
        AutomationProperties.SetName(dockHistoryList, "작업 기록");
        RebuildDockHistory();
        return DockScroll(dockHistoryList);
    }

    void RebuildDockHistory()
    {
        if (dockHistoryList == null) return;
        dockHistoryList.Children.Clear();
        if (!HasDocument) return;
        var past = history.UndoLabels; var future = history.RedoLabels;
        int current = past.Count;
        var steps = new List<string> { "시작 상태" }; steps.AddRange(past); steps.AddRange(future);
        FrameworkElement? currentRow = null;
        for (int i = 0; i < steps.Count; i++)
        {
            int step = i; bool now = i == current, ahead = i > current;
            var row = new DockPanel();
            var mark = i == 0 ? Theme.Glyph(Theme.Glyphs.Document, 13, Theme.Muted) : Theme.Glyph(Theme.Glyphs.History, 13, ahead ? Theme.Subtle : Theme.Muted);
            mark.Margin = new Thickness(0, 0, 6, 0); mark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = steps[i], Foreground = ahead ? Theme.Subtle : Theme.Text, FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            string tip = now ? "지금 상태" : ahead ? "다시 실행해 이 단계로 가기" : "실행 취소해 이 단계로 돌아가기";
            var button = Theme.Button("", () => Guard(() => JumpToHistory(step)), $"{steps[i]} · {tip}");
            button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 1, 6, 1); button.Margin = new Thickness(0, 0, 0, 1); button.MinHeight = 22;
            button.Background = now ? Theme.Selected : Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
            Density.Mark(button, DensityRole.Keep);
            AutomationProperties.SetName(button, $"기록 {i}: {steps[i]}"); AutomationProperties.SetItemStatus(button, now ? "지금 상태" : ahead ? "다시 실행할 단계" : "");
            dockHistoryList.Children.Add(button);
            if (now) currentRow = button;
        }
        if (!headlessTesting && currentRow != null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => currentRow.BringIntoView()));
    }

    /// <summary>Undoes or redoes until the history stands at <paramref name="step"/> (0 = 시작 상태).</summary>
    internal void JumpToHistory(int step)
    {
        if (!HasDocument) return;
        CancelGesture();
        int current = history.UndoLabels.Count, total = current + history.RedoLabels.Count;
        step = Math.Clamp(step, 0, total);
        if (step == current) return;
        while (history.UndoLabels.Count > step && history.CanUndo) doc = history.Undo(doc);
        while (history.UndoLabels.Count < step && history.CanRedo) doc = history.Redo(doc);
        maskEditing = false; selection = null; Refresh();
    }

    // After edits, selection and tab changes: the open dock tabs that list document state.
    void UpdateDockPanels()
    {
        if (!screenCompact || compactDock == null) return;
        if (DockTabVisible("history")) RebuildDockHistory();
        if (DockTabVisible("artboards")) RebuildDockArtboards();
        if (DockTabVisible("patterns")) RebuildDockPatterns();
        if (DockTabVisible("info")) UpdateDockInfo();
        if (DockTabVisible("gradients")) UpdateDockGradients();
        if (DockTabVisible("swatches")) RefreshDockRecent();
        if (DockTabVisible("navigator")) { UpdateNavigatorDocument(); SyncNavigatorZoom(); }
    }

    // After a finished render: the thumbnail, histogram and color under the pointer follow the new image.
    void UpdateDockAfterRender()
    {
        if (!screenCompact || compactDock == null) return;
        if (DockTabVisible("navigator")) UpdateNavigatorDocument();
        if (DockTabVisible("histogram")) UpdateDockHistogram();
        if (DockTabVisible("info")) UpdateDockInfo();
    }
}
