using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// The 간결한 화면 panel dock: stacked tab groups with splitters between them, a ☰ menu per group
// and a thin icon strip for the groups folded away. Groups, their tabs and order start from
// WorkspaceLayoutStore.DockGroupKeys and can be rearranged (MainWindow.DockArrange.cs: dragging
// tabs and groups, flyouts and floating groups); the arrangement, sizes, folding and the open tab
// are saved with the workspace layout. The friendly screen's panes (보정/디자인, 속성, 브러시,
// 레이어) are tabs here, moved in live, so both screens run the same panel code.
public sealed partial class MainWindow
{
    Grid? compactDock, dockStack;
    StackPanel? dockStrip;
    Border? dockStripHost;
    internal DockGroup[] dockGroups = [];
    readonly Dictionary<string, DockTab> dockTabs = new(StringComparer.Ordinal);
    readonly Dictionary<string, UIElement> dockContent = new(StringComparer.Ordinal);
    DockGroupLayout[]? savedDockGroups;
    double compactDockWidth = DefaultDockWidth, friendlyPanelWidth = 396;
    internal const double DefaultDockWidth = 320, MinDockWidth = 240, MaxDockWidth = 520, DockGroupMinHeight = 84, DockGap = 3, DockStripWidth = 34;

    // The friendly pages map onto dock tabs (ShowStudioPage in 간결한 화면).
    static string DockTabForPage(int page) => page switch { 1 => "properties", 2 => "color", 3 => "brush", _ => "work" };
    static int? PageForDockTab(string key) => key switch { "work" => 0, "properties" => 1, "color" => 2, "brush" => 3, _ => null };

    string WorkTabTitle => userProfile.TabCaption ?? (designWorkspace ? "디자인" : "보정");
    // The 작업 tab's icon follows its content: the purpose's own mark, or the work mode's.
    string WorkTabGlyph => userProfile.TabCaption != null ? userProfile.Glyph : designWorkspace ? Theme.Glyphs.Style : Theme.Glyphs.Camera;

    // Every dock tab once; groups hold these and hand them to each other.
    void CreateDockTabs()
    {
        if (dockTabs.Count > 0) return;
        void Tab(string key, string title, string glyph, string tip, Func<UIElement> content) => dockTabs[key] = new DockTab(key, title, glyph, tip, content);
        UIElement Cached(string key, Func<UIElement> build) { if (!dockContent.TryGetValue(key, out var element)) dockContent[key] = element = build(); return element; }
        Tab("color", "색상", Theme.Glyphs.Palette, "전경색·배경색과 색상 선택기", () => Cached("color", BuildDockColor));
        Tab("swatches", "견본", Theme.Glyphs.Swatches, "최근 사용 색 · 색상 견본 · 톤 · 추천 색상", () => Cached("swatches", BuildDockSwatches));
        Tab("gradients", "그라데이션", ToolIcons.PathData(Tool.Gradient), "그라데이션 도구가 칠할 색 흐름", () => Cached("gradients", BuildDockGradients));
        Tab("patterns", "패턴", Theme.Glyphs.Hatch, "해치 패턴과 스크린톤으로 선택 영역이나 재질 레이어 채우기", () => Cached("patterns", BuildDockPatterns));
        Tab("properties", "속성", Theme.Glyphs.Sliders, "선택한 레이어의 속성", () => DockPane(studioPanes[1]));
        Tab("adjustments", "조정", Theme.Glyphs.Adjustment, "조정 레이어 · 사진 현상 · 디자인 스타일", () => Cached("adjustments", BuildDockAdjustments));
        Tab("navigator", "내비게이터", Theme.Glyphs.Navigator, "문서 전체와 지금 보이는 영역 · 누르거나 끌어서 화면 이동", () => Cached("navigator", BuildDockNavigator));
        Tab("histogram", "히스토그램", Theme.Glyphs.Histogram, "합성 이미지의 명도와 RGB 분포", () => Cached("histogram", BuildDockHistogram));
        Tab("info", "정보", Theme.Glyphs.Info, "포인터 위치와 그 자리의 색, 문서와 선택 영역 크기", () => Cached("info", BuildDockInfo));
        Tab("layers", "레이어", Theme.Glyphs.LayerStack, "레이어 목록", () => DockPane(layersPane!));
        Tab("artboards", "대지", ToolIcons.PathData(Tool.Artboard), "문서의 대지 목록", () => Cached("artboards", BuildDockArtboards));
        Tab("history", "기록", Theme.Glyphs.History, "작업 기록 · 단계를 누르면 그 상태로 돌아갑니다", () => Cached("history", BuildDockHistory));
        Tab("work", WorkTabTitle, Theme.Glyphs.Camera, "사진 보정과 디자인 작업 모음", () => DockPane(studioPanes[0]));
        Tab("brush", "브러시", ToolIcons.PathData(Tool.Brush), "브러시 모양 · 크기 · 프리셋", () => DockPane(studioPanes[3]));
    }

