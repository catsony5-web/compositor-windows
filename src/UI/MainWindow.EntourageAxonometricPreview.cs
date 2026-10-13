using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Offscreen review of the axonometric entourage and the richer plan symbols: an axonometric site
// (simple projected masses on a street with a plaza, dressed with people, trees, cars, a cyclist,
// street furniture and ground shadows) in each projection, a site plan dressed with the richer plan
// symbols, and the 점경 palette and properties in the axonometric view.
public sealed partial class MainWindow
{
    void RenderEntourageAxonometricPreviews(string directory)
    {
        void Save(Raster image, string name)
        {
            using var output = File.Create(Path.Combine(directory, name + ".png")); image.WritePng(output);
        }
        Save(DesignRenderer.RenderOutput(EntourageAxonometricDocument(EntourageProjection.Isometric, out _)), "entourage-axonometric");
        Save(DesignRenderer.RenderOutput(EntourageAxonometricDocument(EntourageProjection.Dimetric, out _)), "entourage-axonometric-dimetric");
        Save(DesignRenderer.RenderOutput(EntourageAxonometricDocument(EntourageProjection.Military, out _)), "entourage-axonometric-military");
        Save(DesignRenderer.RenderOutput(EntourageRichPlanDocument(out _)), "entourage-plan-rich");

        // The palette in the axonometric view and the properties of a placed axonometric person.
        TrimPreviewTabs();
        var scene = EntourageAxonometricDocument(EntourageProjection.Isometric, out var person);
        AddTab(scene, null); SetWorkspaceMode(true);
        entourageTab = 0; entourageView = EntourageView.Axonometric; RefreshEntouragePalettes();
        doc.ActiveId = person; selectedLayers.Clear(); selectedLayers.Add(person);
        ShowStudioPage(0); PrepareCompactCapture();
        RevealEntourageSection(); RenderCompactCapture(directory, "entourage-editor-axonometric", 1480, 920);
        RenderPane(studioPanes[0], Path.Combine(directory, "entourage-design-tab-axonometric.png"), 360, 1500);
        ShowStudioPage(1); PrepareCompactCapture(); RenderPane(studioPanes[1], Path.Combine(directory, "entourage-properties-axonometric.png"), 360, 1180);
        entourageTab = 1; RefreshEntouragePalettes();
        ShowStudioPage(0); PrepareCompactCapture(); RevealEntourageSection();
        RenderPane(studioPanes[0], Path.Combine(directory, "entourage-design-tab-axonometric-trees.png"), 360, 1500);
        TrimPreviewTabs();
        var plan = EntourageRichPlanDocument(out var tree);
        AddTab(plan, null); SetWorkspaceMode(true);
        entourageTab = 0; entourageView = EntourageView.Plan; RefreshEntouragePalettes();
        doc.ActiveId = tree; selectedLayers.Clear(); selectedLayers.Add(tree);
        ShowStudioPage(0); PrepareCompactCapture(); RevealEntourageSection();
        RenderCompactCapture(directory, "entourage-editor-plan-rich", 1480, 920);
        entourageView = EntourageView.Elevation;
        TrimPreviewTabs();
    }

    // The ground point of every placed item, in world metres, for painting the scene back to front.
    sealed record AxoScenePart(double X, double Y, Layer Layer);

