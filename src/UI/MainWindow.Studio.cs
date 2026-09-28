using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly HistogramView histogram = new();
    readonly TextBlock histogramInfo = Theme.Label("RGB · 합성 이미지", 12, Theme.Muted);
    readonly StackPanel[] studioContents = [new(), new(), new(), new()];
    StackPanel studioContent => studioContents[studioPage];
    readonly ScrollViewer studioScroll = new() { Height = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0) };
    readonly List<Button> studioTabs = [];
    StackPanel? histogramCard;
    MenuItem? histogramToggle;
    // The histogram is optional (View menu); hidden, the tabbed panel gets its height.
    bool showHistogram;
    System.Windows.Controls.Primitives.UniformGrid? studioTabStrip;
    int studioPage;
    TextBlock? studioColorValue;
    ColorPalettePanel? studioPalette;
    ColorSwatches? studioColorSwatches;
    ParameterSlider? studioDiameter, studioHardness;
    FrameworkElement BuildStudioTop()
    {
        var stack = new StackPanel();
        BuildWorkspaceTools();
        histogramCard = new StackPanel { Margin = new Thickness(12, 9, 12, 2) };
        var label = new DockPanel();
        var hide = Theme.IconButton(Theme.Glyphs.Close, () => SetHistogramVisible(false), "히스토그램 숨기기 · 보기 메뉴에서 다시 표시", 22, 11);
        DockPanel.SetDock(hide, Dock.Right); label.Children.Add(hide);
        var histogramTitle = Theme.Label("히스토그램", Theme.CaptionSize, Theme.Muted); histogramTitle.FontWeight = FontWeights.SemiBold; histogramTitle.VerticalAlignment = VerticalAlignment.Center; label.Children.Add(histogramTitle);
        histogram.Height = 62; histogram.Margin = new Thickness(0, 2, 0, 0);
        histogramInfo.FontSize = Theme.CaptionSize; histogramInfo.Foreground = Theme.Subtle; histogramInfo.Margin = new Thickness(2, 3, 2, 0);
        histogramCard.Children.Add(label); histogramCard.Children.Add(histogram); histogramCard.Children.Add(histogramInfo); stack.Children.Add(histogramCard);
        var tabRow = new DockPanel { Margin = new Thickness(6, 2, 4, 0) };
        Button? paneMenu = null;
        paneMenu = Theme.IconButton(Theme.Glyphs.More, () => { if (studioPanes.Length > studioPage) studioPanes[studioPage].ShowDockMenu(paneMenu!); }, "현재 패널 이동과 도킹", 28, 16);
        paneMenu.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(paneMenu, Dock.Right); tabRow.Children.Add(paneMenu);
        var tabs = studioTabStrip = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        tabRow.Children.Add(tabs);
        string[] labels = ["작업", "속성", "색상", "브러시"];
        for (int i = 0; i < labels.Length; i++)
        {
            int page = i; var button = Theme.Button(labels[i], () => ShowStudioPage(page));
            if (TryFindResource("PanelTab") is Style tabStyle) button.Style = tabStyle;
            button.FontSize = Theme.BodySize; button.MinHeight = 34; studioTabs.Add(button); tabs.Children.Add(button);
        }
        stack.Children.Add(tabRow);
        stack.Children.Add(new Border { Height = 1, Background = Theme.Line });
        studioPanes = labels.Select((label, page) => CreatePane(label, page, new ScrollViewer { Content = studioContents[page], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12, 8, 12, 12) })).ToArray();
        for (int i = 0; i < 4; i++) { studioPage = i; BuildStudioPage(i); }
        stack.Children.Add(studioScroll);
        ApplyWorkspaceStudio(); ShowStudioPage(0);
        return new ClipBorder { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = stack };
    }
    void ShowStudioPage(int page, bool activate = true)
    {
        CommitFocusedInspectorField(); studioPage = page;
        if (activate) studioScroll.Height = PreferredStudioHeight(ActualHeight);
        if (studioPanes.Length == 0) return;
        var pane = studioPanes[page];
        if (pane.Location == "right") studioScroll.Content = pane;
        else
        {
            studioScroll.Content = Theme.Button(pane.Caption + " 패널 · 오른쪽으로 가져오기", () => { pane.Unlock(); MovePane(pane, "right"); });
            if (activate) pane.Floating?.Activate();
        }
        for (int i = 0; i < studioTabs.Count; i++) { studioTabs[i].Background = Brushes.Transparent; studioTabs[i].BorderBrush = i == page ? Theme.Accent : Brushes.Transparent; studioTabs[i].Foreground = i == page ? Theme.Text : Theme.Muted; studioTabs[i].FontWeight = i == page ? FontWeights.SemiBold : FontWeights.Normal; }
    }
    double PreferredStudioHeight(double height)
    {
        double available = Math.Max(380, height - (designWorkspace || !showHistogram ? 260 : 400));
        return Math.Clamp(available * (studioPage is 1 or 2 ? .62 : .56), 240, 560);
    }
    void ApplyWorkspaceStudio()
    {
        if (studioPanes.Length > 0) studioPanes[0].SetCaption(designWorkspace ? "디자인" : "사진 보정");
        UpdateHistogramVisibility();
        if (studioTabs.Count == 4 && studioTabStrip != null)
        {
            studioTabs[0].Content = designWorkspace ? "디자인" : "보정";
            studioTabStrip.Children.Clear();
            foreach (int page in designWorkspace ? new[] { 0, 1, 2, 3 } : new[] { 0, 3, 1, 2 }) studioTabStrip.Children.Add(studioTabs[page]);
        }
        studioContents[0].Children.Clear();
        if (designWorkspace) BuildDesignActions(studioContents[0]); else BuildPhotoActions(studioContents[0]);
    }
    void UpdateHistogramVisibility()
    {
        if (histogramCard != null) histogramCard.Visibility = HasDocument && !designWorkspace && showHistogram ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetHistogramVisible(bool visible)
    {
        showHistogram = visible;
        if (histogramToggle != null) histogramToggle.IsChecked = visible;
        UpdateHistogramVisibility();
        studioScroll.Height = PreferredStudioHeight(ActualHeight);
    }

    void BuildStudioPage(int page)
    {
        if (page == 1) { studioContent.Children.Add(properties); return; }
        if (page == 2) { BuildStudioColors(); return; }
        if (page == 3) { BuildStudioBrushes(); return; }
        BuildPhotoActions(studioContent);
    }
    void BuildStudioColors()
    {
        // Header: large foreground/background pair, then eyedropper and the numeric readout.
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        studioColorSwatches = new ColorSwatches(() => ChooseColor(false), () => ChooseColor(true), SwapColors, ResetColors, 1.5) { Margin = new Thickness(0, 4, 12, 4) };
        DockPanel.SetDock(studioColorSwatches, Dock.Left); header.Children.Add(studioColorSwatches);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var picker = Theme.Button("", () => { SetTool(Tool.Eyedropper); canvas.Focus(); }, "스포이트 · 클릭: 전경색 · Alt+클릭: 배경색");
        var pickerContent = new StackPanel { Orientation = Orientation.Horizontal };
        pickerContent.Children.Add(Theme.Glyph(ToolIcons.PathData(Tool.Eyedropper), 16, Theme.Text, 1.6));
        var pickerLabel = Theme.Label("스포이트", Theme.BodySize); pickerLabel.Margin = new Thickness(6, 0, 0, 0); pickerLabel.VerticalAlignment = VerticalAlignment.Center; pickerContent.Children.Add(pickerLabel);
        picker.Content = pickerContent; picker.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(picker, "스포이트");
        side.Children.Add(picker);
        studioColorValue = Theme.Label("", Theme.CaptionSize, Theme.Muted); studioColorValue.TextWrapping = TextWrapping.Wrap; studioColorValue.Margin = new Thickness(2, 8, 2, 0); side.Children.Add(studioColorValue);
        header.Children.Add(side);
        studioContent.Children.Add(header);
        studioPalette = new ColorPalettePanel();
        studioPalette.ColorChanged += color => { foreground = color; UpdateColor(); };
        studioPalette.SetColor(foreground);
        studioContent.Children.Add(studioPalette);
        UpdateStudioColor();
    }
    void RememberColor(Color color) { if (studioPalette != null) studioPalette.Remember(color); else ColorPalettePanel.RememberShared(color); }
    void UpdateStudioColor()
    {
        if (studioColorValue != null) studioColorValue.Text = $"전경 #{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}\n배경 #{backgroundColor.R:X2}{backgroundColor.G:X2}{backgroundColor.B:X2}";
        studioColorSwatches?.SetColors(foreground, backgroundColor);
        studioPalette?.SetColor(foreground);
    }
    void BuildStudioBrushes()
    {
        studioContent.Children.Add(BuildBrushTipControls());
        studioContent.Children.Add(Theme.Section("크기와 획"));
        var presets = new WrapPanel();
        foreach (var (label, size, edge) in new[] { ("세밀하게", 12d, 1d), ("부드럽게", 100d, .15), ("넓게", 240d, .7) })
            presets.Children.Add(Theme.Button(label, () => { SetTool(Tool.Brush); brushSize = size; hardness = edge; UpdateBrushLabel(); studioHardness?.SetValue(hardness * 100); }, label + " 브러시 프리셋"));
        studioContent.Children.Add(presets);
        var diameter = studioDiameter = new ParameterSlider("크기 px", 1, MaxBrushSize, brushSize, 42); diameter.Changed += v => { brushSize = v; UpdateBrushLabel(); }; studioContent.Children.Add(diameter);
        var soft = studioHardness = new ParameterSlider("경도 %", 0, 100, hardness * 100, 80); soft.Changed += v => { hardness = v / 100; UpdateBrushLabel(); }; studioContent.Children.Add(soft);
        var angle = new ParameterSlider("모양 회전 °", -180, 180, brushAngle);
        angle.Changed += v => { brushAngle = v; UpdateBrushTipCursor(); }; studioContent.Children.Add(angle);
        var spacing = new ParameterSlider("찍는 간격 %", 1, 150, brushSpacing * 100, 10);
        spacing.Changed += v => brushSpacing = v / 100; studioContent.Children.Add(spacing);
        spacing.ToolTip = "간격을 늘리면 모양을 띄워 그립니다. 브러시·지우개·마스크에 적용됩니다.";
        diameter.ToolTip = "Alt + 좌우 드래그로 크기 조절";
    }
}
