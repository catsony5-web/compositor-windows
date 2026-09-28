using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly List<UIElement> documentControls = [];
    readonly StackPanel recentPanel = new() { Margin = new Thickness(5, 22, 5, 0) };
    IReadOnlyList<string> recentDocuments = [];
    FrameworkElement? emptyWorkspace;
    // Start focus: without a document the stage spans the whole body and the tool rail,
    // side panels and tool options step aside; column widths are left untouched.
    const double OptionRowHeight = 44;
    FrameworkElement? toolRail, optionCard, leftPanelHost, rightPanelHost, stageCard;
    RowDefinition? optionRow;
    internal static readonly (string Label, string Caption, string Width, string Height, bool Millimeters, string Dpi, int Background)[] QuickSizes =
    [
        ("정사각형", "1080 × 1080", "1080", "1080", false, "96", 0),
        ("세로 4:5", "1080 × 1350", "1080", "1350", false, "96", 0),
        ("와이드 16:9", "1920 × 1080", "1920", "1080", false, "96", 0),
        ("A4 인쇄", "210 × 297 mm", "210", "297", true, "150", 1)
    ];
    bool startupInitialized;
    bool HasDocument => tabs.Count > 0;

    T DocumentControl<T>(T control) where T : UIElement
    {
        documentControls.Add(control); control.IsEnabled = HasDocument; return control;
    }

    FrameworkElement BuildEmptyWorkspace()
    {
        var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        card.Children.Add(new Image { Source = Theme.BrandIcon, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center });
        var title = Theme.Label("모루픽셀에서 시작하기", Theme.TitleSize); title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 14, 0, 4);
        card.Children.Add(title);
        var subtitle = Theme.Label("새 문서를 만들거나 이미지를 열어 편집을 시작하세요.", Theme.BodySize, Theme.Muted); subtitle.HorizontalAlignment = HorizontalAlignment.Center; subtitle.TextAlignment = TextAlignment.Center;
        card.Children.Add(subtitle);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (label, glyph, hint, action) in new (string, string, string, Action)[] {
            ("새 문서", Theme.Glyphs.NewFile, "Ctrl+N", NewDocument), ("열기", Theme.Glyphs.Open, "Ctrl+O", Open), ("배우기", Theme.Glyphs.Learn, "샘플 작업 열기", OpenLearningSample) })
        {
            var tile = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Theme.Glyph(glyph, 24, Theme.Accent, 1.6); icon.HorizontalAlignment = HorizontalAlignment.Center; tile.Children.Add(icon);
            tile.Children.Add(new TextBlock { Text = label, FontSize = Theme.BodySize, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2) });
            tile.Children.Add(new TextBlock { Text = hint, FontSize = Theme.CaptionSize, Foreground = Theme.Subtle, HorizontalAlignment = HorizontalAlignment.Center });
            var button = Theme.Button(label, () => Guard(action), label + " · " + hint);
            button.Content = tile; button.Width = 148; button.Height = 108; button.Margin = new Thickness(5); button.Padding = new Thickness(10);
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            actions.Children.Add(button);
        }
        card.Children.Add(actions);
        var quickTitle = Theme.Label("빠른 시작", Theme.BodySize, Theme.Muted); quickTitle.FontWeight = FontWeights.SemiBold; quickTitle.Margin = new Thickness(7, 18, 5, 6);
        card.Children.Add(quickTitle);
        var quick = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(2, 0, 2, 0) };
        foreach (var size in QuickSizes)
        {
            var preset = size;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            // The outline shows the page proportion at a glance.
            double w = double.Parse(preset.Width), h = double.Parse(preset.Height), scale = 18 / Math.Max(w, h);
            var outline = new Border { Width = Math.Max(8, w * scale), Height = Math.Max(8, h * scale), BorderBrush = Theme.Muted, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 8), Children = { outline } });
            content.Children.Add(new TextBlock { Text = preset.Label, FontSize = Theme.CaptionSize, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = preset.Caption, FontSize = 11, Foreground = Theme.Subtle, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var button = Theme.Button("", () => Guard(() => AddTab(NewDocumentDialog.CreateDocument("제목 없음", preset.Width, preset.Height, preset.Background, preset.Millimeters, preset.Dpi), null)), $"{preset.Label} · {preset.Caption}{(preset.Millimeters ? $" · {preset.Dpi} DPI" : " px")} · {(preset.Background == 1 ? "흰 배경" : "투명 배경")}으로 새 문서 만들기");
            button.Content = content; button.Width = 108; button.MinHeight = 84; button.Margin = new Thickness(4); button.Padding = new Thickness(6, 10, 6, 8);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(button, $"빠른 시작: {preset.Label} {preset.Caption}");
            quick.Children.Add(button);
        }
        card.Children.Add(quick);
        var drop = new Grid { Margin = new Thickness(5, 14, 5, 0), Height = 44 };
        drop.Children.Add(new System.Windows.Shapes.Rectangle { Stroke = Theme.Stroke, StrokeThickness = 1, StrokeDashArray = [4, 3], RadiusX = 8, RadiusY = 8 });
        drop.Children.Add(new TextBlock { Text = "이미지나 작업 파일을 여기로 끌어다 놓아도 열립니다", Foreground = Theme.Muted, FontSize = Theme.CaptionSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(drop);
        card.Children.Add(recentPanel); RebuildRecentDocuments();
        // Scroll only when the recent list outgrows the stage; otherwise keep the card centered.
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var centered = new Grid(); centered.Children.Add(card);
        centered.SetBinding(MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = scroll });
        scroll.Content = centered;
        return new Border { Background = Theme.Stage, Child = scroll };
    }

    // Headless checks and offscreen renders never read or write the user's recent list.
    void RememberRecent(string path)
    {
        if (headlessTesting || string.IsNullOrWhiteSpace(path)) return;
        recentDocuments = RecentDocuments.Add(path); RebuildRecentDocuments();
    }

    void RebuildRecentDocuments()
    {
        recentPanel.Children.Clear();
        recentPanel.Visibility = recentDocuments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (recentDocuments.Count == 0) return;
        var heading = Theme.Label("최근 문서", Theme.BodySize, Theme.Muted); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(2, 0, 2, 6);
        recentPanel.Children.Add(heading);
        foreach (var path in recentDocuments.Take(6))
        {
            string file = path;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Theme.Glyph(Theme.Glyphs.NewFile.Split(" M12 11")[0], 18, Theme.Muted, 1.6));
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = Path.GetFileName(file), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = Path.GetDirectoryName(file) ?? "", FontSize = Theme.CaptionSize, Foreground = Theme.Subtle, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var button = Theme.Styled(Theme.Button("", () => OpenRecent(file), file), "GhostButton");
            button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(8, 6, 8, 6); button.Margin = new Thickness(0, 1, 0, 1);
            System.Windows.Automation.AutomationProperties.SetName(button, "최근 문서 열기: " + Path.GetFileName(file));
            recentPanel.Children.Add(button);
        }
    }

    void OpenRecent(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (!headlessTesting) recentDocuments = RecentDocuments.Remove(path);
            RebuildRecentDocuments(); MessageDialog.Show(this, "파일을 찾을 수 없어 최근 문서 목록에서 뺐습니다.\n" + path, "최근 문서", NoticeKind.Information);
            return;
        }
        Guard(() => OpenPath(path));
    }

    void UpdateDocumentAvailability()
    {
        bool opened = HasDocument;
        foreach (var control in documentControls) control.IsEnabled = opened;
        canvas.IsEnabled = opened;
        if (emptyWorkspace != null) emptyWorkspace.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
        studioContents[0].IsEnabled = opened;
        properties.IsEnabled = opened;
        UpdateHistogramVisibility();
        if (!opened) histogramInfo.Text = "";
        ApplyStartFocus(!opened);
    }

    void ApplyStartFocus(bool empty)
    {
        if (stageCard == null) return;
        Grid.SetColumn(stageCard, empty ? 0 : 2); Grid.SetColumnSpan(stageCard, empty ? 4 : 1);
        foreach (var element in new[] { toolRail, leftPanelHost, rightPanelHost, optionCard })
            if (element != null) element.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (optionRow != null) optionRow.Height = new GridLength(empty ? 8 : OptionRowHeight);
        if (viewControls != null) viewControls.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void InitializeStartup()
    {
        if (startupInitialized) return;
        startupInitialized = true;
        if (!headlessTesting) { recentDocuments = RecentDocuments.Load(); RebuildRecentDocuments(); }
        UpdateColor(); UpdateBrushLabel(); Refresh(); SetTool(Tool.Move);
        if (savedLayout != null) Guard(() => ApplyPaneLayout(savedLayout));
        if (startupPath != null && (File.Exists(startupPath) || Directory.Exists(startupPath))) Guard(() => OpenPath(startupPath));
    }

    void OpenLearningSample()
    {
        if (tabs.Count >= 8) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        var sample = Demo.Create(); sample.Name = "배우기 · " + sample.Name;
        AddTab(sample, null);
    }

    void EnterEmptyWorkspace()
    {
        CancelGesture(); jobCts?.Cancel(); jobCts = null;
        ++renderGeneration; renderCts?.Cancel();
        pendingFullRender = false; pendingGestureRender = false;
        gestureRenderTimer.Stop(); gestureRenderTimer.Tick -= OnGestureRenderTick;
        ClearTextMovePreview();
        activeTab = -1;
        doc = new Document { Width = 1, Height = 1 }; history = new History(); history.Reset(doc);
        projectPath = null; selection = null; maskEditing = false; pendingInspectorCommit = null;
        selectedLayers.Clear(); collapsedGroups.Clear();
        cloneSource = null; cloneAnchorDocument = null; cloneAnchorLayer = Guid.Empty; cloneAnchorLocal = null;
        canvas.Document = null; canvas.Composite = null; canvas.Selection = null; composite = null;
        canvas.Guides.Clear(); canvas.BrushPoint = null; canvas.Pan = new(); canvas.Zoom = 1;
        histogram.Update(new Raster(1, 1)); histogramInfo.Text = "";
        Refresh(false);
    }
}
