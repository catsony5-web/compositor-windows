using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Offscreen review of the screentones (--render-studio-previews): the 스크린톤 group of the pattern tab
// after a wand pick in the sample plan, a plan in the photocopy look (black poché walls, dot screens,
// a dot gradient and a stipple gradient under crisp linework) with the gradient layer's properties,
// the poché properties, a 400% view of the gradient, and the plan drawn at document pixels.
public sealed partial class MainWindow
{
    void RenderScreentonePreviews(string directory, Action<FrameworkElement, string, int, int> capturePane, Action<string, int, int> captureWindow)
    {
        string plan = Path.Combine(directory, "평면 예시.dxf");
        void Show()
        {
            Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        var previousTab = materialPaletteTab;
        try
        {
            if (File.Exists(plan))
            {
                var imported = CompatibilityImport.ReadAsync(plan, new(CadLongEdge: 900, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: new CadCleanup()))
                    .GetAwaiter().GetResult().Document;
                AddTab(imported, null); SetTool(Tool.MagicWand);
                var picked = PrecisionWand.Select(doc, new Point(doc.Width * .86, doc.Height * .22), wandTolerance, true, true, false, 1);
                selection = picked with { Contour = SelectionContours.Create(picked) };
                materialPaletteTab = MaterialPaletteTab.Patterns; Show(); ShowStudioPage(1);
                capturePane(studioPanes[1], "selection-screentones", 360, 1560);
                capturePane(studioPanes[1], "selection-screentones-narrow", 300, 1900);
                selection = null;
            }
            AddTab(ScreentonePlan(out var gradientId, out var pocheId), null); SetTool(Tool.Move);
            doc.ActiveId = gradientId; selectedLayers.Clear(); selectedLayers.Add(gradientId);
            SectionHeader.SetCollapsedKeys([], []); Show(); ShowStudioPage(1);
            captureWindow("screentone-plan", 1480, 920);
            capturePane(studioPanes[1], "screentone-gradient-properties", 360, 1180);
            capturePane(studioPanes[1], "screentone-gradient-properties-narrow", 300, 1260);
            doc.ActiveId = pocheId; selectedLayers.Clear(); selectedLayers.Add(pocheId); Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "screentone-poche-properties", 360, 760);
            var gradient = doc.Layers.Single(l => l.Id == gradientId);
            SetWorkspaceMode(true); Show();
            CaptureZoomed("screentone-zoom-400", new Point(gradient.X + gradient.Pixels.Width * .5, gradient.Y + gradient.Pixels.Height * .55), 4, directory);
            SetWorkspaceMode(false);
            SavePng(Imaging.Render(doc).Bitmap(), Path.Combine(directory, "screentone-plan-sheet.png"));
        }
        finally { materialPaletteTab = previousTab; SectionHeader.SetCollapsedKeys([], []); }
    }

    // A small plan in the photocopy look: black poché walls, a lobby in a dot gradient, rooms in dot,
    // line and grid screens, a hall in a stipple gradient, furniture lines and thin room outlines on top.
    static Document ScreentonePlan(out Guid gradientId, out Guid pocheId)
    {
        const int width = 1200, height = 820;
        var plan = new Document { Width = width, Height = height, Name = "스크린톤 평면" };
        var paper = VectorShapes.Create(new ShapeSpec { Width = width, Height = height, FillArgb = 0xFFFFFFFF }); paper.Name = "도면 배경"; paper.Locked = true; plan.Add(paper);
        Rect Box(double x0, double y0, double x1, double y1) => new(new Point(x0, y0), new Point(x1, y1));
        Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];
        var outer = Box(80, 80, 1120, 740);
        // Rooms inside the walls (walls are 24 px thick).
        var lobby = Box(104, 104, 600, 500); var lower = Box(104, 524, 600, 716); var office = Box(624, 104, 1096, 420);
        var hall = Box(624, 444, 1096, 716); var core = Box(888, 168, 1032, 356);
        Rect[] rooms = [lobby, lower, office, hall];
        // Poché: the wall ring minus the rooms, and a solid core in the office.
        var walls = MaterialEditing.Polygon([Corners(outer), .. rooms.Select(Corners)]);
        Layer Fill(HatchPattern pattern, Geometry area, string name, Func<MaterialFill, MaterialFill>? tune = null)
        {
            var asset = HatchPatternRenderer.Create(pattern);
            if (!plan.Materials.Any(m => m.Id == asset.Id)) plan.Materials.Add(asset);
            var region = MaterialEditing.Region(plan, name, area, "polygon"); plan.MaterialRegions.Add(region);
            double tile = MaterialEditing.DefaultTile(width, height, asset);
            var layer = MaterialEditing.Apply(plan, asset.Id, region.Id, tile, tile);
            if (tune != null) { layer.Material = tune(layer.Material!); layer.Pixels = MaterialRenderer.Render(layer.Material); }
            layer.Name = Loc.T("패턴 · ") + Loc.T(HatchPatterns.Name(pattern)); plan.Add(layer);
            return layer;
        }
        var poche = Fill(HatchPattern.SolidBlack, walls, "벽체", f => f with { Ink = 0xFF111111 });
        var gradient = Fill(HatchPattern.DotGradient, MaterialEditing.Polygon([Corners(lobby)]), "로비", f => f with { Gradient = new ToneGradient(90, .08, .85) });
        Fill(HatchPattern.DotScreen30, MaterialEditing.Polygon([Corners(office), Corners(core)]), "사무실");
        Fill(HatchPattern.SolidBlack, MaterialEditing.Polygon([Corners(core)]), "코어", f => f with { Ink = 0xFF111111 });
        Fill(HatchPattern.StippleGradient, MaterialEditing.Polygon([Corners(hall)]), "홀", f => f with { Gradient = new ToneGradient(0, .05, .8, 3) });
        Fill(HatchPattern.GridScreen30, MaterialEditing.Polygon([Corners(lower)]), "창고");
        // Linework: room outlines, a stair, tables and a column grid, drawn crisp above the tones.
        var lines = new GeometryGroup();
        foreach (var room in rooms.Append(outer).Append(core)) lines.Children.Add(new RectangleGeometry(room));
        for (int step = 0; step < 9; step++) lines.Children.Add(new LineGeometry(new Point(140 + step * 24, 560), new Point(140 + step * 24, 690)));
        lines.Children.Add(new RectangleGeometry(Box(140, 560, 356, 690)));
        foreach (var (x, y) in new[] { (220.0, 220.0), (420, 220), (220, 380), (420, 380) }) lines.Children.Add(new EllipseGeometry(new Point(x, y), 34, 34));
        foreach (var (x, y) in new[] { (680.0, 150.0), (680, 290), (760, 150), (760, 290) }) lines.Children.Add(new RectangleGeometry(new Rect(x, y, 56, 90)));
        lines.Children.Add(new LineGeometry(new Point(600, 600), new Point(624, 600)));
        var linework = VectorContent.FromPaths(width, height, [new VectorPrimitive(lines, Color.FromRgb(0x10, 0x10, 0x10), false, 1.6)]);
        plan.Add(new Layer { Name = "선", Kind = LayerKind.Vector, Category = LayerCategory.Drawing, Vector = linework, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(linework.Drawing)) });
        plan.ActiveId = gradient.Id;
        gradientId = gradient.Id; pocheId = poche.Id;
        return plan;
    }
}