    // A group with its open tab chosen; later tab changes follow the studio page and refresh panels.
    DockGroup NewDockGroup(string key, IEnumerable<DockTab> tabs, int active = 0)
    {
        var defaults = WorkspaceLayoutStore.DockGroupKeys.FirstOrDefault(k => k.Key == key);
        var group = new DockGroup(key, tabs, defaults.Tabs != null ? defaults.Weight : 1, defaults.Tabs != null && defaults.Collapsed, ShowDockGroupMenu, ToggleDockGroup, DockGroupBack, WireDockDrag);
        group.Select(Math.Clamp(active, 0, Math.Max(0, group.Tabs.Count - 1)));
        group.ActiveChanged += g => { if (PageForDockTab(g.ActiveTab.Key) is { } page) studioPage = page; UpdateDockPanels(); UpdateDockAfterRender(); };
        return group;
    }

    // Builds the groups of an arrangement (complete: every tab once). Flyouts and floating windows
    // of the previous groups close first and their content is let go.
    void BuildDockGroups(DockGroupLayout[] arrangement)
    {
        CreateDockTabs();
        CloseDockFlyout(focusAnchor: false); CancelDockDrag();
        foreach (var old in dockGroups) { CloseDockWindow(old); old.Release(); DockGroup.Detach(old); }
        dockGroups = WorkspaceLayoutStore.CompleteDockGroups(arrangement).Select(entry =>
        {
            var group = NewDockGroup(entry.Key, entry.Tabs!.Select(t => dockTabs[t]), Math.Max(0, Array.IndexOf(entry.Tabs!, entry.Tab)));
            group.Weight = Math.Clamp(entry.Weight, WorkspaceLayoutStore.MinDockWeight, WorkspaceLayoutStore.MaxDockWeight);
            group.Floating = entry.Floating; group.Collapsed = entry.Collapsed && !entry.Floating;
            group.FloatBounds = entry.FloatBounds is { } b ? new Rect(b.Left, b.Top, b.Width, b.Height) : null;
            return group;
        }).ToArray();
        UpdateDockTitles(rebuildStrip: false);
    }

