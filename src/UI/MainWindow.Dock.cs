using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// The 간결한 화면 panel dock: stacked tab groups with splitters between them, a ☰ menu per group
// and a thin icon strip for the groups folded away. Group order and tabs come from
// WorkspaceLayoutStore.DockGroupKeys; sizes, folding and the open tab are saved with the
// workspace layout. The friendly screen's panes (보정/디자인, 속성, 브러시, 레이어) are tabs here,
// moved in live, so both screens run the same panel code.
public sealed partial class MainWindow
{
    Grid? compactDock, dockStack;
    StackPanel? dockStrip;
    Border? dockStripHost;
    internal DockGroup[] dockGroups = [];
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

    DockGroup[] CreateDockGroups()
    {
        DockTab Tab(string key, string title, string glyph, string tip, Func<UIElement> content) => new(key, title, glyph, tip, content);
        UIElement Cached(string key, Func<UIElement> build) { if (!dockContent.TryGetValue(key, out var element)) dockContent[key] = element = build(); return element; }
        var specs = new Dictionary<string, (double Weight, bool Collapsed, DockTab[] Tabs)>(StringComparer.Ordinal)
        {
            ["color"] = (1.2, false, [
                Tab("color", "색상", Theme.Glyphs.Palette, "전경색·배경색과 색상 선택기", () => Cached("color", BuildDockColor)),
                Tab("swatches", "견본", Theme.Glyphs.Swatches, "최근 사용 색 · 색상 견본 · 톤 · 추천 색상", () => Cached("swatches", BuildDockSwatches)),
                Tab("gradients", "그라데이션", ToolIcons.PathData(Tool.Gradient), "그라데이션 도구가 칠할 색 흐름", () => Cached("gradients", BuildDockGradients)),
                Tab("patterns", "패턴", Theme.Glyphs.Hatch, "해치 패턴과 스크린톤으로 선택 영역이나 재질 레이어 채우기", () => Cached("patterns", BuildDockPatterns)),
                Tab("entourage", "점경", Theme.Glyphs.Entourage, "사람·나무·탈것·소품을 도면에 놓기 · 내 점경", () => Cached("entourage", BuildDockEntourage))]),
            ["properties"] = (1.25, false, [
                Tab("properties", "속성", Theme.Glyphs.Sliders, "선택한 레이어의 속성", () => DockPane(studioPanes[1])),
                Tab("adjustments", "조정", Theme.Glyphs.Adjustment, "조정 레이어 · 사진 현상 · 디자인 스타일", () => Cached("adjustments", BuildDockAdjustments))]),
            ["navigator"] = (.85, false, [
                Tab("navigator", "내비게이터", Theme.Glyphs.Navigator, "문서 전체와 지금 보이는 영역 · 누르거나 끌어서 화면 이동", () => Cached("navigator", BuildDockNavigator)),
                Tab("histogram", "히스토그램", Theme.Glyphs.Histogram, "합성 이미지의 명도와 RGB 분포", () => Cached("histogram", BuildDockHistogram)),
                Tab("info", "정보", Theme.Glyphs.Info, "포인터 위치와 그 자리의 색, 문서와 선택 영역 크기", () => Cached("info", BuildDockInfo))]),
            ["layers"] = (1.4, false, [
                Tab("layers", "레이어", Theme.Glyphs.LayerStack, "레이어 목록", () => DockPane(layersPane!)),
                Tab("artboards", "대지", ToolIcons.PathData(Tool.Artboard), "문서의 대지 목록", () => Cached("artboards", BuildDockArtboards)),
                Tab("history", "기록", Theme.Glyphs.History, "작업 기록 · 단계를 누르면 그 상태로 돌아갑니다", () => Cached("history", BuildDockHistory))]),
            ["tools"] = (1.2, true, [
                Tab("work", WorkTabTitle, Theme.Glyphs.Camera, "사진 보정과 디자인 작업 모음", () => DockPane(studioPanes[0])),
                Tab("brush", "브러시", ToolIcons.PathData(Tool.Brush), "브러시 모양 · 크기 · 프리셋", () => DockPane(studioPanes[3]))])
        };
        return WorkspaceLayoutStore.DockGroupKeys.Select(k =>
        {
            var spec = specs[k.Key];
            var group = new DockGroup(k.Key, spec.Tabs, spec.Weight, spec.Collapsed, ShowDockGroupMenu, g => SetDockGroupCollapsed(g, !g.Collapsed));
            group.Select(0);
            group.ActiveChanged += g => { if (PageForDockTab(g.ActiveTab.Key) is { } page) studioPage = page; UpdateDockPanels(); UpdateDockAfterRender(); };
            return group;
        }).ToArray();
    }

