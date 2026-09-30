using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

public static class HatchPatternTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "hatch-patterns"); Directory.CreateDirectory(root);
        MaterialFill Fill(HatchPattern p, int width, int height, double tileWidth, double tileHeight) =>
            new(HatchPatternRenderer.Create(p), Guid.NewGuid(), "Area", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"), width, height, tileWidth, tileHeight);
        MaterialFill ImageFill(MaterialAsset asset, int width, int height, double tileWidth, double tileHeight) =>
            new(asset, Guid.NewGuid(), "Area", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"), width, height, tileWidth, tileHeight);
        Document Holder(MaterialFill fill)
        {
            var doc = new Document { Width = fill.Width, Height = fill.Height };
            doc.Add(new Layer { Name = "Fill", Kind = LayerKind.Material, Category = LayerCategory.Photo, Material = fill, Pixels = MaterialRenderer.Render(fill) });
            return doc;
        }
        static byte[] Alpha(Raster image) { var a = new byte[image.Width * image.Height]; for (int i = 0; i < a.Length; i++) a[i] = image.Data[i * 4 + 3]; return a; }
        static byte[] AlphaOf(BitmapSource bitmap) => Alpha(Raster.FromBitmap(bitmap));

        test("hatch catalog: 19 patterns with stable distinct IDs, keys, names and a spoof-proof identity", () =>
        {
            var all = HatchPatterns.All;
            Check(all.Count == 19 && all.Select(p => (int)p).SequenceEqual(Enumerable.Range(0, 19)), "The catalog is not the 19 append-only patterns");
            Check(all.Select(HatchPatterns.StableId).Distinct().Count() == 19 && all.Select(HatchPatterns.Key).Distinct().Count() == 19 && all.Select(HatchPatterns.Name).Distinct().Count() == 19, "IDs, keys or names repeat");
            Check(all.All(p => HatchPatterns.Source(p) == "morupixel:pattern/" + HatchPatterns.Key(p) + "@1"), "Sources do not follow the reserved form");
            Check(HatchPatterns.StableId(HatchPattern.GrassSparse) == Guid.Parse("4d6f7275-7069-7865-6c70-6174746e0100"), "The lawn ID changed: " + HatchPatterns.StableId(HatchPattern.GrassSparse));
            Check(all.All(p => HatchPatterns.TryParseKey(HatchPatterns.Key(p), out var back) && back == p) && !HatchPatterns.TryParseKey("gras", out _), "Keys do not round-trip");
            var asset = HatchPatternRenderer.Create(HatchPattern.Sand);
            Check(ReferenceEquals(asset, HatchPatternRenderer.Create(HatchPattern.Sand)) && asset.Name == "패턴 · 모래" && asset.Tileable && asset.Pixels.Width == 256 && HatchPatterns.TryGet(asset, out var sand) && sand == HatchPattern.Sand,
                "The library asset is not the shared canonical instance");
            var spoof = new MaterialAsset(Guid.NewGuid(), "잔디", Raster.Solid(4, 4, Colors.Green), HatchPatterns.Source(HatchPattern.GrassSparse), true);
            Check(!HatchPatterns.TryGet(spoof, out _) && !HatchPatterns.IsBuiltInMaterial(spoof), "A random-ID asset with a pattern Source was treated as a pattern");
            var spoofed = MaterialRenderer.Render(ImageFill(spoof, 16, 16, 4, 4));
            Check(spoofed.Data[(8 * 16 + 8) * 4 + 1] > 100 && spoofed.Data[(8 * 16 + 8) * 4 + 3] == 255, "The spoofed asset did not render its own pixels through the image path");
            Check(HatchPatterns.IsBuiltInMaterial(MaterialPresets.Create(MaterialKind.Wood)) && HatchPatterns.IsBuiltInMaterial(asset), "Built-in detection failed");
        });

        test("hatch geometry and canonical tiles are deterministic", () =>
        {
            foreach (var p in HatchPatterns.All)
            {
                var a = HatchPatternRenderer.Build(p); var b = HatchPatternRenderer.Build(p);
                Check(a.Marks.Length == b.Marks.Length && a.Marks.Length > 0 && a.PenDoc.SequenceEqual(b.PenDoc) && a.InkAlphaScale == b.InkAlphaScale &&
                    a.Marks.Zip(b.Marks).All(m => m.First.Anchor == m.Second.Anchor && m.First.Pen == m.Second.Pen && m.First.Closed == m.Second.Closed &&
                        m.First.Filled == m.Second.Filled && m.First.StretchLocal == m.Second.StretchLocal && m.First.Points.SequenceEqual(m.Second.Points)), $"{p}: geometry differs between builds");
                Check(a.Marks.All(m => m.Anchor.X >= 0 && m.Anchor.X < 1 && m.Anchor.Y >= 0 && m.Anchor.Y < 1), $"{p}: an anchor lies outside the unit tile");
                HatchPatternRenderer.ClearCache();
                var first = HatchPatternRenderer.Canonical(p); HatchPatternRenderer.ClearCache(); var second = HatchPatternRenderer.Canonical(p);
                Check(first.Width == 256 && first.Data.AsSpan().SequenceEqual(second.Data), $"{p}: canonical pixels are not reproducible");
            }
        });

        test("hatch tiles are transparent ink: paper shows through and ink takes the chosen color", () =>
        {
            foreach (var p in HatchPatterns.All)
            {
                var tile = HatchPatternRenderer.Create(p).Pixels; int clear = 0;
                for (int i = 0; i < tile.Data.Length; i += 4)
                {
                    if (tile.Data[i + 3] == 0) clear++;
                    if (tile.Data[i + 3] >= 200) Check(tile.Data[i] < 90 && tile.Data[i + 1] < 90 && tile.Data[i + 2] < 90, $"{p}: an opaque pixel is not dark ink");
                }
                Check(clear >= tile.Width * tile.Height * .35, $"{p}: only {clear * 100.0 / (tile.Width * tile.Height):0}% of the tile is clear");
                var red = Raster.FromBitmap(HatchPatternRenderer.Tile(p, 128, 128, 1.6, 0xFFC03020)); int ink = 0;
                for (int i = 0; i < red.Data.Length; i += 4)
                    if (red.Data[i + 3] >= 150) { ink++; Check(red.Data[i + 2] > red.Data[i + 1] + 60, $"{p}: the ink color was not used"); }
                Check(ink > 0, $"{p}: no ink pixels in the red tile");
            }
        });

        test("hatch tiles repeat without seams", () =>
        {
            foreach (var p in HatchPatterns.All)
            {
                // Every mark drawn at all nine neighbouring positions is what a seamless tile must equal.
                var tile = AlphaOf(HatchPatternRenderer.Tile(p, 128, 128, 1.6, 0)); var reference = AlphaOf(HatchPatternRenderer.ReferenceTile(p, 128, 128, 1.6));
                int worst = tile.Zip(reference).Max(x => Math.Abs(x.First - x.Second));
                Check(worst <= 4, $"{p}: tile edges differ from the wrapped reference by {worst}");
                var image = Alpha(MaterialRenderer.Render(Fill(p, 256, 256, 128, 128)));
                double mean = image.Average(a => (double)a);
                if (p is HatchPattern.Meadow or HatchPattern.Sand or HatchPattern.Stipple or HatchPattern.Gravel or HatchPattern.PavingSmall or HatchPattern.Cobble)
                {
                    double column = Enumerable.Range(0, 256).SelectMany(y => Enumerable.Range(124, 8).Select(x => (double)image[y * 256 + x])).Average();
                    double row = Enumerable.Range(124, 8).SelectMany(y => Enumerable.Range(0, 256).Select(x => (double)image[y * 256 + x])).Average();
                    Check(column >= mean * .5 && column <= mean * 2 && row >= mean * .5 && row <= mean * 2, $"{p}: ink near the seams ({column:0.#}, {row:0.#}) differs from the mean {mean:0.#}");
                }
                if (p == HatchPattern.Lines)
                    for (int y = 0; y < 256; y++)
                        if (image[y * 256 + 64] > 100) Check(Math.Abs(image[y * 256 + 127] - image[y * 256 + 128]) < 10, $"Lines: row {y} steps at the seam");
            }
        });

        test("hatch patterns stay crisp when zoomed, unlike a scaled image tile", () =>
        {
            var fill = Fill(HatchPattern.Lines, 200, 200, 100, 100);
            var zoomed = Alpha(DesignRenderer.Render(Holder(fill), new Rect(0, 0, 50, 50), 800, 800));
            var image = new MaterialAsset(Guid.NewGuid(), "Lines copy", HatchPatternRenderer.Create(HatchPattern.Lines).Pixels, "copy.png", true);
            var blurry = Alpha(DesignRenderer.Render(Holder(ImageFill(image, 200, 200, 100, 100)), new Rect(0, 0, 50, 50), 800, 800));
            double Soft(byte[] a) => a.Count(v => v > 20 && v < 230) / (double)Math.Max(1, a.Count(v => v >= 230));
            Check(zoomed.Max() >= 230, "Zoomed pattern lines are not solid");
            Check(Soft(zoomed) * 3 <= Soft(blurry), $"Pattern edges are not sharper: soft/ink {Soft(zoomed):0.###} vs image {Soft(blurry):0.###}");
            Check(HatchPatternRenderer.LargestCachedSide >= 1024, "The zoomed pattern was not redrawn at screen resolution");
        });

        test("hatch vertical ratio changes spacing and keeps dots round", () =>
        {
            List<(int W, int H)> Dots(double tileHeight)
            {
                var alpha = Alpha(DesignRenderer.Render(Holder(Fill(HatchPattern.Dots, 160, 160, 40, tileHeight)), new Rect(0, 0, 40, 40), 320, 320));
                var seen = new bool[alpha.Length]; var dots = new List<(int, int)>();
                for (int start = 0; start < alpha.Length; start++)
                {
                    if (seen[start] || alpha[start] <= 128) continue;
                    int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1; var stack = new Stack<int>(); stack.Push(start); seen[start] = true;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(), x = i % 320, y = i / 320;
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        foreach (int n in new[] { i - 1, i + 1, i - 320, i + 320 })
                            if (n >= 0 && n < alpha.Length && !seen[n] && alpha[n] > 128 && Math.Abs(n % 320 - x) <= 1) { seen[n] = true; stack.Push(n); }
                    }
                    if (left > 0 && top > 0 && right < 319 && bottom < 319) dots.Add((right - left + 1, bottom - top + 1));
                }
                return dots;
            }
            var square = Dots(40); var tall = Dots(80);
            Check(tall.Count > 5 && tall.All(d => Math.Abs(d.W - d.H) <= 1), "Dots were squashed by the ratio: " + string.Join(" ", tall.Select(d => $"{d.W}x{d.H}")));
            double ratio = tall.Count / (double)square.Count;
            Check(ratio is > .35 and < .65, $"Doubling the tile height did not double the vertical spacing ({tall.Count} vs {square.Count} dots)");
        });

        test("hatch line weight scales pen width and is validated", () =>
        {
            double Coverage(double weight) => Alpha(DesignRenderer.Render(Holder(Fill(HatchPattern.Lines, 128, 128, 64, 64) with { LineWeight = weight }), new Rect(0, 0, 64, 64), 256, 256)).Sum(a => (double)a);
            double ratio = Coverage(2) / Coverage(1);
            Check(ratio is >= 1.5 and <= 2.6, $"Line weight 2 covers {ratio:0.##}x of line weight 1");
            var fill = Fill(HatchPattern.Lines, 32, 32, 16, 16); var pixels = new Raster(32, 32);
            foreach (double bad in new[] { .05, 9, double.NaN })
            {
                bool rejected = false;
                try { MaterialEditing.ValidateFill(fill with { LineWeight = bad }, pixels); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, $"Line weight {bad} was accepted");
            }
            MaterialEditing.ValidateFill(fill with { LineWeight = .1 }, pixels); MaterialEditing.ValidateFill(fill with { LineWeight = 8 }, pixels);
        });

        test("hatch fills save and reopen with ink, line weight and ratio as the shared pattern", () =>
        {
            var doc = new Document { Width = 120, Height = 90 }; doc.Add(new Layer { Name = "Paper", Pixels = Raster.Solid(120, 90, Colors.White) });
            var region = MaterialEditing.Region(doc, "Yard", MaterialEditing.Polygon([[new(5, 5), new(110, 5), new(110, 80), new(5, 80)]]), "polygon"); doc.MaterialRegions.Add(region);
            doc.Materials.Add(HatchPatternRenderer.Create(HatchPattern.Cobble));
            var layer = MaterialEditing.Apply(doc, HatchPatterns.StableId(HatchPattern.Cobble), region.Id, 40, 60);
            layer.Material = layer.Material! with { Ink = 0xFF3366AA, LineWeight = 1.5 }; layer.Pixels = MaterialRenderer.Render(layer.Material); doc.Add(layer);
            var before = Imaging.Render(doc); string path = Path.Combine(root, "pattern.moruproj");
            ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            var fill = loaded.Layers.Single(l => l.Kind == LayerKind.Material).Material!;
            Check(fill == layer.Material, "The fill record changed on reopen");
            Check(ReferenceEquals(fill.Asset, HatchPatternRenderer.Create(HatchPattern.Cobble)) && ReferenceEquals(loaded.Materials.Single(), fill.Asset), "The reopened pattern is not the shared instance");
            Check(Imaging.Render(loaded).Data.AsSpan().SequenceEqual(before.Data), "Reopened pixels differ");
            using (var zip = ZipFile.OpenRead(path))
            {
                using var input = zip.GetEntry("document.json")!.Open();
                Check(JsonNode.Parse(input)!["Version"]!.GetValue<int>() == 6 && zip.GetEntry("materials/0.png") != null, "The format version or embedded fallback tile changed");
            }
        });

        test("hatch fill JSON reads older and newer project shapes", () =>
        {
            var info = ProjectStore.MaterialFillInfo.From(Fill(HatchPattern.Sand, 20, 20, 10, 10) with { Ink = 0xFF112233, LineWeight = 2 })!;
            var node = JsonSerializer.SerializeToNode(info)!.AsObject();
            Check(node["Ink"]!.GetValue<uint>() == 0xFF112233 && node["LineWeight"]!.GetValue<double>() == 2, "New fields are not written");
            node.Remove("Ink"); node.Remove("LineWeight");
            var old = JsonSerializer.Deserialize<ProjectStore.MaterialFillInfo>(node.ToJsonString())!;
            Check(old.Ink == 0 && old.LineWeight == 1 && old.TileWidth == 10, "Missing fields did not read as defaults");
            node["FutureField"] = 3;
            Check(JsonSerializer.Deserialize<ProjectStore.MaterialFillInfo>(node.ToJsonString())!.TileHeight == 10, "An unknown field broke reading");
            // A project written by Preview 37 (no Ink/LineWeight) still opens.
            var doc = new Document { Width = 40, Height = 32 };
            doc.Materials.Add(new MaterialAsset(Guid.NewGuid(), "Brick", Raster.Solid(4, 4, Colors.Red), "brick.png", true));
            var region = MaterialEditing.Region(doc, "Floor", MaterialEditing.Polygon([[new(2, 2), new(30, 2), new(30, 24)]]), "polygon"); doc.MaterialRegions.Add(region);
            doc.Add(MaterialEditing.Apply(doc, doc.Materials[0].Id, region.Id, 8, 8));
            string path = Path.Combine(root, "preview37.moruproj"); ProjectStore.Save(doc, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("document.json")!; JsonNode manifest;
                using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
                foreach (var l in manifest["Layers"]!.AsArray()) if (l!["Material"] is JsonObject m) { m.Remove("Ink"); m.Remove("LineWeight"); }
                entry.Delete();
                using var output = new StreamWriter(zip.CreateEntry("document.json").Open()); output.Write(manifest.ToJsonString());
            }
            var loaded = ProjectStore.Load(path).Layers.Single(l => l.Kind == LayerKind.Material).Material!;
            Check(loaded.Ink == 0 && loaded.LineWeight == 1 && loaded.TileWidth == 8, "A Preview 37 fill did not load");
        });

        test("hatch library entries tolerate other builds' fallback pixels but images stay strict", () =>
        {
            var sand = HatchPatternRenderer.Create(HatchPattern.Sand);
            var doc = new Document { Width = 40, Height = 40 }; doc.Materials.Add(sand);
            var other = sand with { Pixels = Raster.Solid(256, 256, Colors.Gray) };
            var layer = new Layer { Name = "Copy", Kind = LayerKind.Material, Material = Fill(HatchPattern.Sand, 20, 20, 10, 10) with { Asset = other } };
            layer.Pixels = MaterialRenderer.Render(layer.Material); doc.Layers.Add(layer);
            Check(MaterialEditing.Assets(doc).Count == 1 && ReferenceEquals(MaterialEditing.Assets(doc)[0], sand), "Two copies of one pattern were not merged");
            var id = Guid.NewGuid(); var strict = new Document { Width = 8, Height = 8 };
            strict.Materials.Add(new MaterialAsset(id, "A", Raster.Solid(2, 2, Colors.Red)));
            var clash = new Layer { Name = "B", Kind = LayerKind.Material, Material = ImageFill(new MaterialAsset(id, "A", Raster.Solid(2, 2, Colors.Blue)), 4, 4, 2, 2) };
            clash.Pixels = MaterialRenderer.Render(clash.Material); strict.Layers.Add(clash);
            string? message = null; try { MaterialEditing.Assets(strict); } catch (InvalidDataException e) { message = e.Message; }
            Check(message == "같은 재료 ID에 다른 원본이 있습니다.", "Different images with one ID were accepted");
            // A pattern layer copied into a document that already holds the pattern.
            var source = new Document { Width = 60, Height = 60 };
            var region = MaterialEditing.Region(source, "A", MaterialEditing.Polygon([[new(2, 2), new(50, 2), new(50, 50)]]), "polygon"); source.MaterialRegions.Add(region); source.Materials.Add(sand);
            var mapped = MaterialEditing.Apply(source, sand.Id, region.Id, 20, 20); source.Add(mapped);
            var target = new Document { Width = 60, Height = 60 }; target.Materials.Add(sand); target.Add(mapped.Snapshot()); target.Validate();
            Check(MaterialEditing.Assets(target).Count == 1, "The copied pattern layer duplicated its asset");
        });

        test("hatch swap keeps size, the user's ratio, direction, ink and line weight", () =>
        {
            var image = new MaterialAsset(Guid.NewGuid(), "Wide", Raster.Solid(32, 16, Colors.Brown), "wide.png");
            var fill = ImageFill(image, 40, 40, 40, 40 * .5 * 1.5) with { Angle = 15, OffsetX = 3, OffsetY = 2, Ink = 0xFF102030, LineWeight = 2 };
            var pattern = MaterialEditing.Swap(fill, HatchPatternRenderer.Create(HatchPattern.Brick));
            Check(Math.Abs(pattern.TileHeight - 60) < 1e-9 && pattern.TileWidth == 40 && pattern.Angle == 15 && pattern.OffsetX == 3 && pattern.OffsetY == 2 && pattern.Ink == 0xFF102030 && pattern.LineWeight == 2,
                "Swapping to a pattern lost a setting: " + pattern);
            Check(Math.Abs(MaterialEditing.Stretch(pattern) - 1.5) < 1e-9 && Math.Abs(MaterialEditing.Swap(pattern, image).TileHeight - 30) < 1e-9, "Swapping back did not restore the image ratio");
            Check(MaterialEditing.DefaultTile(900, 600) == 900 / 14d && MaterialEditing.DefaultTile(100, 100) == 40 && MaterialEditing.DefaultTile(9000, 100) == 320, "Default tile size changed");
        });

        test("hatch tile cache reuses tiles, stays in budget and never keeps a cancelled build", () =>
        {
            HatchPatternRenderer.ClearCache();
            var doc = Holder(Fill(HatchPattern.Gravel, 256, 256, 64, 64));
            int Builds() => HatchPatternRenderer.CacheStats.Builds;
            int start = Builds(); DesignRenderer.Render(doc, new Rect(0, 0, 256, 256), 263, 263);
            int first = Builds(); DesignRenderer.Render(doc, new Rect(0, 0, 256, 256), 263, 263);
            Check(first > start && Builds() == first, "Rendering the same view twice built the tile again");
            DesignRenderer.Render(doc, new Rect(0, 0, 256, 256), 268, 268);
            Check(Builds() == first, "A zoom within one size step built a new tile");
            for (int i = 0; i < 10; i++) HatchPatternRenderer.Tile(HatchPattern.Lines, 2048, 2048 - i, 1, 0);
            Check(HatchPatternRenderer.CacheStats.Bytes <= HatchPatternRenderer.CacheBudgetBytes, "The tile cache exceeded its budget");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool threw = false;
            try { HatchPatternRenderer.Tile(HatchPattern.Stipple, 300, 300, 2, 0, cancelled.Token); } catch (OperationCanceledException) { threw = true; }
            Check(threw, "A cancelled request did not throw");
            var tile = HatchPatternRenderer.Tile(HatchPattern.Stipple, 300, 300, 2, 0);
            Check(tile.PixelWidth == 300 && tile.IsFrozen, "The request after a cancellation did not build");
            var capped = HatchPatternRenderer.TileSize(100, 4000, 20);
            Check(capped.Width <= 2048 && capped.Height <= 2048 && (long)capped.Width * capped.Height <= 4L * 1024 * 1024, "A tile exceeded the size cap: " + capped);
        });

        test("hatch performance: a large stipple fill renders a screen view quickly and warm views reuse tiles", () =>
        {
            HatchPatternRenderer.ClearCache();
            var doc = new Document { Width = 8000, Height = 6000 };
            var asset = HatchPatternRenderer.Create(HatchPattern.Stipple); doc.Materials.Add(asset);
            var fill = new MaterialFill(asset, Guid.NewGuid(), "Field", new RegionPath("M0,0 L4800,0 L4800,3400 L0,3400 Z"), 4800, 3400, MaterialEditing.DefaultTile(8000, 6000), MaterialEditing.DefaultTile(8000, 6000));
            doc.Layers.Add(new Layer { Name = "Field", Kind = LayerKind.Material, Category = LayerCategory.Photo, Material = fill, X = 1000, Y = 1000, Pixels = new Raster(4800, 3400) });
            var clock = Stopwatch.StartNew();
            DesignRenderer.Render(doc, new Rect(0, 0, 8000, 6000), 1480, 1110);
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"Cold view took {clock.Elapsed.TotalMilliseconds:0} ms");
            int builds = HatchPatternRenderer.CacheStats.Builds;
            DesignRenderer.Render(doc, new Rect(0, 0, 8000, 6000), 1480, 1110);
            Check(HatchPatternRenderer.CacheStats.Builds - builds <= 2, "The warm view rebuilt tiles");
        });

        test("hatch patterns are suggested from CAD pattern and layer names", () =>
        {
            foreach (var (pattern, layer, role, expected) in new (string, string, DrawingRole, HatchPattern?)[] {
                ("AR-CONC", "A-FLOR", DrawingRole.Other, HatchPattern.Concrete), ("ANSI37", "A-FLOR", DrawingRole.Other, HatchPattern.Crosshatch),
                ("ANSI31", "A-FLOR", DrawingRole.Other, HatchPattern.Diagonal), ("AR-SAND", "L-SITE", DrawingRole.Other, HatchPattern.Sand),
                ("GRAVEL", "C-SITE", DrawingRole.Other, HatchPattern.Gravel), ("", "L-PLNT", DrawingRole.Furniture, HatchPattern.GrassSparse),
                ("BRICK", "A-WALL", DrawingRole.Structure, HatchPattern.Brick), ("SOLID", "A-WALL", DrawingRole.Structure, null),
                ("HONEY", "A-WALL", DrawingRole.Structure, HatchPattern.Concrete), ("HONEY", "A-FLOR", DrawingRole.Other, HatchPattern.Diagonal) })
                Check(DrawingCleanup.SuggestPattern(pattern, layer, role) == expected, $"{pattern}/{layer} did not suggest {expected}");
            Check(DrawingCleanup.Suggest("AR-CONC", "A-FLOR", DrawingRole.Other) == MaterialKind.Concrete && SelectionMaterials.Presets.Length == 8, "Preset recommendations changed");
            foreach (var surface in Enum.GetValues<SurfaceHint>())
                Check(SelectionMaterials.PatternOrder(surface).Order().SequenceEqual(HatchPatterns.All), $"{surface}: the pattern order is not a permutation of all patterns");
            Check(SelectionMaterials.PatternOrder(SurfaceHint.Ground)[0] == HatchPattern.GrassSparse && SelectionMaterials.PatternOrder(SurfaceHint.Wall)[0] == HatchPattern.Concrete
                && SelectionMaterials.PatternOrder(SurfaceHint.Floor)[0] == HatchPattern.PavingSmall && SelectionMaterials.PatternOrder(SurfaceHint.General).SequenceEqual(HatchPatterns.All), "Surface orders changed");
        });

        test("CAD import can fill hatches with line patterns below the linework", () =>
        {
            var cad = new CadDocument();
            var wall = new ACadSharp.Tables.Layer("A-WALL"); var hatches = new ACadSharp.Tables.Layer("A-HATCH");
            cad.Layers.Add(wall); cad.Layers.Add(hatches);
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
            var hatch = new Hatch { Pattern = new ACadSharp.Entities.HatchPattern("AR-CONC"), Layer = hatches };
            var path = new Hatch.BoundaryPath(); path.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 100, 0), new XYZ(180, 100, 0), new XYZ(180, 180, 0), new XYZ(20, 180, 0) }, true));
            hatch.Paths.Add(path); cad.Entities.Add(hatch);
            string file = Path.Combine(root, "pattern-plan.dxf"); DxfWriter.Write(file, cad);
            CompatibilityResult Read(CadCleanup cleanup) => CompatibilityImport.ReadAsync(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", RetainVectors: false,
                CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: cleanup)).GetAwaiter().GetResult();
            var info = CadCompatibility.InspectLayers(file);
            Check(info.HatchPatternCounts?.GetValueOrDefault(HatchPattern.Concrete) == 1, "Inspection did not count the hatch pattern");
            var result = Read(new CadCleanup(Hatches: HatchTreatment.Pattern)); var doc = result.Document;
            var material = doc.Layers.Single(l => l.Kind == LayerKind.Material);
            Check(material.Material!.Asset.Source == "morupixel:pattern/concrete@1" && doc.Layers.IndexOf(material) == 1 && material.Blend == BlendMode.Multiply,
                "The hatch was not filled with the concrete line pattern below the linework");
            Check(material.Material.RegionName.StartsWith("해치 · ", StringComparison.Ordinal) && DrawingLineCleanup.IsHatchMaterial(material) && material.Name.EndsWith("콘크리트", StringComparison.Ordinal),
                "The pattern layer is not a hatch material for the hatch toggle: " + material.Name);
            Check(result.Warnings.Any(w => w.StartsWith("해치를 선 패턴으로 채웠습니다: ", StringComparison.Ordinal)), "The pattern notice is missing");
            doc.Validate();
            var suggested = Read(new CadCleanup()).Document.Layers.Single(l => l.Kind == LayerKind.Material);
            Check(suggested.Material!.Asset.Source == "morupixel:preset/concrete", "Suggest mode changed");
            var settings = new ImportSettings { CadHatches = HatchTreatment.Pattern };
            string store = Path.Combine(root, "import-settings.json");
            Check(ImportSettingsStore.Save(settings, store).CadHatches == HatchTreatment.Pattern
                && ImportSettingsStore.Load(store).CadHatches == HatchTreatment.Pattern && settings.Cleanup()!.Hatches == HatchTreatment.Pattern, "The pattern choice was not remembered");
        });
    }
}