    // Built on the first switch to 간결한 화면 and kept (hidden) afterwards.
    void EnsureCompactDock()
    {
        if (compactDock != null) return;
        BuildDockGroups(WorkspaceLayoutStore.DefaultDockGroups());
        var dock = compactDock = new Grid { Background = Theme.Line, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(dock, "패널 도크");
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dock.ColumnDefinitions.Add(new ColumnDefinition());
        // The 4 DIP edge beside the canvas sets the dock width; a line shows on hover.
        var grip = new Thumb { Cursor = Cursors.SizeWE, Focusable = true, Background = Theme.Line, ToolTip = "패널 너비 조절 · 두 번 클릭하면 기본 너비" };
        grip.Template = SplitterTemplate();
        AutomationProperties.SetName(grip, "패널 도크 너비 조절");
        void Resize(double change) => rightPanelColumn.Width = new GridLength(Math.Clamp((rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value) + change, MinDockWidth, MaxDockWidth));
        grip.DragDelta += (_, e) => Resize(-e.HorizontalChange);
        grip.PreviewMouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { rightPanelColumn.Width = new GridLength(DefaultDockWidth); e.Handled = true; } };
        grip.KeyDown += (_, e) => { if (e.Key is Key.Left or Key.Right) { Resize(e.Key == Key.Left ? 8 : -8); e.Handled = true; } };
        grip.MouseEnter += (_, _) => grip.Background = Theme.Stroke; grip.MouseLeave += (_, _) => grip.Background = Theme.Line;
        dock.Children.Add(grip);
        dockStrip = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        dockStripHost = new Border { Width = DockStripWidth, Background = Theme.Header, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = dockStrip };
        AutomationProperties.SetName(dockStripHost, "접은 패널");
        Grid.SetColumn(dockStripHost, 1); dock.Children.Add(dockStripHost);
        dockStack = new Grid { Background = Theme.Line };
        Grid.SetColumn(dockStack, 2); dock.Children.Add(dockStack);
        Grid.SetColumn(dock, 3); bodyGrid?.Children.Add(dock);
        BuildDockOverlays();
        if (savedDockGroups != null) ApplyDockGroups(savedDockGroups, layout: false);
        canvas.ViewChanged += () => { if (DockTabVisible("navigator")) UpdateNavigatorViewport(); if (DockTabVisible("info")) UpdateDockInfo(); };
        canvas.MouseMove += (_, e) => { if (screenCompact && DockTabVisible("info")) UpdateDockInfo(canvas.ToDocument(e.GetPosition(canvas))); };
        canvas.MouseLeave += (_, _) => { if (screenCompact && DockTabVisible("info")) UpdateDockInfo(null, clearPointer: true); };
        // A press anywhere outside a flyout closes it (the strip icons toggle it themselves).
        PreviewMouseDown += (_, e) => DockPointerPressed(e.OriginalSource as DependencyObject);
    }

