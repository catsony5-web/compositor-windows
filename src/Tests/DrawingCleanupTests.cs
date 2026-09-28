using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using Color = System.Windows.Media.Color;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunDrawingCleanupTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "drawing-cleanup"); Directory.CreateDirectory(root);

        test("drawing cleanup classifies plan layers and recommends hatch materials", () =>
        {
            foreach (var (name, role) in new (string, DrawingRole)[] {
                ("A-WALL", DrawingRole.Structure), ("A-WALL-FULL", DrawingRole.Structure), ("벽체", DrawingRole.Structure), ("S-COLS", DrawingRole.Structure), ("CONC", DrawingRole.Structure),
                ("A-DOOR", DrawingRole.Opening), ("A-GLAZ", DrawingRole.Opening), ("창호", DrawingRole.Opening),
                ("A-FURN-SOFA", DrawingRole.Furniture), ("가구", DrawingRole.Furniture), ("P-SANI", DrawingRole.Furniture),
                ("A-ANNO-DIMS", DrawingRole.Annotation), ("DIM", DrawingRole.Annotation), ("문자", DrawingRole.Annotation),
                ("A-HATCH", DrawingRole.Hatch), ("0", DrawingRole.Other), ("PLAN|A-WALL", DrawingRole.Structure) })
                Check(DrawingCleanup.Classify(name) == role, $"{name} was not classified as {role}");
            Check(DrawingCleanup.Weight(DrawingRole.Structure) > DrawingCleanup.Weight(DrawingRole.Opening) && DrawingCleanup.Weight(DrawingRole.Opening) > DrawingCleanup.Weight(DrawingRole.Furniture), "Line weights are not ordered");
            static double Luma(Color c) => .2126 * c.R + .7152 * c.G + .0722 * c.B;
            Check(Luma(DrawingCleanup.Tone(DrawingRole.Structure)) < Luma(DrawingCleanup.Tone(DrawingRole.Furniture)), "Structure is not darker than furniture");
            foreach (var (pattern, layer, role, kind) in new (string, string, DrawingRole, MaterialKind)[] {
                ("AR-CONC", "A-FLOR", DrawingRole.Other, MaterialKind.Concrete), ("ANSI31", "A-WALL", DrawingRole.Structure, MaterialKind.Concrete),
                ("ANSI31", "A-FLOR", DrawingRole.Other, MaterialKind.Diagonal), ("AR-B816", "A-WALL", DrawingRole.Structure, MaterialKind.Brick),
                ("AR-PARQ1", "A-FLOR", DrawingRole.Other, MaterialKind.Wood), ("SOLID", "A-WALL", DrawingRole.Structure, MaterialKind.Solid),
                ("", "석재 마감", DrawingRole.Hatch, MaterialKind.Stone), ("EARTH", "C-SITE", DrawingRole.Other, MaterialKind.Gravel) })
                Check(DrawingCleanup.Suggest(pattern, layer, role) == kind, $"{pattern}/{layer} did not suggest {kind}");
            var assets = Enum.GetValues<MaterialKind>().Select(MaterialPresets.Create).ToArray();
            Check(assets.Select(a => a.Id).Distinct().Count() == assets.Length && assets.All(a => a.Pixels.Width == MaterialPresets.Size && a.Tileable), "Preset materials are not distinct tileable swatches");
            Check(MaterialPresets.Create(MaterialKind.Wood).Id == assets[(int)MaterialKind.Wood].Id, "Preset ids are not stable across imports");
            Check(assets.Where(a => a.Source != "morupixel:preset/solid").All(a => a.Pixels.Data.Chunk(4).Select(p => p[0]).Distinct().Count() > 1), "A patterned preset rendered as a flat color: " + string.Join(", ", assets.Select(a => a.Source + "=" + a.Pixels.Data.Chunk(4).Select(p => p[0]).Distinct().Count() + "/a" + a.Pixels.Data[3])));
        });

        string Plan(string name)
        {
            var cad = new CadDocument();
            var wall = new ACadSharp.Tables.Layer("A-WALL"); var furniture = new ACadSharp.Tables.Layer("A-FURN"); var hatches = new ACadSharp.Tables.Layer("A-HATCH");
            foreach (var layer in new[] { wall, furniture, hatches }) cad.Layers.Add(layer);
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 60, 0), EndPoint = new XYZ(200, 60, 0), Layer = furniture });
            var hatch = new Hatch { Pattern = new HatchPattern("AR-CONC"), Layer = hatches };
            var path = new Hatch.BoundaryPath(); path.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 100, 0), new XYZ(180, 100, 0), new XYZ(180, 180, 0), new XYZ(20, 180, 0) }, true));
            hatch.Paths.Add(path); cad.Entities.Add(hatch);
            string file = Path.Combine(root, name); DxfWriter.Write(file, cad); return file;
        }
        CompatibilityResult Read(string file, CadCleanup? cleanup) => CompatibilityImport.ReadAsync(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", RetainVectors: false,
            CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: cleanup)).GetAwaiter().GetResult();
        static (int Ink, double Darkness) Ink(Layer layer)
        {
            int count = 0; double dark = 0;
            for (int i = 0; i < layer.Pixels.Data.Length; i += 4)
                if (layer.Pixels.Data[i + 3] > 40) { count++; dark += 255 - (layer.Pixels.Data[i] + layer.Pixels.Data[i + 1] + layer.Pixels.Data[i + 2]) / 3d; }
            return (count, count == 0 ? 0 : dark / count);
        }

        test("drawing cleanup import weights walls over furniture and fills hatches with a recommended material", () =>
        {
            string file = Plan("plan.dxf");
            var info = CadCompatibility.InspectLayers(file);
            Check(info.Layers.Any(l => l.Name == "A-WALL"), "Inspected: " + string.Join(", ", info.Layers.Select(l => $"{l.Name}:{l.Role}:{l.Objects}:{l.Hatches}")) + " materials " + string.Join(",", info.HatchMaterials));
            Check(info.Layers.Single(l => l.Name == "A-WALL").Role == DrawingRole.Structure && info.Layers.Single(l => l.Name == "A-HATCH").Hatches == 1
                && info.HatchMaterials.GetValueOrDefault(MaterialKind.Concrete) == 1, "Layer inspection did not report roles and hatch materials");
            var plain = Read(file, null).Document;
            Check(plain.Layers.All(l => l.Kind != LayerKind.Material) && plain.MaterialRegions.Count == 0, "Import without cleanup changed hatches");
            var result = Read(file, new CadCleanup());
            var doc = result.Document;
            Check(doc.Layers.Any(l => l.Name == "A-WALL"), "Layers: " + string.Join(", ", doc.Layers.Select(l => l.Name + ":" + l.Kind)));
            var wall = Ink(doc.Layers.Single(l => l.Name == "A-WALL")); var furniture = Ink(doc.Layers.Single(l => l.Name == "A-FURN"));
            Check(wall.Ink > furniture.Ink * 1.5 && wall.Darkness > furniture.Darkness + 40, $"Wall ({wall}) is not heavier and darker than furniture ({furniture})");
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material);
            Check(material.Material!.Asset.Source == "morupixel:preset/concrete" && doc.Layers.IndexOf(material) == 1 && material.Blend == BlendMode.Multiply, "The hatch was not filled with concrete below the linework");
            Check(doc.MaterialRegions.Count == 1 && doc.Materials.Count == 1 && result.Warnings.Any(w => w.Contains("콘크리트")), "Material region, library entry or notice is missing");
            doc.Validate();
            var overridden = Read(file, new CadCleanup(Roles: new Dictionary<string, DrawingRole> { ["A-WALL"] = DrawingRole.Furniture })).Document;
            Check(Ink(overridden.Layers.Single(l => l.Name == "A-WALL")).Ink < wall.Ink, "A role override did not change the line weight");
            var kept = Read(file, new CadCleanup(Hatches: HatchTreatment.Keep)).Document;
            Check(kept.Layers.All(l => l.Kind != LayerKind.Material), "Keeping hatches still added a material");
        });

        test("AI connection reports CAD layer roles and applies cleanup settings with role overrides", () =>
        {
            string file = Plan("ai-plan.dxf");
            var window = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                System.Text.Json.Nodes.JsonObject Call(string command, System.Text.Json.Nodes.JsonObject args)
                {
                    var task = window.ExecuteAutomationAsync(new System.Text.Json.Nodes.JsonObject { ["command"] = command, ["arguments"] = args });
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                    if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame);
                    return task.GetAwaiter().GetResult();
                }
                var info = Call("inspect_file", new() { ["path"] = file });
                var layers = info["result"]!["layers"]!.AsArray();
                Check(layers.Any(l => l!["layer"]!.GetValue<string>() == "A-WALL" && l["role"]!.GetValue<string>() == "structure")
                    && info["result"]!["hatchMaterials"]!["concrete"]!.GetValue<int>() == 1, "inspect_file must list roles and hatch materials: " + info.ToJsonString());
                var bad = Call("open_document", new() { ["path"] = file, ["cadHatches"] = "keep" });
                Check(bad["ok"]!.GetValue<bool>() == false && bad["error"]!["code"]!.GetValue<string>() == "invalid_arguments", "Cleanup options without cadCleanup must be rejected");
                var opened = Call("open_document", new() { ["path"] = file, ["cadStructure"] = "layers", ["cadLongEdge"] = 600, ["cadCleanup"] = true });
                Check(opened["ok"]!.GetValue<bool>(), "Cleanup import failed: " + opened.ToJsonString());
                var cleaned = window.doc; var wall = Ink(cleaned.Layers.First(l => l.Name == "A-WALL"));
                Check(cleaned.Layers.Any(l => l.Kind == LayerKind.Material), "cadCleanup must fill the hatch with a material");
                var overridden = Call("open_document", new() { ["path"] = file, ["cadStructure"] = "layers", ["cadLongEdge"] = 600, ["cadCleanup"] = true, ["cadHatches"] = "keep",
                    ["cadLayerRoles"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["layer"] = "A-WALL", ["role"] = "furniture" }) });
                Check(overridden["ok"]!.GetValue<bool>(), "Override import failed: " + overridden.ToJsonString());
                Check(Ink(window.doc.Layers.First(l => l.Name == "A-WALL")).Ink < wall.Ink && window.doc.Layers.All(l => l.Kind != LayerKind.Material), "Role override and kept hatches were not applied");
                var badRole = Call("open_document", new() { ["path"] = file, ["cadCleanup"] = true, ["cadLayerRoles"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["layer"] = "A-WALL", ["role"] = "wall" }) });
                Check(badRole["ok"]!.GetValue<bool>() == false, "Unknown role must be rejected");
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); window.StopRenderingForShutdown(); }
        });
        test("drawing cleanup applies the user's material image to every hatch", () =>
        {
            string file = Plan("plan-image.dxf"), image = Path.Combine(root, "오크 마루.png");
            var pixels = Raster.Solid(32, 16, Colors.SaddleBrown); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels.Bitmap()));
            using (var output = File.Create(image)) encoder.Save(output);
            var doc = Read(file, new CadCleanup(Hatches: HatchTreatment.Image, MaterialImage: image)).Document;
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material).Material!;
            Check(material.Asset.Name == "오크 마루" && material.Asset.Pixels.Width == 32 && Math.Abs(material.TileHeight - material.TileWidth / 2) < 1e-9, "The user's image was not applied with its proportions");
            bool missing = false;
            try { Read(file, new CadCleanup(Hatches: HatchTreatment.Image, MaterialImage: Path.Combine(root, "없음.png"))); } catch (FileNotFoundException) { missing = true; }
            Check(missing, "A missing material image was not reported");
        });

        test("layer list shift selects a range, ctrl toggles and alt isolates in three steps", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8, Name = "레이어" };
                for (int i = 0; i < 4; i++) document.Add(new Layer { Name = "레이어 " + i, Pixels = Raster.Solid(8, 8, Colors.Red) });
                document.Layers[3].Visible = false;
                w.AddTab(document, null);
                var entries = (IReadOnlyList<LayerListEntry>)w.layerList.ItemsSource;
                Check(entries.Count == 4, "The layer list did not show four rows");
                w.ClickLayerRow(entries[0], ModifierKeys.None); w.ClickLayerRow(entries[2], ModifierKeys.Shift);
                var range = entries.Take(3).Select(e => e.Layer.Id).ToHashSet();
                Check(w.selectedLayers.SetEquals(range) && w.doc.ActiveId == entries[2].Layer.Id, "Shift+click did not select the range");
                entries = (IReadOnlyList<LayerListEntry>)w.layerList.ItemsSource;
                w.ClickLayerRow(entries[1], ModifierKeys.Control);
                Check(!w.selectedLayers.Contains(entries[1].Layer.Id) && w.selectedLayers.Count == 2, "Ctrl+click did not remove a row");
                w.ClickLayerRow(entries[1], ModifierKeys.Control);
                Check(w.selectedLayers.Contains(entries[1].Layer.Id) && w.selectedLayers.Count == 3, "Ctrl+click did not add a row back");
                var target = w.doc.Layers[1].Id; bool[] Visible() => w.doc.Layers.Select(l => l.Visible).ToArray();
                var original = Visible();
                Check(w.IsolateLayers([target]) == 1 && Visible().SequenceEqual([false, true, false, false]), "Alt+click did not show only the layer");
                Check(w.IsolateLayers([target]) == 2 && Visible().SequenceEqual([true, false, true, false]), "Second Alt+click did not hide only the layer");
                Check(w.IsolateLayers([target]) == 0 && Visible().SequenceEqual(original), "Third Alt+click did not restore visibility");
                w.Undo(); Check(Visible().SequenceEqual([true, false, true, false]), "Isolation steps are not separate undo steps");
                w.Undo(); w.Undo(); Check(Visible().SequenceEqual(original), "Undo did not return to the original visibility");
                w.doc.Layers[0].Visible = false;
                Check(w.IsolateLayers([target]) == 1, "A visibility change did not start a fresh isolation cycle");
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }
}
