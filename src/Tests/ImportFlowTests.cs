using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

// Remembered import settings, several drawings with one dialog, the quick path, drawing-sized
// artboards and hatch materials on the photo tab.
public static class ImportFlowFixtures
{
    // A small plan: two walls (one crossing the hatch), a furniture line and one concrete hatch.
    public static string Plan(string directory, string name, string furniture = "A-FURN")
    {
        var cad = new CadDocument();
        var wall = new ACadSharp.Tables.Layer("A-WALL"); var furn = new ACadSharp.Tables.Layer(furniture); var hatches = new ACadSharp.Tables.Layer("A-HATCH");
        foreach (var layer in new[] { wall, furn, hatches }) cad.Layers.Add(layer);
        cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
        cad.Entities.Add(new Line { StartPoint = new XYZ(0, 140, 0), EndPoint = new XYZ(200, 140, 0), Layer = wall });
        cad.Entities.Add(new Line { StartPoint = new XYZ(0, 60, 0), EndPoint = new XYZ(200, 60, 0), Layer = furn });
        var hatch = new Hatch { Pattern = new HatchPattern("AR-CONC"), Layer = hatches };
        var loop = new Hatch.BoundaryPath(); loop.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 100, 0), new XYZ(180, 100, 0), new XYZ(180, 180, 0), new XYZ(20, 180, 0) }, true));
        hatch.Paths.Add(loop); cad.Entities.Add(hatch);
        string file = Path.Combine(directory, name); DxfWriter.Write(file, cad); return file;
    }

    public static CompatibilityResult Read(string file, CompatibilityOptions options) => CompatibilityImport.ReadAsync(file, options).GetAwaiter().GetResult();

    // Runs dialog steps on the test thread's dispatcher, the way a shown dialog would.
    public static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
    public static void Pump(Task task) => Pump(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default));

    public static (int Ink, double Darkness) Ink(Layer layer)
    {
        int count = 0; double dark = 0;
        for (int i = 0; i < layer.Pixels.Data.Length; i += 4)
            if (layer.Pixels.Data[i + 3] > 40) { count++; dark += 255 - (layer.Pixels.Data[i] + layer.Pixels.Data[i + 1] + layer.Pixels.Data[i + 2]) / 3d; }
        return (count, count == 0 ? 0 : dark / count);
    }
}

