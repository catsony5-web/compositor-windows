using System.IO;
using System.Text;
using System.Windows.Media;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace Compositor.Windows;

public static class LayeredCompatibilityTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        string root = Path.Combine(directory, "layered-compatibility"); Directory.CreateDirectory(root);
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        CompatibilityResult Read(string path, CompatibilityOptions? options = null) => CompatibilityImport.ReadAsync(path, options ?? new(Dpi: 72, CadLongEdge: 600, SeparateLayers: true)).GetAwaiter().GetResult();
        void SaveCad(CadDocument cad, string path) => DwgWriter.Write(path, cad);
        static bool Ink(Raster raster) { for (int i = 3; i < raster.Data.Length; i += 4) if (raster.Data[i] > 0) return true; return false; }
        test("PDF OCG preserves Unicode visibility transparency and interleaved paint order", () =>
        {
            string path = Path.Combine(root, "layered.pdf"); WritePdf(path, false);
            var result = Read(path); var flat = Read(path, new(Dpi: 72, PreservePdfLayers: false));
            Assert(result.Document.Layers.Count > 3, "Optional-content runs must remain independent");
            var hidden = result.Document.Layers.Single(l => l.Name == "숨김");
            Assert(!hidden.Visible && Ink(hidden.Pixels), "Hidden layers must retain editable pixels");
            Assert(result.Document.Layers.Any(l => l.Name.StartsWith("벽체")), "Unicode OCG name lost");
            var actual = Imaging.Render(result.Document); var expected = Imaging.Render(flat.Document);
            Assert(actual.Data.Zip(expected.Data, (a, b) => Math.Abs(a - b)).Average() < 1, "Visible composite differs from the original");
            hidden.Visible = true; var revealed = Imaging.Render(result.Document);
            int blue = (20 * revealed.Width + 45) * 4, green = (35 * revealed.Width + 35) * 4;
            Assert(revealed.Data[blue] > 240 && revealed.Data[blue + 2] < 10, "Hidden blue paint was flattened away");
            Assert(revealed.Data[green + 1] > 240 && revealed.Data[green] < 10, "Interleaved front paint changed stacking order");
            string project = Path.Combine(root, "layered.moruproj"); ProjectStore.Save(result.Document, project); var reopened = ProjectStore.Load(project);
            Assert(reopened.Layers.Select(l => l.Name).SequenceEqual(result.Document.Layers.Select(l => l.Name)), "Layer names changed on save");
            Assert(Imaging.Render(reopened).Data.SequenceEqual(revealed.Data), "Layered project did not roundtrip");
        });
        test("PDF optional content inside reused forms keeps clipping and graphics state", () =>
        {
            string path = Path.Combine(root, "forms.pdf"); WritePdf(path, true);
            var result = Read(path); var expected = Imaging.Render(Read(path, new(Dpi: 72, PreservePdfLayers: false)).Document);
            var actual = Imaging.Render(result.Document);
            Assert(actual.Data.Zip(expected.Data, (a, b) => Math.Abs(a - b)).Average() < 1, "Form filtering changed visible pixels");
            Assert(result.Document.Layers.Count(l => l.Name.StartsWith("벽체") && Ink(l.Pixels)) == 2, "Reused form instances must both retain layer content");
        });
        test("Layered placement preserves transforms groups visibility and source document", () =>
        {
            var source = new Document { Width = 80, Height = 60, Name = "Layers" };
            source.Add(new Layer { Name = "Red", Pixels = Raster.Solid(20, 20, Colors.Red), X = 20, Y = 10 });
            source.Add(new Layer { Name = "Hidden", Pixels = Raster.Solid(15, 15, Colors.Blue), X = 10, Visible = false });
            var before = Imaging.Render(source); var target = new Document { Width = 80, Height = 60 };
            foreach (var layer in CompatibilityImport.PlacementLayers(source, target.Width, target.Height)) target.Add(layer);
            target.Validate(); Assert(target.Layers[0].Kind == LayerKind.Group, "Placement must create a containing group");
            Assert(target.Layers.Skip(1).All(l => l.ParentId == target.Layers[0].Id), "Placement hierarchy changed");
            Assert(Imaging.Render(target).Data.SequenceEqual(before.Data), "Placement flattened or moved layers");
            Assert(source.Layers.All(l => l.ParentId == null), "Placement mutated its input");
        });
        test("CAD bound block with stale reference path retains its own geometry", () =>
        {
            var cad = new CadDocument(); var block = new BlockRecord("BOUND");
            block.BlockEntity.XRefPath = "old-reference.dwg"; block.BlockEntity.Flags = BlockTypeFlags.None;
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 30, 0) }); cad.BlockRecords.Add(block);
            cad.Entities.Add(new Insert(block)); string path = Path.Combine(root, "bound.dwg"); SaveCad(cad, path);
            var result = Read(path); Assert(result.Document.Layers.Skip(1).Any(l => Ink(l.Pixels)), "Bound geometry was discarded");
            Assert(!result.Warnings.Any(w => w.Contains("외부참조 파일 없음")), "A bound block was treated as an external file");
        });
        test("CAD relative external references resolve locally and detect missing and cyclic files", () =>
        {
            var external = new CadDocument(); external.Header.ModelSpaceInsertionBase = new XYZ(10, 20, 0);
            external.Entities.Add(new Line { StartPoint = new XYZ(10, 20, 0), EndPoint = new XYZ(110, 50, 0) }); SaveCad(external, Path.Combine(root, "external.dwg"));
            var cad = new CadDocument(); var block = new BlockRecord("LINK"); block.BlockEntity.Flags = BlockTypeFlags.XRef; block.BlockEntity.XRefPath = "external.dwg"; cad.BlockRecords.Add(block);
            cad.Entities.Add(new Insert(block) { InsertPoint = new XYZ(500, 700, 0), XScale = 2, YScale = 2 });
            string path = Path.Combine(root, "reference.dwg"); SaveCad(cad, path); var result = Read(path);
            Assert(result.Warnings.Any(w => w.Contains("외부참조 1개")), "Relative external drawing was not loaded");
            Assert(result.Document.Layers.Any(l => l.Name.StartsWith("LINK|")), "Referenced layer names must retain their namespace");
            block.BlockEntity.XRefPath = "absent.dwg"; SaveCad(cad, path);
            bool missing = false; try { Read(path, new(CadLongEdge: 600, CadLayout: "*Model_Space")); } catch (NotSupportedException e) { missing = e.Message.Contains("외부참조 파일 없음"); }
            Assert(missing, "Missing reference must produce an actionable diagnostic");
            block.BlockEntity.XRefPath = "reference.dwg"; SaveCad(cad, path);
            bool cycle = false; try { Read(path); } catch (NotSupportedException e) { cycle = e.Message.Contains("순환 외부참조"); }
            Assert(cycle, "Circular references must stop without recursion");
        });
        test("CAD mirrored block normal transforms nested insert coordinates before fitting", () =>
        {
            var cad = new CadDocument(); var window = new BlockRecord("WINDOW");
            window.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(80, 80, 0) }); cad.BlockRecords.Add(window);
            var floor = new BlockRecord("FLOOR");
            floor.Entities.Add(new Insert(window) { InsertPoint = new XYZ(1_000_000, 0, 0), Normal = new XYZ(0, 0, -1) }); cad.BlockRecords.Add(floor);
            cad.Entities.Add(new Insert(floor) { InsertPoint = new XYZ(1_000_000, 0, 0) });
            cad.Entities.Add(new Line { StartPoint = new XYZ(-80, 0, 0), EndPoint = new XYZ(0, 80, 0) });
            string path = Path.Combine(root, "mirrored.dwg"); SaveCad(cad, path); var result = Read(path);
            Assert(result.Document.Width == 600 && Math.Abs(result.Document.Height - 600) <= 1, "Mirrored insert created remote geometry and shrank the drawing");
            var pixels = Imaging.Render(result.Document); int dark = 0;
            for (int y = 180; y < 420; y++) for (int x = 180; x < 420; x++) if (pixels.Data[(y * pixels.Width + x) * 4] < 200) dark++;
            Assert(dark > 100, "Mirrored nested geometry was not drawn at the expected position");
        });
        test("CAD paper layout projects clips and freezes model layers in the viewport", () =>
        {
            var cad = new CadDocument(); var red = new ACadSharp.Tables.Layer("RED") { Color = new ACadSharp.Color(1) }; var blue = new ACadSharp.Tables.Layer("BLUE") { Color = new ACadSharp.Color(5) };
            cad.Layers.Add(red); cad.Layers.Add(blue);
            cad.Entities.Add(new Line { StartPoint = new XYZ(-1000, 0, 0), EndPoint = new XYZ(1000, 0, 0), Layer = red });
            cad.Entities.Add(new Line { StartPoint = new XYZ(-1000, 10, 0), EndPoint = new XYZ(1000, 10, 0), Layer = blue });
            var paper = cad.BlockRecords.First(b => b.Name == "*Paper_Space");
            var paperView = paper.Layout.PaperViewport; paperView.Center = new XYZ(30, 30, 0); paperView.Width = 60; paperView.Height = 60; paperView.ViewHeight = 60;
            var viewport = new Viewport { Center = new XYZ(30, 40, 0), Width = 40, Height = 20, ViewHeight = 100, ViewCenter = new XY(50, 0) }; viewport.FrozenLayers.Add(blue); paper.Entities.Add(viewport);
            paper.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(60, 60, 0) });
            string path = Path.Combine(root, "viewport.dwg"); SaveCad(cad, path); var result = Read(path);
            Assert(result.Document.Width == 600 && Math.Abs(result.Document.Height - 600) <= 1, "Viewport clipping must bound the drawing");
            Assert(!result.Document.Layers.Any(l => l.Name == "BLUE"), "Viewport frozen layer was rendered");
            var line = result.Document.Layers.Single(l => l.Name == "RED");
            Assert(line.X > 100 && line.X + line.Pixels.Width < 500, "Model geometry escaped the paper viewport");
            Assert(CadCompatibility.Inspect(path).Count == 2, "Model and paper spaces should be selectable");
            var model = Read(path, new(CadLongEdge: 600, SeparateLayers: true, CadLayout: "*Model_Space"));
            Assert(model.Document.Layers.Any(l => l.Name == "BLUE"), "Paper visibility leaked into model-space import");
        });
    }

    // Independently assembled PDF objects, avoiding the importer's parser/writer.
    static void WritePdf(string path, bool form)
    {
        string Unicode(string value) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
        string content = form
            ? "q 0 0 90 55 re W n /Form Do Q q 1 0 0 1 10 0 cm /Form Do Q\n"
            : "0.9 g 0 0 100 60 re f /OC /Wall BDC 1 0 0 rg 0 0 60 60 re f EMC /OC /Hidden BDC 0 0 1 rg 20 10 50 40 re f EMC /OC /Wall BDC 0 1 0 rg 30 20 10 10 re f EMC\n";
        string formContent = "/OC /Wall BDC 1 0 0 rg 0 0 40 30 re f EMC /OC /Hidden BDC 0 0 1 rg 0 0 20 20 re f EMC\n";
        string Stream(string value, string dictionary = "") => $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(value)} >>\nstream\n{value}endstream";
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /ON /OFF [6 0 R] /Order [6 0 R 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 60] /Resources << /Properties << /Wall 5 0 R /Hidden 6 0 R >> /XObject << /Form 7 0 R >> >> /Contents 4 0 R >>",
            Stream(content),
            $"<< /Type /OCG /Name {Unicode("벽체")} >>", $"<< /Type /OCG /Name {Unicode("숨김")} >>",
            Stream(formContent, "/Type /XObject /Subtype /Form /BBox [0 0 100 60] /Resources << /Properties << /Wall 5 0 R /Hidden 6 0 R >> >>")
        };
        var text = new StringBuilder("%PDF-1.6\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length; text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }
}