    // Built on the first switch to 간결한 화면 and kept (hidden) afterwards.
    void EnsureCompactDock()
    {
        if (compactDock != null) return;
        dockGroups = CreateDockGroups();
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
        if (savedDockGroups != null) ApplyDockGroups(savedDockGroups, layout: false);
        canvas.ViewChanged += () => { if (DockTabVisible("navigator")) UpdateNavigatorViewport(); if (DockTabVisible("info")) UpdateDockInfo(); };
        canvas.MouseMove += (_, e) => { if (screenCompact && DockTabVisible("info")) UpdateDockInfo(canvas.ToDocument(e.GetPosition(canvas))); };
        canvas.MouseLeave += (_, _) => { if (screenCompact && DockTabVisible("info")) UpdateDockInfo(null, clearPointer: true); };
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

    internal IEnumerable<DockGroup> ShownDockGroups => dockGroups.Where(g => !g.Collapsed);

    // Rows: one star row per open group (its Weight), DockGap rows with a splitter between them.
    internal void LayoutDockGroups()
    {
        if (dockStack == null) return;
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
            Grid.SetRow(shown[i], dockStack.RowDefinitions.Count - 1); dockStack.Children.Add(shown[i]);
        }
        foreach (var group in dockGroups)
        {
            if (group.Collapsed || !screenCompact) group.Release();
            else { group.Hosted = true; group.Show(); }
            group.Paint();
        }
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
        if (group.Collapsed == collapsed) return;
        CommitFocusedInspectorField();
        group.Collapsed = collapsed;
        LayoutDockGroups(); UpdateDockPanels(); UpdateDockAfterRender();
    }

    // Opens a tab (unfolding its group when asked). False when the tab stays hidden.
    internal bool ShowDockTab(string key, bool expand = true)
    {
        EnsureCompactDock();
        var group = dockGroups.FirstOrDefault(g => g.Tabs.Any(t => t.Key == key));
        if (group == null) return false;
        if (group.Collapsed)
        {
            if (!expand) return false;
            group.Collapsed = false; LayoutDockGroups();
        }
        group.Select(group.Tabs.ToList().FindIndex(t => t.Key == key));
        UpdateDockPanels(); UpdateDockAfterRender();
        return true;
    }

    void ShowDockPage(int page, bool activate) => ShowDockTab(DockTabForPage(page), activate);

    internal bool DockTabVisible(string key) => screenCompact && dockGroups.Any(g => !g.Collapsed && g.ActiveTab.Key == key);

    void HostDock() { EnsureCompactDock(); LayoutDockGroups(); }

    void ReleaseDock() { foreach (var group in dockGroups) group.Release(); }

    void UpdateDockTitles()
    {
        if (dockGroups.FirstOrDefault(g => g.Key == "tools") is not { } tools) return;
        tools.SetTitle(0, WorkTabTitle);
        RebuildDockStrip();
    }