public static class ImportSettingsTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "import-flow"); Directory.CreateDirectory(root);
        string Store() => Path.Combine(root, "import-settings-" + Guid.NewGuid().ToString("N") + ".json");

        test("import settings store round-trips, sanitizes values and ignores damaged files", () =>
        {
            string store = Store();
            Check(ImportSettingsStore.Load(store).SameAs(new ImportSettings()), "A missing store did not load the defaults");
            var custom = new ImportSettings
            {
                CadStructure = CadImportStructure.Combined, CadLineWeights = false, CadHatches = HatchTreatment.Image, CadMaterialImage = Path.Combine(root, "마루.png"),
                CadLongEdge = 3000, CadRetainVectors = false, CadLayout = "배치1", CadRoles = new Dictionary<string, DrawingRole> { ["A-FURN"] = DrawingRole.Annotation },
                CadSkipDialog = true, PdfDpi = 300, PdfLayers = false, PdfRetainVectors = false, PsdLayers = true, CadArtboard = false, PdfArtboard = false
            };
            ImportSettingsStore.Save(custom, store);
            var loaded = ImportSettingsStore.Load(store);
            Check(loaded.SameAs(custom) && loaded.CadRoles!.ContainsKey("a-furn"), "Settings did not round-trip, or layer names became case sensitive");
            Check(File.ReadAllText(store).Contains("\"Combined\"") && File.ReadAllText(store).Contains("\"Annotation\""), "Choices are not stored by name");
            File.WriteAllText(store, """{"CadLongEdge": 99999, "PdfDpi": -5, "CadStructure": 7, "CadHatches": "Image", "CadMaterialImage": "상대 경로.png", "CadLayout": "", "CadRoles": {"A-WALL": "Furniture", " ": "Other", "X": 42}}""");
            loaded = ImportSettingsStore.Load(store);
            Check(loaded.CadLongEdge == 4096 && loaded.PdfDpi == 36 && loaded.CadStructure == CadImportStructure.Objects && loaded.CadHatches == HatchTreatment.Suggest
                && loaded.CadMaterialImage == null && loaded.CadLayout == null && loaded.CadRoles is { Count: 1 } roles && roles["A-WALL"] == DrawingRole.Furniture,
                "Out-of-range values, unknown choices or a relative image path were accepted");
            foreach (string damaged in new[] { "{not json", """{"CadStructure": "Banana"}""", new string(' ', 300_000) })
            {
                File.WriteAllText(store, damaged);
                Check(ImportSettingsStore.Load(store).SameAs(new ImportSettings()), "A damaged store did not fall back to the defaults");
            }
            var many = Enumerable.Range(0, 500).ToDictionary(i => "L" + i, _ => DrawingRole.Other);
            Check(ImportSettingsStore.Save(new ImportSettings { CadRoles = many }, store).CadRoles!.Count == ImportSettings.MaxRoles && ImportSettingsStore.Load(store).CadRoles!.Count == ImportSettings.MaxRoles, "Remembered roles are not capped");

            Check(custom.Options("가.dwg") is { CadStructure: CadImportStructure.Combined, CadLongEdge: 3000, CadLayout: "배치1", CadLayoutOptional: true, RetainVectors: false,
                GroupDrawingObjects: true, Artboard: false, Cleanup: { LineWeights: false, Hatches: HatchTreatment.Image } cleanup } && cleanup.RoleFor("A-FURN") == DrawingRole.Annotation, "CAD options do not follow the settings");
            Check(!(custom with { CadLayout = ImportSettings.ModelSpace }).Options("가.dxf").CadLayoutOptional && custom.Options("가.dxf").Page == 1, "Model space is not an exact choice");
            Check(custom.Options("가.pdf", 3) is { Page: 3, Dpi: 300, SeparateLayers: false, PreservePdfLayers: false, RetainVectors: false, CadStructure: null, Artboard: false, Cleanup: null }, "PDF options do not follow the settings");
            Check(custom.Options("가.psd").SeparateLayers && custom.Options("가.psb").Cleanup == null, "PSD options do not follow the settings");
            Check(new ImportSettings { CadLineWeights = false, CadHatches = HatchTreatment.Keep }.Options("가.dxf").Cleanup == null, "Cleanup turned off still sent settings");
        });

        test("drawing import makes an artboard from the model extents, a layout's paper or a PDF page", () =>
        {
            string file = ImportFlowFixtures.Plan(root, "대지 평면.dxf");
            var plain = ImportFlowFixtures.Read(file, new(CadLongEdge: 600, CadLayout: "*Model_Space")).Document;
            Check(plain.Artboards.Count == 0, "An artboard was made without the option");
            var model = ImportFlowFixtures.Read(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", Artboard: true)).Document;
            Check(model.Artboards is [{ X: 0, Y: 0 } board] && board.Width == model.Width && board.Height == model.Height && board.Name == "대지 평면"
                && model.Width == plain.Width && model.Height == plain.Height, "Model space artboard is not the drawing extents with the import margin");

            var cad = new CadDocument(); var paper = cad.PaperSpace; var layout = paper.Layout;
            layout.PaperWidth = 420; layout.PaperHeight = 297; layout.UnprintableMargin = new ACadSharp.Objects.PaperMargin(0, 0, 0, 0);
            layout.PlotOriginX = 0; layout.PlotOriginY = 0; layout.PaperRotation = ACadSharp.Objects.PlotRotation.NoRotation; layout.PaperUnits = ACadSharp.Objects.PlotPaperUnits.Millimeters;
            paper.Entities.Add(new Line { StartPoint = new XYZ(100, 80, 0), EndPoint = new XYZ(300, 200, 0) });
            string sheetFile = Path.Combine(root, "배치 시트.dxf"); DxfWriter.Write(sheetFile, cad);
            var hugging = ImportFlowFixtures.Read(sheetFile, new(CadLongEdge: 600)).Document;
            var sheet = ImportFlowFixtures.Read(sheetFile, new(CadLongEdge: 600, Artboard: true)).Document;
            var a = sheet.Artboards.Single(); var drawn = sheet.Layers.Single(l => l.Name.StartsWith("CAD ", StringComparison.Ordinal));
            Check(hugging.Artboards.Count == 0 && Math.Abs(hugging.Width / (double)hugging.Height - sheet.Width / (double)sheet.Height) > .1, "The paper changed an import without the option");
            Check(Math.Abs(a.Width / a.Height - 420 / 297.0) < .02 && a.Width >= sheet.Width - 41 && a.Name.StartsWith("배치 시트 · ", StringComparison.Ordinal),
                $"Layout artboard is not the paper sheet: {a} in {sheet.Width}×{sheet.Height}");
            Check(drawn.X >= a.X - 3 && drawn.Y >= a.Y - 3 && drawn.X + drawn.Pixels.Width <= a.X + a.Width + 3 && drawn.Y + drawn.Pixels.Height <= a.Y + a.Height + 3, "The drawing is not on its paper");
            // The line starts 100 mm from the paper's left edge, as in the layout.
            Check(Math.Abs((drawn.X + 2 - a.X) / a.Width - 100 / 420.0) < .02, "The drawing is not placed on the paper as in the layout");

            string pdfFile = Path.Combine(root, "대지 도면.pdf"); LayeredCompatibilityTests.WritePdf(pdfFile, false);
            var pdf = ImportFlowFixtures.Read(pdfFile, new(Artboard: true)).Document;
            Check(pdf.Artboards is [{ X: 0, Y: 0 } page] && page.Width == pdf.Width && page.Height == pdf.Height, "A PDF page did not become an artboard");
        });

        test("drawings added to a document with artboards go on a new artboard beside them", () =>
        {
            string file = ImportFlowFixtures.Plan(root, "추가 평면.dxf");
            var drawing = ImportFlowFixtures.Read(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", Artboard: true, GroupDrawingObjects: true)).Document;
            var target = new Document { Width = 800, Height = 600, Name = "시트" }; target.Add(new Layer { Name = "배경", Pixels = Raster.Solid(800, 600, Colors.White) });
            var centered = target.Snapshot(); var layers = CompatibilityImport.Place(centered, drawing, true, 800, 600); centered.Validate();
            var folder = layers.Single(l => l.ParentId == null);
            Check(centered.Artboards.Count == 0 && centered.Width == 800 && Math.Abs(folder.X - (800 - drawing.Width) / 2.0) < .5, "A document without artboards did not keep the centered placement");
            target.Artboards.Add(new Artboard(Guid.NewGuid(), "1층", 0, 0, 800, 600));
            var beside = target.Snapshot(); layers = CompatibilityImport.Place(beside, drawing, true, 800, 600); beside.Validate();
            var added = beside.Artboards[^1]; folder = layers.Single(l => l.ParentId == null);
            Check(beside.Artboards.Count == 2 && beside.Artboards[0] == target.Artboards[0] && added.X == 840 && added.Y == 0 && added.Width == drawing.Width && added.Height == drawing.Height
                && !added.Bounds.IntersectsWith(target.Artboards[0].Bounds) && beside.Width >= added.Bounds.Right && added.Name == "추가 평면", $"The new artboard is not beside the existing one: {added}");
            Check(Math.Abs(folder.X - added.X) < .5 && Math.Abs(folder.Y - added.Y) < .5 && beside.Layers[0].X == 0 && beside.ActiveId == layers[^1].Id, "The drawing is not on its new artboard, or existing layers moved");
            var small = target.Snapshot(); small.Artboards[0] = small.Artboards[0] with { Width = 300, Height = 200 };
            CompatibilityImport.Place(small, drawing, true, 800, 600); small.Validate();
            Check(small.Artboards[^1].Width <= 300 && small.Artboards[^1].Height <= 200, "A drawing larger than the artboards was not scaled to fit beside them");
            var off = target.Snapshot(); CompatibilityImport.Place(off, drawing, false, 800, 600); off.Validate();
            Check(off.Artboards.Count == 1 && off.Width == 800, "Turning the option off still added an artboard");
        });

        test("hatch materials are photo layers kept below the linework and remain editable", () =>
        {
            string file = ImportFlowFixtures.Plan(root, "재질 평면.dxf");
            var doc = ImportFlowFixtures.Read(file, new ImportSettings { CadLongEdge = 600 }.Options(file)).Document;
            var categories = DrawingLayers.Categories(doc);
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material); var folder = doc.Layers.Single(l => l.ParentId == null);
            var walls = doc.Layers.First(l => l.Name == "A-WALL" && l.ParentId == folder.Id);
            Check(material.ParentId == folder.Id && material.Category == LayerCategory.Photo && categories[material.Id] == LayerCategory.Photo && categories[folder.Id] == LayerCategory.Drawing
                && categories[walls.Id] == LayerCategory.Drawing && DrawingLayers.IsNestedPhoto(material, categories), "The hatch material is not a photo layer inside the drawing");
            Check(doc.Layers.IndexOf(material) < doc.Layers.IndexOf(walls) && material.Blend == BlendMode.Multiply, "The material is not below the linework");
            var legacy = doc.Snapshot(); legacy.Layers.Single(l => l.Id == material.Id).Category = LayerCategory.Drawing;
            Check(DrawingLayers.Categories(legacy)[material.Id] == LayerCategory.Photo, "A material saved by an earlier version stayed on the drawing tab");
            var pixels = Imaging.Render(doc);
            int Gray(int x, int y) { int i = (y * pixels.Width + x) * 4; return (pixels.Data[i] + pixels.Data[i + 1] + pixels.Data[i + 2]) / 3; }
            // CAD (100, 120) is inside the hatch; the wall at y = 140 crosses it (fit: 2.8 px per unit, 20 px margin).
            Check(Gray(300, 188) < 245 && Gray(300, 132) < 110, $"Material or crossing wall is not visible: fill {Gray(300, 188)}, wall {Gray(300, 132)}");
            var scene = new AutomationScene(doc);
            var photo = scene.Query(new JsonObject { ["category"] = "Photo" }, new HashSet<Guid>())["layers"]!.AsArray();
            var drawing = scene.Query(new JsonObject { ["category"] = "Drawing" }, new HashSet<Guid>())["layers"]!.AsArray();
            Check(photo.Any(l => l!["layerId"]?.GetValue<string>() == material.Id.ToString()) && drawing.All(l => l!["layerId"]?.GetValue<string>() != material.Id.ToString()),
                "The AI connection does not report the material as a photo layer: " + photo.ToJsonString());
            string project = Path.Combine(root, "재질 평면.moruproj"); ProjectStore.Save(doc, project);
            var loaded = ProjectStore.Load(project); var reloaded = loaded.Layers.Single(l => l.Kind == LayerKind.Material);
            Check(reloaded.ParentId == folder.Id && DrawingLayers.Categories(loaded)[reloaded.Id] == LayerCategory.Photo && loaded.Layers.IndexOf(reloaded) == doc.Layers.IndexOf(material), "Saving lost the material's place or category");
            var placed = new Document { Width = 900, Height = 700 }; placed.Add(new Layer { Name = "사진", Pixels = Raster.Solid(900, 700, Colors.White) });
            CompatibilityImport.Place(placed, doc, false, 900, 700); placed.Validate();
            var copied = placed.Layers.Single(l => l.Kind == LayerKind.Material);
            Check(DrawingLayers.IsNestedPhoto(copied, DrawingLayers.Categories(placed)), "Adding the drawing to another document lost the material's category");
        });
    }
}

