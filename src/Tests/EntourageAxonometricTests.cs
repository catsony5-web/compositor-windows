using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Compositor.Windows;

// Axonometric entourage and the richer plan symbols: the items of Preview 45 keep their ids and
// drawings; every axonometric item draws in every projection, deterministically, standing on its
// ground point; the projection math (ground contact, foreshortening, footprints); anchors through
// restyling, flipping and reprojecting; the scatter on the projected ground; saving for older
// versions; ground shadows along the projected ground; and the new plan symbols.
public static class EntourageAxonometricTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // Size, anchor and covered pixels of the Preview 45 items at 30 px/m (id/variant:WxH@anchor#covered).
    static readonly string[] Preview45 =
    [
        "person.standing/0:23x57@10.78,54.00#543", "person.standing/1:23x57@10.18,54.00#549", "person.standing/2:22x57@10.54,54.00#493", "person.standing-side/0:21x57@7.35,54.00#374",
        "person.standing-side/1:19x57@7.30,54.00#349", "person.standing-side/2:19x57@7.30,54.00#350", "person.walking/0:37x58@17.94,54.00#464", "person.walking/1:36x57@17.08,54.00#460",
        "person.walking/2:35x57@16.63,54.00#459", "person.walking-bag/0:33x58@15.82,54.00#477", "person.walking-bag/1:33x58@15.81,54.00#484", "person.walking-bag/2:33x58@15.86,54.00#459",
        "person.sitting/0:28x45@7.64,42.00#320", "person.sitting/1:28x45@7.63,42.00#328", "person.sitting/2:29x45@6.86,42.00#374", "person.stroller/0:50x58@15.45,54.00#740",
        "person.stroller/1:50x58@15.48,54.00#730", "person.child/0:28x40@13.16,36.60#221", "person.child/1:26x40@12.57,36.60#220", "person.child/2:26x40@12.27,36.60#216",
        "person.cyclist/0:57x56@27.90,52.50#779", "person.cyclist/1:57x56@27.93,52.50#790", "person.plan/0:24x15@12.00,7.37#150", "person.plan/1:24x20@12.00,9.91#167",
        "person.plan/2:24x17@12.00,8.14#154", "person.plan-walking/0:24x20@12.00,9.56#177", "person.plan-walking/1:24x20@12.00,9.79#166", "person.plan-walking/2:24x21@12.00,10.05#182",
        "tree.round/0:181x247@84.08,243.00#21580", "tree.round/1:180x247@84.44,243.00#21899", "tree.round/2:196x247@90.92,243.00#23295", "tree.round/3:179x247@86.32,243.00#21568",
        "tree.oval/0:118x307@59.03,303.00#18676", "tree.oval/1:130x307@69.93,303.00#20964", "tree.oval/2:130x307@58.72,303.00#21164", "tree.airy/0:280x276@143.74,273.00#19309",
        "tree.airy/1:239x276@116.66,273.00#17388", "tree.airy/2:296x276@147.76,273.00#19098", "tree.conifer/0:160x336@80.97,333.00#21882", "tree.conifer/1:141x336@72.16,333.00#21055",
        "tree.conifer/2:126x336@62.86,333.00#18151", "tree.palm/0:165x276@74.35,273.00#8730", "tree.palm/1:161x277@67.13,273.00#8896", "tree.palm/2:164x276@98.59,273.00#8797",
        "plant.shrub/0:64x42@32.05,39.00#1820", "plant.shrub/1:64x42@31.67,39.00#1816", "plant.shrub/2:72x42@35.92,39.00#2062", "plant.grass/0:37x31@18.38,27.00#249",
        "plant.grass/1:39x31@19.96,27.00#258", "plant.grass/2:40x31@20.31,27.00#276", "tree.plan-lobed/0:186x186@92.86,93.00#577", "tree.plan-lobed/1:186x186@92.50,93.00#555",
        "tree.plan-lobed/2:186x186@92.84,93.00#559", "tree.plan-lobed/3:185x186@92.24,93.00#551", "tree.plan-branches/0:186x186@93.00,92.73#699", "tree.plan-branches/1:187x186@93.00,92.95#648",
        "tree.plan-branches/2:184x186@91.65,93.00#686", "tree.plan-branches/3:180x186@89.73,93.00#662", "tree.plan-scribble/0:156x156@77.92,78.00#926", "tree.plan-scribble/1:156x156@77.91,78.00#886",
        "tree.plan-scribble/2:156x155@78.00,77.34#828", "tree.plan-scribble/3:155x156@77.43,78.00#853", "tree.plan-conifer/0:125x126@62.14,63.00#551", "tree.plan-conifer/1:125x126@62.26,63.00#538",
        "tree.plan-conifer/2:126x124@63.00,61.77#567", "plant.plan-shrubs/0:71x78@35.09,39.00#218", "plant.plan-shrubs/1:77x78@38.31,39.00#266", "plant.plan-shrubs/2:67x78@33.14,39.00#210",
        "car.sedan/0:145x49@72.32,45.60#3952", "car.sedan/1:145x49@72.32,45.60#3952", "car.hatch/0:122x51@60.52,47.40#3798", "car.hatch/1:122x51@60.52,47.40#3798",
        "car.plan/0:69x147@34.44,73.50#7638", "bike.side/0:61x36@29.74,33.00#522", "bike.plan/0:29x60@14.18,30.00#191", "bench.side/0:60x32@29.83,28.20#524",
        "bench.plan/0:62x28@30.60,13.89#1093", "lamp.street/0:43x153@7.37,150.00#798", "lamp.street/1:20x153@9.88,150.00#744", "table.parasol/0:90x81@44.67,78.00#1308",
        "table.plan/0:65x65@32.25,32.25#2249"
    ];

    internal static readonly string[] NewIds =
    [
        "person.plan-standing", "person.plan-stride", "person.plan-pair", "person.plan-group", "person.plan-stroller", "person.plan-cyclist",
        "tree.plan-canopy", "tree.plan-textured", "tree.plan-palm", "plant.plan-hedge", "plant.plan-groundcover", "plant.plan-grass",
        "car.plan-hatch", "car.plan-van", "bike.plan-detail",
        "person.axo-standing", "person.axo-walking", "person.axo-bag", "person.axo-sitting", "person.axo-pair", "person.axo-cyclist",
        "tree.axo-round", "tree.axo-oval", "tree.axo-branches", "tree.axo-conifer", "tree.axo-palm", "plant.axo-shrub", "plant.axo-hedge",
        "car.axo-sedan", "car.axo-hatch", "bike.axo", "bench.axo", "lamp.axo-street", "table.axo-parasol"
    ];

    internal static string Signature(EntourageItem item, int variant)
    {
        var (vector, preview, anchor) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, variant));
        int covered = 0; for (int i = 3; i < preview.Data.Length; i += 4) if (preview.Data[i] > 128) covered++;
        return FormattableString.Invariant($"{item.Id}/{variant}:{vector.Width}x{vector.Height}@{anchor.X:0.00},{anchor.Y:0.00}#{covered}");
    }

    static int Covered(Raster raster, byte threshold = 128) { int n = 0; for (int i = 3; i < raster.Data.Length; i += 4) if (raster.Data[i] > threshold) n++; return n; }

    static IEnumerable<EntourageItem> Axonometric => EntourageLibrary.All.Where(i => i.View == EntourageView.Axonometric);

    public static void Run(Action<string, Action> test, string directory)
    {
        test("entourage items of Preview 45 keep their ids, sizes and drawings", () =>
        {
            Check(EntourageLibrary.All.Select(i => i.Id).Where(EntourageTests.StableIds.Contains).SequenceEqual(EntourageTests.StableIds), "An old entourage id is missing or moved");
            Check(NewIds.All(id => EntourageLibrary.Find(id) != null) && EntourageLibrary.All.Count == EntourageTests.StableIds.Length + NewIds.Length, "The new items are not all in the library");
            foreach (var expected in Preview45)
            {
                string key = expected[..expected.IndexOf(':')];
                var item = EntourageLibrary.Find(key[..key.IndexOf('/')])!; int variant = int.Parse(key[(key.IndexOf('/') + 1)..], CultureInfo.InvariantCulture);
                Check(item.View != EntourageView.Axonometric && item.Projected == null, $"{item.Id} changed its view");
                string actual = Signature(item, variant);
                static (string Size, double X, double Y, int Covered) Parse(string s)
                {
                    var body = s[(s.IndexOf(':') + 1)..]; var at = body.Split('@'); var anchor = at[1].Split('#'); var xy = anchor[0].Split(',');
                    return (at[0], double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture), int.Parse(anchor[1], CultureInfo.InvariantCulture));
                }
                var a = Parse(actual); var e = Parse(expected);
                Check(a.Size == e.Size && Math.Abs(a.X - e.X) < .06 && Math.Abs(a.Y - e.Y) < .06 && Math.Abs(a.Covered - e.Covered) <= Math.Max(3, e.Covered / 100),
                    $"{key} draws differently: {actual}, expected {expected}");
            }
        });

        test("axonometric entourage: every item, variant and projection draws on its ground point and deterministically", () =>
        {
            Check(Axonometric.Count() >= 19, "The axonometric set is incomplete");
            foreach (var category in Enum.GetValues<EntourageCategory>()) Check(Axonometric.Any(i => i.Category == category), $"No axonometric {category}");
            foreach (var item in Axonometric)
            {
                Check(item.Footprint > 0 && item.Name.Contains("액소", StringComparison.Ordinal) && item.Keywords.Contains("axonometric", StringComparison.Ordinal), $"{item.Id} lacks a footprint, name or keywords");
                foreach (var projection in Enum.GetValues<EntourageProjection>())
                    for (int v = 0; v < item.Variants; v++)
                    {
                        var a = item.Projected!(v, projection); var b = item.Projected!(v, projection);
                        Check(!a.Silhouette.Bounds.IsEmpty && a.Bounds.Width > 1 && a.ModelHeight > 0, $"{item.Id} {v} {projection} has no drawing");
                        Check(a.Silhouette.ToString(CultureInfo.InvariantCulture) == b.Silhouette.ToString(CultureInfo.InvariantCulture) && a.Lines.Count == b.Lines.Count, $"{item.Id} {v} {projection} is not deterministic");
                        // The ground point is under the drawing: inside its width, near its foot, and the top is the item's height on the vertical.
                        var p = AxoProjection.Of(projection); double rise = a.ModelHeight * p.VerticalScale;
                        Check(a.Bounds.Left < 0 && a.Bounds.Right > 0 && a.Bounds.Bottom > -rise * .1 && a.Bounds.Top < -rise * .55, $"{item.Id} {v} {projection} does not stand on its ground point: {a.Bounds}");
                        var (vector, preview, anchor) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, v, projection));
                        Check(Covered(preview) > 20 && vector.Width == preview.Width, $"{item.Id} {v} {projection} draws nothing");
                    }
                var (first, _, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, 0, EntourageProjection.Military));
                var (again, _, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, 0, EntourageProjection.Military));
                using var x = new MemoryStream(); using var y = new MemoryStream(); first.Write(x); again.Write(y);
                Check(x.ToArray().AsSpan().SequenceEqual(y.ToArray()), $"{item.Id} vector differs between renders");
            }
            var person = EntourageLibrary.Find("person.axo-standing")!;
            Check(person.Projected!(0, EntourageProjection.Isometric).Silhouette.ToString() != person.Projected!(0, EntourageProjection.Military).Silhouette.ToString(), "The projections draw the same");
            Check(person.Projected!(0, EntourageProjection.Isometric).Silhouette.ToString() != person.Projected!(1, EntourageProjection.Isometric).Silhouette.ToString(), "Variants are identical");
        });

        test("axonometric entourage: projections keep ground contact and their foreshortening", () =>
        {
            void Near(double a, double b, double tolerance, string what) => Check(Math.Abs(a - b) < tolerance, $"{what}: {a}, expected {b}");
            var iso = AxoProjection.Of(EntourageProjection.Isometric); var di = AxoProjection.Of(EntourageProjection.Dimetric); var mil = AxoProjection.Of(EntourageProjection.Military);
            double Angle(Vector v) => Math.Atan2(v.Y, v.X) * 180 / Math.PI;
            Near(Angle(iso.X), 30, 1e-9, "Isometric x axis"); Near(Angle(iso.Y), 150, 1e-9, "Isometric y axis"); Near(iso.X.Length, 1, 1e-9, "Isometric plan scale"); Near(iso.VerticalScale, 1, 1e-9, "Isometric vertical");
            Near(Angle(di.X), Math.Atan(.5) * 180 / Math.PI, 1e-9, "Dimetric x axis"); Near(di.VerticalScale / di.PlanScale, Math.Sqrt(1.2), 1e-9, "Dimetric vertical ratio");
            Near(Angle(mil.X), 45, 1e-9, "Military x axis"); Near(mil.VerticalScale, 1, 1e-9, "Military vertical");
            foreach (var p in new[] { iso, di, mil })
            {
                Check(p.Project(new Point3D(0, 0, 0)) == new Point(0, 0) && Math.Abs(p.Project(new Point3D(0, 0, 100)).X) < 1e-9 && p.Project(new Point3D(0, 0, 100)).Y < 0, "Verticals are not vertical or the ground point moved");
                var back = p.Ground(p.Project(new Point3D(37, -12, 0)) - new Point(0, 0)); Near(back.X, 37, 1e-9, "Ground x"); Near(back.Y, -12, 1e-9, "Ground y");
                var toward = p.Project(p.Toward); Check(toward.Length < 1e-9 && p.Toward.Z > 0 && p.Toward.X > 0 && p.Toward.Y > 0, "The view direction is not what the projection flattens");
            }
            // Orthographic views draw a sphere as a circle; plan oblique stretches it upright by √2.
            var (a, b, d) = iso.SphereShape; Check(Math.Abs(a - d) < 1e-9 && Math.Abs(b) < 1e-9, "An isometric sphere is not round");
            (a, b, d) = di.SphereShape; Check(Math.Abs(a - d) < 1e-9 && Math.Abs(b) < 1e-9, "A dimetric sphere is not round");
            (a, b, d) = mil.SphereShape; Check(Math.Abs(d / a - 2) < 1e-9, "A plan-oblique sphere is not √2 tall");
            // A 100 cm cube: its outline and the three edges between its visible faces.
            foreach (var (projection, width, height) in new[] { (EntourageProjection.Isometric, 173.205, 200.0), (EntourageProjection.Dimetric, 178.885, 198.997), (EntourageProjection.Military, 141.421, 241.421) })
            {
                var sketch = new AxoSketch(new EntourageSketch(), AxoProjection.Of(projection));
                var cube = sketch.Box(0, 0, 0, 100, 100, 100);
                Near(cube.Outline.Bounds.Width, width, .01, $"{projection} cube width"); Near(cube.Outline.Bounds.Height, height, .01, $"{projection} cube height");
                Check(cube.Edges is GeometryGroup { Children.Count: 3 }, $"{projection} cube shows {(cube.Edges as GeometryGroup)?.Children.Count} inner edges");
                // A canopy's footprint: a ground circle shows as an ellipse (round in plan oblique).
                var ring = sketch.Disc(new Point3D(0, 0, 0), new Vector3D(0, 0, 1), 100).Bounds;
                double ratio = projection switch { EntourageProjection.Isometric => Math.Tan(Math.PI / 6), EntourageProjection.Dimetric => .5, _ => 1 };
                Near(ring.Height / ring.Width, ratio, .002, $"{projection} footprint ellipse");
            }
            // A standing person: feet on the ground point, the head at 1.7 m on the projected vertical.
            foreach (var projection in Enum.GetValues<EntourageProjection>())
            {
                var art = EntourageLibrary.Art(EntourageLibrary.Find("person.axo-standing")!, 0, projection); double rise = 170 * AxoProjection.Of(projection).VerticalScale;
                Check(art.Bounds.Top < -rise * .97 && art.Bounds.Top > -rise * 1.06 && art.Bounds.Bottom > 0 && art.Bounds.Bottom < 32, $"A person in {projection} is {art.Bounds}");
                var spec = EntourageRenderer.Spec(EntourageLibrary.Find("person.axo-standing")!, 50, 0, projection);
                var (_, preview, anchor) = EntourageRenderer.Draw(spec);
                int top = preview.Height; for (int y = 0; y < preview.Height && top == preview.Height; y++) for (int x = 0; x < preview.Width; x++) if (preview.Data[(y * preview.Width + x) * 4 + 3] > 100) { top = y; break; }
                Near((anchor.Y - top) / (1.7 * 50 * AxoProjection.Of(projection).VerticalScale), 1, .07, $"Person height in {projection}");
            }
        });

        test("axonometric entourage: items keep their ground point through restyling, flipping and reprojecting", () =>
        {
            var item = EntourageLibrary.Find("car.axo-sedan")!; var spec = EntourageRenderer.Spec(item, 40, 0, EntourageProjection.Isometric);
            Check(spec.View == EntourageView.Axonometric && spec.Projection == EntourageProjection.Isometric && Math.Abs(spec.SizePixels - 1.42 * 40) < 1e-9, "The default spec is wrong");
            var layer = EntourageRenderer.Create(spec, new Point(400, 500), "차");
            Check((EntourageRenderer.Anchor(layer) - new Point(400, 500)).Length < .01, "An axonometric item missed its ground point");
            int wide = layer.Pixels.Width;
            EntourageRenderer.Update(layer, spec with { Projection = EntourageProjection.Military });
            Check((EntourageRenderer.Anchor(layer) - new Point(400, 500)).Length < .05 && layer.Pixels.Width != wide, "Reprojecting moved the item or did not redraw it");
            var stand = EntourageRenderer.Anchor(layer); layer.FlipX = true; var moved = EntourageRenderer.Anchor(layer); layer.X += stand.X - moved.X;
            EntourageRenderer.Update(layer, layer.Entourage! with { Meters = 2.84, Fill = EntourageFill.Gray, Variant = 1 });
            Check((EntourageRenderer.Anchor(layer) - new Point(400, 500)).Length < .05 && layer.FlipX, "Restyling a flipped item moved it");
            // A flipped drawing in these projections is the item with its plan axes swapped: the same projection.
            var p = AxoProjection.Of(EntourageProjection.Isometric);
            var a = p.Project(new Point3D(30, 70, 50)); var b = p.Project(new Point3D(70, 30, 50));
            Check(Math.Abs(a.X + b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9, "A mirror image does not swap the plan axes");
            Check(EntourageRenderer.DocumentProjection(new Document { Width = 10, Height = 10 }) == null, "An empty document has a projection");
            var doc = new Document { Width = 800, Height = 600 }; doc.Add(layer);
            Check(EntourageRenderer.DocumentProjection(doc) == EntourageProjection.Military, "The document's projection is not its entourage's");
        });

        test("axonometric entourage: the scatter spreads on the projected ground", () =>
        {
            var center = new Point(600, 500); var p = AxoProjection.Of(EntourageProjection.Isometric);
            var a = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Axonometric, 1.7, 40, 24, 4, 12, center, null, null, 7);
            var b = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Axonometric, 1.7, 40, 24, 4, 12, center, null, null, 7);
            var c = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Axonometric, 1.7, 40, 24, 4, 12, center, null, null, 8);
            Check(a.Count == 12 && a.SequenceEqual(b) && !a.SequenceEqual(c), "Axonometric scatter is not seeded");
            double radius = 24 * (.75 + .55 * Math.Sqrt(12));
            Check(a.All(q => p.Ground(q.Anchor - center).Length <= radius + 1e-6 && q.Rotation == 0) && a.Any(q => q.Flip) && a.Any(q => !q.Flip) && a.Select(q => q.Variant).Distinct().Count() > 1,
                "Axonometric scatter left its ground disc, turned items or did not vary them");
            // On the sheet the disc is an ellipse: wider than tall.
            double spanX = a.Max(q => q.Anchor.X) - a.Min(q => q.Anchor.X), spanY = a.Max(q => q.Anchor.Y) - a.Min(q => q.Anchor.Y);
            Check(spanY < spanX * .85, $"The scatter is not spread on the projected ground: {spanX} × {spanY}");
            double nearest = a.SelectMany((q, i) => a.Skip(i + 1).Select(r => p.Ground(q.Anchor - r.Anchor).Length)).Min();
            Check(nearest > 24 * .4, "Axonometric scatter stacked items: " + nearest);
            Check(a.Zip(a.Skip(1)).All(pair => pair.First.Anchor.Y <= pair.Second.Anchor.Y), "Items are not ordered back to front");
            var area = new Rect(300, 300, 400, 200);
            var inside = EntourageRenderer.Scatter(EntourageCategory.Plants, EntourageView.Axonometric, 8, 20, 110, 4, 5, new Point(500, 400), area, q => q.X < 600, 3, EntourageProjection.Military);
            Check(inside.Count == 5 && inside.All(q => area.Contains(q.Anchor) && q.Anchor.X < 600), "Axonometric scatter left the selection");
        });

        test("axonometric entourage: saved for older versions and round trips", () =>
        {
            var spec = EntourageRenderer.Spec(EntourageLibrary.Find("tree.axo-round")!, 30, 2, EntourageProjection.Military);
            string json = JsonSerializer.Serialize(spec);
            using (var parsed = JsonDocument.Parse(json))
                Check(parsed.RootElement.GetProperty("View").GetInt32() == 0 && parsed.RootElement.GetProperty("Axonometric").GetBoolean() && parsed.RootElement.GetProperty("Projection").GetInt32() == 2,
                    "An axonometric item is not saved as an elevation item with its projection: " + json);
            Check(JsonSerializer.Deserialize<EntourageSpec>(json) == spec, "The axonometric spec did not round trip");
            string reordered = json.Replace("\"Axonometric\":true,", "").Replace("{", "{\"Axonometric\":true,");
            Check(JsonSerializer.Deserialize<EntourageSpec>(reordered) == spec, "The saved fields depend on their order");
            var old = EntourageRenderer.Spec(EntourageLibrary.Find("person.walking")!, 30);
            string oldJson = JsonSerializer.Serialize(old);
            Check(!oldJson.Contains("Axonometric", StringComparison.Ordinal) && !oldJson.Contains("Projection", StringComparison.Ordinal) && JsonSerializer.Deserialize<EntourageSpec>(oldJson) == old,
                "Elevation and plan items are saved differently than before: " + oldJson);
            Check(JsonSerializer.Deserialize<EntourageSpec>(oldJson.Replace("\"View\":0", "\"View\":1"))!.View == EntourageView.Plan, "A saved plan item does not load");
            bool refused = false; try { (EntourageTests.CustomSpec(EntourageTests.CustomItem()) with { View = EntourageView.Axonometric }).Validate(); } catch (InvalidDataException) { refused = true; }
            Check(refused, "A user's image was accepted as an axonometric item");
            var doc = new Document { Width = 900, Height = 700 };
            doc.Add(EntourageRenderer.Create(spec, new Point(300, 600), "나무", flip: true));
            doc.Add(EntourageRenderer.Create(EntourageRenderer.Spec(EntourageLibrary.Find("person.axo-pair")!, 30, 1, EntourageProjection.Military), new Point(600, 600), "두 사람"));
            string file = Path.Combine(directory, "entourage-axonometric.moruproj");
            ProjectStore.Save(doc, file);
            var read = ProjectStore.Load(file);
            Check(read.Layers[0].Entourage == doc.Layers[0].Entourage && read.Layers[1].Entourage!.View == EntourageView.Axonometric && read.Layers[0].FlipX, "The axonometric items lost their description");
            Check(Imaging.Render(read).Data.AsSpan().SequenceEqual(Imaging.Render(doc).Data), "The document renders differently after loading");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
            using (var reader = new StreamReader(zip.GetEntry("document.json")!.Open()))
                Check(!reader.ReadToEnd().Contains("\"View\": 2", StringComparison.Ordinal), "The project carries a view older versions refuse");
        });

        test("axonometric entourage: shadows lie along the projected ground", () =>
        {
            foreach (var projection in Enum.GetValues<EntourageProjection>())
            {
                var p = AxoProjection.Of(projection); var spec = EntourageRenderer.AxonometricShadow(projection, ShadowSpec.Default(ShadowProjection.Ground));
                // The shadow direction is the ground direction 70° from the first plan axis, as drawn.
                var ground = p.X * Math.Cos(-70 * Math.PI / 180) + p.Y * Math.Sin(-70 * Math.PI / 180);
                Check(Math.Abs(spec.Angle - (Math.Atan2(ground.Y, ground.X) * 180 / Math.PI + 360) % 360) < .1 && spec.Elevation is > 20 and < 60, $"{projection} shadow: {spec.Angle}° {spec.Elevation}°");
                // Length: a vertical of height h lies on the ground as h / tan(40°) of ground, drawn along the projection.
                double expected = ground.Length / Math.Tan(40 * Math.PI / 180) / p.VerticalScale;
                Check(Math.Abs(spec.GroundRatio() - expected) < .02, $"{projection} shadow length {spec.GroundRatio()}, expected {expected}");
            }
            var doc = new Document { Width = 600, Height = 500 };
            var person = EntourageRenderer.Create(EntourageRenderer.Spec(EntourageLibrary.Find("person.axo-standing")!, 80, 0, EntourageProjection.Isometric) with { Fill = EntourageFill.None }, new Point(200, 400), "사람");
            doc.Add(person);
            var cast = ShadowRenderer.Cast(ShadowRenderer.Silhouette(doc, [person.Id], 1, true), EntourageRenderer.AxonometricShadow(EntourageProjection.Isometric, ShadowSpec.Default(ShadowProjection.Ground)) with { Sources = [person.Id], Softness = 0 });
            Check(cast != null, "No shadow was cast");
            double sx = 0, sy = 0, sum = 0;
            for (int y = 0; y < cast!.Height; y++) for (int x = 0; x < cast.Width; x++) { double v = cast.Data[y * cast.Width + x]; sx += (cast.X + x) * v; sy += (cast.Y + y) * v; sum += v; }
            Check(sum > 0 && sx / sum > 220 && sy / sum < 400 && sy / sum > 330, $"The shadow does not fall to the back right of the feet: {sx / sum}, {sy / sum}");
        });

        test("entourage plan symbols: the richer ones are centred, readable at 1:500 and fuller than the simple ones", () =>
        {
            double scale100 = 96 / 2.54, scale500 = scale100 / 5;
            foreach (var id in NewIds.Where(i => EntourageLibrary.Find(i)!.View == EntourageView.Plan))
            {
                var item = EntourageLibrary.Find(id)!;
                Check(item.ShadowHeight > 0, $"{id} has no shadow height");
                for (int v = 0; v < item.Variants; v++)
                {
                    var art = EntourageLibrary.Art(item, v); var b = art.Bounds;
                    Check(Math.Abs(b.X + b.Width / 2) < b.Width * .2 && Math.Abs(b.Y + b.Height / 2) < b.Height * .2, $"{id} {v} is not centred");
                    var (_, small, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, scale500, v));
                    var (_, large, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, scale100, v));
                    Check(Covered(small, 60) >= 4 && Covered(large) > Covered(small), $"{id} {v} does not read at 1:500");
                }
            }
            int Lines(string id) => EntourageLibrary.Art(EntourageLibrary.Find(id)!, 0).Lines.Count;
            Check(Lines("person.plan-standing") > Lines("person.plan") && Lines("tree.plan-canopy") > Lines("tree.plan-branches") && Lines("car.plan-hatch") > 6, "The richer symbols are not more detailed");
        });
    }
}
