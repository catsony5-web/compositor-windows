using System.IO;
using System.Windows;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Point = System.Windows.Point;

namespace Compositor.Windows;

public static class CadObjectImportTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "cad-objects"); Directory.CreateDirectory(root);
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        string Write(CadDocument cad, string name)
        {
            string path = Path.Combine(root, name);
            if (name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) DxfWriter.Write(path, cad); else DwgWriter.Write(path, cad);
            return path;
        }
        CompatibilityResult Read(string path, CadImportStructure structure = CadImportStructure.Objects, string? layout = "*Model_Space", bool vectors = true)
            => CompatibilityImport.ReadAsync(path, new(CadLongEdge: 600, SeparateLayers: true, CadLayout: layout, RetainVectors: vectors, CadStructure: structure)).GetAwaiter().GetResult();
        static Layer[] Objects(Document doc) => doc.Layers.Where(l => l.ParentId != null && l.Kind != LayerKind.Group).ToArray();
        static Line Line(double x1, double y1, double x2, double y2) => new() { StartPoint = new XYZ(x1, y1, 0), EndPoint = new XYZ(x2, y2, 0) };
        static Point InkPoint(Layer layer)
        {
            Point? point = null; double closest = double.PositiveInfinity;
            for (int y = 0; y < layer.Pixels.Height; y++) for (int x = 0; x < layer.Pixels.Width; x++)
            {
                if (layer.Pixels.Data[(y * layer.Pixels.Width + x) * 4 + 3] <= 100) continue;
                double distance = Math.Pow(x + .5 - layer.Pixels.Width / 2d, 2) + Math.Pow(y + .5 - layer.Pixels.Height / 2d, 2);
                if (distance < closest) { closest = distance; point = new(layer.X + x + .5, layer.Y + y + .5); }
            }
            return point ?? throw new Exception("Object preview contains no visible pixels");
        }
        void SameImage(Document a, Document b)
        {
            Assert(a.Width == b.Width && a.Height == b.Height, "Import structure changed document bounds");
            var expected = Imaging.Render(a); var actual = Imaging.Render(b);
            Assert(expected.Data.Zip(actual.Data, (x, y) => Math.Abs(x - y)).Average() < .5, "Object decomposition changed the rendered drawing");
        }

        test("CAD object import keeps a closed rectangle whole and touching LINE entities separate in DWG and DXF", () =>
        {
            foreach (string extension in new[] { ".dwg", ".dxf" })
            {
                var cad = new CadDocument(); var layer = new ACadSharp.Tables.Layer("벽체"); cad.Layers.Add(layer);
                cad.Entities.Add(new LwPolyline(new XY[] { new(0, 0), new(100, 0), new(100, 100), new(0, 100) }) { IsClosed = true, Layer = layer });
                foreach (var line in new[] { Line(150, 0, 250, 0), Line(250, 0, 250, 100), Line(250, 100, 150, 100), Line(150, 100, 150, 0) }) { line.Layer = layer; cad.Entities.Add(line); }
                string path = Write(cad, "rectangle-and-lines" + extension); var doc = Read(path).Document; doc.Validate();
                var objects = Objects(doc); Assert(objects.Length == 5, "A closed polyline must remain one object while its four independent neighboring LINEs stay separate");
                Assert(objects.Count(l => l.Name.StartsWith("닫힌 폴리라인")) == 1 && objects.Count(l => l.Name.StartsWith("선 ")) == 4, "Source entity kinds were lost");
                var folder = doc.Layers.Single(l => l.Kind == LayerKind.Group); Assert(folder.Name == "벽체" && objects.All(l => l.ParentId == folder.Id), "Source CAD layer must contain object children");
                Assert(objects.All(l => l.Kind == LayerKind.Vector && l.Vector != null), "Editable retained vectors were flattened");
                Assert(objects.All(l => l.Pixels.Width < doc.Width || l.Pixels.Height < doc.Height), "Individual objects should retain tight previews");
                SameImage(Read(path, CadImportStructure.Combined).Document, doc);
                foreach (var item in objects.Where(l => l.Name.StartsWith("선 "))) Assert(LayerPicking.PickNear(doc, InkPoint(item), 1)?.Id == item.Id, "Independent source lines cannot be selected separately");
            }
        });
        test("CAD structure choice overrides legacy SeparateLayers without changing legacy callers", () =>
        {
            var cad = new CadDocument(); cad.Entities.Add(Line(0, 0, 100, 0)); cad.Entities.Add(Line(0, 20, 100, 20));
            string path = Write(cad, "structure.dwg");
            var combined = Read(path, CadImportStructure.Combined).Document; var layered = Read(path, CadImportStructure.Layers).Document;
            Assert(combined.Layers.Count == 2 && layered.Layers.Count == 2, "Legacy structures changed their background-plus-content shape");
            var legacy = CompatibilityImport.ReadAsync(path, new(CadLongEdge: 600, SeparateLayers: true, CadLayout: "*Model_Space")).GetAwaiter().GetResult().Document;
            Assert(legacy.Layers.Select(l => l.Name).SequenceEqual(layered.Layers.Select(l => l.Name)), "Omitted CadStructure must preserve legacy SeparateLayers behavior");
            var raster = Read(path, vectors: false).Document;
            Assert(Objects(raster).Length == 2 && Objects(raster).All(l => l.Kind == LayerKind.Raster), "RetainVectors=false must still respect object granularity");
        });
        test("CAD object can move beyond original source-layer bounds and survive native save and grouped placement", () =>
        {
            var cad = new CadDocument(); cad.Entities.Add(Line(0, 0, 0, 100)); string path = Write(cad, "move-line.dwg");
            var doc = Read(path).Document; var item = Objects(doc).Single(); var oldPoint = InkPoint(item); item.X += 25;
            var movedPoint = new Point(oldPoint.X + 25, oldPoint.Y);
            Assert(LayerPicking.Pick(doc, movedPoint)?.Id == item.Id, "Moving outside the original layer crop made the line unselectable");
            var rendered = Imaging.Render(doc); int offset = ((int)movedPoint.Y * doc.Width + (int)movedPoint.X) * 4;
            Assert(rendered.Data[offset] < 200, "The source group clipped the moved line");
            string project = Path.Combine(root, "moved.moruproj"); ProjectStore.Save(doc, project); var reopened = ProjectStore.Load(project); SameImage(doc, reopened);
            Assert(Objects(reopened).Single().ParentId == reopened.Layers.Single(l => l.Kind == LayerKind.Group).Id, "Native save flattened source groups");
            var target = new Document { Width = doc.Width, Height = doc.Height };
            foreach (var layer in CompatibilityImport.PlacementLayers(reopened, target.Width, target.Height)) target.Add(layer);
            target.Validate(); SameImage(reopened, target);
            Assert(target.Layers.Count(l => l.Kind == LayerKind.Group) == 2, "Placement must retain a source layer group inside the containing file group");
        });
        test("CAD repeated nested block instances retain distinct selectable child objects", () =>
        {
            var cad = new CadDocument(); var source = new BlockRecord("DETAIL"); source.Entities.Add(Line(0, 0, 20, 0)); source.Entities.Add(Line(0, 20, 20, 20)); cad.BlockRecords.Add(source);
            var nested = new BlockRecord("NESTED"); nested.Entities.Add(new Insert(source) { InsertPoint = new XYZ(10, 10, 0) }); cad.BlockRecords.Add(nested);
            cad.Entities.Add(new Insert(nested)); cad.Entities.Add(new Insert(nested) { InsertPoint = new XYZ(60, 0, 0), XScale = 2, YScale = 2 });
            string path = Write(cad, "instances.dwg"); var doc = Read(path).Document; var objects = Objects(doc);
            Assert(objects.Length == 4 && objects.Select(l => l.Name).Distinct().Count() == 4, "Repeated block source handles incorrectly merged separate instances");
            foreach (var item in objects) Assert(LayerPicking.PickNear(doc, InkPoint(item), 1)?.Id == item.Id, "Instance geometry or selection coordinates changed");
            SameImage(Read(path, CadImportStructure.Combined).Document, doc);
        });
        test("CAD dimension block remains one object while preserving all its graphics", () =>
        {
            var cad = new CadDocument(); var block = new BlockRecord("*D1"); block.Entities.Add(Line(0, 0, 100, 0)); block.Entities.Add(Line(0, 10, 100, 10)); cad.BlockRecords.Add(block);
            cad.Entities.Add(new DimensionAligned(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Block = block, DefinitionPoint = new XYZ(50, 10, 0) });
            string path = Write(cad, "dimension.dwg"); var doc = Read(path).Document;
            Assert(Objects(doc).Length == 1 && Objects(doc)[0].Name.StartsWith("치수"), "Dimension components should form a single source entity");
            SameImage(Read(path, CadImportStructure.Combined).Document, doc);
        });
        test("CAD repeated viewport appearances remain separate objects with retained clipping", () =>
        {
            var cad = new CadDocument(); cad.Entities.Add(Line(-1000, 0, 1000, 0));
            var paper = cad.BlockRecords.First(b => b.Name == "*Paper_Space");
            paper.Entities.Add(new Viewport { Center = new XYZ(20, 20, 0), Width = 20, Height = 20, ViewHeight = 100 });
            paper.Entities.Add(new Viewport { Center = new XYZ(80, 60, 0), Width = 20, Height = 20, ViewHeight = 100 });
            string path = Write(cad, "viewports.dwg"); var doc = Read(path, layout: null).Document; var objects = Objects(doc);
            Assert(objects.Length == 2 && objects[0].Name != objects[1].Name, "Two viewport instances merged into one source object");
            Assert(objects.All(l => l.Pixels.Width < doc.Width / 2), "Viewport crop was lost while splitting entities");
            foreach (var item in objects) Assert(LayerPicking.PickNear(doc, InkPoint(item), 1)?.Id == item.Id, "Viewport-local transform no longer matches the selectable object");
            SameImage(Read(path, CadImportStructure.Combined, layout: null).Document, doc);
            string project = Path.Combine(root, "viewports.moruproj"); ProjectStore.Save(doc, project); SameImage(doc, ProjectStore.Load(project));
        });
        test("CAD source layer group runs preserve interleaved paint order", () =>
        {
            var cad = new CadDocument(); var a = new ACadSharp.Tables.Layer("A"); var b = new ACadSharp.Tables.Layer("B"); cad.Layers.Add(a); cad.Layers.Add(b);
            Solid Face(ACadSharp.Tables.Layer layer, short color, double inset) => new() { Layer = layer, Color = new ACadSharp.Color(color), FirstCorner = new XYZ(inset, inset, 0), SecondCorner = new XYZ(100 - inset, inset, 0), ThirdCorner = new XYZ(inset, 100 - inset, 0), FourthCorner = new XYZ(100 - inset, 100 - inset, 0) };
            cad.Entities.Add(Face(a, 1, 0)); cad.Entities.Add(Face(b, 5, 10)); cad.Entities.Add(Face(a, 3, 20));
            string path = Write(cad, "paint-order.dwg"); var result = Read(path); var doc = result.Document;
            Assert(doc.Layers.Where(l => l.Kind == LayerKind.Group).Select(l => l.Name).SequenceEqual(new[] { "A", "B", "A · 2" }), "Grouping by layer changed source paint order");
            Assert(result.Warnings.Any(w => w.Contains("겹침 순서")), "Repeated groups need an understandable reason");
            SameImage(Read(path, CadImportStructure.Combined).Document, doc);
            var rendered = Imaging.Render(doc); int center = (300 * rendered.Width + 300) * 4;
            Assert(rendered.Data[center + 1] > 240 && rendered.Data[center] < 10, "Last green source object is no longer on top");
        });
        test("CAD object import exceeds old raster layer count with bounded tight previews", () =>
        {
            var cad = new CadDocument(); for (int i = 0; i < 160; i++) cad.Entities.Add(Line(i % 16 * 10, i / 16 * 10, i % 16 * 10 + 5, i / 16 * 10 + 3));
            string path = Write(cad, "many-objects.dwg"); var doc = Read(path).Document; doc.Validate();
            Assert(Objects(doc).Length == 160, "Object import silently collapsed or dropped entities beyond 128");
            long bytes = doc.Layers.Sum(l => (long)l.Pixels.Data.Length);
            Assert(bytes < (long)doc.Width * doc.Height * 4 * 4, "Tiny source objects each allocated a full canvas preview");
            string project = Path.Combine(root, "many-objects.moruproj"); ProjectStore.Save(doc, project);
            Assert(Objects(ProjectStore.Load(project)).Length == 160, "High object count did not roundtrip");
            bool rejected = false; try { Read(path, vectors: false); } catch (InvalidDataException e) { rejected = e.Message.Contains("128") && e.Message.Contains("벡터"); }
            Assert(rejected, "Pixel-object limit must reject before silently flattening or allocating all previews");
        });
        test("CAD object count overflow offers import alternatives before rendering", () =>
        {
            var cad = new CadDocument(); for (int i = 0; i < Document.MaxNodes; i++) cad.Entities.Add(Line(i, 0, i, 1));
            string path = Write(cad, "too-many-objects.dwg"); bool rejected = false;
            try { Read(path); } catch (InvalidDataException e) { rejected = e.Message.Contains("한도") && e.Message.Contains("레이어별"); }
            Assert(rejected, "Too many source objects must produce an actionable bounded-import error");
        });
    }
}
