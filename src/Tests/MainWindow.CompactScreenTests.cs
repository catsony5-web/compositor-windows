using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// 간결한 화면: the live switch and its way back, the dock (groups, splitters, folding, menu,
// persistence) and the panels only the dock has (내비게이터, 히스토그램, 정보, 기록).
public sealed partial class MainWindow
{
    internal static void RunCompactScreenTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<DependencyObject>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop(); if (!seen.Add(node)) continue; yield return node;
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) stack.Push(child);
                if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
        static Document Photo(int width = 400, int height = 300)
        {
            var document = new Document { Width = width, Height = height, Name = "간결" };
            document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(width, height, Colors.SteelBlue) });
            return document;
        }
        static void Layout(MainWindow w, double width = 1480, double height = 920)
        {
            var content = (FrameworkElement)w.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        MainWindow Open(Document? document = null)
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.AddTab(document ?? Photo(), null);
            return w;
        }
        void Close(MainWindow w) { try { w.SetScreenStyle(false); } finally { w.StopRenderingForShutdown(); } }

        test("compact screen: switches live and gives the friendly screen back unchanged", () =>
        {
            var w = Open();
            try
            {
                Layout(w);
                var root = w.rootGrid!;
                double title = root.RowDefinitions[0].Height.Value, status = root.RowDefinitions[4].Height.Value, rail = w.toolRailColumn.Width.Value, right = w.rightPanelColumn.Width.Value;
                Check(!w.screenCompact && !Theme.Compact && !Density.GetCompact(w) && title == 44 && status == 30 && rail == ToolRailWidth, "The friendly screen is not the default");
                Check(ReferenceEquals(w.layersSlot.Child, w.layersPane) && ReferenceEquals(w.studioScroll.Content, w.studioPanes[w.studioPage]), "The friendly cards do not hold their panes");
                w.SetScreenStyle(true); Layout(w);
                Check(w.screenCompact && Theme.Compact && Density.GetCompact(w) && root.RowDefinitions[0].Height.Value == CompactTitleHeight && root.RowDefinitions[4].Height.Value == CompactStatusHeight, "The compact chrome was not applied");
                Check(w.compactDock!.Visibility == Visibility.Visible && w.rightPanelHost!.Visibility == Visibility.Collapsed && w.optionRow!.Height.Value == CompactBarHeight, "The dock did not replace the friendly cards");
                Check(w.dockGroups.Select(g => (g.Key, string.Join(",", g.Tabs.Select(t => t.Key)))).SequenceEqual(WorkspaceLayoutStore.DockGroupKeys.Select(k => (k.Key, string.Join(",", k.Tabs)))), "The dock groups or tabs differ from the layout keys");
                Check(w.DockTabVisible("color") && w.DockTabVisible("properties") && w.DockTabVisible("navigator") && w.DockTabVisible("layers") && w.dockGroups.Single(g => g.Key == "tools").Collapsed, "The default groups are not open");
                Check(w.dockGroups.Single(g => g.Key == "layers").Body.Child == w.layersPane && w.dockGroups.Single(g => g.Key == "properties").Body.Child == w.studioPanes[1], "The friendly panes were not moved into the dock");
                Check(w.toolRailColumn.Width.Value <= 44 && w.toolButtons.Values.All(b => b.Width == b.Height && b.Height <= 32), "The tool rail is not one column of icon buttons");
                var text = Descendants(w.properties).OfType<TextBlock>().First(t => t.Text == "혼합 모드");
                Check(text.FontSize == 11, $"Property captions did not step down ({text.FontSize})");
                var compactRows = Descendants(w.layerList).OfType<LayerRow>().ToArray();
                Check(compactRows.Length > 0 && compactRows.All(r => r.Compact && r.MinHeight == 26), "Layer rows did not become compact");
                Check(w.dockGroups.All(g => g.TabButtons.All(b => AutomationProperties.GetName(b)?.EndsWith(" 패널") == true) && AutomationProperties.GetName(g.MenuButton)?.Length > 0), "Dock tabs or menus lack accessible names");
                w.SetScreenStyle(false); Layout(w);
                Check(!w.screenCompact && !Theme.Compact && !Density.GetCompact(w) && root.RowDefinitions[0].Height.Value == title && root.RowDefinitions[4].Height.Value == status, "The friendly chrome did not come back");
                Check(w.toolRailColumn.Width.Value == rail && w.rightPanelColumn.Width.Value == right && w.rightPanelHost!.Visibility == Visibility.Visible && w.compactDock!.Visibility == Visibility.Collapsed, "The friendly columns did not come back");
                Check(ReferenceEquals(w.layersSlot.Child, w.layersPane) && ReferenceEquals(w.studioScroll.Content, w.studioPanes[w.studioPage]) && w.layersPane!.Parent != null, "The panes did not return to the friendly cards");
                Check(Descendants(w.properties).OfType<TextBlock>().First(t => t.Text == "혼합 모드").FontSize == Theme.CaptionSize, "A property caption kept the compact size");
                var friendlyRows = Descendants(w.layerList).OfType<LayerRow>().ToArray();
                Check(friendlyRows.Length > 0 && friendlyRows.All(r => !r.Compact && r.MinHeight == 40), "Layer rows kept the compact density");
                Check(w.Background == Theme.Panel && w.rootGrid!.Background == Theme.Header, "Friendly brushes were not repainted back");
            }
            finally { Close(w); }
        });

        test("compact screen: density steps a control down in a compact tree and restores it outside", () =>
        {
            var host = new Border(); Density.SetCompact(host, true);
            var button = new Button { MinHeight = 30, Padding = new Thickness(10, 5, 10, 5), FontSize = Theme.BodySize };
            var label = new TextBlock { Text = "설명", FontSize = Theme.CaptionSize };
            var description = Density.Mark(new TextBlock { Text = "긴 설명" }, DensityRole.Description);
            var keep = Density.Mark(new Button { MinHeight = 30 }, DensityRole.Keep);
            var panel = new StackPanel(); panel.Children.Add(button); panel.Children.Add(label); panel.Children.Add(description); panel.Children.Add(keep);
            host.Child = panel;
            Check(button.MinHeight == 24 && button.Padding == new Thickness(8, 3, 8, 3) && button.FontSize == 12 && label.FontSize == 11, "Controls in a compact tree did not step down");
            Check(description.Visibility == Visibility.Collapsed && keep.MinHeight == 30, "A description stayed or a kept control changed");
            host.Child = null;
            Check(button.MinHeight == 30 && button.Padding == new Thickness(10, 5, 10, 5) && button.FontSize == Theme.BodySize && label.FontSize == Theme.CaptionSize && description.Visibility == Visibility.Visible, "Leaving the compact tree did not restore the friendly values");
            var unset = new Button();
            host.Child = unset; host.Child = null;
            Check(unset.ReadLocalValue(FrameworkElement.MinHeightProperty) == DependencyProperty.UnsetValue, "A value the control never set was left behind");
        });

        test("compact screen: screen style and dock layout round-trip through the workspace layout", () =>
        {
            string store = Path.Combine(directory, "workspace-compact-" + Guid.NewGuid().ToString("N") + ".json");
            var source = Open(); MainWindow? target = null;
            try
            {
                source.SetScreenStyle(true); Layout(source);
                source.rightPanelColumn.Width = new GridLength(332); Layout(source);
                source.SetDockGroupCollapsed(source.dockGroups.Single(g => g.Key == "navigator"), true);
                source.ShowDockTab("history"); source.ShowDockTab("swatches");
                var properties = source.dockGroups.Single(g => g.Key == "properties"); properties.Weight = 2.5;
                var saved = source.CaptureLayout();
                Check(saved.ScreenStyle == WorkspaceLayoutStore.CompactStyle && saved.CompactDockWidth == 332 && saved.RightPanelWidth == 396, "The screen style or widths were not captured");
                WorkspaceLayoutStore.Save(saved, store);
                var loaded = WorkspaceLayoutStore.Load(store)!;
                Check(loaded.DockGroups!.Single(g => g.Key == "navigator").Collapsed && loaded.DockGroups!.Single(g => g.Key == "layers").Tab == "history"
                    && loaded.DockGroups!.Single(g => g.Key == "color").Tab == "swatches" && loaded.DockGroups!.Single(g => g.Key == "properties").Weight == 2.5, "Dock groups did not survive the store");
                target = Open();
                target.ApplyPaneLayout(loaded); Layout(target);
                Check(target.screenCompact && target.rightPanelColumn.Width.Value == 332, "The compact screen or dock width was not restored");
                Check(target.dockGroups.Single(g => g.Key == "navigator").Collapsed && target.DockTabVisible("history") && target.DockTabVisible("swatches") && target.dockGroups.Single(g => g.Key == "properties").Weight == 2.5, "Group folds, tabs or sizes were not restored");
                target.SetScreenStyle(false);
                Check(target.rightPanelColumn.Width.Value == 396 && target.CaptureLayout().ScreenStyle == null, "Leaving the compact screen did not bring back the friendly width");
                var odd = WorkspaceLayoutStore.Sanitize(new WorkspaceLayout { ScreenStyle = "tiny", CompactDockWidth = double.NaN, DockGroups = [new("nope", 1, false), new("color", 999, true, "missing"), new("color", 1, false)] })!;
                Check(odd.ScreenStyle == null && odd.CompactDockWidth == null && odd.DockGroups!.Length == 1 && odd.DockGroups[0].Weight == WorkspaceLayoutStore.MaxDockWeight && odd.DockGroups[0].Tab == null, "Unknown dock values were not sanitized");
            }
            finally { Close(source); if (target != null) Close(target); if (File.Exists(store)) File.Delete(store); }
        });

        test("compact screen: dock splitters move height between groups and folded groups go to the icon strip", () =>
        {
            var w = Open();
            try
            {
                w.SetScreenStyle(true); Layout(w);
                var shown = w.ShownDockGroups.ToArray();
                double Height(DockGroup g) => g.ActualHeight;
                double upper = Height(shown[0]), lower = Height(shown[1]), others = shown.Skip(2).Sum(Height);
                w.ResizeDockGroups(0, 40); Layout(w);
                Check(Math.Abs(Height(shown[0]) - upper - 40) < 1.5 && Math.Abs(lower - Height(shown[1]) - 40) < 1.5 && Math.Abs(shown.Skip(2).Sum(Height) - others) < 1.5, "The splitter did not move height between its two groups only");
                w.ResizeDockGroups(0, -5000); Layout(w);
                Check(Height(shown[0]) >= DockGroupMinHeight - .5, "A group was squeezed below its minimum");
                var navigator = w.dockGroups.Single(g => g.Key == "navigator");
                var menu = w.DockGroupMenu(navigator).Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray();
                Check(menu.Contains("그룹 접기") && menu.Contains("패널 배치 초기화"), "The group menu lacks fold or reset: " + string.Join(", ", menu));
                w.SetDockGroupCollapsed(navigator, true); Layout(w);
                var strip = w.dockStrip!.Children.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
                Check(!w.dockStack!.Children.Contains(navigator) && w.dockStripHost!.Visibility == Visibility.Visible && strip.Contains("내비게이터 패널 열기") && strip.Contains("정보 패널 열기"), "The folded group is not in the icon strip");
                // The strip icon opens its tab (as a flyout beside the strip since Preview 45); 📌 도킹 unfolds the group.
                w.dockStrip.Children.OfType<Button>().First(b => AutomationProperties.GetName(b) == "정보 패널 열기").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.DockTabVisible("info") && w.dockFlyoutGroup == navigator && navigator.Collapsed && !w.dockStack.Children.Contains(navigator), "The strip icon did not open its tab");
                navigator.DockButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!navigator.Collapsed && w.DockTabVisible("info") && w.dockStack.Children.Contains(navigator) && w.dockFlyoutGroup == null, "도킹 did not put the group back into the stack");
                w.ShowStudioPage(3);
                Check(w.DockTabVisible("brush") && w.dockGroups.Single(g => g.Key == "tools").Body.Child == w.studioPanes[3], "Opening the brush page did not unfold its dock group");
                w.ResetDockLayout(); Layout(w);
                Check(w.dockGroups.All(g => g.Weight == g.DefaultWeight && g.Collapsed == g.DefaultCollapsed && g.ActiveIndex == 0), "패널 배치 초기화 did not restore the groups");
                var registry = w.BuildCommandRegistry();
                Check(registry.Any(c => c.Id == "panel:dock-history") && registry.Any(c => c.Id == "panel:dock-navigator"), "The dock tabs are not in the command palette");
                w.RunCommand(registry.Single(c => c.Id == "panel:dock-history"));
                Check(w.DockTabVisible("history"), "The palette did not open the history tab");
            }
            finally { Close(w); }
        });

        test("compact screen: navigator press centers the canvas on that document point", () =>
        {
            var w = Open(Photo(1000, 500));
            try
            {
                w.SetScreenStyle(true); w.ShowDockTab("navigator"); Layout(w);
                w.SetZoom(2);
                var view = w.navigator!;
                Check(view.ActualWidth > 0 && view.Viewport.Width > 0, "The navigator has no viewport");
                var image = view.ImageRect(view.RenderSize);
                view.NavigateAt(new Point(image.X + image.Width * .25, image.Y + image.Height * .5)); Layout(w);
                var center = w.canvas.ToDocument(new Point(w.canvas.ActualWidth / 2, w.canvas.ActualHeight / 2));
                Check(Math.Abs(center.X - 250) < 2 && Math.Abs(center.Y - 250) < 2, $"The canvas was not centered on the pressed point ({center.X:0.#}, {center.Y:0.#})");
                var visible = w.canvas.VisibleDocumentRect;
                Check(Math.Abs(view.Viewport.X - visible.X) < 1 && Math.Abs(view.Viewport.Width - visible.Width) < 1, "The navigator rectangle does not follow the view");
                w.navigatorZoom!.Value = Math.Log2(4);
                Check(Math.Abs(w.canvas.Zoom - 4) < .01, "The navigator zoom slider did not zoom the canvas");
            }
            finally { Close(w); }
        });

        test("compact screen: histogram panel counts luminance and RGB of a known image", () =>
        {
            var raster = new Raster(4, 1);
            void Put(int x, byte r, byte g, byte b, byte a) { int i = x * 4; raster.Data[i] = b; raster.Data[i + 1] = g; raster.Data[i + 2] = r; raster.Data[i + 3] = a; }
            Put(0, 0, 0, 0, 255); Put(1, 255, 255, 255, 255); Put(2, 255, 0, 0, 255); Put(3, 0, 255, 0, 0);
            var bins = HistogramView.Compute(raster);
            int red = HistogramView.Luminance(255, 0, 0);
            Check(bins.Length == 4 && bins[3][0] == 1 && bins[3][255] == 1 && bins[3][red] == 1 && bins[3].Sum() == 3 && red == 54, "Luminance bins are wrong");
            Check(bins[0][255] == 2 && bins[0][0] == 1 && bins[1][255] == 1 && bins[1][0] == 2, "RGB bins are wrong (or the transparent pixel counted)");
            var (mean, weight) = HistogramView.LuminanceStats(bins);
            Check(weight == 3 && Math.Abs(mean - (0 + 255 + 54) / 3.0) < 1e-9, "Luminance statistics are wrong");
            var document = new Document { Width = 4, Height = 1 }; document.Add(new Layer { Pixels = raster });
            var w = Open(document);
            try
            {
                w.SetScreenStyle(true); w.ShowDockTab("histogram");
                w.composite = Imaging.Render(w.doc); w.histogram.Update(w.composite); w.UpdateDockAfterRender();
                Check(ReferenceEquals(w.dockHistogram!.Bins, w.histogram.Bins) && w.dockHistogram.ShowLuminance && w.dockHistogramStats!.Text == "명도 평균 103 · 중간값 54", "The histogram panel did not follow the render: " + w.dockHistogramStats!.Text);
            }
            finally { Close(w); }
        });

        test("compact screen: info panel reads the pointer position, its color and the selection size", () =>
        {
            var document = Photo(200, 100);
            var dot = new Raster(10, 10); for (int i = 0; i < dot.Data.Length; i += 4) { dot.Data[i + 2] = 255; dot.Data[i + 3] = 255; }
            document.Add(new Layer { Name = "점", Pixels = dot, X = 20, Y = 30 });
            var w = Open(document);
            try
            {
                w.SetScreenStyle(true); w.ShowDockTab("info");
                w.composite = Imaging.Render(w.doc);
                w.UpdateDockInfo(new Point(25.4, 34.9));
                Check(w.infoPosition!.Text == "X 25 · Y 34 px" && w.infoColor!.Text.StartsWith("#FF0000 · R 255 G 0 B 0", StringComparison.Ordinal), $"Pointer readout: {w.infoPosition.Text} / {w.infoColor!.Text}");
                Check(w.infoDocument!.Text == "200 × 100 px · 96 DPI" && w.infoSelection!.Text == "없음", $"Document or selection readout: {w.infoDocument.Text} / {w.infoSelection!.Text}");
                w.UpdateDockInfo(new Point(-4, 5));
                Check(w.infoColor.Text == "문서 밖", "A pointer outside the document read a color");
                w.selection = new Selection(new Rect(10, 10, 40, 20)); w.Refresh(false);
                Check(w.infoSelection.Text == "40 × 20 px · X 10 Y 10", "Selection size: " + w.infoSelection.Text);
            }
            finally { Close(w); }
        });

        test("compact screen: history panel jumps back and forward to the chosen step", () =>
        {
            var w = Open();
            try
            {
                w.SetScreenStyle(true); w.ShowDockTab("history");
                w.EditLayer("첫째", l => l.X = 10); w.EditLayer("둘째", l => l.X = 20); w.EditLayer("셋째", l => l.X = 30);
                Button Row(int i) => w.dockHistoryList!.Children.OfType<Button>().ElementAt(i);
                Check(w.dockHistoryList!.Children.OfType<Button>().Select(AutomationProperties.GetName).SequenceEqual(["기록 0: 시작 상태", "기록 1: 첫째", "기록 2: 둘째", "기록 3: 셋째"]), "The history rows do not list the steps");
                Row(1).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.doc.Layers[0].X == 10 && w.history.UndoLabels.Count == 1 && w.history.RedoLabels.SequenceEqual(["둘째", "셋째"]), "Clicking a step did not undo to it");
                Check(AutomationProperties.GetItemStatus(Row(1)) == "지금 상태" && AutomationProperties.GetItemStatus(Row(3)) == "다시 실행할 단계", "The rows do not mark the current and redo steps");
                Row(3).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.doc.Layers[0].X == 30 && !w.history.CanRedo, "Clicking a later step did not redo to it");
                Row(0).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.doc.Layers[0].X == 0 && w.history.RedoLabels.Count == 3, "Clicking 시작 상태 did not undo everything");
            }
            finally { Close(w); }
        });

        test("compact screen: the screen style is reachable from the View menu, the palette and the start screen", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var registry = w.BuildCommandRegistry();
                var compact = registry.Single(c => c.Id == "menu:보기/화면 스타일/간결한 화면");
                Check(registry.Any(c => c.Id == "menu:보기/화면 스타일/친절한 화면") && compact.IsAvailable(), "The View menu lacks the screen styles");
                w.RunCommand(compact);
                Check(w.screenCompact && w.screenStyleItems[true].IsChecked && !w.screenStyleItems[false].IsChecked && w.startScreenChoice!.Selected, "The palette command did not switch the screen");
                Check(w.compactDock == null || w.compactDock.Visibility == Visibility.Collapsed, "The dock showed on the start screen");
                Check(Descendants(w.emptyWorkspace!).Contains(w.startScreenChoice) && w.startScreenSummary!.Text == ScreenStyleSummary(true), "The start screen does not offer the screen style");
                w.startScreenChoice!.Buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!w.screenCompact && !Theme.Compact, "The start screen choice did not switch back");
                Check(RibbonGlyph("간결한 화면") == Theme.Glyphs.ScreenCompact && RibbonGlyph("친절한 화면") == Theme.Glyphs.ScreenFriendly, "The screen styles lack ribbon icons");
            }
            finally { Close(w); }
        });

        test("compact screen: dock tab rows wrap long names instead of clipping them", () =>
        {
            var tabs = new[] { "Farbfelder und Verläufe", "Muster und Raster", "Eigenschaften" }.Select((t, i) => new DockTab("t" + i, t, Theme.Glyphs.Palette, t, () => new Border())).ToArray();
            var group = new DockGroup("test", tabs, 1, false, _ => { }, _ => { }) { Hosted = true };
            group.Select(0);
            group.Measure(new Size(210, 400)); group.Arrange(new Rect(0, 0, 210, 400)); group.UpdateLayout();
            Check(group.TabButtons.All(b => b.ActualWidth >= b.DesiredSize.Width - .5 && b.ActualWidth > 0) && group.TabButtons.Max(b => b.TranslatePoint(new Point(), group).Y) > 0, "Long tab names did not wrap to a second row");
        });
    }

}
