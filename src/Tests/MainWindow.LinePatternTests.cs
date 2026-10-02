using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// The pattern tab's favorites and 내 패턴 groups, adding, renaming and removing the user's line
// patterns, and the background color row of a pattern layer.
public sealed partial class MainWindow
{
    internal static void RunLinePatternTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static SegmentedChoice<MaterialPaletteTab> Tabs(Panel panel) => Descendants(panel).OfType<SegmentedChoice<MaterialPaletteTab>>().Single();
        static string[] Groups(Panel panel) => Descendants(panel).OfType<TextBlock>().Select(t => t.Text).Where(t => t is "즐겨찾기" or "기본 패턴" or "내 패턴").ToArray();
        static Button Star(Panel panel, string name) => Descendants(panel).OfType<Grid>()
            .First(g => g.Children.Count == 2 && g.Children[0] is Button { Tag: SelectionMaterialChoice } tile && AutomationProperties.GetName(tile) == name).Children[1] as Button
            ?? throw new InvalidOperationException("No star on " + name);
        static MaterialAsset Sample(string name)
        {
            var tile = new Raster(32, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) if ((x + y) % 8 < 2) tile.Data[(y * 32 + x) * 4 + 3] = 255;
            return LinePatterns.Create(name, tile);
        }
        string root = Path.Combine(directory, "line-pattern-ui"); Directory.CreateDirectory(root);
        string NewStore() => Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
        MainWindow Open(string? store)
        {
            var w = new MainWindow(null) { headlessTesting = true, linePatternStore = store };
            w.AddTab(Plan(), null);
            w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
            return w;
        }