    // Folded groups as icons: one per tab, a hairline between groups.
    void RebuildDockStrip()
    {
        if (dockStrip == null || dockStripHost == null) return;
        dockStrip.Children.Clear();
        foreach (var group in dockGroups.Where(g => g.Collapsed))
        {
            if (dockStrip.Children.Count > 0) dockStrip.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(6, 4, 6, 4) });
            for (int i = 0; i < group.Tabs.Count; i++)
            {
                var tab = group.Tabs[i]; string title = group.Title(i);
                var button = Theme.IconButton(tab.Key == "work" ? WorkTabGlyph : tab.Glyph, () => Guard(() => ShowDockTab(tab.Key)), $"{title} 패널 펼치기", 28, 16);
                button.Margin = new Thickness(3, 1, 3, 1); Density.Mark(button, DensityRole.Keep);
                var menu = new ContextMenu();
                var expand = new MenuItem { Header = "그룹 펼치기" }; expand.Click += (_, _) => SetDockGroupCollapsed(group, false); menu.Items.Add(expand);
                var reset = new MenuItem { Header = "패널 배치 초기화" }; reset.Click += (_, _) => ResetDockLayout(); menu.Items.Add(reset);
                button.ContextMenu = menu;
                dockStrip.Children.Add(button);
            }
        }
        dockStripHost.Visibility = dockStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal ContextMenu DockGroupMenu(DockGroup group)
    {
        var menu = new ContextMenu();
        var fold = new MenuItem { Header = group.Collapsed ? "그룹 펼치기" : "그룹 접기", ToolTip = "그룹을 오른쪽 아이콘 줄로 접거나 다시 펼칩니다" };
        fold.Click += (_, _) => Guard(() => SetDockGroupCollapsed(group, !group.Collapsed));
        menu.Items.Add(fold);
        menu.Items.Add(new Separator());
        for (int i = 0; i < group.Tabs.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = $"{group.Title(i)} 패널", IsCheckable = true, IsChecked = i == group.ActiveIndex };
            item.Click += (_, _) => Guard(() => group.Select(index));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "패널 배치 초기화", ToolTip = "그룹 크기·접기·열린 탭과 도크 너비를 처음 상태로 되돌립니다" };
        reset.Click += (_, _) => Guard(ResetDockLayout);
        menu.Items.Add(reset);
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
        foreach (var group in dockGroups) { group.Weight = group.DefaultWeight; group.Collapsed = group.DefaultCollapsed; group.Select(0); }
        compactDockWidth = DefaultDockWidth;
        if (screenCompact) rightPanelColumn.Width = new GridLength(DefaultDockWidth);
        LayoutDockGroups(); UpdateDockPanels(); UpdateDockAfterRender();
    }

    internal DockGroupLayout[]? CaptureDockGroups() => dockGroups.Length == 0 ? savedDockGroups
        : dockGroups.Select(g => new DockGroupLayout(g.Key, Math.Round(g.Weight, 4), g.Collapsed, g.ActiveTab.Key)).ToArray();

    // Saved sizes, folds and open tabs; groups the layout does not mention keep their defaults.
    internal void ApplyDockGroups(DockGroupLayout[]? saved, bool layout = true)
    {
        savedDockGroups = saved;
        if (saved == null || dockGroups.Length == 0) return;
        foreach (var entry in saved)
        {
            if (dockGroups.FirstOrDefault(g => g.Key == entry.Key) is not { } group) continue;
            group.Weight = Math.Clamp(entry.Weight, WorkspaceLayoutStore.MinDockWeight, WorkspaceLayoutStore.MaxDockWeight);
            group.Collapsed = entry.Collapsed;
            int tab = group.Tabs.ToList().FindIndex(t => t.Key == entry.Tab);
            if (tab >= 0) group.Select(tab);
        }
        if (layout) LayoutDockGroups();
    }

    internal double CompactDockWidthNow => screenCompact ? (rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value) : compactDockWidth;
    internal double FriendlyPanelWidthNow => screenCompact ? friendlyPanelWidth : rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value;
}