    static ControlTemplate SplitterTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        template.VisualTree = surface;
        return template;
    }

    UIElement DockPane(StudioPane pane)
    {
        if (ReferenceEquals(studioScroll.Content, pane)) studioScroll.Content = null;
        if (ReferenceEquals(layersSlot.Child, pane)) layersSlot.Child = null;
        pane.SetEmbedded(true); pane.Margin = new Thickness(0);
        return pane;
    }

    /// <summary>The groups stacked in the dock (not folded, not floating), top to bottom.</summary>
    internal IEnumerable<DockGroup> ShownDockGroups => dockGroups.Where(g => !g.Collapsed && !g.Floating);

    internal DockGroup? DockGroupOf(string tab) => dockGroups.FirstOrDefault(g => g.Tabs.Any(t => t.Key == tab));

    // Rows: one star row per open group (its Weight), DockGap rows with a splitter between them.
    // Folded groups wait in the icon strip (one may be open as a flyout); floating groups live in
    // their own windows.
    internal void LayoutDockGroups()
    {
        if (dockStack == null) return;
        if (dockFlyoutGroup is { } flyout && (!screenCompact || !flyout.Collapsed || flyout.Floating || !dockGroups.Contains(flyout))) CloseDockFlyout(focusAnchor: false);
        dockStack.Children.Clear(); dockStack.RowDefinitions.Clear();
        var shown = ShownDockGroups.ToArray();
        for (int i = 0; i < shown.Length; i++)
        {
            if (i > 0)
            {
                dockStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DockGap) });
                int upper = i - 1;
                var splitter = new Thumb { Cursor = Cursors.SizeNS, Focusable = true, Background = Theme.Line, Template = SplitterTemplate(), ToolTip = "패널 높이 조절" };
                AutomationProperties.SetName(splitter, $"{shown[upper].Title(0)} · {shown[i].Title(0)} 패널 높이 조절");
                splitter.DragDelta += (_, e) => ResizeDockGroups(upper, e.VerticalChange);
                splitter.KeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down) { ResizeDockGroups(upper, e.Key == Key.Up ? -8 : 8); e.Handled = true; } };
                splitter.MouseEnter += (_, _) => splitter.Background = Theme.Stroke; splitter.MouseLeave += (_, _) => splitter.Background = Theme.Line;
                Grid.SetRow(splitter, dockStack.RowDefinitions.Count - 1); dockStack.Children.Add(splitter);
            }
            dockStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(shown[i].Weight, GridUnitType.Star), MinHeight = DockGroupMinHeight });
            DockGroup.Detach(shown[i]);
            Grid.SetRow(shown[i], dockStack.RowDefinitions.Count - 1); dockStack.Children.Add(shown[i]);
        }
        foreach (var group in dockGroups)
        {
            bool open = !group.Collapsed && !group.Floating || ReferenceEquals(group, dockFlyoutGroup);
            group.SetDockButtonVisible(group.Floating || ReferenceEquals(group, dockFlyoutGroup));
            if (!open || !screenCompact) { if (!group.Floating) group.Release(); }
            else { group.Hosted = true; group.Show(); }
            group.Paint();
        }
        SyncDockWindows();
        RebuildDockStrip();
    }

    RowDefinition? DockRow(DockGroup group) => dockStack != null && dockStack.Children.Contains(group) ? dockStack.RowDefinitions[Grid.GetRow(group)] : null;

    // Moves height between the open groups above and below a splitter; weights follow the pixels.
    internal void ResizeDockGroups(int upper, double change)
    {
        var shown = ShownDockGroups.ToArray();
        if (upper < 0 || upper + 1 >= shown.Length) return;
        var heights = shown.Select(g => DockRow(g)?.ActualHeight ?? 0).ToArray();
        double total = heights.Sum();
        if (total <= 0) return;
        double low = DockGroupMinHeight - heights[upper], high = heights[upper + 1] - DockGroupMinHeight;
        if (low > high) return;
        change = Math.Clamp(change, low, high);
        heights[upper] += change; heights[upper + 1] -= change;
        double scale = shown.Sum(g => g.Weight) / total;
        for (int i = 0; i < shown.Length; i++)
        {
            shown[i].Weight = Math.Clamp(heights[i] * scale, WorkspaceLayoutStore.MinDockWeight, WorkspaceLayoutStore.MaxDockWeight);
            if (DockRow(shown[i]) is { } row) row.Height = new GridLength(shown[i].Weight, GridUnitType.Star);
        }
    }

    internal void SetDockGroupCollapsed(DockGroup group, bool collapsed)
    {
        bool floating = group.Floating;
        if (group.Collapsed == collapsed && !floating) return;
        if (floating && !collapsed) { DockGroupBack(group); return; }
        CommitFocusedInspectorField();
        if (ReferenceEquals(group, dockFlyoutGroup)) CloseDockFlyout(focusAnchor: false);
        if (floating) { CloseDockWindow(group); group.Floating = false; }
        group.Collapsed = collapsed;
        LayoutDockGroups(); UpdateDockPanels(); UpdateDockAfterRender();
    }

    // Double-clicking a tab row folds a stacked group and docks a flyout or floating one.
    void ToggleDockGroup(DockGroup group)
    {
        if (group.Floating || ReferenceEquals(group, dockFlyoutGroup)) DockGroupBack(group);
        else SetDockGroupCollapsed(group, !group.Collapsed);
    }

    // Opens a tab (unfolding its group when asked). False when the tab stays hidden.
    internal bool ShowDockTab(string key, bool expand = true)
    {
        EnsureCompactDock();
        var group = DockGroupOf(key);
        if (group == null) return false;
        int index = group.Tabs.ToList().FindIndex(t => t.Key == key);
        if (group.Floating)
        {
            group.Select(index);
            if (!headlessTesting && group.FloatingWindow is { IsVisible: true } window) window.Activate();
        }
        else if (ReferenceEquals(group, dockFlyoutGroup)) group.Select(index);
        else
        {
            if (group.Collapsed)
            {
                if (!expand) return false;
                group.Collapsed = false; LayoutDockGroups();
            }
            group.Select(index);
        }
        UpdateDockPanels(); UpdateDockAfterRender();
        return true;
    }

    void ShowDockPage(int page, bool activate) => ShowDockTab(DockTabForPage(page), activate);

    /// <summary>True while the tab's content is on screen: open in a stacked, flyout or floating group.</summary>
    internal bool DockTabVisible(string key) => screenCompact && dockGroups.Any(g => g.ActiveTab.Key == key
        && (g.Floating ? g.FloatingWindow != null : !g.Collapsed || ReferenceEquals(g, dockFlyoutGroup)));

    void HostDock() { EnsureCompactDock(); LayoutDockGroups(); }

    // Leaving 간결한 화면: the flyout and floating windows close (floating groups reopen on the way
    // back) and every group lets go of its content for the friendly cards.
    void ReleaseDock()
    {
        CancelDockDrag(); CloseDockFlyout(focusAnchor: false);
        foreach (var group in dockGroups) { CloseDockWindow(group); group.Release(); }
    }

    void UpdateDockTitles(bool rebuildStrip = true)
    {
        if (dockTabs.TryGetValue("work", out var work)) work.Title = WorkTabTitle;
        foreach (var group in dockGroups)
            for (int i = 0; i < group.Tabs.Count; i++)
                if (group.Tabs[i].Key == "work") group.SetTitle(i, WorkTabTitle);
        if (rebuildStrip) RebuildDockStrip();
    }

    // Folded groups as icons: one per tab, a hairline between groups. An icon opens its group as a
    // flyout beside the strip (the open flyout's icon is marked); its menu unfolds or floats it.
    void RebuildDockStrip()
    {
        if (dockStrip == null || dockStripHost == null) return;
        dockStrip.Children.Clear();
        foreach (var group in dockGroups.Where(g => g.Collapsed && !g.Floating))
        {
            if (dockStrip.Children.Count > 0) dockStrip.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(6, 4, 6, 4) });
            for (int i = 0; i < group.Tabs.Count; i++)
            {
                var tab = group.Tabs[i]; string title = group.Title(i); int index = i;
                Button? button = null;
                button = Theme.IconButton(tab.Key == "work" ? WorkTabGlyph : tab.Glyph, () => Guard(() => ToggleDockFlyout(group, index, button)), $"{title} 패널 열기", 28, 16);
                button.ToolTip = $"{title} 패널 열기 · 오른쪽 클릭: 그룹 펼치기와 떠 있는 패널";
                button.Margin = new Thickness(3, 1, 3, 1); Density.Mark(button, DensityRole.Keep);
                bool open = ReferenceEquals(group, dockFlyoutGroup) && group.ActiveIndex == i;
                if (open) button.Background = Theme.Selected;
                AutomationProperties.SetItemStatus(button, open ? "열림" : "");
                var menu = new ContextMenu();
                var expand = new MenuItem { Header = "그룹 펼치기" }; expand.Click += (_, _) => Guard(() => SetDockGroupCollapsed(group, false)); menu.Items.Add(expand);
                var floating = new MenuItem { Header = "떠 있는 패널로 열기" }; floating.Click += (_, _) => Guard(() => FloatDockGroup(group)); menu.Items.Add(floating);
                var reset = new MenuItem { Header = "패널 배치 초기화" }; reset.Click += (_, _) => Guard(ResetDockLayout); menu.Items.Add(reset);
                button.ContextMenu = menu;
                dockStrip.Children.Add(button);
            }
        }
        dockStripHost.Visibility = dockStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ☰: fold, the group's tabs, moving the open tab to another group or a new one, the group's
    // place in the stack, floating it, and the reset. Every drag in the dock has its item here.
    internal ContextMenu DockGroupMenu(DockGroup group)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, string? tip = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, ToolTip = tip, IsEnabled = enabled };
            item.Click += (_, _) => Guard(action);
            return item;
        }
        bool away = group.Floating || ReferenceEquals(group, dockFlyoutGroup);
        if (away) menu.Items.Add(Item("도킹", () => DockGroupBack(group), "이 그룹을 오른쪽 도크에 다시 넣습니다"));
        if (!group.Floating) menu.Items.Add(Item(group.Collapsed ? "그룹 펼치기" : "그룹 접기", () => SetDockGroupCollapsed(group, !group.Collapsed), "그룹을 오른쪽 아이콘 줄로 접거나 다시 펼칩니다"));
        menu.Items.Add(new Separator());
        for (int i = 0; i < group.Tabs.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = $"{group.Title(i)} 패널", IsCheckable = true, IsChecked = i == group.ActiveIndex };
            item.Click += (_, _) => Guard(() => group.Select(index));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var tab = group.ActiveTab;
        var others = dockGroups.Where(g => !ReferenceEquals(g, group)).ToArray();
        var move = new MenuItem { Header = "다른 그룹으로 옮기기", ToolTip = $"열린 탭({tab.Title})을 고른 그룹의 끝으로 옮깁니다", IsEnabled = others.Length > 0 };
        foreach (var other in others)
        {
            var target = other;
            move.Items.Add(Item(target.TitleList, () => MoveDockTab(tab.Key, target, target.Tabs.Count)));
        }
        menu.Items.Add(move);
        menu.Items.Add(Item("새 그룹으로 분리", () => SplitDockTab(tab.Key, NextDockGroup(group)), $"열린 탭({tab.Title})을 이 그룹 아래의 새 그룹으로 꺼냅니다", group.Tabs.Count > 1));
        menu.Items.Add(Item("위로 옮기기", () => MoveDockGroupBy(group, -1), "그룹을 한 칸 위로 옮깁니다", DockNeighbor(group, -1) != null));
        menu.Items.Add(Item("아래로 옮기기", () => MoveDockGroupBy(group, 1), "그룹을 한 칸 아래로 옮깁니다", DockNeighbor(group, 1) != null));
        if (!group.Floating) menu.Items.Add(Item("떠 있는 패널로 열기", () => FloatDockGroup(group), "그룹을 따로 움직이는 작은 창으로 엽니다 · 닫거나 📌 도킹을 누르면 도크로 돌아옵니다"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("패널 배치 초기화", ResetDockLayout, "그룹 구성·순서·크기·접기·떠 있는 패널과 도크 너비를 처음 상태로 되돌립니다"));
        return menu;
    }

    void ShowDockGroupMenu(DockGroup group)
    {
        var menu = DockGroupMenu(group);
        if (headlessTesting) return;
        menu.PlacementTarget = group.MenuButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    internal void ResetDockLayout()
    {
        EnsureCompactDock();
        CommitFocusedInspectorField();
        BuildDockGroups(WorkspaceLayoutStore.DefaultDockGroups());
        compactDockWidth = DefaultDockWidth;
        if (screenCompact) rightPanelColumn.Width = new GridLength(DefaultDockWidth);
        LayoutDockGroups(); UpdateDockPanels(); UpdateDockAfterRender();
    }

    internal DockGroupLayout[]? CaptureDockGroups() => dockGroups.Length == 0 ? savedDockGroups
        : dockGroups.Select(g => new DockGroupLayout(g.Key, Math.Round(g.Weight, 4), g.Collapsed, g.ActiveTab.Key, g.Tabs.Select(t => t.Key).ToArray(), g.Floating,
            FloatRect(g) is { } r ? new WindowBounds(Math.Round(r.X, 1), Math.Round(r.Y, 1), Math.Round(r.Width, 1), Math.Round(r.Height, 1), false) : null)).ToArray();

    // The saved arrangement: groups, their tabs and order, sizes, folds, floating and open tabs.
    // Tabs it does not place return to their default group (WorkspaceLayoutStore.CompleteDockGroups).
    internal void ApplyDockGroups(DockGroupLayout[]? saved, bool layout = true)
    {
        savedDockGroups = saved;
        if (saved == null || dockGroups.Length == 0) return;
        BuildDockGroups(saved);
        if (layout) LayoutDockGroups();
    }

    internal double CompactDockWidthNow => screenCompact ? (rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value) : compactDockWidth;
    internal double FriendlyPanelWidthNow => screenCompact ? friendlyPanelWidth : rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value;
}
