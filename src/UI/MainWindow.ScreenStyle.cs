using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// 화면 스타일: 친절한 화면 (the default: cards, captions and descriptions) or 간결한 화면 (a slim
// icon tool column, thin title and options bars and a dock of stacked tab groups on the right).
// Both screens use the same live controls: switching swaps the token palette (Theme), steps the
// density of every control (Density), moves the panes between the friendly cards and the dock
// (MainWindow.Dock.cs) and resizes the chrome below. The switch is live and saved with the
// workspace layout.
public sealed partial class MainWindow
{
    internal bool screenCompact;
    Grid? rootGrid, bodyGrid, stageGrid;
    DockPanel? optionHostPanel, titleBar, documentStrip;
    FrameworkElement? statusBar, brandMark, commandSearchButton;
    StackPanel? headerActions;
    Button? headerImport, headerSave, headerExport;
    Border? headerDivider, toolIdentityChip;
    ComboBox? gradientModeBox;
    Grid? layersPanelGrid;
    readonly Dictionary<bool, MenuItem> screenStyleItems = [];
    SegmentedChoice<bool>? startScreenChoice;
    TextBlock? startScreenSummary;
    // Where friendly panes were (left, floating) before 간결한 화면 docked them; restored on the way back.
    List<(StudioPane Pane, string Location, bool Pinned, Rect? Bounds)> friendlyPanes = [];
    const double CompactBarHeight = 30, CompactTitleHeight = 30, CompactStatusHeight = 24;

    internal static string ScreenStyleName(bool compact) => compact ? "간결한 화면" : "친절한 화면";
    internal static string ScreenStyleSummary(bool compact) => compact
        ? "다른 편집기에 익숙한 분께: 아이콘 도구 열과 탭으로 겹친 패널로 작업 화면을 넓게 씁니다."
        : "큰 버튼과 설명으로 기능을 안내합니다. 처음 쓰는 분께 알맞습니다.";

    internal void SetScreenStyle(bool compact)
    {
        if (screenCompact == compact) { UpdateScreenStyleChoices(); return; }
        CommitFocusedInspectorField(); CancelGesture();
        if (compact) DockFriendlyPanes(); else ReleaseDock();
        screenCompact = compact;
        var repaint = Theme.UseScreenStyle(compact);
        if (compact) EnsureCompactDock();
        ApplyScreenChrome(compact);
        // Controls built before the switch still hold the other palette's brushes.
        ScreenStyleRepaint(repaint);
        Density.SetCompact(this, compact);
        if (compact) { HostDock(); UpdateDockTitles(); }
        else { RestoreFriendlyPanes(); ShowStudioPage(studioPage, false); }
        UpdateDocumentAvailability();
        BuildLayers(); RebuildTabs(); RebuildRibbon(); FitToolRail();
        UpdateScreenStyleChoices(); UpdateDockPanels(); UpdateDockAfterRender();
        if (HasDocument) { canvas.InvalidateVisual(); UpdateStatus(); }
        status.Text = compact ? "간결한 화면 · 도구 열과 오른쪽 패널 그룹을 얇게 표시합니다" : "친절한 화면 · 버튼과 설명을 크게 표시합니다";
    }

    // The friendly right card hands every pane to the dock: panes that were moved left or
    // floated come back to the right first, and their places are remembered.
    void DockFriendlyPanes()
    {
        friendlyPanes = [];
        foreach (var pane in movablePanels)
        {
            if (pane.Location == "right" && !pane.Pinned) continue;
            Rect? bounds = pane.Floating is { } window ? new Rect(window.Left, window.Top, window.ActualWidth > 0 ? window.ActualWidth : window.Width, window.ActualHeight > 0 ? window.ActualHeight : window.Height) : null;
            friendlyPanes.Add((pane, pane.Location, pane.Pinned, bounds));
            pane.Unlock(); if (pane.Location != "right") MovePane(pane, "right");
        }
        studioScroll.Content = null;
        if (ReferenceEquals(layersSlot.Child, layersPane)) layersSlot.Child = null;
    }

    void RestoreFriendlyPanes()
    {
        if (layersPane != null && layersPane.Parent == null) { layersPane.SetEmbedded(false); layersSlot.Child = layersPane; }
        foreach (var pane in studioPanes) pane.SetEmbedded(true);
        layersPane?.SetEmbedded(false);
        foreach (var (pane, location, pinned, bounds) in friendlyPanes)
        {
            if (location != "right") MovePane(pane, location);
            if (location == "float" && pane.Floating is { } window && bounds is { } rect && OnScreen(rect, 120, 80)) { window.Left = rect.Left; window.Top = rect.Top; window.Width = rect.Width; window.Height = rect.Height; }
            if (pinned) pane.SetPinned(true);
        }
        friendlyPanes = [];
    }

