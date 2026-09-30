using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunRibbonTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        Button RibbonButton(MainWindow w, string name) => Descendants(w.ribbonBody!).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

        test("ribbon mirrors the menu with titled groups, icons and working buttons", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                Check(!w.ribbonMode && w.menuHost!.Visibility == Visibility.Visible && w.ribbonBody!.Visibility == Visibility.Collapsed, "The menu bar is not the default");
                w.AddTab(NewDocumentDialog.CreateDocument("리본", "32", "32", 1), null);
                w.SetRibbonMode(true);
                Check(w.menuHost!.Visibility == Visibility.Collapsed && w.ribbonBody!.Visibility == Visibility.Visible, "Ribbon mode did not replace the menu bar");
                var tabs = w.RibbonTabNames().ToArray();
                Check(tabs[0] == FavoritesTab && tabs.Length == w.mainMenu!.Items.OfType<MenuItem>().Count() + 1, "Ribbon tabs do not follow the menu");
                var missing = DefaultFavorites.Where(id => !w.ribbonItems.ContainsKey(id)).ToArray();
                Check(missing.Length == 0, "Default favorites missing from the menu: " + string.Join(", ", missing));
                var unmapped = w.ribbonItems.Keys.Where(id => RibbonGlyph(w.ribbonItems[id].Header?.ToString() ?? "") == Theme.Glyphs.More).ToArray();
                Check(unmapped.Length == 0, "Ribbon commands without an icon: " + string.Join(", ", unmapped));
                var file = w.RibbonGroups("파일");
                Check(file.Count >= 3 && file[0].Title == "문서" && file.All(g => g.Items.Length > 0), "File groups are not titled from the menu separators");
                Check(w.RibbonGroups("레이어").Any(g => g.Title == "새 조정 레이어"), "A submenu did not become its own group");
                w.SelectRibbonTab("보기");
                bool grid = w.canvas.PixelGrid;
                RibbonButton(w, "픽셀 격자 전환").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.canvas.PixelGrid != grid, "A ribbon button did not run its menu command");
                w.SetRibbonCollapsed(true);
                Check(w.ribbonBody!.Visibility == Visibility.Collapsed && w.ribbonTabs!.Visibility == Visibility.Visible, "^ did not fold the ribbon to its tabs");
                w.SelectRibbonTab("편집");
                Check(!w.ribbonCollapsed && w.ribbonBody!.Visibility == Visibility.Visible && w.ribbonTab == "편집", "Choosing a tab did not unfold the ribbon");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("ribbon favorites can be added, reordered, removed and restored with the layout", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true }; var target = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.SetRibbonMode(true);
                w.AddFavorite("menu:보정/레벨…"); Check(w.ribbonFavorites[^1] == "menu:보정/레벨…", "A command was not added to 내 탭");
                w.MoveFavorite("menu:보정/레벨…", -1); Check(w.ribbonFavorites[^2] == "menu:보정/레벨…", "A favorite did not move forward");
                w.RemoveFavorite("menu:파일/열기…"); Check(!w.ribbonFavorites.Contains("menu:파일/열기…"), "A favorite was not removed");
                w.AddFavorite("menu:없는/명령"); Check(!w.ribbonFavorites.Contains("menu:없는/명령"), "An unknown command was accepted");
                w.SelectRibbonTab(FavoritesTab);
                Check(w.RibbonGroups(FavoritesTab).Single().Items.Length == w.ribbonFavorites.Count, "내 탭 does not show every favorite");
                w.SetRibbonCollapsed(true);
                var layout = WorkspaceLayoutStore.Sanitize(w.CaptureLayout())!;
                Check(layout.RibbonMode && layout.RibbonCollapsed && layout.RibbonFavorites!.SequenceEqual(w.ribbonFavorites), "The ribbon choice was not captured");
                target.ApplyPaneLayout(layout);
                Check(target.ribbonMode && target.ribbonCollapsed && target.ribbonFavorites.SequenceEqual(w.ribbonFavorites) && target.menuHost!.Visibility == Visibility.Collapsed, "The ribbon was not restored");
                target.ResetFavorites(); Check(target.ribbonFavorites.SequenceEqual(DefaultFavorites), "내 탭 did not reset");
            }
            finally { w.StopRenderingForShutdown(); target.StopRenderingForShutdown(); }
        });

        test("file menu keeps the print export beside the export preview in the menu and the ribbon", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var file = w.mainMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "파일"));
                var entries = file.Items.Cast<object>().ToList();
                int preview = entries.FindIndex(e => e is MenuItem m && Equals(m.Header, "내보내기 미리보기…"));
                int print = entries.FindIndex(e => e is MenuItem m && Equals(m.Header, "인쇄용 CMYK 내보내기…"));
                Check(preview >= 0 && print == preview + 1, "인쇄용 CMYK 내보내기… does not directly follow 내보내기 미리보기…");
                int save = entries.FindIndex(e => e is MenuItem m && Equals(m.Header, "저장"));
                Check(save > 0 && entries[save - 1] is Separator && entries[preview - 1] is Separator, "The Save and Export groups lost their separators");
                Check(entries[save + 1] is MenuItem saveAs && Equals(saveAs.Header, "다른 이름으로 저장…") && entries[save + 2] is Separator, "The Save group holds more than 저장 and 다른 이름으로 저장…");
                var group = w.RibbonGroups("파일").Single(g => g.Items.Any(i => Equals(i.Header, "내보내기 미리보기…")));
                Check(group.Items.Any(i => Equals(i.Header, "인쇄용 CMYK 내보내기…")), "The ribbon split the print export from the export group");
                Check(!w.RibbonGroups("파일").Single(g => g.Items.Any(i => Equals(i.Header, "저장"))).Items.Any(i => Equals(i.Header, "인쇄용 CMYK 내보내기…")), "The print export sits in the ribbon's Save group");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("ribbon favorites saved under a renamed command id move to its new id", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                const string old = "menu:파일/PDF / PSD 파일로 내보내기…";
                string replacement = $"menu:파일/{CompatibilityExportMenu}/{CompatibilityExport.Choices[0].Title}…";
                w.SetRibbonMode(true);
                Check(w.ribbonItems.ContainsKey(replacement), "The replacement export command is missing: " + replacement);
                // Same as the current layout, only with the favorites a saved workspace.json holds.
                WorkspaceLayout Saved(params string[] favorites)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(w.CaptureLayout()))!;
                    node[nameof(WorkspaceLayout.RibbonFavorites)] = new System.Text.Json.Nodes.JsonArray(favorites.Select(f => (System.Text.Json.Nodes.JsonNode?)f).ToArray());
                    return WorkspaceLayoutStore.Sanitize(System.Text.Json.JsonSerializer.Deserialize<WorkspaceLayout>(node)!)!;
                }
                w.ApplyPaneLayout(Saved("menu:파일/열기…", old, "menu:보기/화면에 맞춤"));
                Check(w.ribbonFavorites.SequenceEqual(["menu:파일/열기…", replacement, "menu:보기/화면에 맞춤"]), "The old export favorite was not mapped: " + string.Join(", ", w.ribbonFavorites));
                Check(w.RibbonGroups(FavoritesTab).Single().Items.Length == 3, "내 탭 dropped the renamed export favorite");
                Check(!w.CaptureLayout().RibbonFavorites!.Contains(old), "The stale id is written back to the layout");
                w.ApplyPaneLayout(Saved(replacement, old));
                Check(w.ribbonFavorites.SequenceEqual([replacement]), "Mapping an old id duplicated a favorite that was already there");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("windows and dialogs are fitted inside the screen work area", () =>
        {
            var area = SystemParameters.WorkArea;
            var window = new Window { Width = area.Width * 3, Height = area.Height * 3, MinWidth = area.Width * 2, MinHeight = area.Height * 2, Left = area.Right + 500, Top = area.Bottom + 500 };
            FitToWorkArea(window);
            Check(window.Width <= area.Width && window.Height <= area.Height && window.MinWidth <= area.Width && window.MinHeight <= area.Height, "Size exceeds the work area");
            Check(window.Left >= area.Left && window.Left + window.Width <= area.Right + .5 && window.Top + window.Height <= area.Bottom + .5, "Position is outside the work area");
            var main = new MainWindow(null) { headlessTesting = true };
            try { Check(main.MinWidth <= area.Width && main.Width <= area.Width && main.Height <= area.Height, "The main window does not fit the screen"); }
            finally { main.StopRenderingForShutdown(); }
        });
    }
}
