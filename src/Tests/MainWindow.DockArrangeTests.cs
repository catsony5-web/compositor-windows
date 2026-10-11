using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// 간결한 화면 dock arrangement: tabs dragged between groups and into new groups, groups reordered,
// flyouts beside the icon strip, floating groups, and the arrangement in the workspace layout
// (with Preview 44 layouts migrated). Drags run through BeginDockDrag / DockDragOver / EndDockDrag,
// the same calls the mouse handlers make, with points taken from the laid-out dock.
public sealed partial class MainWindow
{
    internal static void RunDockArrangeTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Layout(MainWindow w, double width = 1480, double height = 920)
        {
            var content = (FrameworkElement)w.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        static Document Photo()
        {
            var document = new Document { Width = 400, Height = 300, Name = "도크" };
            document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(400, 300, Colors.SteelBlue) });
            return document;
        }
        static MainWindow Open()
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.AddTab(Photo(), null);
            w.SetScreenStyle(true); Layout(w);
            return w;
        }
        static void Close(MainWindow w) { try { w.SetScreenStyle(false); } finally { w.StopRenderingForShutdown(); } }
        static DockGroup G(MainWindow w, string tab) => w.DockGroupOf(tab) ?? throw new InvalidOperationException("No group holds " + tab);
        static string Keys(DockGroup g) => string.Join(",", g.Tabs.Select(t => t.Key));
        static string Stack(MainWindow w) => string.Join(" | ", w.ShownDockGroups.Select(Keys));
        static string Describe(MainWindow w) => string.Join(" / ", w.dockGroups.Select(g => $"{g.Key}:{Keys(g)}:{g.ActiveTab.Key}:{(g.Floating ? "floating" : g.Collapsed ? "folded" : "open")}"));
        static Point OnTab(MainWindow w, DockGroup g, int index, double fraction = .25)
        {
            var button = g.TabButtons[index];
            return button.TranslatePoint(new Point(button.ActualWidth * fraction, button.ActualHeight / 2), w.dockStack!);
        }
        static Point Top(MainWindow w, DockGroup g) => g.TranslatePoint(new Point(g.ActualWidth / 2, 2), w.dockStack!);
        static Point Bottom(MainWindow w, DockGroup g) => g.TranslatePoint(new Point(g.ActualWidth / 2, g.ActualHeight - 6), w.dockStack!);
        static Point Middle(MainWindow w, DockGroup g) => g.TranslatePoint(new Point(g.ActualWidth / 2, g.ActualHeight / 2), w.dockStack!);
        static MenuItem Menu(MainWindow w, DockGroup g, string header) => w.DockGroupMenu(g).Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == header);
        static void Choose(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        static Button Strip(MainWindow w, string name) => w.dockStrip!.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        const string Defaults = "color,swatches,gradients,patterns,entourage | properties,adjustments | navigator,histogram,info | layers,artboards,history";

        test("compact dock: a tab dragged onto another group's tab row joins it at the marker and drags back", () =>
        {
            var w = Open();
            try
            {
                Check(Stack(w) == Defaults, "Unexpected default stack: " + Stack(w));
                var color = G(w, "color"); var layers = G(w, "layers"); var swatches = color.Tabs.Single(t => t.Key == "swatches");
                w.BeginDockDrag(color, swatches);
                var at = OnTab(w, layers, 1);
                var drop = w.DockDragOver(at);
                Check(drop.Kind == DockDropKind.Join && drop.Group == layers && drop.Index == 1, $"The drop target over the tab row is wrong: {drop.Kind} {drop.Index}");
                var marker = w.dockDropMarker!; var before = layers.TabButtons[1].TranslatePoint(new Point(), w.dockOverlay!);
                Check(marker.Visibility == Visibility.Visible && marker.Height > marker.Width && Math.Abs(Canvas.GetLeft(marker) - (before.X - 1)) < 1.5 && w.dockDropOutline!.Visibility == Visibility.Visible && w.dockDragGhost!.Visibility == Visibility.Visible,
                    $"The insertion marker does not stand before the tab ({Canvas.GetLeft(marker):0.#} vs {before.X:0.#})");
                Check(w.EndDockDrag(at), "The drop did not change the dock");
                Layout(w);
                Check(Keys(layers) == "layers,swatches,artboards,history" && Keys(color) == "color,gradients,patterns,entourage" && layers.ActiveTab == swatches && w.DockTabVisible("swatches"), "The tab did not move into the group: " + Stack(w));
                Check(ReferenceEquals(layers.Body.Child, w.dockContent["swatches"]) && ReferenceEquals(color.Body.Child, w.dockContent["color"]), "The groups do not show their open tabs");
                Check(marker.Visibility == Visibility.Collapsed && w.dockDropOutline!.Visibility == Visibility.Collapsed && w.dockDragGhost!.Visibility == Visibility.Collapsed, "The drag feedback stayed after the drop");
                w.BeginDockDrag(layers, swatches);
                Check(w.EndDockDrag(OnTab(w, color, 1)), "Dragging back did nothing");
                Layout(w);
                Check(Stack(w) == Defaults, "Dragging back did not restore the groups: " + Stack(w));
                // Inside one group, then Esc and a drop on the tab's own place.
                var patterns = color.Tabs.Single(t => t.Key == "patterns");
                w.BeginDockDrag(color, patterns); w.EndDockDrag(OnTab(w, color, 0)); Layout(w);
                Check(Keys(color) == "patterns,color,swatches,gradients,entourage" && color.ActiveTab == patterns, "Reordering inside the group failed: " + Keys(color));
                w.BeginDockDrag(color, patterns); w.DockDragOver(OnTab(w, layers, 0));
                Check(w.DockEscape() && Keys(color) == "patterns,color,swatches,gradients,entourage" && marker.Visibility == Visibility.Collapsed && !w.EndDockDrag(OnTab(w, layers, 0)), "Esc did not cancel the drag");
                w.BeginDockDrag(color, patterns);
                Check(w.DockDragOver(OnTab(w, color, 1, .3)).Kind == DockDropKind.None && marker.Visibility == Visibility.Collapsed && !w.EndDockDrag(OnTab(w, color, 1, .3)), "Dropping a tab on its own place was not a no-op");
                // The body of a group adds the tab at its end.
                w.BeginDockDrag(color, patterns);
                var body = w.DockDragOver(Middle(w, layers));
                Check(body.Kind == DockDropKind.Join && body.Group == layers && body.Index == layers.Tabs.Count, "A group's body does not add at the end");
                w.DockEscape();
            }
            finally { Close(w); }
        });

        test("compact dock: a tab dropped between groups becomes a new group that leaves with its last tab", () =>
        {
            var w = Open();
            try
            {
                var color = G(w, "color"); var properties = G(w, "properties"); var navigator = G(w, "navigator"); var histogram = navigator.Tabs.Single(t => t.Key == "histogram");
                w.BeginDockDrag(navigator, histogram);
                var at = Bottom(w, properties);
                var drop = w.DockDragOver(at);
                var marker = w.dockDropMarker!;
                double gap = (properties.TranslatePoint(new Point(0, properties.ActualHeight), w.dockOverlay!).Y + navigator.TranslatePoint(new Point(), w.dockOverlay!).Y) / 2;
                Check(drop.Kind == DockDropKind.Between && drop.Index == 2 && marker.Width > marker.Height * 10 && Math.Abs(Canvas.GetTop(marker) + 1.5 - gap) < 2 && w.dockDropOutline!.Visibility == Visibility.Collapsed,
                    $"The edge between groups is not a new-group target ({drop.Kind} {drop.Index}, marker at {Canvas.GetTop(marker):0.#} vs {gap:0.#})");
                Check(w.DockDragOver(Top(w, color)) is { Kind: DockDropKind.Between, Index: 0 }, "The stack's top edge is not a new-group target");
                Check(w.DockDragOver(navigator.TranslatePoint(new Point(navigator.ActualWidth / 2, -1), w.dockStack!)) is { Kind: DockDropKind.Between, Index: 2 }, "The gap between groups is not a new-group target");
                w.EndDockDrag(at); Layout(w);
                var split = G(w, "histogram");
                Check(Stack(w) == "color,swatches,gradients,patterns,entourage | properties,adjustments | histogram | navigator,info | layers,artboards,history" && split.Key == "group1" && w.dockGroups.Length == 6 && w.DockTabVisible("histogram") && w.DockTabVisible("navigator"),
                    "The tab did not become a new group: " + Stack(w));
                Check(AutomationProperties.GetName(split) == "히스토그램 패널 그룹" && split.TabButtons.All(b => AutomationProperties.GetName(b) == "히스토그램 패널"), "The new group lacks accessible names");
                // The lone tab back onto its own edges changes nothing; onto another row it leaves and the group goes.
                w.BeginDockDrag(split, histogram);
                Check(w.DockDragOver(Bottom(w, properties)).Kind == DockDropKind.None && w.DockDragOver(Bottom(w, split)).Kind == DockDropKind.None, "A lone tab's own place was offered as a target");
                w.EndDockDrag(OnTab(w, navigator, 1)); Layout(w);
                Check(Stack(w) == Defaults && w.dockGroups.Length == 5 && !w.dockGroups.Contains(split) && split.Parent == null, "The emptied group did not leave: " + Stack(w));
                // The keyboard route in ☰: 새 그룹으로 분리, then 다른 그룹으로 옮기기 › back.
                navigator.Select(1);
                Choose(Menu(w, navigator, "새 그룹으로 분리")); Layout(w);
                split = G(w, "histogram");
                Check(Stack(w) == "color,swatches,gradients,patterns,entourage | properties,adjustments | navigator,info | histogram | layers,artboards,history", "새 그룹으로 분리 did not split below the group: " + Stack(w));
                Check(!Menu(w, split, "새 그룹으로 분리").IsEnabled && Menu(w, navigator, "새 그룹으로 분리").IsEnabled, "Splitting a one-tab group was offered");
                var move = Menu(w, split, "다른 그룹으로 옮기기");
                Check(move.Items.OfType<MenuItem>().Count() == w.dockGroups.Length - 1, "다른 그룹으로 옮기기 does not list the other groups");
                Choose(move.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == navigator.TitleList)); Layout(w);
                Check(Keys(navigator) == "navigator,info,histogram" && navigator.ActiveTab.Key == "histogram" && w.dockGroups.Length == 5, "다른 그룹으로 옮기기 did not move the tab: " + Stack(w));
            }
            finally { Close(w); }
        });

        test("compact dock: group headers drag groups to another place and ☰ moves them up and down", () =>
        {
            var w = Open();
            try
            {
                var color = G(w, "color"); var layers = G(w, "layers");
                w.BeginDockDrag(layers, null);
                var drop = w.DockDragOver(Top(w, color));
                Check(drop.Kind == DockDropKind.Between && drop.Index == 0 && w.dockDropMarker!.Visibility == Visibility.Visible && w.dockDragGhost!.Child is TextBlock { Text: var ghost } && ghost == layers.TitleList, "The group drag has no target at the top");
                Check(w.EndDockDrag(Top(w, color)), "The group did not move");
                Layout(w);
                Check(Stack(w) == "layers,artboards,history | color,swatches,gradients,patterns,entourage | properties,adjustments | navigator,histogram,info", "The group is not at the top: " + Stack(w));
                w.BeginDockDrag(layers, null);
                Check(w.DockDragOver(Top(w, layers)).Kind == DockDropKind.None && w.DockDragOver(Bottom(w, layers)).Kind == DockDropKind.None && w.DockDragOver(OnTab(w, G(w, "properties"), 0)).Kind == DockDropKind.Between, "A group's own place was offered, or a tab row joined a group drag");
                w.DockEscape();
                Check(!Menu(w, layers, "위로 옮기기").IsEnabled && Menu(w, layers, "아래로 옮기기").IsEnabled, "위로/아래로 옮기기 availability is wrong at the top");
                for (int i = 0; i < 3; i++) Choose(Menu(w, layers, "아래로 옮기기"));
                Layout(w);
                Check(Stack(w) == Defaults && !Menu(w, layers, "아래로 옮기기").IsEnabled && Menu(w, layers, "위로 옮기기").IsEnabled, "아래로 옮기기 did not walk the group down: " + Stack(w));
                Choose(Menu(w, layers, "위로 옮기기")); Layout(w);
                Check(Stack(w) == "color,swatches,gradients,patterns,entourage | properties,adjustments | layers,artboards,history | navigator,histogram,info", "위로 옮기기 did not move the group up: " + Stack(w));
                w.BeginDockDrag(color, null); w.EndDockDrag(Bottom(w, G(w, "navigator"))); Layout(w);
                Check(Stack(w) == "properties,adjustments | layers,artboards,history | navigator,histogram,info | color,swatches,gradients,patterns,entourage", "Dragging a group to the bottom edge failed: " + Stack(w));
            }
            finally { Close(w); }
        });

        test("compact dock: a folded group opens as a flyout beside the strip and closes on Esc, a press outside or 도킹", () =>
        {
            var w = Open();
            try
            {
                var navigator = G(w, "navigator"); var layers = G(w, "layers");
                w.SetDockGroupCollapsed(navigator, true); Layout(w);
                Press(Strip(w, "정보 패널 열기")); Layout(w);
                var flyout = w.dockFlyout!;
                Check(w.dockFlyoutGroup == navigator && flyout.Child == navigator && flyout.Visibility == Visibility.Visible && navigator.ActiveTab.Key == "info" && w.DockTabVisible("info")
                    && ReferenceEquals(navigator.Body.Child, w.dockContent["info"]) && navigator.DockButton.Visibility == Visibility.Visible && AutomationProperties.GetName(navigator.DockButton) == "도킹", "The strip icon did not open the flyout");
                var bounds = flyout.TransformToAncestor(w.bodyGrid!).TransformBounds(new Rect(flyout.RenderSize));
                double dockLeft = w.compactDock!.TranslatePoint(new Point(), w.bodyGrid).X;
                Check(Math.Abs(bounds.Right - dockLeft) < 1.5 && bounds.Top >= -.5 && bounds.Bottom <= w.bodyGrid!.ActualHeight + .5 && bounds.Width >= 200 && bounds.Height >= 200, $"The flyout is not beside the strip inside the body ({bounds})");
                Check(AutomationProperties.GetItemStatus(Strip(w, "정보 패널 열기")) == "열림" && AutomationProperties.GetName(flyout) == "잠시 연 패널", "The open flyout is not marked");
                w.DockPointerPressed(navigator.TabButtons[0]); w.DockPointerPressed(w.dockStripHost);
                Check(w.dockFlyoutGroup == navigator, "A press inside the flyout or on the strip closed it");
                Check(w.DockEscape() && w.dockFlyoutGroup == null && flyout.Child == null && flyout.Visibility == Visibility.Collapsed && navigator.Collapsed && !w.DockTabVisible("info") && navigator.Body.Child == null, "Esc did not close the flyout");
                Press(Strip(w, "내비게이터 패널 열기"));
                Check(w.dockFlyoutGroup == navigator && navigator.ActiveTab.Key == "navigator", "The navigator icon did not open its tab");
                w.DockPointerPressed(w.canvas);
                Check(w.dockFlyoutGroup == null && navigator.Collapsed, "A press on the canvas did not close the flyout");
                Press(Strip(w, "정보 패널 열기")); Press(Strip(w, "히스토그램 패널 열기"));
                Check(w.dockFlyoutGroup == navigator && navigator.ActiveTab.Key == "histogram", "Another icon of the open group did not switch its tab");
                Press(Strip(w, "히스토그램 패널 열기"));
                Check(w.dockFlyoutGroup == null, "The open icon did not close its flyout");
                // A tab dragged out of the flyout lands in the stack; the flyout keeps the rest.
                Press(Strip(w, "정보 패널 열기")); Layout(w);
                w.BeginDockDrag(navigator, navigator.Tabs.Single(t => t.Key == "info"));
                Check(w.EndDockDrag(OnTab(w, layers, 1)), "The flyout tab did not drop into the stack");
                Layout(w);
                Check(Keys(layers) == "layers,info,artboards,history" && Keys(navigator) == "navigator,histogram" && w.dockFlyoutGroup == navigator && navigator.Body.Child != null, "The flyout tab did not move: " + Describe(w));
                Press(navigator.DockButton); Layout(w);
                Check(!navigator.Collapsed && w.dockStack!.Children.Contains(navigator) && w.dockFlyoutGroup == null && flyout.Visibility == Visibility.Collapsed && navigator.DockButton.Visibility == Visibility.Collapsed, "도킹 did not dock the flyout");
            }
            finally { Close(w); }
        });

        test("compact dock: a group floats in a tool window inside the work area, remembers its place and docks back", () =>
        {
            var w = Open();
            try
            {
                var color = G(w, "color");
                Choose(Menu(w, color, "떠 있는 패널로 열기")); Layout(w);
                var window = color.FloatingWindow;
                Check(color.Floating && window != null && window.Content is Border host && host.Child == color && Density.GetCompact(host) && window.WindowStyle == WindowStyle.ToolWindow && !window.ShowInTaskbar
                    && AutomationProperties.GetName(window) == "색상 · 견본 · 그라데이션 · 패턴 · 점경 떠 있는 패널", "The group did not open in a tool window");
                Check(!w.dockStack!.Children.Contains(color) && Stack(w) == "properties,adjustments | navigator,histogram,info | layers,artboards,history" && w.DockTabVisible("color")
                    && ReferenceEquals(color.Body.Child, w.dockContent["color"]) && color.DockButton.Visibility == Visibility.Visible, "The floating group still docks or hides its content");
                Check(Menu(w, color, "도킹").IsEnabled && !w.DockGroupMenu(color).Items.OfType<MenuItem>().Any(i => i.Header?.ToString() is "떠 있는 패널로 열기" or "그룹 접기"), "The floating group's menu does not offer 도킹");
                var area = SystemParameters.WorkArea;
                window!.Left = area.Right + 400; window.Top = area.Bottom + 400; window.Width = area.Width + 600;
                FitToWorkArea(window);
                Check(window.Left >= area.Left - .5 && window.Left + window.Width <= area.Right + .5 && window.Top + window.Height <= area.Bottom + .5, "The floating panel left the work area");
                window.Width = 300; window.Height = 380; window.Left = area.Left + 40; window.Top = area.Top + 50;
                var saved = w.CaptureLayout().DockGroups!.Single(g => g.Key == "color");
                Check(saved.Floating && saved.FloatBounds is { } b && Math.Abs(b.Left - (area.Left + 40)) < .6 && Math.Abs(b.Top - (area.Top + 50)) < .6 && b.Width == 300 && b.Height == 380, "The floating place was not captured");
                Press(color.DockButton); Layout(w);
                Check(!color.Floating && color.FloatingWindow == null && w.dockStack.Children.Contains(color) && Stack(w) == Defaults && color.FloatBounds is { } kept && Math.Abs(kept.Left - (area.Left + 40)) < .6, "도킹 did not dock the floating group (or forgot its place)");
                w.FloatDockGroup(color);
                Check(color.FloatingWindow is { } again && Math.Abs(again.Left - (area.Left + 40)) < .6 && Math.Abs(again.Top - (area.Top + 50)) < .6 && again.Width == 300, "Floating again did not reopen at the remembered place");
                // The friendly screen closes the window and takes its panes; 간결한 화면 reopens it.
                Choose(Menu(w, G(w, "layers"), "떠 있는 패널로 열기"));
                Check(G(w, "layers").Body.Child == w.layersPane, "The layers pane did not float with its group");
                w.SetScreenStyle(false);
                Check(color.FloatingWindow == null && color.Floating && color.Body.Child == null && ReferenceEquals(w.layersSlot.Child, w.layersPane), "Leaving 간결한 화면 did not close the floating windows and return the panes");
                w.SetScreenStyle(true); Layout(w);
                Check(color.FloatingWindow != null && G(w, "layers").FloatingWindow != null && G(w, "layers").Body.Child == w.layersPane && ReferenceEquals(color.Body.Child, w.dockContent["color"]), "간결한 화면 did not reopen the floating groups");
            }
            finally { Close(w); }
        });

        test("compact dock: the arrangement round-trips through the workspace layout and Preview 44 layouts migrate", () =>
        {
            string store = Path.Combine(directory, "workspace-dock-" + Guid.NewGuid().ToString("N") + ".json");
            string old = Path.Combine(directory, "workspace-dock-p44-" + Guid.NewGuid().ToString("N") + ".json");
            var source = Open(); MainWindow? target = null, migrated = null;
            try
            {
                var area = SystemParameters.WorkArea;
                source.MoveDockTab("history", G(source, "navigator"), 1);
                source.SplitDockTab("histogram", G(source, "layers"));
                source.MoveDockGroup(G(source, "layers"), source.ShownDockGroups.First());
                source.FloatDockGroup(G(source, "color"));
                var floating = G(source, "color").FloatingWindow!; floating.Left = area.Left + 60; floating.Top = area.Top + 70; floating.Width = 280; floating.Height = 360;
                source.SetDockGroupCollapsed(G(source, "properties"), true);
                G(source, "navigator").Select(1);
                G(source, "histogram").Weight = 2.25;
                string arrangement = Describe(source);
                Check(arrangement == "layers:layers,artboards:layers:open / color:color,swatches,gradients,patterns,entourage:color:floating / properties:properties,adjustments:properties:folded / navigator:navigator,history,info:history:open / group1:histogram:histogram:open / tools:work,brush:work:folded"
                    && Stack(source) == "layers,artboards | navigator,history,info | histogram", "Unexpected arrangement: " + arrangement);
                var saved = source.CaptureLayout();
                Check(saved.DockVersion == WorkspaceLayoutStore.DockLayoutVersion && saved.DockGroups!.Length == 6, "The dock version or groups were not captured");
                WorkspaceLayoutStore.Save(saved, store);
                var loaded = WorkspaceLayoutStore.Load(store)!;
                Check(loaded.DockVersion == 2 && loaded.DockGroups!.Single(g => g.Key == "group1").Tabs!.SequenceEqual(["histogram"]), "The arrangement did not survive the store");
                target = new MainWindow(null) { headlessTesting = true }; target.AddTab(Photo(), null);
                target.ApplyPaneLayout(loaded); Layout(target);
                Check(target.screenCompact && Describe(target) == arrangement && Stack(target) == Stack(source) && G(target, "histogram").Weight == 2.25, "The arrangement was not restored: " + Describe(target));
                Check(G(target, "color").FloatingWindow is { } restored && Math.Abs(restored.Left - (area.Left + 60)) < .6 && Math.Abs(restored.Top - (area.Top + 70)) < .6 && restored.Width == 280 && restored.Height == 360, "The floating window did not reopen in its place");
                // A Preview 44 layout: fixed groups without tabs or order.
                File.WriteAllText(old, """
                    { "Version": 1, "RightPanelWidth": 396, "ScreenStyle": "compact", "CompactDockWidth": 330,
                      "DockGroups": [ { "Key": "color", "Weight": 1.2, "Collapsed": false, "Tab": "swatches" }, { "Key": "properties", "Weight": 2.5, "Collapsed": false, "Tab": "adjustments" },
                        { "Key": "navigator", "Weight": 0.85, "Collapsed": true, "Tab": "info" }, { "Key": "layers", "Weight": 1.4, "Collapsed": false, "Tab": "history" },
                        { "Key": "tools", "Weight": 1.2, "Collapsed": true, "Tab": "brush" } ] }
                    """);
                var p44 = WorkspaceLayoutStore.Load(old)!;
                Check(p44.DockVersion == 2 && p44.DockGroups!.Select(g => g.Key + ":" + string.Join(",", g.Tabs!)).SequenceEqual(WorkspaceLayoutStore.DockGroupKeys.Select(k => k.Key + ":" + string.Join(",", k.Tabs))), "A Preview 44 layout did not migrate to the default groups");
                migrated = new MainWindow(null) { headlessTesting = true }; migrated.AddTab(Photo(), null);
                migrated.ApplyPaneLayout(p44); Layout(migrated);
                Check(Describe(migrated) == "color:color,swatches,gradients,patterns,entourage:swatches:open / properties:properties,adjustments:adjustments:open / navigator:navigator,histogram,info:info:folded / layers:layers,artboards,history:history:open / tools:work,brush:brush:folded"
                    && G(migrated, "adjustments").Weight == 2.5 && migrated.rightPanelColumn.Width.Value == 330, "The migrated layout was not applied: " + Describe(migrated));
                // Damaged arrangements: unknown keys and tabs go, a tab counts once, emptied groups go, missing tabs come home.
                var odd = WorkspaceLayoutStore.Sanitize(new WorkspaceLayout
                {
                    DockGroups = [new("group7", 1, false, "history", ["history", "nope", "layers"]), new("layers", 1, false, null, ["layers", "artboards"]), new("group0", 1, false, null, ["info"]),
                        new("color", 1, true, null, ["nope"]), new("navigator", 1, true, null, null, true, new WindowBounds(10, 10, 50, 50, false))]
                })!;
                Check(string.Join(" ", odd.DockGroups!.Select(g => g.Key + ":" + string.Join(",", g.Tabs!))) == "group7:history,layers layers:artboards navigator:navigator,histogram,info"
                    && odd.DockGroups![0].Tab == "history" && odd.DockGroups[2] is { Floating: true, Collapsed: false, FloatBounds: null }, "A damaged arrangement was not sanitized: " + string.Join(" ", odd.DockGroups!.Select(g => g.Key)));
                var complete = WorkspaceLayoutStore.CompleteDockGroups(odd.DockGroups);
                Check(complete.SelectMany(g => g.Tabs!).OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(WorkspaceLayoutStore.DockTabKeys.OrderBy(t => t, StringComparer.Ordinal))
                    && complete.Single(g => g.Key == "color").Tabs!.SequenceEqual(["color", "swatches", "gradients", "patterns", "entourage"]) && complete.Single(g => g.Key == "tools").Collapsed, "Missing tabs did not return to their groups");
                Check(!WorkspaceLayoutStore.IsDockGroupKey("group0") && !WorkspaceLayoutStore.IsDockGroupKey("group") && !WorkspaceLayoutStore.IsDockGroupKey("groupx") && WorkspaceLayoutStore.IsDockGroupKey("group12"), "Group keys are not validated");
            }
            finally
            {
                Close(source); if (target != null) Close(target); if (migrated != null) Close(migrated);
                foreach (var file in new[] { store, old }) if (File.Exists(file)) File.Delete(file);
            }
        });

        test("compact dock: 패널 배치 초기화 restores the groups, their order and tabs and docks every panel", () =>
        {
            var w = Open();
            try
            {
                string defaults = Describe(w);
                w.MoveDockTab("info", G(w, "color"), 0);
                w.SplitDockTab("swatches", null);
                w.MoveDockGroup(G(w, "layers"), w.ShownDockGroups.First());
                w.FloatDockGroup(G(w, "properties"));
                var window = G(w, "properties").FloatingWindow;
                w.SetDockGroupCollapsed(G(w, "navigator"), true); Layout(w);
                Press(Strip(w, "내비게이터 패널 열기"));
                w.rightPanelColumn.Width = new GridLength(400);
                Check(Describe(w) != defaults && w.dockFlyoutGroup != null && window != null, "The test arrangement was not made");
                Choose(Menu(w, G(w, "layers"), "패널 배치 초기화")); Layout(w);
                Check(Describe(w) == defaults && Stack(w) == Defaults && w.dockFlyoutGroup == null && w.dockGroups.All(g => g.FloatingWindow == null && g.FloatBounds == null && g.Weight == g.DefaultWeight)
                    && w.rightPanelColumn.Width.Value == DefaultDockWidth && w.dockFlyout!.Visibility == Visibility.Collapsed, "패널 배치 초기화 did not restore the dock: " + Describe(w));
                Check(ReferenceEquals(G(w, "properties").Body.Child, w.studioPanes[1]) && ReferenceEquals(G(w, "layers").Body.Child, w.layersPane), "The panes did not return to their groups");
            }
            finally { Close(w); }
        });
    }
}
