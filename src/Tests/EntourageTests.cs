using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// 점경: the built-in library (stable ids, every item and variant drawn, deterministic), sizes and
// anchors, fills and line weights, the scatter, 내 점경 storage and conversion, project round trips,
// vector PDF export and shadows cast from the whole silhouette.
public static class EntourageTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // The ids saved in documents and used by the AI connection. Changing one breaks saved work.
    internal static readonly string[] StableIds =
    [
        "person.standing", "person.standing-side", "person.walking", "person.walking-bag", "person.sitting", "person.stroller", "person.child", "person.cyclist",
        "person.plan", "person.plan-walking", "tree.round", "tree.oval", "tree.airy", "tree.conifer", "tree.palm", "plant.shrub", "plant.grass",
        "tree.plan-lobed", "tree.plan-branches", "tree.plan-scribble", "tree.plan-conifer", "plant.plan-shrubs", "car.sedan", "car.hatch", "car.plan",
        "bike.side", "bike.plan", "bench.side", "bench.plan", "lamp.street", "table.parasol", "table.plan"
    ];

    static Raster Alpha(int width, int height, Func<int, int, byte> alpha, Color? color = null)
    {
        var raster = new Raster(width, height); var c = color ?? Colors.Black;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4; raster.Data[i] = c.B; raster.Data[i + 1] = c.G; raster.Data[i + 2] = c.R; raster.Data[i + 3] = alpha(x, y);
        }
        return raster;
    }

    static int Covered(Raster raster, byte threshold = 0) { int n = 0; for (int i = 3; i < raster.Data.Length; i += 4) if (raster.Data[i] > threshold) n++; return n; }

    static VectorContent.Scene Scene(VectorContent vector) { using var input = vector.Open(); return JsonSerializer.Deserialize<VectorContent.Scene>(input)!; }

    internal static EntourageCustomItem CustomItem(bool lines = false)
    {
        // A small figure: a disc head on a rounded body (an original test image), opaque colours or ink coverage.
        var pixels = Alpha(40, 100, (x, y) => (x - 20) * (x - 20) + (y - 12) * (y - 12) < 100 || y > 26 && x > 8 && x < 32 ? (byte)255 : (byte)0, lines ? Colors.Black : Color.FromRgb(200, 80, 40));
        return new EntourageCustomItem(Guid.NewGuid(), "내 사람", EntourageCategory.People, EntourageView.Elevation, 1.7, lines, pixels);
    }

    internal static EntourageSpec CustomSpec(EntourageCustomItem item, double ppm = 40) => new()
    {
        ItemId = item.ItemId, View = item.View, Meters = item.Meters, PixelsPerMeter = ppm, Fill = EntourageFill.None, LineDrawing = item.LineDrawing, Source = item.Pixels
    };

    public static void Run(Action<string, Action> test, string directory)
    {
        test("entourage library has stable unique ids in every category and view", () =>
        {
            var ids = EntourageLibrary.All.Select(i => i.Id).ToArray();
            Check(ids.Distinct().Count() == ids.Length && ids.All(EntourageSpec.IsValidId), "Duplicate or malformed entourage ids");
            Check(ids.SequenceEqual(StableIds), "Built-in entourage ids changed: " + string.Join(", ", ids));
            foreach (var category in Enum.GetValues<EntourageCategory>())
                foreach (var view in Enum.GetValues<EntourageView>())
                    Check(EntourageLibrary.All.Any(i => i.Category == category && i.View == view), $"No {category} item in {view}");
            Check(EntourageLibrary.All.All(i => i.DefaultMeters is >= EntourageSpec.MinMeters and <= 30 && i.Variants >= 1 && i.Name.Length > 0 && i.Keywords.Length > 0), "An item lacks a size, name or keywords");
            Check(EntourageLibrary.All.Where(i => i.View == EntourageView.Plan).All(i => i.ShadowHeight > 0), "A plan symbol has no shadow height");
            Check(EntourageLibrary.Find("person.walking")!.DefaultMeters == 1.7 && EntourageLibrary.Matches(EntourageLibrary.Find("bike.side")!, "자전거") && !EntourageLibrary.Matches(EntourageLibrary.Find("tree.round")!, "자전거"), "Search or defaults are wrong");
        });

        test("every built-in entourage item and variant draws non-empty and deterministically", () =>
        {
            foreach (var item in EntourageLibrary.All)
                for (int v = 0; v < item.Variants; v++)
                {
                    var a = item.Draw(v); var b = item.Draw(v);
                    Check(!a.Silhouette.Bounds.IsEmpty && a.Bounds.Width > 1 && a.Bounds.Height > 1, $"{item.Id} {v} has no silhouette");
                    Check(a.Silhouette.ToString(System.Globalization.CultureInfo.InvariantCulture) == b.Silhouette.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                        a.Lines.Count == b.Lines.Count, $"{item.Id} {v} is not deterministic");
                    if (item.View == EntourageView.Elevation) Check(Math.Abs(a.Bounds.Bottom) < 8, $"{item.Id} {v} does not stand on the ground: {a.Bounds.Bottom}");
                    else Check(Math.Abs(a.Bounds.X + a.Bounds.Width / 2) < a.Bounds.Width * .2 && Math.Abs(a.Bounds.Y + a.Bounds.Height / 2) < a.Bounds.Height * .2, $"{item.Id} {v} is not centred");
                    var (vector, preview, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, v));
                    Check(Covered(preview, 128) > 20 && vector.Width == preview.Width && vector.Height == preview.Height, $"{item.Id} {v} draws nothing");
                    var (again, _, _) = EntourageRenderer.Draw(EntourageRenderer.Spec(item, 30, v));
                    using var first = new MemoryStream(); using var second = new MemoryStream(); vector.Write(first); again.Write(second);
                    Check(first.ToArray().AsSpan().SequenceEqual(second.ToArray()), $"{item.Id} {v} vector differs between renders");
                }
            Check(EntourageLibrary.Find("tree.round")!.Draw(0).Silhouette.ToString() != EntourageLibrary.Find("tree.round")!.Draw(1).Silhouette.ToString(), "Variants of a tree are identical");
        });

        test("entourage sizes, anchors and shared line weights follow the scale", () =>
        {
            var person = EntourageLibrary.Find("person.standing")!; var tree = EntourageLibrary.Find("tree.round")!;
            var spec = EntourageRenderer.Spec(person, 50);
            var (vector, preview, anchor) = EntourageRenderer.Draw(spec);
            int bottom = -1, top = preview.Height;
            for (int y = 0; y < preview.Height; y++) for (int x = 0; x < preview.Width; x++) if (preview.Data[(y * preview.Width + x) * 4 + 3] > 100) { bottom = Math.Max(bottom, y); top = Math.Min(top, y); }
            Check(Math.Abs(bottom - top - 1.7 * 50) < 4 && Math.Abs(anchor.Y - bottom) < 3 && Math.Abs(anchor.X - preview.Width / 2.0) < preview.Width * .2, $"A 1.7 m person at 50 px/m is {bottom - top} px tall, anchor {anchor}");
            var layer = EntourageRenderer.Create(spec, new Point(300, 400), "사람");
            var at = EntourageRenderer.Anchor(layer); Check(Math.Abs(at.X - 300) < .01 && Math.Abs(at.Y - 400) < .01, "Placement missed its anchor");
            layer.Rotation = 20; layer.FlipX = true; var turned = EntourageRenderer.Anchor(layer);
            EntourageRenderer.Update(layer, spec with { Meters = 3.4, Fill = EntourageFill.Gray, Variant = 2 });
            var kept = EntourageRenderer.Anchor(layer); Check((kept - turned).Length < .05 && layer.Rotation == 20 && layer.FlipX, "Restyling moved the item or dropped its transform");
            Check(layer.Pixels.Height > preview.Height * 1.8, "Doubling the height did not redraw larger");
            layer.Scale = 1.5; var scaled = EntourageRenderer.Anchor(layer);
            EntourageRenderer.Update(layer, layer.Entourage! with { Meters = 1.7 }, resetSize: true);
            Check(layer.Scale == 1 && (EntourageRenderer.Anchor(layer) - scaled).Length < .05, "Resetting the size moved the item");
            double OutlineWidth(EntourageItem item) => Scene(EntourageRenderer.Draw(EntourageRenderer.Spec(item, 40)).Vector).Items.First(i => !i.Fill).StrokeWidth;
            Check(Math.Abs(OutlineWidth(person) - OutlineWidth(tree)) < 1e-9 && Math.Abs(OutlineWidth(person) - EntourageRenderer.BasePen(40)) < 1e-9, "Items at one scale do not share a line weight");
            Check(Scene(vector).Items.Where(i => !i.Fill).All(i => i.Round), "Entourage strokes are not round-capped");
            var pen = vector.Drawing.Children.OfType<GeometryDrawing>().Concat(vector.Drawing.Children.OfType<DrawingGroup>().SelectMany(g => g.Children.OfType<GeometryDrawing>())).First(d => d.Pen != null).Pen;
            Check(pen.StartLineCap == PenLineCap.Round && pen.LineJoin == PenLineJoin.Round, "Round strokes did not decode with round caps");
            Check(EntourageRenderer.DefaultPixelsPerMeter(new Document { Width = 1280, Height = 800, Dpi = 96 }) == Math.Round(96 / 2.54, 2), "The default scale is not 1:100 at the document DPI");
            bool refused = false; try { EntourageRenderer.Draw(spec with { Meters = 100, PixelsPerMeter = 100 }); } catch (InvalidDataException) { refused = true; }
            Check(refused, "An item larger than the size limit was drawn");
        });

        test("entourage fill styles change only the fill and the solid silhouette", () =>
        {
            var item = EntourageLibrary.Find("person.walking")!; var spec = EntourageRenderer.Spec(item, 40) with { LineArgb = 0xFF204060 };
            var none = Scene(EntourageRenderer.Draw(spec with { Fill = EntourageFill.None }).Vector);
            var white = Scene(EntourageRenderer.Draw(spec with { Fill = EntourageFill.White }).Vector);
            var gray = Scene(EntourageRenderer.Draw(spec with { Fill = EntourageFill.Gray }).Vector);
            var solid = Scene(EntourageRenderer.Draw(spec with { Fill = EntourageFill.Solid }).Vector);
            Check(!none.Items.Any(i => i.Fill) && white.Items.Count(i => i.Fill) == 1 && white.Items.First(i => i.Fill).Color == 0xFFFFFFFF, "White fill is wrong");
            Check(gray.Items.First(i => i.Fill).Color is var g && g != 0xFFFFFFFF && g != 0xFF204060, "Grey fill is not a tint of the line colour");
            Check(solid.Items.First(i => i.Fill).Color == 0xFF204060 && solid.Items.Length == 2 && none.Items.Length > 2, "Solid fill kept the inner lines or used another colour");
            Check(none.Items.Where(i => !i.Fill).All(i => i.Color == 0xFF204060), "Lines do not use the line colour");
        });

        test("entourage scatter is seeded, varied and stays in its area", () =>
        {
            var a = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Elevation, 1.7, 40, 20, 3, 12, new Point(500, 600), null, null, 7);
            var b = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Elevation, 1.7, 40, 20, 3, 12, new Point(500, 600), null, null, 7);
            var c = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Elevation, 1.7, 40, 20, 3, 12, new Point(500, 600), null, null, 8);
            Check(a.Count == 12 && a.SequenceEqual(b) && !a.SequenceEqual(c), "Scatter is not deterministic per seed");
            Check(a.All(p => p.Anchor.Y == 600 && p.Meters >= 1.7 * .93 && p.Meters <= 1.7 * 1.07 && p.Rotation == 0) && a.Any(p => p.Flip) && a.Any(p => !p.Flip) && a.Select(p => p.Variant).Distinct().Count() > 1,
                "Elevation scatter does not stand on the line with slight variations");
            var area = new Rect(100, 100, 400, 300);
            var plan = EntourageRenderer.Scatter(EntourageCategory.Plants, EntourageView.Plan, 5, 20, 100, 4, 6, new Point(300, 250), area, p => p.X < 400, 3);
            Check(plan.Count == 6 && plan.All(p => area.Contains(p.Anchor) && p.Anchor.X < 400) && plan.Select(p => p.Rotation).Distinct().Count() > 1, "Plan scatter left its area or did not turn the items");
            var spaced = plan.SelectMany((p, i) => plan.Skip(i + 1).Select(q => (p.Anchor - q.Anchor).Length)).Min();
            Check(spaced > 30, "Plan scatter stacked items: " + spaced);
            var line = EntourageRenderer.Scatter(EntourageCategory.Plants, EntourageView.Elevation, 8, 20, 120, 3, 4, new Point(0, 0), new Rect(200, 100, 600, 300), null, 1);
            Check(line.All(p => p.Anchor.Y == 400 && p.Anchor.X >= 200 && p.Anchor.X <= 800), "Elevation scatter ignored the selection's ground line");
        });

        test("entourage specs validate kinds, ids and sizes", () =>
        {
            void Rejects(Action action, string message) { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } Check(failed, message); }
            var spec = EntourageRenderer.Spec(EntourageLibrary.Find("tree.round")!, 40);
            var layer = EntourageRenderer.Create(spec, new Point(100, 500), "나무");
            Document.ValidateLayer(layer);
            Rejects(() => Document.ValidateLayer(new Layer { Name = "x", Pixels = new Raster(4, 4), Entourage = spec }), "A built-in item on an image layer was accepted");
            var custom = CustomItem();
            Rejects(() => Document.ValidateLayer(new Layer { Name = "x", Pixels = new Raster(4, 4), Entourage = CustomSpec(custom) with { Source = null } }), "A user item without its original was accepted");
            Rejects(() => (spec with { ItemId = "Tree Round" }).Validate(), "A malformed id was accepted");
            Rejects(() => (spec with { Meters = 100, PixelsPerMeter = 100 }).Validate(), "An oversized item was accepted");
            Rejects(() => (spec with { LineArgb = 0x00FFFFFF }).Validate(), "A transparent line colour was accepted");
            Rejects(() => (spec with { LineDrawing = true }).Validate(), "A built-in item marked as a line drawing was accepted");
            var doc = new Document { Width = 400, Height = 600 }; doc.Add(layer);
            var history = new History(); history.Reset(doc); var before = doc.Snapshot();
            doc.Layers[0].Entourage = spec with { Variant = 1 };
            history.Commit("spec", before, doc);
            Check(history.CanUndo, "A change of the entourage description alone was not recorded");
            var copy = layer.Snapshot(); DocumentFeatures.Rasterize(copy);
            Check(copy.Entourage == null && copy.Kind == LayerKind.Raster, "Painting on entourage kept its description");
        });

        test("내 점경 conversion keeps transparency, cuts out photos and turns drawings into lines", () =>
        {
            var transparent = Alpha(300, 200, (x, y) => x > 50 && x < 120 && y > 20 && y < 180 ? (byte)255 : (byte)0, Colors.SeaGreen);
            Check(EntourageImport.Suggest(transparent) == EntourageImportMode.Transparent, "A transparent image was not recognized");
            var kept = EntourageImport.Convert(transparent, EntourageImportMode.Transparent);
            Check(kept.Width == 69 && kept.Height == 159 && kept.Data[1] == Colors.SeaGreen.G, "Transparent margins were not trimmed or colours changed: " + kept.Width + "x" + kept.Height);
            var photo = Raster.Solid(200, 300, Color.FromRgb(120, 140, 200));
            var mask = new byte[200 * 300]; for (int y = 60; y < 280; y++) for (int x = 70; x < 130; x++) mask[y * 200 + x] = 255;
            var cut = EntourageImport.Convert(photo, EntourageImportMode.RemoveBackground, _ => mask);
            Check(cut.Width == 60 && cut.Height == 220 && Covered(cut, 250) == 60 * 220, "The cut-out did not follow the mask");
            var drawing = MainWindow.EntourageSampleDrawing();
            Check(EntourageImport.Suggest(drawing) == EntourageImportMode.LineDrawing, "A pen drawing on paper was not recognized");
            var lines = EntourageImport.Convert(drawing, EntourageImportMode.LineDrawing);
            Check(lines.Width < drawing.Width && Covered(lines, 128) > 500 && Covered(lines, 128) < lines.Width * lines.Height / 3, "The drawing did not become lines");
            var large = EntourageImport.Fit(Raster.Solid(3000, 1500, Colors.Black), EntourageStoreLimits.MaxSide);
            Check(large.Width == 1024 && large.Height == 512, "Large images are not reduced to the stored size");
            bool refused = false; try { EntourageImport.Convert(new Raster(20, 20), EntourageImportMode.Transparent); } catch (InvalidDataException) { refused = true; }
            Check(refused, "An empty image was accepted");
        });

        test("내 점경 styles recolour lines, fill closed areas and keep originals shared", () =>
        {
            // A ring of ink: its inside is enclosed, the outside is not.
            var ring = Alpha(60, 60, (x, y) => { double d = Math.Sqrt((x - 30) * (x - 30) + (y - 30) * (y - 30)); return d is > 20 and < 24 ? (byte)255 : (byte)0; });
            var item = new EntourageCustomItem(Guid.NewGuid(), "고리", EntourageCategory.Props, EntourageView.Plan, 1, true, ring);
            var spec = CustomSpec(item) with { LineArgb = 0xFFCC2200 };
            var lines = EntourageRenderer.Style(ring, spec);
            int centre = (30 * 60 + 30) * 4, rim = (30 * 60 + 52) * 4;
            Check(lines.Data[rim + 2] == 0xCC && lines.Data[rim + 3] == 255 && lines.Data[centre + 3] == 0, "Lines were not recoloured");
            var white = EntourageRenderer.Style(ring, spec with { Fill = EntourageFill.White });
            Check(white.Data[centre + 3] == 255 && white.Data[centre] == 255 && white.Data[3] == 0, "The enclosed area was not filled white");
            var photo = CustomItem(); var photoSpec = CustomSpec(photo) with { LineArgb = 0xFF102030 };
            Check(ReferenceEquals(EntourageRenderer.Style(photo.Pixels, photoSpec), photo.Pixels), "An unchanged original was copied");
            var solid = EntourageRenderer.Style(photo.Pixels, photoSpec with { Fill = EntourageFill.Solid });
            int head = (12 * 40 + 20) * 4;
            Check(solid.Data[head] == 0x30 && solid.Data[head + 2] == 0x10 && solid.Data[head + 3] == 255 && solid.Data[3] == 0, "The silhouette is not in the line colour");
            var grey = EntourageRenderer.Style(photo.Pixels, photoSpec with { Fill = EntourageFill.Gray });
            Check(grey.Data[head] == grey.Data[head + 1] && grey.Data[head + 1] == grey.Data[head + 2], "Greyscale kept colour");
            var layer = EntourageRenderer.Create(photoSpec, new Point(200, 300), photo.Name);
            Check(layer.Kind == LayerKind.Raster && Math.Abs(layer.Pixels.Height * layer.Scale - 1.7 * 40) < .01 && (EntourageRenderer.Anchor(layer) - new Point(200, 300)).Length < .01, "A user item is not 1.7 m at the scale or missed its anchor");
            EntourageRenderer.Update(layer, layer.Entourage! with { Fill = EntourageFill.Solid });
            Check(layer.Entourage!.Source == photo.Pixels && !ReferenceEquals(layer.Pixels, photo.Pixels), "Restyling lost the original");
        });

        test("내 점경 library is stored per user in its own folder", () =>
        {
            string folder = Path.Combine(directory, "entourage-store-" + Guid.NewGuid().ToString("N"));
            var first = CustomItem() with { Name = "  산책하는\u0001 사람  " }; var second = CustomItem(lines: true);
            EntourageStore.Save([first, second], folder);
            var loaded = EntourageStore.Load(folder);
            Check(loaded.Count == 2 && loaded[0].Id == first.Id && loaded[0].Name == "산책하는 사람" && loaded[1].LineDrawing && loaded[0].Pixels.Data.AsSpan().SequenceEqual(first.Pixels.Data),
                "The library did not round trip");
            File.WriteAllText(Path.Combine(folder, second.Id.ToString("N") + ".png"), "broken");
            Check(EntourageStore.Load(folder).Count == 1, "A damaged entry was not skipped");
            EntourageStore.Save([first], folder);
            Check(Directory.GetFiles(folder, "*.png").Length == 1, "A removed item's image stayed");
            Check(EntourageStore.Load(Path.Combine(folder, "missing")).Count == 0, "A missing library is not empty");
            bool refused = false; try { EntourageStore.ValidatePixels(new Raster(1500, 10)); } catch (InvalidDataException) { refused = true; }
            Check(refused, "An oversized item was accepted");
        });

        test("entourage project round trip keeps specs, vectors and the user's originals", () =>
        {
            var doc = new Document { Width = 800, Height = 600 };
            var tree = EntourageRenderer.Create(EntourageRenderer.Spec(EntourageLibrary.Find("tree.conifer")!, 30, 2) with { Fill = EntourageFill.Gray, LineArgb = 0xFF335577, LineWeight = 1.5 }, new Point(200, 500), "침엽수", flip: true);
            doc.Add(tree);
            var custom = CustomItem(); var styled = EntourageRenderer.Create(CustomSpec(custom) with { Fill = EntourageFill.Solid }, new Point(500, 500), custom.Name);
            doc.Add(styled);
            var plain = EntourageRenderer.Create(CustomSpec(custom), new Point(600, 500), custom.Name); doc.Add(plain);
            string file = Path.Combine(directory, "entourage-roundtrip.moruproj");
            ProjectStore.Save(doc, file);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                Check(zip.GetEntry("entourage/1.png") != null && zip.GetEntry("entourage/2.png") == null && zip.GetEntry("vectors/0.source") != null, "Originals were not stored only when restyled");
            var read = ProjectStore.Load(file);
            Check(read.Layers[0].Entourage == tree.Entourage && read.Layers[0].Kind == LayerKind.Vector && read.Layers[0].FlipX, "The built-in item lost its description");
            Check(read.Layers[1].Entourage! with { Source = null } == styled.Entourage! with { Source = null } && read.Layers[1].Entourage!.Source!.Data.AsSpan().SequenceEqual(custom.Pixels.Data), "The restyled user item lost its original");
            Check(read.Layers[2].Entourage!.Source!.Data.AsSpan().SequenceEqual(custom.Pixels.Data), "The plain user item lost its original");
            Check(Imaging.Render(read).Data.AsSpan().SequenceEqual(Imaging.Render(doc).Data), "The document renders differently after loading");
            EntourageRenderer.Update(read.Layers[1], read.Layers[1].Entourage! with { Fill = EntourageFill.None });
            Check(read.Layers[1].Pixels.Data.AsSpan().SequenceEqual(custom.Pixels.Data), "A loaded user item could not return to its original");
            EntourageRenderer.Update(read.Layers[0], read.Layers[0].Entourage! with { Meters = 6 });
            Check(read.Layers[0].Pixels.Height < tree.Pixels.Height, "A loaded built-in item could not be redrawn");
        });

        test("entourage exports as PDF vectors and casts its whole silhouette as a shadow", () =>
        {
            var doc = new Document { Width = 900, Height = 500 };
            foreach (var (id, x) in new[] { ("person.walking", 150.0), ("tree.round", 400.0), ("car.sedan", 700.0) })
                doc.Add(EntourageRenderer.Create(EntourageRenderer.Spec(EntourageLibrary.Find(id)!, 40), new Point(x, 450), id));
            var plan = PdfLayerExport.Build(doc, true);
            Check(plan.VectorParts == 3 && plan.ImageParts == 0, $"Entourage did not export as vectors: {plan.VectorParts} vector, {plan.ImageParts} image");
            foreach (var format in new[] { CompatibilityExportFormat.PdfLayers, CompatibilityExportFormat.AiLayers, CompatibilityExportFormat.PdfSingle })
            {
                using var output = new MemoryStream(); CompatibilityExport.Write(doc, format, output);
                string text = Encoding.Latin1.GetString(output.ToArray());
                Check(output.Length > 1000 && !text.Contains("/Subtype /Image", StringComparison.Ordinal) && !text.Contains("/Subtype/Image", StringComparison.Ordinal), $"{format} placed an image of the entourage");
            }
            // A person drawn as lines only still shades the ground like a solid figure.
            var lineOnly = new Document { Width = 300, Height = 300 }; var filled = new Document { Width = 300, Height = 300 };
            var spec = EntourageRenderer.Spec(EntourageLibrary.Find("person.standing")!, 100) with { Fill = EntourageFill.None };
            var a = EntourageRenderer.Create(spec, new Point(150, 260), "a"); lineOnly.Add(a);
            var b = EntourageRenderer.Create(spec with { Fill = EntourageFill.Solid }, new Point(150, 260), "b"); filled.Add(b);
            int Area(Document d, Guid id) => ShadowRenderer.Silhouette(d, [id], 1, false).Items.Sum(m => m.Data.Count(v => v > 128));
            int lines = Covered(a.Pixels, 128), area = Area(lineOnly, a.Id), solid = Area(filled, b.Id);
            Check(area > lines * 2 && Math.Abs(area - solid) < solid * .03, $"The shadow source is not the silhouette: lines {lines}, cast {area}, solid {solid}");
            var cast = ShadowRenderer.Cast(ShadowRenderer.Silhouette(lineOnly, [a.Id], 1, false), ShadowSpec.Default(ShadowProjection.Ground) with { Sources = [a.Id] });
            Check(cast != null && cast.Data.Count(v => v > 60) > area / 4, "A ground shadow was not cast");
            // Crisp at zoom: the design renderer draws the paths at the device scale.
            var zoomed = DesignRenderer.Render(lineOnly, new Rect(120, 100, 60, 60), 360, 360);
            Check(zoomed.Data.Where((_, i) => i % 4 == 3).Count(v => v == 255) > 1000, "Entourage is not redrawn sharply when zoomed");
        });
    }
}
