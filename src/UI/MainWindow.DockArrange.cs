using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Compositor.Windows;

// Rearranging the 간결한 화면 dock. A tab dragged onto a group's tab row joins that group at the
// insertion marker (a vertical accent bar; the body of a group adds it at the end); dropped on the
// edge between groups it becomes a new group there (a horizontal bar). A group header's empty area
// drags the whole group to another place in the stack. A group that loses its last tab leaves.
// Folded groups open as a flyout beside the icon strip, and any group can float in its own small
// tool window owned by the main window. The ☰ menu has a keyboard route for each of these.
// The mouse handlers only translate pointer positions into the dock stack's coordinates and call
// BeginDockDrag / DockDragOver / EndDockDrag, which tests drive the same way.
public sealed partial class MainWindow
{
    internal enum DockDropKind { None, Join, Between }
    /// <summary>Join: into Group before tab Index. Between: a new place in the stack before shown group Index.</summary>
    internal readonly record struct DockDrop(DockDropKind Kind, DockGroup? Group, int Index);
    sealed record DockDrag(DockGroup Group, DockTab? Tab, FrameworkElement? Handle);

    DockDrag? dockDrag;
    bool dockDragReleasing, closingDockWindow;
    Canvas? dockOverlay;
    internal Border? dockDropMarker, dockDropOutline, dockFlyout;
    Border? dockDragGhost, dockFlyoutShadow;
    TextBlock? dockDragGhostText;
    internal DockGroup? dockFlyoutGroup;
    double dockFlyoutTop;
    const double DockFlyoutMinHeight = 260, DockEdgeBand = 44;