    /// <summary>
    /// An axonometric site 70 × 50 m at up to 24 px/m: a base slab, a street with a sidewalk, a paved plaza
    /// and a lawn, two building masses with their shadows, and axonometric entourage standing on the
    /// projected ground (people scattered on the plaza with the axonometric scatter).
    /// </summary>
    internal static Document EntourageAxonometricDocument(EntourageProjection projection, out Guid selected)
    {
        const int width = 2600, height = 1800;
        var p = AxoProjection.Of(projection);
        // 24 px/m, or less when the projection needs more room (the sheet's margin is 70 px).
        var reach = new[] { (0.0, 0.0, 0.0), (70, 0, 0), (70, 50, -.6), (0, 50, -.6), (6, 8, 15), (30, 8, 15), (6, 27, 15), (3, 4, 9) }
            .Select(c => p.X * c.Item1 + p.Y * c.Item2 + p.Z * c.Item3).ToArray();
        double ppm = Math.Min(24, Math.Min((width - 140) / (reach.Max(v => v.X) - reach.Min(v => v.X)), (height - 140) / (reach.Max(v => v.Y) - reach.Min(v => v.Y))));
        var document = new Document { Width = width, Height = height, Name = Loc.T("점경 예시 · 액소노메트릭") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(width, height, Colors.White) });
        Vector Raw(double x, double y, double z) => (p.X * x + p.Y * y + p.Z * z) * ppm;
        var extent = reach.Select(v => v * ppm).ToArray();
        var origin = new Point(width / 2.0 - (extent.Min(v => v.X) + extent.Max(v => v.X)) / 2, height / 2.0 - (extent.Min(v => v.Y) + extent.Max(v => v.Y)) / 2);
        Point S(double x, double y, double z = 0) => origin + Raw(x, y, z);
        var ink = Color.FromRgb(38, 38, 40);
        Layer Vector(string name, List<VectorPrimitive> parts)
        {
            var content = VectorContent.FromPaths(width, height, parts);
            return new Layer { Name = name, Kind = LayerKind.Vector, Vector = content, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(content.Drawing)) };
        }
        Geometry Quad(params Point[] points) => EntourageShapes.Poly(true, points);

        // Ground: the slab with its two front edges, the street, curbs, the plaza paving and a lawn.
        var ground = new List<VectorPrimitive>();
        void Fill(Geometry g, byte shade) => ground.Add(new VectorPrimitive(g, Color.FromRgb(shade, shade, (byte)Math.Max(0, shade - 2)), true, 0));
        void Line(Geometry g, double w, byte shade = 38) => ground.Add(new VectorPrimitive(g, Color.FromRgb(shade, shade, (byte)(shade + 2)), false, w));
        Fill(Quad(S(0, 0), S(70, 0), S(70, 50), S(0, 50)), 247);
        Fill(Quad(S(70, 0), S(70, 50), S(70, 50, -.6), S(70, 0, -.6)), 214);
        Fill(Quad(S(0, 50), S(70, 50), S(70, 50, -.6), S(0, 50, -.6)), 232);
        foreach (var edge in new[] { (S(0, 0), S(70, 0)), (S(70, 0), S(70, 50)), (S(70, 50), S(0, 50)), (S(0, 50), S(0, 0)), (S(70, 0, -.6), S(70, 50, -.6)), (S(70, 50, -.6), S(0, 50, -.6)), (S(70, 50), S(70, 50, -.6)), (S(70, 0), S(70, 0, -.6)), (S(0, 50), S(0, 50, -.6)) })
            Line(new LineGeometry(edge.Item1, edge.Item2), 1.4);
        Fill(Quad(S(0, 41), S(70, 41), S(70, 49), S(0, 49)), 230);
        Line(new LineGeometry(S(0, 41), S(70, 41)), 1); Line(new LineGeometry(S(0, 49), S(70, 49)), 1); Line(new LineGeometry(S(0, 37), S(70, 37)), .8);
        for (double x = 1; x < 70; x += 4) Line(new LineGeometry(S(x, 45), S(x + 2, 45)), 1.1, 110);
        Fill(Quad(S(34, 21), S(67, 21), S(67, 36), S(34, 36)), 240);
        for (double x = 34; x <= 67; x += 1.5) Line(new LineGeometry(S(x, 21), S(x, 36)), .45, 175);
        for (double y = 21; y <= 36; y += 1.5) Line(new LineGeometry(S(34, y), S(67, y)), .45, 175);
        Line(Quad(S(34, 21), S(67, 21), S(67, 36), S(34, 36)), .9);
        Fill(Quad(S(4, 31), S(30, 31), S(30, 36), S(4, 36)), 236);
        Line(Quad(S(4, 31), S(30, 31), S(30, 36), S(4, 36)), .7, 120);
        document.Add(Vector(Loc.T("바닥"), ground));

        // Masses: a white top, two lit faces, floor lines and mullions; their shadows lie on the ground.
        var sun = EntourageRenderer.AxonometricShadow(projection, ShadowSpec.Default(ShadowProjection.Ground));
        double ratio = sun.GroundRatio(); var fall = new Vector(Math.Cos(sun.Angle * Math.PI / 180), Math.Sin(sun.Angle * Math.PI / 180)) * ratio;
        var buildings = new (double X0, double Y0, double X1, double Y1, double H)[] { (6, 8, 30, 27, 15), (40, 6, 62, 17, 9) };
        var shade = new List<VectorPrimitive>();
        foreach (var b in buildings)
        {
            double rise = b.H * p.VerticalScale * ppm;
            var corners = new[] { S(b.X0, b.Y0), S(b.X1, b.Y0), S(b.X1, b.Y1), S(b.X0, b.Y1) };
            var all = corners.Concat(corners.Select(c => c + fall * rise)).ToArray();
            shade.Add(new VectorPrimitive(EntourageShapes.Poly(true, AxoSketch.Hull(all)), Color.FromRgb(30, 35, 43), true, 0));
        }
        var shadeLayer = Vector(Loc.T("그림자 · 매스"), shade); shadeLayer.Opacity = .2;
        document.Add(shadeLayer);
        var masses = new List<(double X1, double Y1, double X0, double Y0, Layer Layer)>();
        foreach (var b in buildings)
        {
            var parts = new List<VectorPrimitive>();
            void Face(Point[] q, byte tone) { parts.Add(new VectorPrimitive(Quad(q), Color.FromRgb(tone, tone, (byte)Math.Min(255, tone + 2)), true, 0)); }
            Face([S(b.X0, b.Y0, b.H), S(b.X1, b.Y0, b.H), S(b.X1, b.Y1, b.H), S(b.X0, b.Y1, b.H)], 255);
            Face([S(b.X0, b.Y1), S(b.X1, b.Y1), S(b.X1, b.Y1, b.H), S(b.X0, b.Y1, b.H)], 238);
            Face([S(b.X1, b.Y0), S(b.X1, b.Y1), S(b.X1, b.Y1, b.H), S(b.X1, b.Y0, b.H)], 218);
            for (double z = 3.5; z < b.H - .5; z += 3.5)
            {
                parts.Add(new VectorPrimitive(new LineGeometry(S(b.X0, b.Y1, z), S(b.X1, b.Y1, z)), ink, false, .6));
                parts.Add(new VectorPrimitive(new LineGeometry(S(b.X1, b.Y0, z), S(b.X1, b.Y1, z)), ink, false, .6));
            }
            for (double x = b.X0 + 3; x < b.X1 - .5; x += 3) parts.Add(new VectorPrimitive(new LineGeometry(S(x, b.Y1, 0), S(x, b.Y1, b.H)), ink, false, .35));
            for (double y = b.Y0 + 3; y < b.Y1 - .5; y += 3) parts.Add(new VectorPrimitive(new LineGeometry(S(b.X1, y, 0), S(b.X1, y, b.H)), ink, false, .35));
            foreach (var (a, c) in new[] { (S(b.X0, b.Y1), S(b.X1, b.Y1)), (S(b.X1, b.Y1), S(b.X1, b.Y0)), (S(b.X1, b.Y1), S(b.X1, b.Y1, b.H)), (S(b.X0, b.Y1), S(b.X0, b.Y1, b.H)), (S(b.X1, b.Y0), S(b.X1, b.Y0, b.H)),
                (S(b.X0, b.Y0, b.H), S(b.X1, b.Y0, b.H)), (S(b.X1, b.Y0, b.H), S(b.X1, b.Y1, b.H)), (S(b.X1, b.Y1, b.H), S(b.X0, b.Y1, b.H)), (S(b.X0, b.Y1, b.H), S(b.X0, b.Y0, b.H)) })
                parts.Add(new VectorPrimitive(new LineGeometry(a, c), ink, false, 1.6));
            masses.Add((b.X1, b.Y1, b.X0, b.Y0, Vector(Loc.T("매스"), parts)));
        }

        // Entourage, each standing on its ground point.
        var placed = new List<AxoScenePart>();
        uint line = VectorShapes.Argb(ink);
        Layer Put(string id, double x, double y, int variant = 0, bool flip = false, double? meters = null)
        {
            var item = EntourageLibrary.Find(id)!;
            var spec = EntourageRenderer.Spec(item, ppm, variant, projection) with { LineArgb = line, Meters = meters ?? item.DefaultMeters };
            var layer = EntourageRenderer.Create(spec, S(x, y), Loc.T(item.Name), flip);
            placed.Add(new AxoScenePart(x, y, layer));
            return layer;
        }
        for (int i = 0; i < 9; i++) Put("tree.axo-round", 4 + i * 8, 39, variant: i % 4, flip: i % 2 == 1, meters: 7 + i % 3 * .6);
        foreach (double x in new[] { 8.0, 24, 40, 56 }) Put("lamp.axo-street", x, 40.4, flip: true);
        Put("car.axo-sedan", 15, 47); Put("car.axo-hatch", 36, 47.2); Put("car.axo-sedan", 54, 43, variant: 1); Put("car.axo-hatch", 66, 43.2, variant: 1);
        Put("person.axo-cyclist", 26, 43.6);
        foreach (var (x, y) in new[] { (3.0, 4.0), (3.5, 15.0), (14.0, 3.5), (22.0, 4.0) }) Put("tree.axo-conifer", x, y, variant: (int)x % 3, meters: 9);
        Put("tree.axo-oval", 63, 3, variant: 1, meters: 9); Put("tree.axo-oval", 64.5, 10, variant: 2, meters: 8.5);
        for (int i = 0; i < 6; i++) Put("plant.axo-hedge", 7.5 + i * 3.1, 30);
        Put("plant.axo-shrub", 32, 31, variant: 1); Put("plant.axo-shrub", 33, 27.5, variant: 2, meters: 1);
        Put("tree.axo-branches", 63, 25, variant: 1, meters: 8); Put("tree.axo-branches", 37, 24, variant: 2, meters: 7.5);
        Put("table.axo-parasol", 46, 20.5, variant: 1); Put("table.axo-parasol", 54, 20.8);
        Put("bench.axo", 42, 33.2); Put("bench.axo", 58, 33.2);
        Put("person.axo-sitting", 42.3, 33.1, flip: true);
        Put("person.axo-sitting", 57.6, 33.1, variant: 1, flip: true);
        // People scattered on the plaza with the axonometric scatter (distances on the ground).
        var plaza = new Rect(S(34, 21), S(67, 36)); plaza.Union(S(67, 21)); plaza.Union(S(34, 36));
        bool OnPlaza(Point q) { var g = p.Ground(q - origin) / ppm; return g.X > 36 && g.X < 65 && g.Y > 24 && g.Y < 32; }
        var crowd = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Axonometric, 1.7, ppm, 1.7 * ppm * .4, 4, 9, S(50, 28), plaza, OnPlaza, 11, projection);
        string[] kinds = ["person.axo-walking", "person.axo-standing", "person.axo-bag", "person.axo-walking", "person.axo-pair"];
        for (int i = 0; i < crowd.Count; i++)
        {
            var g = p.Ground(crowd[i].Anchor - origin) / ppm;
            Put(kinds[i % kinds.Length], g.X, g.Y, crowd[i].Variant, crowd[i].Flip, crowd[i].Meters);
        }
        var walker = Put("person.axo-walking", 30, 38.6, variant: 0);
        Put("person.axo-bag", 47, 38.2, variant: 1, flip: true);
        Put("person.axo-pair", 61, 38.4, variant: 1);

        // Back to front: entourage by its ground depth, each mass after everything that stands behind it.
        var order = placed.OrderBy(q => p.Depth(new System.Windows.Media.Media3D.Point3D(q.X, q.Y, 0))).Select(q => (Part: (AxoScenePart?)q, Mass: (Layer?)null)).ToList();
        foreach (var m in masses.OrderBy(m => m.X1 + m.Y1))
        {
            bool Behind(AxoScenePart q) => q.X <= m.X1 && q.Y <= m.Y1;
            int last = order.FindLastIndex(o => o.Part is { } q && Behind(q));
            var front = order.Take(last + 1).Where(o => o.Part is { } q && !Behind(q)).ToList();
            order = order.Take(last + 1).Except(front).Append((null, m.Layer)).Concat(front).Concat(order.Skip(last + 1)).ToList();
        }
        foreach (var o in order) document.Add(o.Mass ?? o.Part!.Layer);
        // Ground shadows of the entourage, cast along the projected ground, under the masses.
        var shadow = ShadowRenderer.Insert(document, sun with { Sources = placed.Select(q => q.Layer.Id).ToArray(), Opacity = .22, Softness = 8 }, Loc.T("그림자 · 점경"));
        document.Layers.Remove(shadow); document.Layers.Insert(document.Layers.IndexOf(shadeLayer) + 1, shadow);
        // Shadows stay on the base: a mask from the slab's top face.
        var slab = Quad(S(0, 0), S(70, 0), S(70, 50), S(0, 50));
        foreach (var cast in new[] { shadow, shadeLayer })
        {
            var cover = Imaging.Draw(cast.Pixels.Width, cast.Pixels.Height, dc => { dc.PushTransform(new TranslateTransform(-cast.X, -cast.Y)); dc.DrawGeometry(Brushes.Black, null, slab); dc.Pop(); });
            cast.Mask = Enumerable.Range(0, cover.Width * cover.Height).Select(i => cover.Data[i * 4 + 3]).ToArray();
        }
        selected = walker.Id; document.ActiveId = walker.Id;
        return document;
    }

    /// <summary>The site plan dressed with the richer plan symbols: crowns with branch structure and leaf texture, palms, hedges, ground covers, people seen from above, cars and bicycles.</summary>
    internal static Document EntourageRichPlanDocument(out Guid selected)
    {
        const double ppm = 36; const int width = 2400, height = 1500;
        var document = new Document { Width = width, Height = height, Name = Loc.T("점경 예시 · 배치도") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(width, height, Colors.White) });
        var ink = Color.FromRgb(34, 34, 34); var parts = new List<VectorPrimitive>();
        void Stroke(Geometry g, double w) => parts.Add(new VectorPrimitive(g, ink, false, w));
        Stroke(new LineGeometry(new Point(0, 1260), new Point(width, 1260)), 2.5); Stroke(new LineGeometry(new Point(0, 1460), new Point(width, 1460)), 2.5);
        Stroke(new LineGeometry(new Point(0, 1180), new Point(width, 1180)), 1.2);
        for (double x = 0; x < width; x += 90) Stroke(new LineGeometry(new Point(x, 1360), new Point(x + 50, 1360)), 1);
        for (double x = 1240; x <= 2220; x += 92) Stroke(new LineGeometry(new Point(x, 980), new Point(x, 1180)), 1.2);
        Stroke(new LineGeometry(new Point(1240, 980), new Point(2220, 980)), 1.2);
        parts.Add(new VectorPrimitive(new RectangleGeometry(new Rect(300, 240, 760, 520)), Color.FromRgb(236, 234, 228), true, 0));
        Stroke(new RectangleGeometry(new Rect(300, 240, 760, 520)), 4);
        Stroke(new RectangleGeometry(new Rect(1160, 200, 980, 640)), 1.5);
        for (double y = 240; y < 840; y += 40) Stroke(new LineGeometry(new Point(1160, y), new Point(2140, y)), .45);
        Stroke(Geometry.Parse("M40 840 C300 800 700 820 1100 860"), 1.2);
        var lines = VectorContent.FromPaths(width, height, parts);
        document.Add(new Layer { Name = Loc.T("배치도"), Kind = LayerKind.Vector, Vector = lines, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(lines.Drawing)) });
        Layer Put(string id, double x, double y, double? meters = null, EntourageFill? fill = null, double rotation = 0, int variant = 0)
        {
            var item = EntourageLibrary.Find(id)!;
            var spec = EntourageRenderer.Spec(item, ppm, variant) with { Meters = meters ?? item.DefaultMeters, Fill = fill ?? item.DefaultFill };
            var layer = EntourageRenderer.Create(spec, new Point(x, y), Loc.T(item.Name), false, rotation);
            document.Add(layer); return layer;
        }
        var trees = new List<Guid>();
        // Ground covers and grass under the lawn trees, hedges along the building.
        Put("plant.plan-grass", 260, 960, 4); Put("plant.plan-grass", 760, 1000, 3.6, variant: 1); Put("plant.plan-groundcover", 1000, 930, 3, variant: 2);
        Put("plant.plan-groundcover", 160, 420, 3.2, variant: 1);
        for (int i = 0; i < 4; i++) Put("plant.plan-hedge", 380 + i * 150, 800, 4, variant: i % 2);
        var grove = EntourageRenderer.Scatter(EntourageCategory.Plants, EntourageView.Plan, 5.5, ppm, 5.5 * ppm, 4, 5, new Point(560, 960), new Rect(120, 900, 960, 120), null, 3);
        foreach (var g in grove) trees.Add(Put(g.Variant % 2 == 0 ? "tree.plan-canopy" : "tree.plan-textured", g.Anchor.X, g.Anchor.Y, g.Meters, EntourageFill.White, g.Rotation, g.Variant).Id);
        trees.Add(Put("tree.plan-palm", 1110, 150, 5).Id); trees.Add(Put("tree.plan-palm", 200, 170, 4.5, variant: 1).Id);
        trees.Add(Put("tree.plan-canopy", 1700, 440, 6.5, EntourageFill.White, variant: 2).Id); trees.Add(Put("tree.plan-textured", 1990, 660, 5.5, EntourageFill.White, variant: 1).Id);
        // Parking with three kinds of car, bicycles, plaza furniture and people seen from above.
        string[] cars = ["car.plan", "car.plan-hatch", "car.plan-van", "car.plan-hatch", "car.plan", "car.plan-van"];
        for (int i = 0; i < 6; i++) if (i != 2) Put(cars[i], 1286 + i * 184, 1080, rotation: 180);
        for (int i = 0; i < 4; i++) Put("bike.plan-detail", 1110 + i * 28, 1100);
        Put("bench.plan", 1450, 560); Put("bench.plan", 1450, 680, rotation: 180);
        Put("table.plan", 1850, 300); Put("table.plan", 2040, 400, rotation: 20);
        Put("person.plan-group", 1640, 330, rotation: 15); Put("person.plan-pair", 1760, 720, rotation: -30);
        Put("person.plan-pair", 1300, 760, rotation: 90, variant: 1); Put("person.plan-stroller", 300, 1200, rotation: -90);
        Put("person.plan-cyclist", 520, 1340, rotation: 90); Put("person.plan-cyclist", 1900, 1410, rotation: -90, variant: 1);
        var people = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Plan, .6, ppm, .6 * ppm, 3, 7, new Point(1600, 560), new Rect(1220, 420, 820, 300), null, 5);
        foreach (var q in people) Put(q.Variant == 1 ? "person.plan-stride" : "person.plan-standing", q.Anchor.X, q.Anchor.Y, rotation: q.Rotation, variant: q.Variant);
        for (int i = 0; i < 7; i++) trees.Add(Put(i % 2 == 0 ? "tree.plan-canopy" : "tree.plan-textured", 120 + i * 360, 1220, 5.5, EntourageFill.White, variant: i).Id);
        ShadowRenderer.Insert(document, ShadowSpec.Default(ShadowProjection.Plan) with { Sources = [.. trees], Height = 7 * ppm, Elevation = 50, Angle = 320, Opacity = .22 }, Loc.T("그림자 · 수목"));
        selected = trees[^1]; document.ActiveId = selected;
        return document;
    }
}
