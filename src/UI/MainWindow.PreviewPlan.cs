using System.IO;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

// A synthetic floor plan for the offscreen previews: walls, a door, furniture,
// dimensions and two hatches, rendered with and without drawing cleanup.
public sealed partial class MainWindow
{
    static void RenderCleanupPreviews(string directory)
    {
        var cad = new CadDocument();
        ACadSharp.Tables.Layer Layer(string name) { var layer = new ACadSharp.Tables.Layer(name); cad.Layers.Add(layer); return layer; }
        var wall = Layer("A-WALL"); var door = Layer("A-DOOR"); var furniture = Layer("A-FURN"); var dims = Layer("A-ANNO-DIMS"); var hatch = Layer("A-HATCH");
        void Add(Entity entity, ACadSharp.Tables.Layer layer) { entity.Layer = layer; cad.Entities.Add(entity); }
        void Box(double x, double y, double w, double h, ACadSharp.Tables.Layer layer) =>
            Add(new LwPolyline(new XY[] { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) }) { IsClosed = true }, layer);
        void Fill(string pattern, params XYZ[] points)
        {
            var fill = new Hatch { Pattern = new HatchPattern(pattern) }; var loop = new Hatch.BoundaryPath();
            loop.Edges.Add(new Hatch.BoundaryPath.Polyline(points, true)); fill.Paths.Add(loop); Add(fill, hatch);
        }
        Box(0, 0, 600, 400, wall); Box(20, 20, 560, 360, wall); Box(300, 20, 20, 250, wall);
        Fill("AR-CONC", new XYZ(0, 0, 0), new XYZ(600, 0, 0), new XYZ(600, 20, 0), new XYZ(0, 20, 0));
        Fill("AR-CONC", new XYZ(300, 20, 0), new XYZ(320, 20, 0), new XYZ(320, 270, 0), new XYZ(300, 270, 0));
        Fill("ANSI37", new XYZ(320, 20, 0), new XYZ(580, 20, 0), new XYZ(580, 200, 0), new XYZ(320, 200, 0));
        Add(new Line { StartPoint = new XYZ(300, 270, 0), EndPoint = new XYZ(300, 360, 0) }, door);
        Add(new Arc { Center = new XYZ(300, 270, 0), Radius = 90, StartAngle = Math.PI / 2, EndAngle = Math.PI }, door);
        Box(60, 60, 180, 70, furniture); Box(60, 60, 180, 20, furniture); Box(90, 180, 120, 80, furniture);
        Add(new Circle { Center = new XYZ(450, 290, 0), Radius = 45 }, furniture);
        Add(new Line { StartPoint = new XYZ(0, 430, 0), EndPoint = new XYZ(600, 430, 0) }, dims);
        Add(new TextEntity { Value = "6000", InsertPoint = new XYZ(280, 438, 0), Height = 16 }, dims);
        string path = Path.Combine(directory, "평면 예시.dxf"); DxfWriter.Write(path, cad);
        foreach (var (name, cleanup) in new[] { ("cad-cleanup-before", (CadCleanup?)null), ("cad-cleanup-after", new CadCleanup()) })
        {
            var result = CompatibilityImport.ReadAsync(path, new(CadLongEdge: 900, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: cleanup)).GetAwaiter().GetResult();
            var image = Imaging.Render(result.Document);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image.Bitmap()));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
    }
}