    // The drag feedback layer over the whole body and the flyout host beside the strip.
    void BuildDockOverlays()
    {
        if (bodyGrid == null || dockOverlay != null) return;
        dockOverlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetColumnSpan(dockOverlay, 4); Panel.SetZIndex(dockOverlay, 60);
        dockDropOutline = new Border { BorderBrush = Theme.Accent, BorderThickness = new Thickness(1.5), Visibility = Visibility.Collapsed };
        dockDropMarker = new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed };
        dockDragGhostText = new TextBlock { FontSize = 12, Foreground = Theme.Text, TextWrapping = TextWrapping.NoWrap };
        dockDragGhost = new Border { Background = Theme.Surface, BorderBrush = Theme.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 8, 3), Opacity = .92, Child = dockDragGhostText, Visibility = Visibility.Collapsed };
        dockOverlay.Children.Add(dockDropOutline); dockOverlay.Children.Add(dockDropMarker); dockOverlay.Children.Add(dockDragGhost);
        bodyGrid.Children.Add(dockOverlay);
        // The shadow is its own element behind the flyout, so the panel inside renders without an effect.
        dockFlyoutShadow = new Border
        {
            Background = Theme.Panel, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 225, Opacity = .45, Color = Colors.Black }
        };
        Grid.SetColumn(dockFlyoutShadow, 2); Panel.SetZIndex(dockFlyoutShadow, 39);
        bodyGrid.Children.Add(dockFlyoutShadow);
        dockFlyout = new Border
        {
            Background = Theme.Panel, BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(dockFlyout, "잠시 연 패널");
        Grid.SetColumn(dockFlyout, 2); Panel.SetZIndex(dockFlyout, 40);
        bodyGrid.Children.Add(dockFlyout);
        bodyGrid.SizeChanged += (_, _) => { if (dockFlyoutGroup != null) PlaceDockFlyout(); };
    }

    // ---- Dragging tabs and groups -------------------------------------------------------------

    // Each tab button (with its tab) and each group header (tab null: the whole group) is a drag source.
    void WireDockDrag(DockGroup group, FrameworkElement source, DockTab? tab)
    {
        Point? start = null;
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            if (tab == null && !ReferenceEquals(e.OriginalSource, source) || e.ClickCount > 1) return;
            start = e.GetPosition(source);
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (dockDrag is { } drag && ReferenceEquals(drag.Handle, source)) { DockDragOver(DockStackPoint(e, source)); e.Handled = true; return; }
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(source);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            BeginDockDrag(group, tab, source);
            source.CaptureMouse();
            DockDragOver(DockStackPoint(e, source)); e.Handled = true;
        };
        source.PreviewMouseLeftButtonUp += (_, e) =>
        {
            start = null;
            if (dockDrag is not { } drag || !ReferenceEquals(drag.Handle, source)) return;
            e.Handled = true;
            var point = DockStackPoint(e, source);
            dockDragReleasing = true;
            try { source.ReleaseMouseCapture(); } finally { dockDragReleasing = false; }
            Guard(() => EndDockDrag(point));
        };
        source.LostMouseCapture += (_, _) => { if (!dockDragReleasing && dockDrag is { } drag && ReferenceEquals(drag.Handle, source)) CancelDockDrag(); };
    }

    // The pointer in the dock stack's coordinates, also from a floating window (through the screen).
    Point DockStackPoint(MouseEventArgs e, FrameworkElement source)
    {
        if (dockStack == null) return default;
        var from = PresentationSource.FromVisual(source); var to = PresentationSource.FromVisual(dockStack);
        if (from != null && to != null && !ReferenceEquals(from, to)) return dockStack.PointFromScreen(source.PointToScreen(e.GetPosition(source)));
        return e.GetPosition(dockStack);
    }

    /// <summary>Starts dragging a tab (or, with no tab, its whole group).</summary>
    internal void BeginDockDrag(DockGroup group, DockTab? tab, FrameworkElement? handle = null)
    {
        if (!screenCompact || dockStack == null) return;
        CommitFocusedInspectorField();
        if (tab != null) group = dockGroups.FirstOrDefault(g => g.Tabs.Contains(tab)) ?? group;
        if (!dockGroups.Contains(group)) return;
        dockDrag = new DockDrag(group, tab, handle);
        if (dockDragGhost != null && dockDragGhostText != null) { dockDragGhostText.Text = tab?.Title ?? group.TitleList; dockDragGhost.Visibility = Visibility.Collapsed; }
    }

    /// <summary>The pointer moved (dock stack coordinates): shows where the drag would land.</summary>
    internal DockDrop DockDragOver(Point point)
    {
        if (dockDrag is not { } drag) return default;
        var drop = DockDropAt(point, drag);
        ShowDockDrop(drop, point);
        return drop;
    }

    /// <summary>Drops at the point (dock stack coordinates), or cancels with null. True when the dock changed.</summary>
    internal bool EndDockDrag(Point? point)
    {
        if (dockDrag is not { } drag) return false;
        var drop = point is { } at ? DockDropAt(at, drag) : default;
        dockDrag = null; HideDockDrop();
        if (drop.Kind == DockDropKind.None) return false;
        bool changed = drag.Tab is { } tab
            ? drop.Kind == DockDropKind.Join ? MoveDockTab(tab.Key, drop.Group!, drop.Index) : SplitDockTab(tab.Key, StackReference(drop.Index))
            : MoveDockGroup(drag.Group, StackReference(drop.Index));
        if (changed) status.Text = "패널 배치를 바꿨습니다 · ☰ 메뉴의 패널 배치 초기화로 되돌릴 수 있습니다";
        return changed;
    }

    void CancelDockDrag()
    {
        if (dockDrag is not { } drag) return;
        dockDrag = null; HideDockDrop();
        if (drag.Handle is { IsMouseCaptured: true } handle) { dockDragReleasing = true; try { handle.ReleaseMouseCapture(); } finally { dockDragReleasing = false; } }
    }

    /// <summary>Esc: cancels a dock drag or closes the flyout. False when neither was open.</summary>
    internal bool DockEscape()
    {
        if (dockDrag != null) { CancelDockDrag(); return true; }
        if (dockFlyoutGroup != null) { CloseDockFlyout(focusAnchor: true); return true; }
        return false;
    }

    Rect DockBounds(FrameworkElement element) => dockStack == null ? Rect.Empty
        : element.TransformToVisual(dockStack).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    // Where a drag would land. Over a group's tab row: join it before the nearest tab. Over its
    // body: join at the end, except near the group's lower edge (or the stack's top edge), which
    // places a new group between. A group drag only lands between groups.
    DockDrop DockDropAt(Point point, DockDrag drag)
    {
        if (dockStack == null || !screenCompact || dockStack.ActualWidth <= 0) return default;
        double width = dockStack.ActualWidth, height = dockStack.ActualHeight;
        if (point.X < -12 || point.X > width + 12 || point.Y < -24 || point.Y > height + 16) return default;
        var shown = ShownDockGroups.ToArray();
        if (shown.Length == 0) return new DockDrop(DockDropKind.Between, null, 0);
        int i = 0;
        while (i < shown.Length - 1 && point.Y > DockBounds(shown[i]).Bottom + DockGap / 2) i++;
        var target = shown[i]; var r = DockBounds(target);
        int sourceIndex = Array.IndexOf(shown, drag.Group);
        DockDrop Between(int index)
        {
            // Back to its own place: a lone tab's group, or the group itself, would not move.
            bool alone = drag.Tab == null || drag.Group.Tabs.Count == 1;
            return alone && sourceIndex >= 0 && (index == sourceIndex || index == sourceIndex + 1) ? default : new DockDrop(DockDropKind.Between, null, index);
        }
        if (drag.Tab == null) return Between(point.Y < r.Top + r.Height / 2 ? i : i + 1);
        // Above the group: the stack's top edge, or the gap under the group before it.
        if (point.Y < r.Top + (i == 0 ? 5 : 0)) return Between(i);
        double headerBottom = r.Top + (target.Header.ActualHeight > 0 ? target.Header.ActualHeight : DockGroup.HeaderHeight);
        double band = Math.Clamp(r.Height * .25, 20, DockEdgeBand);
        DockDrop Join(int index)
        {
            if (ReferenceEquals(target, drag.Group))
            {
                int from = target.Tabs.ToList().IndexOf(drag.Tab);
                if (target.Tabs.Count == 1 || index == from || index == from + 1) return default;
            }
            return new DockDrop(DockDropKind.Join, target, index);
        }
        if (point.Y <= headerBottom) return Join(DockInsertionIndex(target, point));
        if (point.Y >= r.Bottom - band) return Between(i + 1);
        return Join(target.Tabs.Count);
    }

    // Before the first tab (in the pointer's row of the wrapped tab row) whose middle is right of it.
    int DockInsertionIndex(DockGroup group, Point point)
    {
        var rects = group.TabButtons.Select(DockBounds).ToArray();
        if (rects.Length == 0) return 0;
        double Distance(Rect rect) => point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0;
        double rowTop = rects.OrderBy(Distance).First().Top;
        var row = Enumerable.Range(0, rects.Length).Where(k => Math.Abs(rects[k].Top - rowTop) < 1).ToArray();
        foreach (int k in row) if (point.X < rects[k].Left + rects[k].Width / 2) return k;
        return row[^1] + 1;
    }

    void ShowDockDrop(DockDrop drop, Point point)
    {
        if (dockOverlay == null || dockStack == null || dockDropMarker == null || dockDropOutline == null || dockDragGhost == null) return;
        Point Overlay(Point p) => dockStack.TranslatePoint(p, dockOverlay);
        void Place(FrameworkElement element, Rect rect) { var at = Overlay(rect.TopLeft); Canvas.SetLeft(element, at.X); Canvas.SetTop(element, at.Y); element.Width = Math.Max(0, rect.Width); element.Height = Math.Max(0, rect.Height); element.Visibility = Visibility.Visible; }
        dockDropMarker.Visibility = dockDropOutline.Visibility = Visibility.Collapsed;
        if (drop.Kind == DockDropKind.Join && drop.Group != null)
        {
            var group = drop.Group; var r = DockBounds(group);
            Place(dockDropOutline, r);
            dockDropOutline.Background = new SolidColorBrush(((SolidColorBrush)Theme.Accent).Color) { Opacity = .08 };
            var rects = group.TabButtons.Select(DockBounds).ToArray();
            Rect marker;
            if (rects.Length == 0) marker = new Rect(r.Left + 4, r.Top + 3, 2, DockGroup.HeaderHeight - 6);
            else if (drop.Index < rects.Length) { var b = rects[drop.Index]; marker = new Rect(b.Left - 1, b.Top + 3, 2, Math.Max(8, b.Height - 6)); }
            else { var b = rects[^1]; marker = new Rect(b.Right - 1, b.Top + 3, 2, Math.Max(8, b.Height - 6)); }
            Place(dockDropMarker, marker);
        }
        else if (drop.Kind == DockDropKind.Between)
        {
            var shown = ShownDockGroups.ToArray();
            double y = shown.Length == 0 ? 0
                : drop.Index <= 0 ? DockBounds(shown[0]).Top
                : drop.Index >= shown.Length ? DockBounds(shown[^1]).Bottom
                : (DockBounds(shown[drop.Index - 1]).Bottom + DockBounds(shown[drop.Index]).Top) / 2;
            y = Math.Clamp(y - 1.5, 0, Math.Max(0, dockStack.ActualHeight - 3));
            Place(dockDropMarker, new Rect(2, y, Math.Max(0, dockStack.ActualWidth - 4), 3));
        }
        var ghost = Overlay(point);
        Canvas.SetLeft(dockDragGhost, ghost.X + 12); Canvas.SetTop(dockDragGhost, ghost.Y + 10);
        dockDragGhost.Visibility = Visibility.Visible;
    }

    void HideDockDrop()
    {
        foreach (var element in new FrameworkElement?[] { dockDropMarker, dockDropOutline, dockDragGhost }) if (element != null) element.Visibility = Visibility.Collapsed;
    }

    // ---- Moving tabs and groups ---------------------------------------------------------------

    // A stack index as "before this group" (null: after the last group).
    DockGroup? StackReference(int index)
    {
        var shown = ShownDockGroups.ToArray();
        return index >= 0 && index < shown.Length ? shown[index] : null;
    }

    internal DockGroup? NextDockGroup(DockGroup group)
    {
        int index = Array.IndexOf(dockGroups, group);
        return index >= 0 && index + 1 < dockGroups.Length ? dockGroups[index + 1] : null;
    }

    // Groups in the stack move among the stack; folded or floating ones among all groups.
    internal DockGroup? DockNeighbor(DockGroup group, int step)
    {
        var lane = ShownDockGroups.Contains(group) ? ShownDockGroups.ToArray() : dockGroups;
        int index = Array.IndexOf(lane, group) + step;
        return index >= 0 && index < lane.Length && index != Array.IndexOf(lane, group) ? lane[index] : null;
    }

    string NewDockGroupKey()
    {
        for (int n = 1; ; n++) if (!dockGroups.Any(g => g.Key == "group" + n)) return "group" + n;
    }

    void RemoveDockGroup(DockGroup group)
    {
        if (ReferenceEquals(group, dockFlyoutGroup)) CloseDockFlyout(focusAnchor: false);
        CloseDockWindow(group); group.Release(); DockGroup.Detach(group);
        dockGroups = dockGroups.Where(g => !ReferenceEquals(g, group)).ToArray();
    }

    void InsertDockGroup(DockGroup group, DockGroup? before)
    {
        var list = dockGroups.Where(g => !ReferenceEquals(g, group)).ToList();
        int at = before == null ? -1 : list.IndexOf(before);
        list.Insert(at < 0 ? list.Count : at, group);
        dockGroups = list.ToArray();
    }

    void AfterDockChange()
    {
        LayoutDockGroups(); UpdateDockPanels(); UpdateDockAfterRender();
    }

    /// <summary>Moves a tab into a group before tab <paramref name="index"/>; it opens there. Its old group goes when emptied.</summary>
    internal bool MoveDockTab(string key, DockGroup target, int index)
    {
        if (DockGroupOf(key) is not { } source || !dockGroups.Contains(target)) return false;
        var tab = source.Tabs.First(t => t.Key == key);
        CommitFocusedInspectorField();
        if (ReferenceEquals(source, target))
        {
            int from = source.Tabs.ToList().IndexOf(tab);
            int to = index > from ? index - 1 : index;
            if (to == from) return false;
            source.Move(from, to); source.Select(source.Tabs.ToList().IndexOf(tab));
            AfterDockChange();
            return true;
        }
        source.Remove(tab);
        if (source.Tabs.Count == 0) RemoveDockGroup(source);
        target.Insert(index, tab);
        target.Select(target.Tabs.ToList().IndexOf(tab));
        AfterDockChange();
        return true;
    }

    /// <summary>Takes a tab out into a new group placed before <paramref name="before"/> (null: last); the new group is open in the stack.</summary>
    internal bool SplitDockTab(string key, DockGroup? before)
    {
        if (DockGroupOf(key) is not { } source) return false;
        if (source.Tabs.Count == 1 && !source.Collapsed && !source.Floating && (ReferenceEquals(before, source) || ReferenceEquals(before, NextShown(source)))) return false;
        var tab = source.Tabs.First(t => t.Key == key);
        CommitFocusedInspectorField();
        var shown = ShownDockGroups.ToArray();
        double weight = shown.Length > 0 ? shown.Average(g => g.Weight) : 1;
        source.Remove(tab);
        if (source.Tabs.Count == 0) { if (ReferenceEquals(before, source)) before = NextDockGroup(source); RemoveDockGroup(source); }
        var group = NewDockGroup(NewDockGroupKey(), [tab]);
        group.Weight = Math.Clamp(weight, WorkspaceLayoutStore.MinDockWeight, WorkspaceLayoutStore.MaxDockWeight);
        InsertDockGroup(group, before);
        AfterDockChange();
        return true;
    }

    DockGroup? NextShown(DockGroup group)
    {
        var shown = ShownDockGroups.ToArray(); int index = Array.IndexOf(shown, group);
        return index >= 0 && index + 1 < shown.Length ? shown[index + 1] : null;
    }

    /// <summary>Moves a whole group before <paramref name="before"/> (null: last) and docks it there if it was a flyout or floating.</summary>
    internal bool MoveDockGroup(DockGroup group, DockGroup? before)
    {
        if (!dockGroups.Contains(group) || ReferenceEquals(before, group)) return false;
        bool docked = !group.Collapsed && !group.Floating;
        if (docked && ReferenceEquals(NextShown(group), before) && (before != null || ShownDockGroups.Last() == group)) return false;
        CommitFocusedInspectorField();
        if (ReferenceEquals(group, dockFlyoutGroup)) CloseDockFlyout(focusAnchor: false);
        if (group.Floating) { CloseDockWindow(group); group.Floating = false; }
        group.Collapsed = false;
        InsertDockGroup(group, before);
        AfterDockChange();
        return true;
    }

    /// <summary>☰ 위로/아래로 옮기기: one place up or down among its neighbours.</summary>
    internal bool MoveDockGroupBy(DockGroup group, int step)
    {
        if (DockNeighbor(group, step) is not { } neighbor) return false;
        CommitFocusedInspectorField();
        var list = dockGroups.Where(g => !ReferenceEquals(g, group)).ToList();
        int at = list.IndexOf(neighbor) + (step > 0 ? 1 : 0);
        list.Insert(at, group);
        dockGroups = list.ToArray();
        AfterDockChange();
        return true;
    }

    // ---- Flyout beside the icon strip --------------------------------------------------------

    void ToggleDockFlyout(DockGroup group, int index, FrameworkElement? anchor)
    {
        if (ReferenceEquals(group, dockFlyoutGroup) && group.ActiveIndex == index) CloseDockFlyout(focusAnchor: true);
        else OpenDockFlyout(group, index, anchor);
    }

    /// <summary>Opens a folded group beside the icon strip, at the icon's height, until a press outside, Esc or 📌 도킹.</summary>
    internal void OpenDockFlyout(DockGroup group, int index, FrameworkElement? anchor)
    {
        if (dockFlyout == null || !group.Collapsed || group.Floating || !dockGroups.Contains(group)) return;
        CommitFocusedInspectorField();
        if (!ReferenceEquals(group, dockFlyoutGroup))
        {
            CloseDockFlyout(focusAnchor: false);
            dockFlyoutGroup = group;
            DockGroup.Detach(group); dockFlyout.Child = group;
            group.Hosted = true; group.SetDockButtonVisible(true);
            dockFlyoutTop = anchor != null && bodyGrid != null && anchor.IsDescendantOf(bodyGrid) ? anchor.TranslatePoint(new Point(), bodyGrid).Y - 4 : 0;
        }
        group.Select(index); group.Show(); group.Paint();
        dockFlyout.Visibility = Visibility.Visible;
        PlaceDockFlyout();
        RebuildDockStrip();
        UpdateDockPanels(); UpdateDockAfterRender();
        if (!headlessTesting) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (ReferenceEquals(dockFlyoutGroup, group) && group.ActiveIndex >= 0) group.TabButtons[group.ActiveIndex].Focus(); }));
    }

    // Right edge on the dock, as wide as the stack, about half the body high, inside the body.
    void PlaceDockFlyout()
    {
        if (dockFlyout == null || bodyGrid == null) return;
        double body = bodyGrid.ActualHeight > 0 ? bodyGrid.ActualHeight : 600;
        double stage = bodyGrid.ColumnDefinitions.Count > 2 && bodyGrid.ColumnDefinitions[2].ActualWidth > 0 ? bodyGrid.ColumnDefinitions[2].ActualWidth : 800;
        double stack = dockStack is { ActualWidth: > 0 } s ? s.ActualWidth : CompactDockWidthNow - DockStripWidth - 4;
        double width = Math.Min(Math.Max(stack, MinDockWidth - DockStripWidth), Math.Max(160, stage - 16));
        double height = Math.Clamp(body * .56, Math.Min(DockFlyoutMinHeight, body), body);
        double top = Math.Clamp(dockFlyoutTop, 0, Math.Max(0, body - height));
        dockFlyout.Width = width; dockFlyout.Height = height; dockFlyout.Margin = new Thickness(0, top, 0, 0);
        if (dockFlyoutShadow != null) { dockFlyoutShadow.Width = width; dockFlyoutShadow.Height = height; dockFlyoutShadow.Margin = dockFlyout.Margin; dockFlyoutShadow.Visibility = dockFlyout.Visibility; }
    }

    internal void CloseDockFlyout(bool focusAnchor)
    {
        if (dockFlyoutGroup is not { } group) return;
        dockFlyoutGroup = null;
        if (dockFlyout != null) { dockFlyout.Child = null; dockFlyout.Visibility = Visibility.Collapsed; }
        if (dockFlyoutShadow != null) dockFlyoutShadow.Visibility = Visibility.Collapsed;
        group.SetDockButtonVisible(group.Floating);
        if (group.Collapsed && !group.Floating) group.Release();
        RebuildDockStrip();
        UpdateDockPanels();
        // Keyboard users land back on the strip icon that opened it (once the rebuilt strip is laid out).
        if (focusAnchor && !headlessTesting && dockStrip != null && group.Tabs.Count > 0)
        {
            string name = $"{group.ActiveTab.Title} 패널 열기";
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                dockStrip.Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) is { } shown && (shown == name || shown == Loc.T(name)))?.Focus()));
        }
    }

    /// <summary>A press in the main window: outside the flyout (and the strip that toggles it) closes the flyout.</summary>
    internal void DockPointerPressed(DependencyObject? source)
    {
        if (dockFlyoutGroup == null || source == null) return;
        for (var node = source; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, dockFlyout) || ReferenceEquals(node, dockStripHost)) return;
        CloseDockFlyout(focusAnchor: false);
    }

    /// <summary>📌 도킹: a flyout or floating group goes back into the stack, open.</summary>
    internal void DockGroupBack(DockGroup group)
    {
        if (!dockGroups.Contains(group)) return;
        CommitFocusedInspectorField();
        if (ReferenceEquals(group, dockFlyoutGroup)) CloseDockFlyout(focusAnchor: false);
        if (group.Floating) { CloseDockWindow(group); group.Floating = false; }
        group.Collapsed = false; group.SetDockButtonVisible(false);
        AfterDockChange();
    }

    // ---- Floating groups ------------------------------------------------------------------------

    /// <summary>떠 있는 패널로 열기: the group moves into a small tool window owned by the main window.</summary>
    internal void FloatDockGroup(DockGroup group)
    {
        if (!dockGroups.Contains(group)) return;
        if (group.Floating) { if (!headlessTesting && group.FloatingWindow is { IsVisible: true } open) open.Activate(); return; }
        CommitFocusedInspectorField();
        if (ReferenceEquals(group, dockFlyoutGroup)) CloseDockFlyout(focusAnchor: false);
        group.Floating = true; group.Collapsed = false;
        AfterDockChange();
    }

    // The floating window's bounds now, or the ones it had when it last closed.
    Rect? FloatRect(DockGroup group) => group.FloatingWindow is { } window && window.WindowState == WindowState.Normal && double.IsFinite(window.Left) && double.IsFinite(window.Top)
        ? new Rect(window.Left, window.Top, window.ActualWidth > 0 ? window.ActualWidth : window.Width, window.ActualHeight > 0 ? window.ActualHeight : window.Height)
        : group.FloatBounds;

    // Floating windows show while 간결한 화면 shows the dock (a document is open); the start screen
    // hides them and the friendly screen closes them, handing their panes back. Runs on every
    // document refresh (ApplyStartFocus), so it only touches what changed.
    void SyncDockWindows()
    {
        bool show = screenCompact && compactDock is { Visibility: Visibility.Visible };
        foreach (var group in dockGroups)
        {
            if (!group.Floating) { CloseDockWindow(group); continue; }
            if (!screenCompact) { CloseDockWindow(group); group.Release(); continue; }
            if (show)
            {
                if (group.FloatingWindow == null) OpenDockWindow(group);
                else
                {
                    // Tabs may have moved in or out; the title and name follow (translated, as Loaded did).
                    var window = group.FloatingWindow;
                    string title = Loc.T("Morupixel · " + group.TitleList), name = Loc.T($"{group.TitleList} 떠 있는 패널");
                    if (window.Title != title) window.Title = title;
                    if (AutomationProperties.GetName(window) != name) AutomationProperties.SetName(window, name);
                    if (!headlessTesting && !window.IsVisible) window.Show();
                }
            }
            else group.FloatingWindow?.Hide();
        }
    }

    void OpenDockWindow(DockGroup group)
    {
        DockGroup.Detach(group);
        var host = new Border { Background = Theme.Panel, Child = group };
        Density.SetCompact(host, true);
        var window = new Window
        {
            Title = "Morupixel · " + group.TitleList, Content = host, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize,
            FontFamily = Theme.UiFont, FontSize = Theme.BodySize, Background = Theme.Header, Foreground = Theme.Text, UseLayoutRounding = true, SnapsToDevicePixels = true,
            MinWidth = 220, MinHeight = 160, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false
        };
        AutomationProperties.SetName(window, $"{group.TitleList} 떠 있는 패널");
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero) window.Owner = this;
        var bounds = group.FloatBounds is { } saved && OnScreen(saved, 120, 80) ? saved : DefaultFloatBounds();
        window.Left = bounds.Left; window.Top = bounds.Top; window.Width = Math.Max(window.MinWidth, bounds.Width); window.Height = Math.Max(window.MinHeight, bounds.Height);
        FitToWorkArea(window);
        group.FloatingWindow = window; group.Hosted = true; group.SetDockButtonVisible(true); group.Show(); group.Paint();
        void Remember() { if (ReferenceEquals(group.FloatingWindow, window) && FloatRect(group) is { } rect) group.FloatBounds = rect; }
        window.LocationChanged += (_, _) => Remember();
        window.SizeChanged += (_, _) => Remember();
        // Closing the window docks the group, like the friendly screen's floating panels.
        window.Closing += (_, e) =>
        {
            if (closingPanels || closingDockWindow || !ReferenceEquals(group.FloatingWindow, window)) return;
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => Guard(() => DockGroupBack(group))));
        };
        if (!headlessTesting) window.Show();
    }

    // Beside the dock's left edge (screen DIPs), or near the main window's right side before it is shown.
    Rect DefaultFloatBounds()
    {
        double width = Math.Max(260, dockStack is { ActualWidth: > 0 } stack ? stack.ActualWidth : DefaultDockWidth - DockStripWidth), height = 420;
        if (compactDock is { IsVisible: true } dock && PresentationSource.FromVisual(dock) != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this); var corner = dock.PointToScreen(new Point());
            return new Rect(corner.X / dpi.DpiScaleX - width - 12, corner.Y / dpi.DpiScaleY + 24, width, height);
        }
        double left = double.IsFinite(Left) ? Left : 120, top = double.IsFinite(Top) ? Top : 120, span = ActualWidth > 0 ? ActualWidth : double.IsFinite(Width) ? Width : 1280;
        return new Rect(left + Math.Max(0, span - width - DefaultDockWidth - 24), top + 120, width, height);
    }

    // Closes a group's window without docking it (the group stays Floating to reopen later).
    void CloseDockWindow(DockGroup group)
    {
        if (group.FloatingWindow is not { } window) return;
        if (FloatRect(group) is { } rect) group.FloatBounds = rect;
        group.FloatingWindow = null;
        closingDockWindow = true;
        try { if (window.Content is Border host) host.Child = null; window.Content = null; window.Close(); }
        finally { closingDockWindow = false; }
        if (!group.Floating) group.SetDockButtonVisible(ReferenceEquals(group, dockFlyoutGroup));
    }

    void CloseDockWindows() { foreach (var group in dockGroups) CloseDockWindow(group); }
}
