using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunWorkspaceUpgradeTests(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static MainWindow Window()
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.doc = new Document { Width = 4, Height = 4 };
            foreach (var color in new[] { Colors.Red, Colors.Blue, Colors.Green }) window.doc.Add(new Layer { Name = color.ToString(), Pixels = Raster.Solid(4, 4, color) });
            window.history.Reset(window.doc); window.InitializeWorkspace(); window.Refresh(); return window;
        }
        test("screen presets lead and all A series sizes fit at 150 DPI", () =>
        {
            var presets = NewDocumentDialog.Presets(2560, 1440);
            Check(presets[0].Width == 2560 && presets[0].Height == 1440 && !presets[0].Paper, "Current monitor must lead");
            foreach (var preset in presets.Where(p => p.Paper))
            {
                var size = NewDocumentDialog.Dimensions(preset.Width.ToString(), preset.Height.ToString(), true, "150");
                Check(Math.Abs(size.Width / size.Dpi * 25.4 - preset.Width) < .1 && Math.Abs(size.Height / size.Dpi * 25.4 - preset.Height) < .1, "Print dimensions changed");
            }
            var a4 = NewDocumentDialog.Dimensions("210", "297", true, "300"); Check(a4.Width == 2480 && a4.Height == 3508, "A4 300 DPI incorrect");
        });
        test("A2 at 300 DPI is accepted while oversized resolution and invalid DPI are rejected before allocation", () =>
        {
            var a2 = NewDocumentDialog.Dimensions("420", "594", true, "300");
            Check(a2.Width == 4961 && a2.Height == 7016, "A2 300 DPI dimensions changed");
            foreach (var args in new[] { ("420", "594", "1200"), ("210", "297", "0"), ("210", "297", "NaN") })
            { bool rejected = false; try { NewDocumentDialog.Dimensions(args.Item1, args.Item2, true, args.Item3); } catch { rejected = true; } Check(rejected, "Unsafe dimensions accepted"); }
        });
        test("document DPI survives project snapshot and RGB image export", () =>
        {
            var document = NewDocumentDialog.CreateDocument("print", "8", "8", 1, false, "150");
            string project = Path.Combine(directory, "print-dpi.moruproj"); ProjectStore.Save(document, project);
            Check(ProjectStore.Load(project).Dpi == 150 && document.Snapshot().Dpi == 150, "DPI lost in project");
            using var encoded = new MemoryStream(); ImportExport.Write(document.Active!.Pixels, encoded, "tif", 95, document.Dpi); encoded.Position = 0;
            var decoded = BitmapDecoder.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(Math.Abs(decoded.Frames[0].DpiX - 150) < .1, "DPI lost in RGB output");
        });
        test("DPI changes participate in undo and document dirtiness", () =>
        {
            var window = Window(); window.Edit("DPI", () => window.doc.Dpi = 300);
            Check(window.history.Dirty(window.doc), "DPI edit not tracked"); window.Undo(); Check(window.doc.Dpi == 96, "DPI undo failed"); window.Redo(); Check(window.doc.Dpi == 300, "DPI redo failed");
        });
        test("layer insertion above below and undo change actual composite order", () =>
        {
            var window = Window(); var ids = window.doc.Layers.Select(l => l.Id).ToArray();
            window.ReorderDrop(ids[0], ids[2], true, false);
            Check(window.doc.Layers.Last().Id == ids[0] && Imaging.Render(window.doc).Data[2] == 255, "Top insertion failed");
            window.Undo(); Check(window.doc.Layers.Select(l => l.Id).SequenceEqual(ids), "Reorder undo failed");
            window.ReorderDrop(ids[2], ids[0], false, false); Check(window.doc.Layers.First().Id == ids[2], "Bottom insertion failed");
        });
        test("no-op layer drops preserve redo", () =>
        {
            var window = Window(); var ids = window.doc.Layers.Select(l => l.Id).ToArray();
            window.ReorderDrop(ids[0], ids[2], true, false); window.Undo();
            window.ReorderDrop(ids[1], ids[0], true, false); window.ReorderDrop(ids[0], ids[0]);
            Check(window.history.CanRedo && !window.history.Dirty(window.doc), "No-op destroyed redo");
        });
        test("group drops reject cycles and locked destinations without mutation", () =>
        {
            var window = Window(); var group = new Layer { Kind = LayerKind.Group, Name = "group", Pixels = new Raster(1, 1) }; window.doc.Add(group);
            var child = window.doc.Layers[0]; child.ParentId = group.Id; var original = window.doc.Snapshot();
            bool cycle = false; try { window.ReorderDrop(group.Id, child.Id, true, false); } catch (InvalidOperationException) { cycle = true; }
            Check(cycle && SameDocument(original, window.doc), "Group cycle changed document");
            group.Locked = true; var moving = window.doc.Layers[1]; bool locked = false;
            try { window.ReorderDrop(moving.Id, group.Id); } catch (InvalidOperationException) { locked = true; }
            Check(locked && moving.ParentId == null, "Locked group accepted layer");
        });
        test("properties brush and layers dock independently without losing pixels or redo", () =>
        {
            var window = Window(); window.NudgeSelected(new Vector(1, 0)); window.Undo(); var snapshot = window.doc.Snapshot();
            foreach (var pane in new[] { window.studioPanes[1], window.studioPanes[3], window.layersPane! }) window.MovePane(pane, "left");
            Check(window.leftPanels.Children.Count == 3 && window.leftPanelColumn.Width.Value > 0, "Independent dock failed");
            window.studioDiameter!.SetValue(77, true); Check(window.brushSize == 77, "Detached brush lost binding");
            window.ResetPanelLayout();
            Check(window.leftPanels.Children.Count == 0 && ReferenceEquals(window.layersSlot.Child, window.layersPane) && window.history.CanRedo && SameDocument(snapshot, window.doc), "Dock reset changed editor state");
        });
        test("RGB CMYK preview toggle preserves source raster undo and redo", () =>
        {
            var window = Window(); window.NudgeSelected(new Vector(1, 0)); window.Undo(); var snapshot = window.doc.Snapshot();
            for (int i = 0; i < 3; i++) { window.SetProof(true); Check(window.cmykProof, "CMYK toggle failed"); window.SetProof(false); }
            Check(SameDocument(snapshot, window.doc) && window.history.CanRedo && !window.history.Dirty(window.doc), "Proof altered source");
        });
        test("invalid CMYK preview profile cannot turn on proof", () =>
        {
            var window = Window(); window.proofProfile = Path.Combine(directory, "missing.icc"); bool rejected = false;
            try { window.SetProof(true); } catch (FileNotFoundException) { rejected = true; }
            Check(rejected && !window.cmykProof, "Invalid profile enabled preview");
        });
    }
}