    // Row heights, cards and the title bar's controls for each screen. The friendly values are
    // the ones the constructor and builders set; tests compare them unchanged.
    void ApplyScreenChrome(bool compact)
    {
        if (rootGrid is { } root)
        {
            root.RowDefinitions[0].Height = new GridLength(compact ? CompactTitleHeight : 44);
            root.RowDefinitions[4].Height = new GridLength(compact ? CompactStatusHeight : 30);
        }
        if (optionCard is Border options)
        {
            options.CornerRadius = new CornerRadius(compact ? 0 : 8); options.Margin = compact ? new Thickness(0) : new Thickness(8, 0, 8, 4);
            options.BorderThickness = compact ? new Thickness(0, 1, 0, 1) : new Thickness(1); options.Background = compact ? Theme.Header : Theme.Panel;
        }
        if (optionHostPanel != null) optionHostPanel.Margin = compact ? new Thickness(6, 0, 6, 0) : new Thickness(12, 0, 6, 0);
        if (toolIdentityChip != null)
        {
            toolIdentityChip.Background = compact ? Brushes.Transparent : Theme.Surface; toolIdentityChip.MinWidth = compact ? 0 : 96;
            toolIdentityChip.Padding = compact ? new Thickness(2, 0, 6, 0) : new Thickness(8, 4, 10, 4); toolIdentityChip.Margin = compact ? new Thickness(0, 0, 8, 0) : new Thickness(2, 0, 12, 0);
            Density.Mark(toolIdentityChip, DensityRole.Keep);
        }
        if (bodyGrid != null) bodyGrid.Margin = compact ? new Thickness(0) : new Thickness(8, 4, 8, 0);
        if (toolRail is GlassPanel rail)
        {
            rail.CornerRadius = new CornerRadius(compact ? 0 : 10); rail.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 8);
            rail.BorderThickness = compact ? new Thickness(0, 0, 1, 0) : new Thickness(1); rail.Background = compact ? Theme.Header : Theme.Panel;
        }
        if (workspaceTools is FrameworkElement tools) tools.Margin = compact ? new Thickness(3, 4, 3, 2) : new Thickness(4, 6, 4, 4);
        colorSwatches.SetCompact(compact);
        if (stageCard is ClipBorder stage)
        {
            stage.CornerRadius = new CornerRadius(compact ? 0 : 10); stage.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 8);
            stage.BorderThickness = new Thickness(compact ? 0 : 1);
        }
        if (stageGrid != null) { stageGrid.RowDefinitions[0].Height = new GridLength(compact ? 26 : 36); stageGrid.Background = compact ? Theme.Header : Theme.Panel; }
        if (documentStrip != null) documentStrip.Background = compact ? Theme.Header : Theme.Panel;
        if (layersPanelGrid != null)
        {
            layersPanelGrid.RowDefinitions[0].Height = compact ? GridLength.Auto : new GridLength(40);
            layersPanelGrid.RowDefinitions[3].Height = new GridLength(compact ? 28 : 40);
            if (layersPanelGrid.Children[0] is Border categories) { categories.Margin = compact ? new Thickness(6, 5, 6, 4) : new Thickness(8, 6, 8, 4); categories.CornerRadius = new CornerRadius(compact ? 3 : 8); categories.Padding = new Thickness(compact ? 1 : 2); }
        }
        // Title bar: 30 DIP high in 간결한 화면; the export button is a quiet button (the accent marks only focus and selection).
        if (brandMark is Image brand) { brand.Width = brand.Height = compact ? 16 : 22; brand.Margin = compact ? new Thickness(10, 0, 4, 0) : new Thickness(16, 0, 6, 0); }
        if (headerActions != null) headerActions.Margin = compact ? new Thickness(4, 3, 6, 3) : new Thickness(4, 7, 10, 7);
        foreach (var button in new[] { headerImport, headerSave })
        {
            if (button == null) continue;
            button.Padding = compact ? new Thickness(8, 0, 8, 0) : new Thickness(10, 4, 10, 4);
            if (compact) { button.MinHeight = 22; button.Height = 22; } else { button.ClearValue(MinHeightProperty); button.ClearValue(HeightProperty); }
            Density.Mark(button, DensityRole.Keep);
        }
        if (headerExport != null)
        {
            Theme.Styled(headerExport, compact ? "GhostButton" : "PrimaryButton");
            headerExport.Margin = compact ? new Thickness(4, 0, 0, 0) : new Thickness(8, 0, 0, 0); headerExport.Padding = compact ? new Thickness(8, 0, 8, 0) : new Thickness(16, 4, 16, 4);
            if (compact) { headerExport.Foreground = Theme.Text; headerExport.MinHeight = 22; headerExport.Height = 22; }
            else { headerExport.ClearValue(ForegroundProperty); headerExport.ClearValue(MinHeightProperty); headerExport.ClearValue(HeightProperty); }
            Density.Mark(headerExport, DensityRole.Keep);
        }
        if (headerDivider != null) headerDivider.Height = compact ? 14 : 20;
        if (workspaceSwitch != null) { workspaceSwitch.Height = compact ? 22 : 30; workspaceSwitch.Width = compact ? 150 : 168; Density.Mark(workspaceSwitch, DensityRole.Keep); }
        if (commandSearchButton is Button search)
        {
            search.MinHeight = search.Height = compact ? 22 : 28; search.Margin = compact ? new Thickness(6, 0, 4, 0) : new Thickness(8, 0, 6, 0);
            Density.Mark(search, DensityRole.Keep);
        }
        if (statusBar is DockPanel bar) bar.Background = Theme.Header;
        if (compactDock != null) compactDock.Visibility = compact && HasDocument ? Visibility.Visible : Visibility.Collapsed;
        if (rightPanelHost != null) rightPanelHost.Visibility = !compact && HasDocument ? Visibility.Visible : Visibility.Collapsed;
        rightPanelColumn.MinWidth = 0; rightPanelColumn.MaxWidth = double.PositiveInfinity;
        if (compact)
        {
            friendlyPanelWidth = rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value;
            rightPanelColumn.MinWidth = MinDockWidth; rightPanelColumn.MaxWidth = MaxDockWidth;
            rightPanelColumn.Width = new GridLength(Math.Clamp(compactDockWidth, MinDockWidth, MaxDockWidth));
            leftPanelColumn.Width = new GridLength(0);
        }
        else
        {
            compactDockWidth = rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value;
            rightPanelColumn.MinWidth = 324; rightPanelColumn.MaxWidth = 520;
            rightPanelColumn.Width = new GridLength(Math.Clamp(friendlyPanelWidth, 324, 520));
        }
        updateSearchCompact?.Invoke();
    }

    // Swaps the brushes that controls already hold (local values only) for the new palette's.
    void ScreenStyleRepaint(IReadOnlyDictionary<Brush, Brush> map)
    {
        var roots = new List<DependencyObject> { this };
        roots.AddRange(movablePanels); if (compactDock != null) roots.Add(compactDock);
        roots.AddRange(dockContent.Values.OfType<DependencyObject>());
        roots.AddRange(toolButtons.Values);
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var changes = new List<(DependencyProperty, Brush)>();
        void Walk(DependencyObject node)
        {
            if (!seen.Add(node)) return;
            changes.Clear();
            var values = node.GetLocalValueEnumerator();
            while (values.MoveNext())
                if (values.Current.Value is Brush brush && !values.Current.Property.ReadOnly && map.TryGetValue(brush, out var next)) changes.Add((values.Current.Property, next));
            foreach (var (property, brush) in changes) node.SetValue(property, brush);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
            if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        foreach (var root in roots) Walk(root);
    }

    MenuItem BuildScreenStyleMenu()
    {
        var menu = new MenuItem { Header = "화면 스타일", ToolTip = "친절한 화면: 큰 버튼과 설명 / 간결한 화면: 아이콘 도구 열과 탭으로 겹친 패널" };
        foreach (bool compact in new[] { false, true })
        {
            bool target = compact;
            var item = new MenuItem { Header = ScreenStyleName(compact), IsCheckable = true, IsChecked = compact == screenCompact, ToolTip = ScreenStyleSummary(compact) };
            item.Click += (_, _) => Guard(() => SetScreenStyle(target));
            screenStyleItems[compact] = item; menu.Items.Add(item);
        }
        return menu;
    }

    // Start screen: the screen style sits under the 사용 목적 choice and switches at once.
    FrameworkElement BuildStartScreenStyleChoice()
    {
        var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var caption = Theme.Label("화면 스타일", Theme.BodySize, Theme.Muted); caption.FontWeight = FontWeights.SemiBold; caption.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(caption);
        var choice = startScreenChoice = new SegmentedChoice<bool>([(false, ScreenStyleName(false)), (true, ScreenStyleName(true))], screenCompact) { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(choice, "화면 스타일");
        foreach (var (compact, button) in new[] { false, true }.Zip(choice.Buttons))
        {
            bool target = compact;
            button.ToolTip = ScreenStyleSummary(compact); button.Padding = new Thickness(16, 4, 16, 4); button.MinHeight = 28;
            button.Click += (_, _) => Guard(() => SetScreenStyle(target));
        }
        row.Children.Add(choice); host.Children.Add(row);
        var summary = startScreenSummary = Theme.Label(ScreenStyleSummary(screenCompact), Theme.CaptionSize, Theme.Muted);
        summary.HorizontalAlignment = HorizontalAlignment.Center; summary.TextAlignment = TextAlignment.Center; summary.MaxWidth = 480; summary.Margin = new Thickness(0, 6, 0, 0);
        host.Children.Add(summary);
        return host;
    }

    void UpdateScreenStyleChoices()
    {
        foreach (var (compact, item) in screenStyleItems) item.IsChecked = compact == screenCompact;
        startScreenChoice?.Select(screenCompact);
        if (startScreenSummary != null) startScreenSummary.Text = ScreenStyleSummary(screenCompact);
    }
}
