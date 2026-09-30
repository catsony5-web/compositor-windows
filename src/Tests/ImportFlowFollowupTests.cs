using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

// Follow-ups to the drawing import flow: per-kind artboard choice, unreadable files in a multi-file
// import, several drawings placed beside one artboard, and hatch materials on the photo tab.
internal sealed partial class CompatibilityDialog
{
    internal static void RunImportFlowFollowupDialogTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        // Fixed file names inside; a fresh root per run keeps a reused report folder from feeding stale fixtures.
        string root = Path.Combine(directory, "import-flow-followup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);

        test("the artboard choice is remembered per file kind and the older shared choice still loads", () =>
        {
            var remembered = new ImportSettings { CadSkipDialog = true };
            var pdfDialog = new CompatibilityDialog(null, "도면.pdf", false, remembered);
            try
            {
                Check(pdfDialog.artboard.IsChecked == true, "The PDF artboard choice was not prefilled");
                pdfDialog.artboard.IsChecked = false;
                var captured = pdfDialog.CaptureSettings();
                Check(!captured.PdfArtboard && captured.CadArtboard && captured.Options("평면.dwg").Artboard && !captured.Options("도면.pdf").Artboard,
                    "Turning the artboard off for a PDF also changed quick DWG/DXF imports");
            }
            finally { pdfDialog.Close(); }
            var cadDialog = new CompatibilityDialog(null, "평면.dxf", false, new ImportSettings { PdfArtboard = false });
            try
            {
                Check(cadDialog.artboard.IsChecked == true, "The PDF choice was prefilled into the DWG/DXF dialog");
                cadDialog.artboard.IsChecked = false;
                Check(cadDialog.CaptureSettings() is { CadArtboard: false, PdfArtboard: false }, "The DWG/DXF artboard choice was not captured on its own");
            }
            finally { cadDialog.Close(); }

            string store = Path.Combine(root, "import-settings-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(store, """{"Artboard": false, "PdfDpi": 200}""");
            var legacy = ImportSettingsStore.Load(store);
            Check(legacy is { CadArtboard: false, PdfArtboard: false, LegacyArtboard: null, PdfDpi: 200 }, "The artboard choice saved by an earlier version was not applied to both kinds");
            ImportSettingsStore.Save(legacy with { PdfArtboard = true }, store);
            string saved = File.ReadAllText(store); var reloaded = ImportSettingsStore.Load(store);
            Check(!saved.Contains("\"Artboard\"") && reloaded is { CadArtboard: false, PdfArtboard: true }, "The per-kind artboard choices did not round-trip: " + saved);
        });

        test("an unreadable previewed file can be skipped so the other chosen files still import", () =>
        {
            string bad = Path.Combine(root, "0 손상 평면.dxf"); File.WriteAllText(bad, "이 파일은 도면이 아닙니다");
            var good = new[] { ImportFlowFixtures.Plan(root, "1 평면.dxf"), ImportFlowFixtures.Plan(root, "2 평면.dxf") };
            var settings = new ImportSettings { CadLongEdge = 600 };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var dialog = new CompatibilityDialog(null, bad, false, settings, [bad, .. good]);
                try
                {
                    ImportFlowFixtures.Pump(dialog.PrepareAsync());
                    Check(dialog.prepared == null && dialog.accept.IsEnabled && Equals(dialog.accept.Content, "이 파일 빼고 2개 가져오기"),
                        $"A failed preview left no way to import the other files: {dialog.accept.IsEnabled} · {dialog.accept.Content}");
                    dialog.ApplyAllForTest = false; Check(Equals(dialog.accept.Content, "이 파일 건너뛰기"), "The skip action did not follow the apply-to-all choice");
                    dialog.ApplyAllForTest = true;
                    Check(ImportFlowFixtures.Pump(dialog.ImportAsync()), "Skipping the unreadable file did not import the rest");
                    Check(dialog.Results.Count == 3 && dialog.Results[0] is { Document: null, Error.Length: > 0 } && dialog.Results.Skip(1).All(r => r.Document != null && r.Error == null),
                        "The unreadable file was not reported, or the other files were not imported");
                }
                finally { dialog.Close(); }
                var single = new CompatibilityDialog(null, bad, false, settings);
                try
                {
                    ImportFlowFixtures.Pump(single.PrepareAsync());
                    Check(!single.accept.IsEnabled && !ImportFlowFixtures.Pump(single.ImportAsync()), "A single unreadable file offered an import");
                }
                finally { single.Close(); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
    }
}

public sealed partial class MainWindow
{
    internal static void RunImportFlowFollowupTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        // Fixed file names inside; a fresh root per run keeps a reused report folder from feeding stale fixtures.
        string root = Path.Combine(directory, "import-flow-followup-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string Store() => Path.Combine(root, "import-settings-" + Guid.NewGuid().ToString("N") + ".json");
        // A rectangle of walls, w × h drawing units.
        static string Frame(string folder, string name, double w, double h)
        {
            var cad = new CadDocument(); var layer = new ACadSharp.Tables.Layer("A-WALL"); cad.Layers.Add(layer);
            var corners = new[] { new XYZ(0, 0, 0), new XYZ(w, 0, 0), new XYZ(w, h, 0), new XYZ(0, h, 0) };
            for (int i = 0; i < 4; i++) cad.Entities.Add(new Line { StartPoint = corners[i], EndPoint = corners[(i + 1) % 4], Layer = layer });
            string file = Path.Combine(folder, name); DxfWriter.Write(file, cad); return file;
        }

        test("an unreadable first file does not block the other drawings chosen with it", () =>
        {
            string bad = Path.Combine(root, "0 손상 도면.dxf"); File.WriteAllText(bad, "이 파일은 도면이 아닙니다");
            var files = new[] { bad, ImportFlowFixtures.Plan(root, "1층.dxf"), ImportFlowFixtures.Plan(root, "2층.dxf") };
            string store = Store();
            var window = new MainWindow(null) { headlessTesting = true, importSettingsStore = store };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                ImportSettingsStore.Save(new ImportSettings { CadLongEdge = 600 }, store);
                int dialogs = 0;
                // The user sees the error and imports the others.
                window.importDialogRunner = dialog => { dialogs++; ImportFlowFixtures.Pump(dialog.PrepareAsync()); return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths(files);
                Check(dialogs == 1 && window.tabs.Select(t => t.Document.Name).SequenceEqual(["1층", "2층"]) && window.status.Text.Contains("0 손상 도면.dxf"),
                    $"With the dialog, the good files did not open after the first failed: {dialogs} dialogs, {window.tabs.Count} tabs · {window.status.Text}");
                // One dialog per file: the unreadable one is skipped and the next dialog opens.
                dialogs = 0;
                window.importDialogRunner = dialog => { dialogs++; dialog.ApplyAllForTest = false; ImportFlowFixtures.Pump(dialog.PrepareAsync()); return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths(files);
                Check(dialogs == 3 && window.tabs.Count == 4, $"Skipping the unreadable file did not continue with the next dialog: {dialogs} dialogs, {window.tabs.Count} tabs");
                // Quick path: the others open with the remembered settings and the failure is reported.
                ImportSettingsStore.Save(new ImportSettings { CadLongEdge = 600, CadSkipDialog = true }, store);
                dialogs = 0;
                window.importDialogRunner = dialog => { dialogs++; return ImportFlowFixtures.Pump(dialog.ImportAsync()); };
                window.OpenPaths(files);
                Check(dialogs == 1 && window.tabs.Count == 6 && window.status.Text.Contains("0 손상 도면.dxf"),
                    $"The quick path dropped the good files after the first failed: {dialogs} dialogs, {window.tabs.Count} tabs · {window.status.Text}");
                // When nothing could be read, the quick path still brings the settings back.
                bool direct = true;
                window.importDialogRunner = dialog => { bool done = ImportFlowFixtures.Pump(dialog.ImportAsync()); direct = dialog.Direct; return done; };
                window.OpenPaths([bad]);
                Check(!direct && window.tabs.Count == 6, "A quick import that read nothing did not bring the settings back");
            }
            finally
            {
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                SynchronizationContext.SetSynchronizationContext(previous); window.StopRenderingForShutdown();
            }
        });

        test("drawings placed together are each sized against the document's own artboard", () =>
        {
            string store = Store();
            ImportSettingsStore.Save(new ImportSettings { CadLongEdge = 600, CadLayout = ImportSettings.ModelSpace, CadSkipDialog = true }, store);
            string landscapeFile = Frame(root, "가로 평면.dxf", 240, 170), portraitFile = Frame(root, "세로 단면.dxf", 120, 240);
            var landscape = ImportFlowFixtures.Read(landscapeFile, ImportSettingsStore.Load(store).Options(landscapeFile)).Document;
            var sheet = landscape.Artboards.Single();
            var target = new Document { Width = (int)sheet.Width, Height = (int)sheet.Height, Name = "시트" };
            target.Add(new Layer { Name = "배경", Pixels = Raster.Solid(target.Width, target.Height, Colors.White) });
            target.Artboards.Add(new Artboard(Guid.NewGuid(), "1층", 0, 0, sheet.Width, sheet.Height));
            var window = new MainWindow(null) { headlessTesting = true, importSettingsStore = store };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                window.importDialogRunner = dialog => ImportFlowFixtures.Pump(dialog.ImportAsync());
                window.AddTab(target, null);
                window.ImportFiles([portraitFile, landscapeFile]);
                var boards = window.doc.Artboards;
                Check(boards.Count == 3, $"Expected two new artboards, got {boards.Count - 1}");
                Check(boards[1].Width <= sheet.Width && boards[1].Height <= sheet.Height && Math.Abs(boards[1].Height - sheet.Height) < 1,
                    $"The portrait drawing was not fitted to the document's artboard: {boards[1].Bounds} for {sheet.Bounds}");
                Check(Math.Abs(boards[2].Width - sheet.Width) < 1 && Math.Abs(boards[2].Height - sheet.Height) < 1 && boards[2].Y == sheet.Y,
                    $"The second drawing was sized against the artboard added for the first: {boards[2].Bounds} for {sheet.Bounds}");
            }
            finally
            {
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                SynchronizationContext.SetSynchronizationContext(previous); window.StopRenderingForShutdown();
            }
        });

        test("photos dropped or moved beside hatch materials on the photo tab stay out of the drawing", () =>
        {
            string file = ImportFlowFixtures.Plan(root, "사진 탭 평면.dxf");
            var doc = ImportFlowFixtures.Read(file, new ImportSettings { CadLongEdge = 600 }.Options(file)).Document;
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material); var folder = doc.Layers.Single(l => l.ParentId == null);
            var second = material.Snapshot(); second.Id = Guid.NewGuid(); second.Name = "재질 · 두 번째";
            doc.Layers.Insert(doc.Layers.IndexOf(material) + 1, second); doc.Validate();
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(doc, null);
                IReadOnlyList<LayerListEntry> Entries() => (IReadOnlyList<LayerListEntry>)window.layerList.ItemsSource;
                var photo = new Layer { Name = "현장 사진", Pixels = Raster.Solid(40, 40, Colors.SkyBlue) };
                window.Edit("사진 추가", () => window.doc.Add(photo));
                int Index(Guid id) => window.doc.Layers.FindIndex(l => l.Id == id);
                Layer Get(Guid id) => window.doc.Layers.Single(l => l.Id == id);
                // The linework directly above the two materials; the photo tab does not list it.
                var linework = window.doc.Layers.Skip(Math.Max(Index(material.Id), Index(second.Id)) + 1).First(l => l.ParentId == folder.Id).Id;
                int FirstLinework() => Index(linework);

                window.ReorderDrop(photo.Id, material.Id, true, false);
                Check(Get(photo.Id).ParentId == null && DrawingLayers.Categories(window.doc)[photo.Id] == LayerCategory.Photo && Index(photo.Id) == Index(folder.Id) + 1,
                    "A photo dropped above a hatch material moved into the drawing folder");
                window.ReorderDrop(photo.Id, second.Id, false, false);
                Check(Get(photo.Id).ParentId == null && Index(photo.Id) == Index(folder.Id) - 1, "A photo dropped below a hatch material did not go below the drawing");
                window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                Check(Entries().Any(e => e.Layer.Id == photo.Id), "The dropped photo disappeared from the photo tab");
                window.ReorderDrop(second.Id, material.Id, false, false);
                Check(Get(second.Id).ParentId == folder.Id && Index(second.Id) == Index(material.Id) - 1, "Materials of one drawing could not be reordered among themselves");

                // Keyboard order: a material steps among the materials the photo tab lists, below the linework.
                window.SelectLayer(second.Id); window.Reorder(1);
                Check(Index(second.Id) == Index(material.Id) + 1 && Index(second.Id) < FirstLinework(), "Bring forward did not swap the two materials");
                int before = Index(second.Id); window.Reorder(1);
                Check(Index(second.Id) == before && Index(second.Id) < FirstLinework() && Get(second.Id).ParentId == folder.Id, "Bring forward moved the top material above the linework");
                window.Reorder(-1); Check(Index(second.Id) == Index(material.Id) - 1, "Send backward did not swap the two materials");

                // The photo tab says when the drawing folder hides or locks a material.
                window.Edit("도면 숨기기", () => Get(folder.Id).Visible = false);
                window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                var row = Entries().Single(e => e.Layer.Id == material.Id);
                Check(row.Description!.Contains("도면 숨김") && !row.Description.Contains("도면 잠김") && row.Layer.Visible, "A material hidden by its drawing folder did not say so: " + row.Description);
                window.Edit("도면 잠금", () => { Get(folder.Id).Visible = true; Get(folder.Id).Locked = true; });
                window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                row = Entries().Single(e => e.Layer.Id == material.Id);
                Check(row.Description!.Contains("도면 잠김") && !row.Description.Contains("도면 숨김"), "A material locked by its drawing folder did not say so: " + row.Description);
                window.Edit("도면 잠금 해제", () => Get(folder.Id).Locked = false);
                window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                Check(Entries().Single(e => e.Layer.Id == material.Id).Description is { } plain && !plain.Contains("도면 숨김") && !plain.Contains("도면 잠김"), "The parent state stayed after it was cleared");
            }
            finally { foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document); window.StopRenderingForShutdown(); }
        });
    }
}