        test("pattern palette: favorites first, built-in patterns, then 내 패턴, with stars that toggle in place", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                Check(w.LinePatternDirectory == store && w.LinePatternLibrary().Count == 0, "The test store is not used or not empty");
                var mine = w.AddLinePattern(Sample("빗살 무늬"));
                Check(File.Exists(Path.Combine(store, mine.Id.ToString("N") + ".png")) && File.Exists(Path.Combine(store, "patterns.json")), "The pattern was not stored");
                w.ToggleFavoritePattern(HatchPatternRenderer.Create(HatchPattern.Brick), "벽돌"); w.Refresh(false);
                var panel = w.selectionMaterialPanel!; Tabs(panel).Select(MaterialPaletteTab.Patterns);
                Check(Groups(panel).SequenceEqual(new[] { "즐겨찾기", "기본 패턴", "내 패턴" }), "Groups are missing or out of order: " + string.Join(", ", Groups(panel)));
                var names = Tiles(panel).Select(AutomationProperties.GetName).ToArray();
                var order = SelectionMaterials.PatternOrder(w.SelectionMaterialSuggestion().Surface).Select(HatchPatterns.Name).Where(n => n != "벽돌");
                Check(names.SequenceEqual(new[] { "벽돌" }.Concat(order).Append("빗살 무늬")), "Tiles are not favorites, built-ins, then 내 패턴: " + string.Join(", ", names));
                var custom = Tiles(panel).Last();
                Check(custom.Content is StackPanel { Children: [Border { Background: SolidColorBrush { Color: var paper }, Child: System.Windows.Shapes.Rectangle { Fill: ImageBrush { TileMode: TileMode.Tile } } }, TextBlock label] }
                    && paper == Colors.White && (bool)label.GetValue(Loc.KeepProperty), "The custom swatch is not a tiled pattern on paper with its own name kept");
                Check(Star(panel, "벽돌").Opacity == 1 && Star(panel, "빗살 무늬").Opacity == 0 && AutomationProperties.GetName(Star(panel, "빗살 무늬")) == "즐겨찾기에 추가", "Stars are not shown on favorites only");
                Click(Star(panel, "빗살 무늬"));
                Check(w.patternFavorites.SequenceEqual(new[] { "brick", LinePatterns.FavoriteKey(mine) }) && Tabs(panel).Selected == MaterialPaletteTab.Patterns, "The star did not add a favorite in place");
                names = Tiles(panel).Select(AutomationProperties.GetName).ToArray();
                Check(names.Take(2).SequenceEqual(new[] { "벽돌", "빗살 무늬" }) && names.Length == HatchPatterns.All.Count + 1 && Groups(panel).SequenceEqual(new[] { "즐겨찾기", "기본 패턴" }),
                    "The starred pattern did not move to the favorites: " + string.Join(", ", names));
                var menu = Tiles(panel)[1].ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();
                Check(menu.SequenceEqual(new[] { "즐겨찾기에서 빼기", "이름 바꾸기…", "내 패턴에서 삭제" }), "The custom tile menu is " + string.Join(", ", menu));
                Check(Tiles(panel)[0].ContextMenu!.Items.Count == 1, "A built-in tile offers library commands");
                // The custom tile fills the selection like a built-in pattern.
                int layers = w.doc.Layers.Count;
                Click(Tiles(panel)[1]);
                var layer = w.doc.Layers.Single(l => l.Kind == LayerKind.Material);
                Check(w.doc.Layers.Count == layers + 1 && layer.Name == "패턴 · 빗살 무늬" && LinePatterns.IsCustom(layer.Material!.Asset) && layer.Material.Asset.Id == mine.Id
                    && layer.Blend == BlendMode.Multiply && w.doc.Materials.Any(m => m.Id == mine.Id) && w.history.UndoLabel == "선택 영역 재질", "The custom pattern did not make one pattern layer");
                Check(Tabs(w.selectionMaterialPanel!).Selected == MaterialPaletteTab.Patterns, "A custom pattern layer did not reopen the pattern tab");
                // Favorites travel with the workspace layout.
                var layout = WorkspaceLayoutStore.Sanitize(w.CaptureLayout())!;
                Check(layout.PatternFavorites!.SequenceEqual(w.patternFavorites), "The layout does not carry the favorites");
                var other = new MainWindow(null) { headlessTesting = true };
                try { other.ApplyPaneLayout(layout); Check(other.patternFavorites.SequenceEqual(w.patternFavorites), "Favorites were not restored"); }
                finally { other.StopRenderingForShutdown(); }
                // The library survives a restart.
                var restarted = new MainWindow(null) { headlessTesting = true, linePatternStore = store };
                try
                {
                    var library = restarted.LinePatternLibrary();
                    Check(library.Count == 1 && library[0].Id == mine.Id && library[0].Name == "빗살 무늬" && library[0].Pixels.Data.AsSpan().SequenceEqual(mine.Pixels.Data), "The library did not reload");
                }
                finally { restarted.StopRenderingForShutdown(); }
            }
            finally { w.materialPaletteTab = MaterialPaletteTab.Images; w.StopRenderingForShutdown(); }
            // Without a test store, headless windows keep the library in memory.
            var memory = new MainWindow(null) { headlessTesting = true };
            try { Check(memory.LinePatternDirectory == null && memory.LinePatternLibrary().Count == 0, "A headless window reads the user's library"); }
            finally { memory.StopRenderingForShutdown(); }
        });

        test("내 패턴: renaming keeps documents valid, removing keeps their copy, and a document's pattern can be saved again", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                var mine = w.AddLinePattern(Sample("격자 스케치"));
                w.ToggleFavoritePattern(mine, mine.Name);
                w.ApplySelectionMaterial(mine, mine.Name);
                w.RenameLinePattern(mine.Id, "  새 격자  ");
                var renamed = w.LinePatternLibrary().Single();
                Check(renamed.Id == mine.Id && renamed.Name == "새 격자" && LinePatternStore.Load(store).Single().Name == "새 격자", "The rename was not stored");
                // Filling another area with the renamed entry reuses the document's own copy.
                w.selection = new Selection(new Rect(110, 30, 60, 90)); w.Refresh(false);
                var second = w.ApplySelectionMaterial(renamed, renamed.Name)!;
                w.doc.Validate();
                var customs = MaterialEditing.Assets(w.doc).Where(LinePatterns.IsCustom).ToArray();
                Check(customs.Length == 1 && customs[0].Name == "격자 스케치" && second.Name == "패턴 · 새 격자" && ReferenceEquals(second.Material!.Asset, customs[0]), "The renamed pattern conflicted with the document's copy");
                w.RemoveLinePattern(mine.Id);
                Check(w.LinePatternLibrary().Count == 0 && LinePatternStore.Load(store).Count == 0 && !w.patternFavorites.Contains(LinePatterns.FavoriteKey(mine)), "The pattern was not removed with its favorite");
                Check(w.doc.Layers.Count(l => l.Material?.Asset.Id == mine.Id) == 2 && Imaging.Render(w.doc).Width == w.doc.Width, "Removing from the library changed the document");
                // The document still offers its pattern under 내 패턴, with a command to keep it.
                var choices = w.CustomPatternChoices();
                Check(choices.Count == 1 && choices[0].Id == mine.Id, "The document's pattern is not offered");
                w.Refresh(false);
                var panel = w.selectionMaterialPanel!; Tabs(panel).Select(MaterialPaletteTab.Patterns);
                var tile = Tiles(panel).Single(t => ((SelectionMaterialChoice)t.Tag).Asset.Id == mine.Id);
                var save = tile.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "내 패턴에 저장");
                save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(w.LinePatternLibrary().Single().Id == mine.Id && LinePatternStore.Load(store).Count == 1, "The document's pattern was not saved to the library");
            }
            finally { w.materialPaletteTab = MaterialPaletteTab.Images; w.StopRenderingForShutdown(); }
        });

        test("내 패턴 dialog converts an image with the automatic threshold and refuses an image without lines", () =>
        {
            string store = NewStore(); var w = Open(store);
            try
            {
                var scan = Raster.Solid(60, 40, Color.FromRgb(0xF6, 0xF2, 0xE8));
                for (int y = 0; y < 40; y++) for (int x = 0; x < 60; x++) if (x % 12 < 2 || y % 10 == 0) { int i = (y * 60 + x) * 4; scan.Data[i] = scan.Data[i + 1] = scan.Data[i + 2] = 0x22; }
                LinePatternDialog? shown = null;
                w.linePatternDialogRunner = dialog => { shown = dialog; dialog.SetName("스캔한 타일"); return dialog.Accept(); };
                var created = w.CreateLinePattern(LinePatternSource.From(scan), "scan-01");
                Check(created is { Name: "스캔한 타일" } && LinePatterns.IsCustom(created) && created.Pixels.Width == 60 && w.LinePatternLibrary().Single().Id == created.Id, "The dialog did not create the pattern");
                Check(shown!.Error == null && shown.Converted!.Data[3] == 255 && shown.Converted.Data[(1 * 60 + 5) * 4 + 3] == 0, "The automatic threshold did not separate ink and paper");
                Check(Descendants((Panel)shown.Content).OfType<Button>().Any(b => b.Content as string == "패턴 추가") && shown.Title == "Morupixel · 내 패턴 추가", "The dialog is not a DialogShell dialog");
                w.linePatternDialogRunner = dialog => { shown = dialog; return dialog.Accept(); };
                Check(w.CreateLinePattern(LinePatternSource.From(Raster.Solid(30, 30, Colors.White)), "blank") == null && shown!.Error != null && w.LinePatternLibrary().Count == 1,
                    "A blank image became a pattern");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("pattern layer properties: background color row with 없음, one undo step, kept by swaps; images have none", () =>
        {
            var w = Open(null);
            try
            {
                var mine = w.AddLinePattern(Sample("빗살"));
                var layer = w.ApplySelectionMaterial(mine, mine.Name)!;
                w.selection = null; w.Refresh(false);
                DockPanel? Row(string caption) => w.properties.Children.OfType<DockPanel>().FirstOrDefault(d => d.Children.OfType<TextBlock>().Any(t => t.Text == caption));
                string ChipText() => Descendants(Row("바탕색")!).OfType<Button>().Select(b => b.Content).OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Single().Text;
                Check(Row("바탕색") != null && Row("잉크 색") != null && w.properties.Children.OfType<ParameterSlider>().Count() >= 4, "The custom pattern layer lacks pattern rows");
                Check(ChipText() == "없음" && !Descendants(Row("바탕색")!).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "바탕색 없음").IsEnabled, "A new pattern layer does not show 없음");
                Check(Descendants(w.properties).OfType<TextBlock>().Any(t => t.Text == "해치 패턴 레이어"), "The custom layer is not described as a hatch pattern layer");
                var before = w.doc.Layers.Single(l => l.Id == layer.Id).Pixels;
                w.SetPatternBackground(Color.FromRgb(0xFF, 0xE0, 0x70));
                var tinted = w.doc.Layers.Single(l => l.Id == layer.Id);
                Check(tinted.Material!.Background == 0xFFFFE070 && w.history.UndoLabel == "패턴 바탕색" && !ReferenceEquals(tinted.Pixels, before), "The background was not one recorded change");
                int yellow = 0; for (int i = 0; i < tinted.Pixels.Data.Length; i += 4) if (tinted.Pixels.Data[i + 3] == 255 && tinted.Pixels.Data[i + 2] == 0xFF && tinted.Pixels.Data[i] == 0x70) yellow++;
                Check(yellow > 100, "The layer pixels do not show the background");
                Check(ChipText() == "#FFE070", "The chip does not show the background color");
                // Swapping to a built-in pattern keeps it; the clear button removes it; undo restores.
                w.SwapLayerMaterial(HatchPatternRenderer.Create(HatchPattern.Brick), HatchPatterns.Name(HatchPattern.Brick));
                Check(w.doc.Active!.Material!.Background == 0xFFFFE070 && Row("바탕색") != null, "Swapping lost the background");
                Click(Descendants(Row("바탕색")!).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "바탕색 없음"));
                Check(w.doc.Active!.Material!.Background == 0 && w.history.UndoLabel == "패턴 바탕색" && ChipText() == "없음", "The clear button did not remove the background");
                w.Undo(); Check(w.doc.Active!.Material!.Background == 0xFFFFE070, "Undo did not restore the background");
                w.SetPatternBackground(Colors.Transparent); Check(w.doc.Active!.Material!.Background == 0, "A transparent pick did not mean none");
            }
            finally { w.StopRenderingForShutdown(); }
            var image = new MainWindow(null) { headlessTesting = true };
            try
            {
                image.AddTab(Plan(), null); image.selection = new Selection(new Rect(20, 20, 80, 120)); image.Refresh(false);
                image.ApplySelectionMaterial(PresetMaterial(MaterialKind.Wood), DrawingCleanup.MaterialName(MaterialKind.Wood));
                image.selection = null; image.Refresh(false);
                Check(!image.properties.Children.OfType<DockPanel>().Any(d => d.Children.OfType<TextBlock>().Any(t => t.Text == "바탕색")), "An image material shows a background row");
            }
            finally { image.StopRenderingForShutdown(); }
        });
    }
}
