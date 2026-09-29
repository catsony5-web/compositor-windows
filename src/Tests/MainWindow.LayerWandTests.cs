using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Compositor.Windows;

// Shift+click range visibility in the layer list and material swatches for a selection.
public sealed partial class MainWindow
{
    internal static void RunLayerWandTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "layer-wand"); Directory.CreateDirectory(root);
        static IReadOnlyList<LayerListEntry> Rows(MainWindow w) => (IReadOnlyList<LayerListEntry>)w.layerList.ItemsSource;

        test("layer eye Shift+click copies the reference row's visibility to the range in one undo step", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8, Name = "눈 범위" };
                for (int i = 0; i < 5; i++) document.Add(new Layer { Name = "레이어 " + i, Pixels = Raster.Solid(8, 8, Colors.Red) });
                w.AddTab(document, null);
                var rows = Rows(w); Check(rows.Count == 5, "The layer list did not show five rows");
                Guid Id(int row) => Rows(w)[row].Layer.Id;
                bool Shown(int row) => w.doc.Layers.Single(l => l.Id == Id(row)).Visible;
                w.doc.Layers.Single(l => l.Id == Id(2)).Locked = true;
                var selected = w.selectedLayers.ToHashSet(); var active = w.doc.ActiveId;
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.None);
                Check(!Shown(0) && Shown(1), "A plain eye click did not hide only its row");
                w.ClickLayerEye(Rows(w)[3], ModifierKeys.Shift);
                Check(!Shown(0) && !Shown(1) && !Shown(2) && !Shown(3) && Shown(4), "Shift+click did not hide the rows between the reference and the clicked row");
                Check(w.history.UndoLabel == "레이어 범위 숨기기", "The range was not one named history step: " + w.history.UndoLabel);
                Check(w.selectedLayers.SetEquals(selected) && w.doc.ActiveId == active, "Shift+click on an eye changed the row selection");
                w.Undo();
                Check(!Shown(0) && Shown(1) && Shown(2) && Shown(3), "One undo did not restore the whole range");
                w.Redo(); Check(!Shown(3), "Redo did not repeat the range");

                // The reference stays: a later Shift+click reaches up from it again, now showing rows.
                w.ClickLayerEye(Rows(w)[1], ModifierKeys.None); Check(Shown(1), "A plain click did not show the row");
                w.ClickLayerEye(Rows(w)[4], ModifierKeys.Shift);
                Check(Shown(1) && Shown(2) && Shown(3) && Shown(4) && !Shown(0), "Shift+click did not apply the new reference's shown state below it");
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.Shift);
                Check(Shown(0) && Shown(1), "Shift+click above the reference did not apply its state upward");

                // Without a reference in the list the topmost row decides.
                w.visibilityAnchor = Guid.Empty;
                w.Edit("준비", () => w.doc.Layers.Single(l => l.Id == Id(0)).Visible = false);
                w.ClickLayerEye(Rows(w)[2], ModifierKeys.Shift);
                Check(!Shown(0) && !Shown(1) && !Shown(2) && Shown(3), "Without a reference the topmost row did not decide");
                // Shift on the reference row itself is an ordinary toggle.
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.Shift);
                Check(Shown(0) && !Shown(1), "Shift+click on the reference row did not toggle it");

                // Alt keeps priority over Shift and still isolates.
                w.suppressAltMenu = false;
                w.ClickLayerEye(Rows(w)[3], ModifierKeys.Alt | ModifierKeys.Shift);
                Check(Shown(3) && !Shown(0) && !Shown(1) && !Shown(2) && !Shown(4) && w.suppressAltMenu, "Alt+Shift+click did not isolate the row");
                Check(w.history.UndoLabel == "레이어 단독 표시", "Alt isolation did not keep its step");
                var eye = FindEye(w, Rows(w)[3]);
                Check(eye?.ToolTip is string tip && tip.Contains("Shift+클릭") && tip.Contains("Alt+클릭"), "The eye tooltip does not explain Shift and Alt");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("layer eye Shift range covers every run of grouped drawing layers", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8, Name = "도면 눈" };
                Layer Group(string source)
                {
                    var group = new Layer { Name = source, SourceLayerName = source, Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = new Raster(8, 8) };
                    document.Layers.Add(group);
                    document.Layers.Add(new Layer { Name = source + " 객체", ParentId = group.Id, Category = LayerCategory.Drawing, Pixels = Raster.Solid(8, 8, Colors.Black) });
                    return group;
                }
                var wallLow = Group("A-WALL"); var furniture = Group("A-FURN"); var door = Group("A-DOOR"); var wallHigh = Group("A-WALL"); var notes = Group("A-ANNO");
                document.ActiveId = notes.Id; document.Validate();
                w.AddTab(document, null);
                var rows = Rows(w);
                Check(rows.Select(r => r.Layer.Name).SequenceEqual(["A-ANNO", "A-WALL", "A-DOOR", "A-FURN"]) && rows[1].GroupMembers?.Length == 2,
                    "Drawing rows were not grouped by source layer: " + string.Join(", ", rows.Select(r => r.Layer.Name)));
                bool Shown(Layer layer) => w.doc.Layers.Single(l => l.Id == layer.Id).Visible;
                w.ClickLayerEye(Rows(w)[1], ModifierKeys.None);
                Check(!Shown(wallLow) && !Shown(wallHigh) && Shown(door), "A plain click did not hide both runs of the source layer");
                w.ClickLayerEye(Rows(w)[3], ModifierKeys.Shift);
                Check(!Shown(door) && !Shown(furniture) && Shown(notes) && !Shown(wallLow) && !Shown(wallHigh), "Shift+click did not hide the grouped range");
                w.Undo();
                Check(Shown(door) && Shown(furniture) && !Shown(wallLow) && !Shown(wallHigh), "The grouped range was not one undo step");
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.Alt);
                Check(Shown(notes) && !Shown(door) && !Shown(furniture), "Alt+click no longer isolates a drawing group");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("W selects the magic wand and Ctrl+W does not", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8, Name = "단축키" }; document.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.White) });
                w.AddTab(document, null); w.SetTool(Tool.Move);
                Check(w.ExecuteEditorShortcut(Key.W, ModifierKeys.None) && w.tool == Tool.MagicWand, "W did not select the magic wand");
                w.SetTool(Tool.Move);
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                w.ExecuteEditorShortcut(Key.W, ModifierKeys.Control);
                Check(w.tool != Tool.MagicWand, "Ctrl+W selected the wand");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("selection material recommendations follow wall, floor and ground layers", () =>
        {
            Check(SelectionMaterials.Recommend("A-WALL")[0] == MaterialKind.Concrete && SelectionMaterials.Recommend("A-WALL")[1] == MaterialKind.Brick, "Walls do not lead with concrete and brick");
            Check(SelectionMaterials.Recommend("A-FLOR-PATT")[0] == MaterialKind.Wood && SelectionMaterials.Recommend("바닥 마감")[0] == MaterialKind.Wood, "Floors do not lead with wood");
            Check(SelectionMaterials.Recommend("A-FLOR-TILE")[0] == MaterialKind.Tile, "A material named by the layer is not first");
            Check(SelectionMaterials.Recommend("A-WALL-BRICK")[0] == MaterialKind.Brick, "A wall layer naming brick did not lead with brick");
            Check(SelectionMaterials.Recommend("L-SITE")[0] == MaterialKind.Gravel, "Ground layers do not lead with gravel");
            Check(SelectionMaterials.Surface("A-WALL-PATT") == SurfaceHint.Wall && SelectionMaterials.Surface("A-HATCH") == SurfaceHint.General && SelectionMaterials.Surface(null) == SurfaceHint.General, "Surface names were misread");
            foreach (var name in new[] { "A-WALL", "A-FLOR-PATT", null, "L-SITE" })
            {
                var order = SelectionMaterials.Recommend(name);
                Check(order.Length == SelectionMaterials.Presets.Length && order.ToHashSet().SetEquals(SelectionMaterials.Presets), "An order dropped or repeated a swatch");
            }

            var plan = Plan();
            var wallBand = SelectionTools.FromMask(Ring(plan.Width, plan.Height), plan.Width, plan.Height);
            var wall = SelectionMaterials.Suggest(plan, wallBand);
            Check(wall.Surface == SurfaceHint.Wall && wall.LayerName == "A-WALL" && wall.Order[0] == MaterialKind.Concrete, $"A wall band was not recommended wall materials: {wall.Surface} {wall.LayerName}");
            var hatched = SelectionMaterials.Suggest(plan, new Selection(new Rect(20, 20, 80, 120)));
            Check(hatched.Surface == SurfaceHint.Floor && hatched.LayerName == "A-FLOR-PATT" && hatched.Order[0] == MaterialKind.Wood, $"A floor hatch room was not recommended floor materials: {hatched.Surface} {hatched.LayerName}");
            var room = SelectionMaterials.Suggest(plan, new Selection(new Rect(100, 20, 80, 120)));
            Check(room.Surface == SurfaceHint.Floor && room.Order[0] == MaterialKind.Wood, "A room between walls was not treated as a floor");
            var bounded = SelectionMaterials.Suggest(plan, new Selection(new Rect(12, 12, 176, 136)));
            Check(bounded.Surface == SurfaceHint.Floor && bounded.LayerName == null && bounded.Order[0] == MaterialKind.Wood, $"A wide area inside the wall ring was not a floor: {bounded.Surface} {bounded.LayerName}");
            var photo = new Document { Width = 40, Height = 40 }; photo.Add(new Layer { Pixels = Raster.Solid(40, 40, Colors.White) });
            var general = SelectionMaterials.Suggest(photo, new Selection(new Rect(4, 4, 10, 10)));
            Check(general.Surface == SurfaceHint.General && general.LayerName == null && general.Order.SequenceEqual(SelectionMaterials.Order(SurfaceHint.General)), "A photo did not use the default order");
            var place = SelectionMaterials.Placement(plan);
            Check(place is { Parent: null } spot && spot.Index == 1, "Material layers are not placed above the paper and below the linework");
            Check(SelectionMaterials.Placement(photo) == null, "A photo placed the material below its layers");
        });

        test("material swatches appear in properties only while a selection exists", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Plan(), null);
                w.selection = null; w.Refresh(false);
                Check(w.selectionMaterialPanel == null && !w.properties.Children.OfType<StackPanel>().Any(p => Header(p) == "선택 영역 재질"), "Swatches showed without a selection");
                w.selection = new Selection(new Rect(20, 20, 80, 120)); w.Refresh(false);
                var panel = w.selectionMaterialPanel;
                Check(panel != null && w.properties.Children.Contains(panel) && Header(panel) == "선택 영역 재질", "The selection section is missing from properties");
                var names = Tiles(panel!).Select(AutomationProperties.GetName).ToArray();
                Check(names.Length == SelectionMaterials.Presets.Length && names[0] == DrawingCleanup.MaterialName(MaterialKind.Wood), "Swatches are missing or not in floor order: " + string.Join(", ", names));
                Check(Tiles(panel!).All(t => t.Content is StackPanel { Children.Count: 2 } c && c.Children[0] is Border { Background: ImageBrush { ImageSource: not null } } && double.IsNaN(t.Width)), "A swatch lacks its image or uses a fixed width");
                Check(panel!.Children.OfType<Button>().Any(b => AutomationProperties.GetName(b) == "이미지로 재질 추가…"), "The own-image option is missing");
                w.doc.ActiveId = Guid.Empty; w.selectedLayers.Clear(); w.Refresh(false);
                Check(w.selectionMaterialPanel != null, "Swatches disappeared when no layer was active");
                w.selection = null; w.Refresh(false);
                Check(w.selectionMaterialPanel == null, "Swatches stayed after the selection was cleared");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("a swatch turns the selection into one undoable material layer below the linework", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Plan(), null);
                int layers = w.doc.Layers.Count; var bounds = new Rect(20, 20, 80, 120);
                w.selection = new Selection(bounds); w.Refresh(false);
                var wood = Tiles(w.selectionMaterialPanel!).First(t => AutomationProperties.GetName(t) == DrawingCleanup.MaterialName(MaterialKind.Wood));
                wood.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var created = w.doc.Layers.Where(l => l.Kind == LayerKind.Material).ToArray();
                Check(created.Length == 1 && w.doc.Layers.Count == layers + 1, "One swatch click did not create exactly one material layer");
                var layer = created[0]; var fill = layer.Material!;
                var outline = fill.Boundary.Geometry.Bounds; outline.Offset(layer.X, layer.Y);
                Check(Math.Abs(outline.X - bounds.X) < .01 && Math.Abs(outline.Y - bounds.Y) < .01 && Math.Abs(outline.Width - bounds.Width) < .01 && Math.Abs(outline.Height - bounds.Height) < .01,
                    "The material boundary is not the selection's boundary: " + outline);
                Check(w.doc.MaterialRegions.Single().Source == "selection" && fill.SourceRegionId == w.doc.MaterialRegions[0].Id && fill.Asset.Id == MaterialPresets.Create(MaterialKind.Wood).Id, "The region or material was not registered like define_region/apply_material");
                int index = w.doc.Layers.IndexOf(layer), paper = w.doc.Layers.FindIndex(l => l.Name == "도면 배경"), lines = w.doc.Layers.FindIndex(l => l.Kind == LayerKind.Vector);
                Check(paper < index && index < lines && layer.Blend == BlendMode.Multiply, "The material layer is not above the paper and below the linework");
                Check(w.doc.ActiveId == layer.Id && w.selectedLayers.SetEquals([layer.Id]) && w.history.UndoLabel == "선택 영역 재질", "The new layer was not selected or not one named step");
                Check(w.selection != null && w.selection.Bounds == bounds, "Creating the layer dropped the selection");

                // The layer just made stays the target: another swatch swaps its material.
                var tile = Tiles(w.selectionMaterialPanel!).First(t => AutomationProperties.GetName(t) == DrawingCleanup.MaterialName(MaterialKind.Tile));
                tile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.doc.Layers.Count(l => l.Kind == LayerKind.Material) == 1 && w.doc.Active?.Material?.Asset.Id == MaterialPresets.Create(MaterialKind.Tile).Id && w.history.UndoLabel == "선택 영역 재질 바꾸기",
                    "A second swatch stacked a layer instead of swapping the material");
                Check(Tiles(w.selectionMaterialPanel!).Single(t => t.Background == Theme.Selected) is { } current && AutomationProperties.GetName(current) == DrawingCleanup.MaterialName(MaterialKind.Tile), "The applied swatch is not marked");
                w.Undo(); Check(w.doc.Layers.Single(l => l.Kind == LayerKind.Material).Material!.Asset.Id == MaterialPresets.Create(MaterialKind.Wood).Id, "Undo did not restore the first material");
                w.Undo();
                Check(w.doc.Layers.Count == layers && w.doc.MaterialRegions.Count == 0 && w.doc.Materials.Count == 0, "One undo did not remove the layer, region and material together");

                // The user's own image is registered and applied in one step.
                string image = Path.Combine(root, "오크 마루.png");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Raster.Solid(32, 16, Colors.SaddleBrown).Bitmap()));
                using (var output = File.Create(image)) encoder.Save(output);
                w.selection = new Selection(new Rect(100, 20, 80, 120)); w.Refresh(false);
                var own = w.ApplySelectionMaterialImage(image);
                Check(own?.Material is { } mine && mine.Asset.Name == "오크 마루" && mine.Asset.Source == "오크 마루.png" && Math.Abs(mine.TileHeight - mine.TileWidth / 2) < 1e-9 && w.doc.Materials.Any(m => m.Id == mine.Asset.Id),
                    "The user's image was not registered and applied with its proportions");
                var choices = w.SelectionMaterialChoices(w.SelectionMaterialSuggestion());
                Check(choices.Select(c => c.Name).TakeWhile(n => n != DrawingCleanup.MaterialName(MaterialKind.Wood)).Contains("오크 마루"), "A document image named as wood is not offered before the wood swatch");
                w.Undo(); Check(w.doc.Materials.Count == 0 && w.doc.Layers.Count == layers, "Undo left the registered image behind");
                w.selection = null; w.Refresh(false);
                Check(w.ApplySelectionMaterial(MaterialPresets.Create(MaterialKind.Stone), "석재") == null && w.doc.Layers.Count == layers, "A swatch applied without a selection");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("a wand pick on a drawing brings the material swatches forward", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current; var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                w.AddTab(PrecisionWandTests.Drawing(), null); w.ShowStudioPage(0); w.SetTool(Tool.MagicWand);
                var task = w.SelectWandAsync(new Point(47, 38), true, SelectionCombine.Replace);
                if (!task.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                }
                Check(task.GetAwaiter().GetResult() && w.selection != null, "The wand did not select");
                Check(w.studioPage == 1 && w.selectionMaterialPanel != null && w.properties.Children.Contains(w.selectionMaterialPanel), "The properties tab with swatches was not shown after the wand");
                Check(!w.history.CanUndo, "Showing swatches changed the document");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); w.StopRenderingForShutdown(); }
        });
    }

    static Button? FindEye(MainWindow w, LayerListEntry entry)
    {
        var row = w.CreateLayerRow(entry);
        return row.Children.OfType<Button>().FirstOrDefault(b => Grid.GetColumn(b) == 0 && !ReferenceEquals(b, row.DragHandle));
    }

    static string? Header(StackPanel panel) => panel.Children.OfType<SectionHeader>().FirstOrDefault()?.Key;
    static Button[] Tiles(StackPanel panel) => panel.Children.OfType<UniformGrid>().SelectMany(g => g.Children.OfType<Button>()).ToArray();

    // A 200×160 plan: paper, a wall ring (10–190 × 10–150, 10 px thick) and a floor hatch in the left room.
    static Document Plan()
    {
        var plan = new Document { Width = 200, Height = 160, Name = "평면" };
        var paper = VectorShapes.Create(new ShapeSpec { Width = 200, Height = 160, FillArgb = 0xFFFFFFFF }); paper.Name = "도면 배경"; paper.Locked = true; plan.Add(paper);
        Layer Vector(string name, Rect box, Geometry local, bool fill)
        {
            var source = VectorContent.FromPaths((int)box.Width, (int)box.Height, [new(local, Colors.Black, fill, 1)]);
            return new Layer { Name = name, Kind = LayerKind.Vector, Category = LayerCategory.Drawing, Vector = source, X = box.X, Y = box.Y,
                Pixels = Imaging.Draw((int)box.Width, (int)box.Height, dc => dc.DrawDrawing(source.Drawing)) };
        }
        var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new RectangleGeometry(new Rect(0, 0, 180, 140))); ring.Children.Add(new RectangleGeometry(new Rect(10, 10, 160, 120)));
        plan.Add(Vector("A-FLOR-PATT", new Rect(20, 20, 80, 120), new RectangleGeometry(new Rect(0, 0, 80, 120)), false));
        plan.Add(Vector("A-WALL", new Rect(10, 10, 180, 140), ring, true));
        return plan;
    }

    static byte[] Ring(int width, int height)
    {
        var mask = new byte[width * height];
        for (int y = 10; y < 150; y++) for (int x = 10; x < 190; x++)
            if (x < 20 || x >= 180 || y < 20 || y >= 140) mask[y * width + x] = 255;
        return mask;
    }
}
