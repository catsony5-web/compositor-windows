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
    System.Windows.Controls.Primitives.UniformGrid? studioTabStrip;
    int studioPage;
    TextBlock? studioColorValue;
    ColorPalettePanel? studioPalette;
    ParameterSlider? studioDiameter, studioHardness;
    FrameworkElement BuildStudioTop()
    {
        var stack = new StackPanel();
        BuildWorkspaceTools();
        histogramCard = new StackPanel { Margin = new Thickness(13, 10, 13, 10) };
        var label = new DockPanel(); var badge = Theme.Label("R   G   B", 12, Theme.Accent); DockPanel.SetDock(badge, Dock.Right); label.Children.Add(badge); label.Children.Add(Theme.Label("히스토그램", 13));
        histogramCard.Children.Add(label); histogramCard.Children.Add(histogram); histogramCard.Children.Add(histogramInfo); stack.Children.Add(histogramCard);
        var tabs = studioTabStrip = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 8) };
        string[] labels = ["작업", "속성", "색상", "브러시"];
        for (int i = 0; i < labels.Length; i++)
        {
            int page = i; var button = Theme.Button(labels[i], () => ShowStudioPage(page));
            if (TryFindResource("PanelTab") is Style tabStyle) button.Style = tabStyle;
            button.FontSize = Theme.BodySize; button.MinHeight = 36; studioTabs.Add(button); tabs.Children.Add(button);
        }
        stack.Children.Add(tabs);
        studioPanes = labels.Select((label, page) => CreatePane(label, page, new ScrollViewer { Content = studioContents[page], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12, 8, 12, 12) })).ToArray();
        for (int i = 0; i < 4; i++) { studioPage = i; BuildStudioPage(i); }
        stack.Children.Add(studioScroll);
        ApplyWorkspaceStudio(); ShowStudioPage(0); return stack;
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
        for (int i = 0; i < studioTabs.Count; i++) { studioTabs[i].Background = Brushes.Transparent; studioTabs[i].BorderBrush = i == page ? Theme.Accent : Theme.Line; studioTabs[i].Foreground = i == page ? Theme.Text : Theme.Muted; studioTabs[i].FontWeight = i == page ? FontWeights.SemiBold : FontWeights.Normal; }
    }
    double PreferredStudioHeight(double height)
    {
        double available = Math.Max(380, height - (designWorkspace ? 260 : 400));
        return Math.Clamp(available * (studioPage is 1 or 2 ? .62 : .56), 240, 560);
    }
    void ApplyWorkspaceStudio()
    {
        if (studioPanes.Length > 0) studioPanes[0].SetCaption(designWorkspace ? "디자인" : "사진 보정");
        if (histogramCard != null) histogramCard.Visibility = HasDocument && !designWorkspace ? Visibility.Visible : Visibility.Collapsed;
        if (studioTabs.Count == 4 && studioTabStrip != null)
        {
            studioTabs[0].Content = designWorkspace ? "디자인" : "보정";
            studioTabStrip.Children.Clear();
            foreach (int page in designWorkspace ? new[] { 0, 1, 2, 3 } : new[] { 0, 3, 1, 2 }) studioTabStrip.Children.Add(studioTabs[page]);
        }
        studioContents[0].Children.Clear();
        if (designWorkspace) BuildDesignActions(studioContents[0]); else BuildPhotoActions(studioContents[0]);
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
        var actions = new WrapPanel();
        actions.Children.Add(Theme.Button("전경색", () => ChooseColor(false))); actions.Children.Add(Theme.Button("배경색", () => ChooseColor(true))); actions.Children.Add(Theme.Button("⇄", SwapColors, "색 교환 · X")); studioContent.Children.Add(actions);
        studioColorValue = Theme.Label("", 12, Theme.Muted); studioColorValue.TextWrapping = TextWrapping.Wrap; studioColorValue.Margin = new Thickness(2, 6, 2, 10); studioContent.Children.Add(studioColorValue); UpdateStudioColor();
        studioContent.Children.Add(Theme.Label("색상 견본", 13));
        var swatches = new System.Windows.Controls.Primitives.UniformGrid { Columns = 9 };
        foreach (string hex in new[] { "#FFFFFF", "#D8DCE2", "#88929F", "#4A515B", "#171A20", "#000000", "#EAE2D5", "#C5AE94", "#8C7061", "#F28792", "#DB5269", "#A12D4D", "#FFB774", "#F28446", "#B75132", "#F4D37A", "#D3AD4E", "#826C35", "#A5D9AF", "#50A98D", "#2A665E", "#AFDCF1", "#76A5E5", "#375C9D", "#CBB5EB", "#9D7BC8", "#624B86" })
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var b = Theme.Button("", () => { foreground = color; UpdateColor(); }, hex + " · 전경색 지정"); b.Background = new SolidColorBrush(color); b.Height = 27; b.MinHeight = 0; b.Padding = new Thickness(0); b.Margin = new Thickness(2); swatches.Children.Add(b);
        }
        studioContent.Children.Add(swatches);
        studioPalette = new ColorPalettePanel();
        studioPalette.ColorChanged += color => { foreground = color; UpdateColor(); };
        studioPalette.SetColor(foreground);
        studioContent.Children.Add(studioPalette);
    }
    void UpdateStudioColor()
    {
        if (studioColorValue != null) studioColorValue.Text = $"전경 #{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}   /   배경 #{backgroundColor.R:X2}{backgroundColor.G:X2}{backgroundColor.B:X2}";
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