internal sealed partial class CompatibilityDialog
{
    internal bool ApplyAllForTest { get => applyAll.IsChecked == true; set => applyAll.IsChecked = value; }
    internal CadImportStructure StructureForTest => SelectedStructure;

    internal static void RunImportFlowDialogTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        test("import dialog opens with remembered settings and remembers the changed ones", () =>
        {
            var remembered = new ImportSettings
            {
                CadStructure = CadImportStructure.Layers, CadLineWeights = false, CadHatches = HatchTreatment.Keep, CadLongEdge = 1800, CadRetainVectors = false, CadLayout = "배치1",
                CadRoles = new Dictionary<string, DrawingRole>(StringComparer.OrdinalIgnoreCase) { ["A-FURN-SOFA"] = DrawingRole.Annotation, ["OTHER-LAYER"] = DrawingRole.Structure },
                CadSkipDialog = true, CadArtboard = false
            };
            var dialog = new CompatibilityDialog(null, "평면.dxf", false, remembered, ["평면.dxf", "2층.dxf", "3층.dwg"]);
            try
            {
                Check(dialog.SelectedStructure == CadImportStructure.Layers && dialog.lineWeights.IsChecked == false && dialog.SelectedHatches == HatchTreatment.Keep && dialog.edge.Text == "1800"
                    && dialog.retain.IsChecked == false && dialog.artboard.IsChecked == false && dialog.skipDialog.IsChecked == true && dialog.skipHint.Visibility == Visibility.Visible, "Remembered CAD settings were not prefilled");
                dialog.ShowSpaces([new("*Model_Space", "모델 공간"), new("*Paper_Space", "배치1")]);
                Check(dialog.space.SelectedIndex == 2, "The remembered layout was not chosen again");
                Check(dialog.ReadOptions() is { CadLayout: "*Paper_Space", CadLayoutOptional: false, CadStructure: CadImportStructure.Layers, Artboard: false, Cleanup: null, RetainVectors: false, CadLongEdge: 1800 },
                    "Preview options do not match the dialog");
                dialog.ShowDrawingInfo(SampleDrawing);
                Check(((RoleChoice)dialog.rolePickers["A-FURN-SOFA"].SelectedItem).Value == DrawingRole.Annotation, "A remembered layer role was not chosen again");
                dialog.rolePickers["A-WALL"].SelectedIndex = (int)DrawingRole.Opening; dialog.rolePickers["A-FURN-SOFA"].SelectedIndex = (int)DrawingRole.Furniture;
                var captured = dialog.CaptureSettings();
                Check(captured.CadRoles is { } roles && roles["A-WALL"] == DrawingRole.Opening && !roles.ContainsKey("A-FURN-SOFA") && roles["OTHER-LAYER"] == DrawingRole.Structure
                    && captured.CadLayout == "배치1" && captured.CadSkipDialog && captured.CadLongEdge == 1800, "Changed settings were not captured, or other drawings' roles were lost");
                dialog.space.SelectedIndex = 1; Check(dialog.CaptureSettings().CadLayout == ImportSettings.ModelSpace, "Model space was not remembered");
                dialog.space.SelectedIndex = 0; Check(dialog.CaptureSettings().CadLayout == null, "Automatic layout was not remembered");
                Check(dialog.applyAll.Visibility == Visibility.Visible && dialog.ApplyAllForTest && Equals(dialog.accept.Content, "3개 모두 가져오기"), "Several files do not offer one import for all");
                dialog.ApplyAllForTest = false; Check(Equals(dialog.accept.Content, "가져오기"), "The import button did not follow the choice");
                dialog.skipDialog.IsChecked = false; Check(dialog.skipHint.Visibility == Visibility.Collapsed && !dialog.CaptureSettings().CadSkipDialog, "Asking again was not captured");
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Morupixel;component/UI/Theme.xaml", UriKind.Relative) });
                var root = (FrameworkElement)dialog.Content; root.Resources = dialog.Resources;
                root.Measure(new Size(760, 530)); root.Arrange(new Rect(0, 0, 760, 530)); root.UpdateLayout();
                var bounds = dialog.accept.TransformToAncestor(root).TransformBounds(new Rect(dialog.accept.RenderSize));
                Check(bounds.Bottom <= root.ActualHeight + 1 && dialog.applyAll.ActualHeight > 10 && dialog.skipDialog.ActualHeight > 10, "The multi-file choices push the import action offscreen");
            }
            finally { dialog.Close(); }

            var pdfDialog = new CompatibilityDialog(null, "도면.pdf", false, new ImportSettings { PdfDpi = 300, PdfLayers = false, PdfRetainVectors = false, PdfArtboard = false });
            try
            {
                Check(pdfDialog.dpi.Text == "300" && pdfDialog.ReadOptions() is { Dpi: 300, SeparateLayers: false, PreservePdfLayers: false, Artboard: false, RetainVectors: false }
                    && pdfDialog.skipDialog.Visibility == Visibility.Collapsed && pdfDialog.applyAll.Visibility == Visibility.Collapsed, "Remembered PDF settings were not prefilled");
                Check(pdfDialog.CaptureSettings() is { PdfDpi: 300, PdfLayers: false, CadSkipDialog: false }, "PDF settings were not captured");
            }
            finally { pdfDialog.Close(); }
            var psdDialog = new CompatibilityDialog(null, "사진.psd", false, new ImportSettings { PsdLayers = true });
            try { Check(psdDialog.ReadOptions().SeparateLayers && !psdDialog.settings.Children.Contains(psdDialog.artboard), "Remembered PSD settings were not prefilled"); }
            finally { psdDialog.Close(); }
            var missing = new CompatibilityDialog(null, "평면.dxf", false, new ImportSettings { CadHatches = HatchTreatment.Image, CadMaterialImage = @"C:\없는 폴더\재질.png", CadSkipDialog = true }, null, quick: true);
            try { Check(!missing.Direct && missing.SelectedHatches == HatchTreatment.Suggest && missing.startNotice != null, "A missing material image did not bring the settings back"); }
            finally { missing.Close(); }
        });
    }
}

