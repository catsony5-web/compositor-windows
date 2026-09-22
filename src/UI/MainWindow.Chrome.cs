using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly TextBlock toolCaption = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, MinWidth = 88, Margin = new Thickness(4, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    ScrollViewer? documentTabsScroll;

    FrameworkElement BuildHeader()
    {
        var header = new DockPanel { Background = Theme.Header, LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 7, 14, 7) };
        var import = Theme.Button("가져오기", () => Guard(Import), "이미지를 레이어로 가져오기 · Ctrl+Shift+O");
        var save = DocumentControl(Theme.Button("저장", () => { if (HasDocument) Save(false); }, "레이어를 보존하는 작업 저장 · Ctrl+S"));
        foreach (var button in new[] { import, save }) { button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent; button.Margin = new Thickness(2, 0, 2, 0); actions.Children.Add(button); }
        var export = DocumentControl(Theme.Button("내보내기  ↗", () => { if (HasDocument) Guard(Export); }, "이미지 내보내기 · Ctrl+Shift+E"));
        export.Background = Theme.Primary; export.BorderBrush = Theme.Primary; export.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(export);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var proof = DocumentControl(BuildProofSwitch()); DockPanel.SetDock(proof, Dock.Right); header.Children.Add(proof);
        var modes = BuildWorkspaceSwitch(); DockPanel.SetDock(modes, Dock.Right); header.Children.Add(modes);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        brand.Children.Add(new Image { Source = Theme.BrandIcon, Width = 26, Height = 26 });
        brand.Children.Add(new TextBlock { Text = "Morupixel", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) });
        header.Children.Add(brand); return header;
    }

    FrameworkElement BuildViewportActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var (label, action, tip) in new (string, Action, string)[] {
            ("↶", Undo, "실행 취소 · Ctrl+Z"), ("↷", Redo, "다시 실행 · Ctrl+Shift+Z"),
            ("맞춤", () => { canvas.Fit(); UpdateStatus(); canvas.Focus(); }, "화면에 맞춤 · Ctrl+0"),
            ("100%", () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); canvas.Focus(); }, "실제 크기 · Ctrl+1") })
        {
            var button = DocumentControl(Theme.Button(label, () => { if (HasDocument) action(); }, tip)); button.Height = 30; button.MinWidth = 30;
            button.Padding = new Thickness(9, 3, 9, 3); button.Margin = new Thickness(2, 0, 2, 0);
            button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
            panel.Children.Add(button);
        }
        return panel;
    }

    FrameworkElement BuildDocumentStrip()
    {
        var host = new DockPanel { Background = Theme.Header, LastChildFill = true };
        var add = Theme.Button("＋", () => Guard(NewDocument), "새 문서 · Ctrl+N");
        add.Width = 34; add.Margin = new Thickness(5, 3, 8, 3); add.Padding = new Thickness(0); add.FontSize = 18;
        add.Background = Brushes.Transparent; add.BorderBrush = Brushes.Transparent;
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
            var card = new Border { Background = active ? Theme.Surface : Brushes.Transparent, BorderBrush = active ? Theme.Accent : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2), Margin = new Thickness(0, 2, 2, 0) };
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); card.Child = content;
            var title = new TextBlock { Text = tab.Document.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 185, Foreground = active ? Theme.Text : Theme.Muted, FontSize = 12, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
            var labels = new StackPanel { Orientation = Orientation.Horizontal };
            if (tab.History.Dirty(tab.Document)) labels.Children.Add(new TextBlock { Text = "●", FontSize = 7, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), ToolTip = "저장하지 않은 변경 사항" });
            labels.Children.Add(title);
            var select = Theme.Button("", () => { SwitchTab(index); canvas.Focus(); }, tab.Document.Name + (tab.History.Dirty(tab.Document) ? " · 저장하지 않음" : ""));
            select.Content = labels; select.Background = Brushes.Transparent; select.BorderBrush = Brushes.Transparent; select.Margin = new Thickness(0); select.Padding = new Thickness(13, 6, 5, 6); select.MinWidth = 90;
            AutomationProperties.SetName(select, "문서 선택: " + tab.Document.Name); content.Children.Add(select);
            var close = Theme.Button("×", () => Guard(() => CloseTabAt(index)), "문서 닫기 · " + tab.Document.Name);
            close.FontSize = 17; close.Margin = new Thickness(2, 4, 4, 4); close.Padding = new Thickness(0); close.Background = Brushes.Transparent; close.BorderBrush = Brushes.Transparent;
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
