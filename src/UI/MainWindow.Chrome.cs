using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly TextBlock toolCaption = new() { FontSize = Theme.BodySize, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, MinWidth = 88, Margin = new Thickness(4, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    ScrollViewer? documentTabsScroll;

    FrameworkElement BuildHeader()
    {
        var header = new DockPanel { Background = Theme.Header, LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 7, 10, 7), VerticalAlignment = VerticalAlignment.Center };
        var import = Theme.Styled(Theme.Button("가져오기", () => Guard(Import), "이미지를 레이어로 가져오기 · Ctrl+Shift+O"), "GhostButton");
        var save = DocumentControl(Theme.Styled(Theme.Button("저장", () => { if (HasDocument) Save(false); }, "레이어를 보존하는 작업 저장 · Ctrl+S"), "GhostButton"));
        foreach (var button in new[] { import, save }) { button.Margin = new Thickness(1, 0, 1, 0); button.Padding = new Thickness(10, 4, 10, 4); button.Foreground = Theme.Text; actions.Children.Add(button); }
        var export = DocumentControl(Theme.Styled(Theme.Button("내보내기", () => { if (HasDocument) Guard(Export); }, "이미지 내보내기 · Ctrl+Shift+E"), "PrimaryButton"));
        export.Margin = new Thickness(8, 0, 0, 0); export.Padding = new Thickness(16, 4, 16, 4); actions.Children.Add(export);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var divider = new Border { Width = 1, Height = 20, Background = Theme.Line, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(divider, Dock.Right); header.Children.Add(divider);
        var modes = BuildWorkspaceSwitch(); DockPanel.SetDock(modes, Dock.Right); header.Children.Add(modes);
        var find = BuildCommandSearchButton(header); DockPanel.SetDock(find, Dock.Right); header.Children.Add(find);
        var brand = new Image { Source = Theme.BrandIcon, Width = 22, Height = 22, Margin = new Thickness(16, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Morupixel · 모루픽셀" };
        DockPanel.SetDock(brand, Dock.Left); header.Children.Add(brand);
        // Menu bar and ribbon tabs share the title bar; one of them is visible.
        var navigation = new Grid(); navigation.Children.Add(menuHost = mainMenu = BuildMenu()); navigation.Children.Add(BuildRibbonTabs());
        IndexMenuItems(); header.Children.Add(navigation); return header;
    }

    // Title bar entry to the command palette; the key chip teaches Ctrl+K. Below
    // CompactHeaderWidth only the icon remains so the menu bar stays on one line.
    const double CompactHeaderWidth = 1180;
    Action? updateSearchCompact;
    Button BuildCommandSearchButton(FrameworkElement header)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Theme.Glyph(Theme.Glyphs.Search, 14, Theme.Muted); icon.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(icon);
        var label = new TextBlock { Text = "명령 검색", Foreground = Theme.Muted, FontSize = Theme.BodySize, Margin = new Thickness(7, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var chip = new Border
        {
            BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Ctrl K", FontSize = 11, Foreground = Theme.Subtle }
        };
        content.Children.Add(label); content.Children.Add(chip);
        var button = Theme.Styled(Theme.Button("", () => Guard(ShowCommandPalette), "명령·도구·패널 찾기 · Ctrl+K"), "GhostButton");
        button.Content = content; button.Background = Theme.Input; button.BorderBrush = Theme.Line;
        button.MinHeight = 28; button.Height = 28; button.Padding = new Thickness(10, 0, 6, 0); button.Margin = new Thickness(8, 0, 6, 0); button.VerticalAlignment = VerticalAlignment.Center;
        // Ribbon tabs need more room than the menu bar, so the search shrinks earlier there.
        updateSearchCompact = () =>
        {
            double width = header.ActualWidth;
            bool compact = width > 0 && width < (ribbonMode ? CompactHeaderWidth + 220 : CompactHeaderWidth);
            label.Visibility = chip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            button.Padding = compact ? new Thickness(7, 0, 7, 0) : new Thickness(10, 0, 6, 0);
        };
        header.SizeChanged += (_, _) => updateSearchCompact();
        AutomationProperties.SetName(button, "명령 찾기");
        return button;
    }

    FrameworkElement BuildViewportActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (glyph, action, tip) in new (string, Action, string)[] { (Theme.Glyphs.Undo, Undo, "실행 취소 · Ctrl+Z"), (Theme.Glyphs.Redo, Redo, "다시 실행 · Ctrl+Shift+Z") })
            panel.Children.Add(DocumentControl(Theme.IconButton(glyph, () => { if (HasDocument) action(); }, tip)));
        // Fit, actual size and zoom steps are in the status bar zoom control.
        return panel;
    }

    FrameworkElement BuildDocumentStrip()
    {
        var host = new DockPanel { Background = Theme.Panel, LastChildFill = true };
        var add = Theme.IconButton(Theme.Glyphs.Plus, () => Guard(NewDocument), "새 문서 · Ctrl+N");
        add.Margin = new Thickness(4, 4, 8, 4);
        DockPanel.SetDock(add, Dock.Right); host.Children.Add(add);
        documentTabsScroll = new ScrollViewer { Content = tabsBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        documentTabsScroll.PreviewMouseWheel += (_, e) => { if (documentTabsScroll.ScrollableWidth <= 0) return; documentTabsScroll.ScrollToHorizontalOffset(documentTabsScroll.HorizontalOffset - e.Delta); e.Handled = true; };
        host.Children.Add(documentTabsScroll); return host;
    }

    void RebuildTabs()
    {
        StoreTab(); tabsBar.Children.Clear();
        FrameworkElement? selectedTab = null;
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i; var tab = tabs[i]; bool active = index == activeTab;
            var card = new Border { Background = active ? Theme.Stage : Brushes.Transparent, CornerRadius = new CornerRadius(7, 7, 0, 0), Margin = new Thickness(i == 0 ? 6 : 0, 5, 2, 0) };
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); card.Child = content;
            var title = Loc.Keep(new TextBlock { Text = tab.Document.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, Foreground = active ? Theme.Text : Theme.Muted, FontSize = Theme.BodySize, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Orientation = Orientation.Horizontal };
            if (tab.History.Dirty(tab.Document)) labels.Children.Add(new TextBlock { Text = "●", FontSize = 7, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), ToolTip = "저장하지 않은 변경 사항" });
            labels.Children.Add(title);
            var select = Theme.Button("", () => { SwitchTab(index); canvas.Focus(); }, tab.Document.Name + (tab.History.Dirty(tab.Document) ? " · 저장하지 않음" : ""));
            Theme.Styled(select, "GhostButton"); select.Content = labels; select.Margin = new Thickness(0); select.Padding = new Thickness(12, 4, 4, 4); select.MinWidth = 90; select.MinHeight = 30;
            AutomationProperties.SetName(select, "문서 선택: " + tab.Document.Name); content.Children.Add(select);
            var close = Theme.IconButton(Theme.Glyphs.Close, () => Guard(() => CloseTabAt(index)), "문서 닫기 · " + tab.Document.Name, 22, 11);
            close.Margin = new Thickness(0, 0, 5, 0); close.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(close, "문서 닫기: " + tab.Document.Name); Grid.SetColumn(close, 1); content.Children.Add(close);
            tabsBar.Children.Add(card); if (active) selectedTab = card;
        }
        if (!headlessTesting && selectedTab != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => selectedTab.BringIntoView()));
    }

    void CloseTabAt(int index)
    {
        if (index < 0 || index >= tabs.Count) return;
        if (index == activeTab) { CloseTab(); return; }
        var original = tabs[activeTab];
        SwitchTab(index); CloseTab();
        int restore = tabs.IndexOf(original);
        // CloseTab may already select the original document and fit its canvas.
        // Reload the stored viewport even in that case.
        if (restore >= 0) LoadTab(restore);
    }

    static string ToolDisplayName(Tool tool) => tool switch
    {
        Tool.Move => "이동", Tool.Brush => "브러시", Tool.Eraser => "지우개", Tool.Bucket => "버킷 채우기", Tool.Text => "텍스트", Tool.Artboard => "대지 편집",
        Tool.RectangleSelect => "사각 선택", Tool.EllipseSelect => "타원 선택", Tool.Crop => "자르기", Tool.Rectangle => "사각형", Tool.Ellipse => "타원",
        Tool.Gradient => "그라데이션", Tool.Eyedropper => "색상 추출", Tool.Hand => "화면 이동", Tool.Lasso => "올가미", Tool.PolygonLasso => "다각형 선택",
        Tool.MagicWand => "자동 선택", Tool.CloneStamp => "복제 도장", Tool.Heal => "복구 브러시", Tool.Smudge => "스머지", Tool.Liquify => "액화", Tool.BlurBrush => "흐림 브러시", _ => "도구"
    };
}