public sealed partial class MainWindow
{
    internal static void RunImportFlowTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "import-flow-window"); Directory.CreateDirectory(root);

        test("several drawings import with one dialog and the same settings, one tab each", () =>
        {
            var files = new[] { ImportFlowFixtures.Plan(root, "2층.dxf"), ImportFlowFixtures.Plan(root, "3층.dxf"), ImportFlowFixtures.Plan(root, "4층.dxf", "B-FURN") };
            string store = Path.Combine(root, "import-settings-" + Guid.NewGuid().ToString("N") + ".json");
            var window = new MainWindow(null) { headlessTesting = true, importSettingsStore = store };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                // Quick path: no dialog steps, every file with the remembered settings.
                ImportSettingsStore.Save(new ImportSettings { CadStructure = CadImportStructure.Layers, CadLongEdge = 600, CadSkipDialog = true,
                    CadRoles = new Dictionary<string, DrawingRole> { ["A-FURN"] = DrawingRole.Structure } }, store);
                int dialogs = 0;
                window.importDialogRunner = dialog => { dialogs++; Check(dialog.Direct, "The quick path showed the settings"); return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths(files);
                var docs = window.tabs.Select(t => t.Document).ToArray();
                Check(dialogs == 1 && docs.Length == 3 && docs.Select(d => d.Name).SequenceEqual(["2층", "3층", "4층"]), $"Expected one dialog and three tabs, got {dialogs} and {docs.Length}");
                Check(docs.All(d => d.Layers.Any(l => l.Kind == LayerKind.Material) && d.Layers.Any(l => l.Name == "A-WALL" && l.Kind != LayerKind.Group) && d.Artboards.Count == 1),
                    "Not every drawing used the same structure, cleanup and artboard settings");
                var overridden = ImportFlowFixtures.Ink(docs[0].Layers.Single(l => l.Name == "A-FURN")); var automatic = ImportFlowFixtures.Ink(docs[2].Layers.Single(l => l.Name == "B-FURN"));
                Check(ImportFlowFixtures.Ink(docs[1].Layers.Single(l => l.Name == "A-FURN")).Darkness > automatic.Darkness + 40 && overridden.Darkness > automatic.Darkness + 40,
                    $"A role override was not applied by layer name only: {overridden} vs {automatic}");
                Check(window.status.Text.Contains("이전 가져오기 설정"), "The quick path did not say how the settings were chosen: " + window.status.Text);

                // Placing a drawing into a document that has an artboard adds one beside it, in one undo step.
                window.SwitchTab(0); int width = window.doc.Width;
                window.ImportFiles([files[1]]);
                Check(dialogs == 2 && window.doc.Artboards.Count == 2 && window.doc.Artboards[1].X == window.doc.Artboards[0].Bounds.Right + 40 && window.doc.Width > width,
                    "The placed drawing did not get its own artboard beside the first");
                window.Undo(); Check(window.doc.Artboards.Count == 1 && window.doc.Width == width, "Placing the drawing was not one undo step");

                // Asking again: the dialog returns, prefilled, and "apply to all" still imports once.
                window.AskImportSettingsAgain();
                Check(!ImportSettingsStore.Load(store).CadSkipDialog && window.BuildCommandRegistry().Any(c => c.Id == "menu:파일/도면 가져오기 설정 다시 묻기"), "The dialog could not be brought back");
                dialogs = 0;
                window.importDialogRunner = dialog =>
                {
                    dialogs++; Check(!dialog.Direct && dialog.ApplyAllForTest && dialog.StructureForTest == CadImportStructure.Layers, "The dialog did not open with the remembered settings");
                    ImportFlowFixtures.Pump(dialog.PrepareAsync()); return ImportFlowFixtures.Pump(dialog.ImportAsync());
                };
                window.OpenPaths([files[0], files[1]]);
                Check(dialogs == 1 && window.tabs.Count == 5, "Apply to all did not import both files after one dialog");
                Check(ImportSettingsStore.Load(store) is { CadStructure: CadImportStructure.Layers, CadRoles: { } kept } && kept["A-FURN"] == DrawingRole.Structure, "Settings from the dialog were not remembered");
                dialogs = 0;
                window.importDialogRunner = dialog => { dialogs++; dialog.ApplyAllForTest = false; ImportFlowFixtures.Pump(dialog.PrepareAsync()); return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths([files[0], files[1]]);
                Check(dialogs == 2 && window.tabs.Count == 7, "Without apply to all every file should get its own dialog");
                dialogs = 0;
                window.importDialogRunner = dialog => { dialogs++; ImportFlowFixtures.Pump(dialog.PrepareAsync()); return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths(files);
                Check(dialogs == 1 && window.tabs.Count == 8 && window.status.Text.Contains("최대 8개"), "The tab limit was not respected or explained: " + window.status.Text);
                window.importDialogRunner = _ => false; int tabs = window.tabs.Count; window.CloseTabAt(7);
                window.OpenPaths([files[0]]); Check(window.tabs.Count == tabs - 1, "A cancelled dialog opened a tab");
            }
            finally
            {
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                SynchronizationContext.SetSynchronizationContext(previous); window.StopRenderingForShutdown();
            }
        });

        test("hatch materials are listed on the photo tab and edited there without opening the drawing", () =>
        {
            string file = ImportFlowFixtures.Plan(root, "패널 평면.dxf");
            var doc = ImportFlowFixtures.Read(file, new ImportSettings { CadLongEdge = 600 }.Options(file)).Document;
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material); var folder = doc.Layers.Single(l => l.ParentId == null);
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(doc, null);
                IReadOnlyList<LayerListEntry> Entries() => (IReadOnlyList<LayerListEntry>)window.layerList.ItemsSource;
                Check(window.layerCategory == LayerCategory.Drawing && Entries().All(e => e.Layer.Id != material.Id), "The drawing tab listed the material");
                window.collapsedGroups.Remove(folder.Id); window.BuildLayers();
                Check(Entries().Count > 1 && Entries().All(e => e.Layer.Id != material.Id), "The opened drawing folder listed the material on the drawing tab");
                window.collapsedGroups.Add(folder.Id);
                window.layerCategoryButtons[LayerCategory.Photo].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var row = Entries().Single(e => e.Layer.Id == material.Id);
                Check(row.Depth == 0 && row.Description!.StartsWith("도면 해치 재질", StringComparison.Ordinal), "The material is not a row of its own on the photo tab");
                window.ClickLayerRow(row, ModifierKeys.None);
                Check(window.doc.ActiveId == material.Id && window.layerCategory == LayerCategory.Photo && window.collapsedGroups.Contains(folder.Id), "Selecting the material left the photo tab or opened the drawing");
                window.EditLayer("불투명도", l => l.Opacity = .5); window.EditLayer("혼합 모드", l => l.Blend = BlendMode.Darken);
                var edited = window.doc.Layers.Single(l => l.Id == material.Id);
                Check(edited.Opacity == .5 && edited.Blend == BlendMode.Darken && edited.ParentId == folder.Id && Entries().Single(e => e.Layer.Id == material.Id).Description!.Contains("50%"), "The material could not be edited as a photo layer");
                window.Undo(); window.Undo(); Check(window.doc.Layers.Single(l => l.Id == material.Id) is { Opacity: 1, Blend: BlendMode.Multiply }, "Material edits did not undo");
                var photo = new Layer { Name = "현장 사진", Pixels = Raster.Solid(40, 40, Colors.SkyBlue) };
                window.Edit("사진 추가", () => window.doc.Add(photo)); window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                Check(Entries().Select(e => e.Layer.Id).SequenceEqual([photo.Id, material.Id]), "The photo tab does not follow the paint order");
            }
            finally { foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document); window.StopRenderingForShutdown(); }
        });
    }
}
