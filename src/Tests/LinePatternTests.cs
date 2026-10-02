using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// The user's line patterns (image → ink coverage tile), the 내 패턴 store, pattern favorites in the
// workspace layout, and a line pattern's background color.
public static class LinePatternTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "line-patterns"); Directory.CreateDirectory(root);
        static int A(Raster r, int x, int y) => r.Data[(y * r.Width + x) * 4 + 3];
        static (int B, int G, int R, int A) Px(Raster r, int x, int y) { int i = (y * r.Width + x) * 4; return (r.Data[i], r.Data[i + 1], r.Data[i + 2], r.Data[i + 3]); }
        // A "scan": warm paper with grain and dark lines (two px wide every 16 px), one antialiased edge row.
        static Raster Scan(int width, int height, Color paper, Color ink, int period = 16)
        {
            var image = new Raster(width, height); var random = new Random(7);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4; int row = y % period;
                double t = row is 4 or 5 ? 1 : row == 6 ? .5 : 0, grain = (random.NextDouble() - .5) * 18;
                image.Data[i] = (byte)Math.Clamp(paper.B + (ink.B - paper.B) * t + grain * (1 - t), 0, 255);
                image.Data[i + 1] = (byte)Math.Clamp(paper.G + (ink.G - paper.G) * t + grain * (1 - t), 0, 255);
                image.Data[i + 2] = (byte)Math.Clamp(paper.R + (ink.R - paper.R) * t + grain * (1 - t), 0, 255);
                image.Data[i + 3] = 255;
            }
            return image;
        }
        static MaterialFill Fill(MaterialAsset asset, int width, int height, double tileWidth, double tileHeight, string? boundary = null) =>
            new(asset, Guid.NewGuid(), "Area", new RegionPath(boundary ?? $"M0,0 L{width},0 L{width},{height} L0,{height} Z"), width, height, tileWidth, tileHeight);
        MaterialAsset Stripes(int period = 8, int size = 64, int thickness = 2)
        {
            var tile = new Raster(size, size);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (x % period < thickness) tile.Data[(y * size + x) * 4 + 3] = 255;
            return LinePatterns.Create("세로줄", tile);
        }

        test("line pattern conversion: dark strokes become ink, light paper becomes transparent", () =>
        {
            var source = LinePatternSource.From(Scan(64, 64, Color.FromRgb(0xF4, 0xEE, 0xE0), Color.FromRgb(0x20, 0x20, 0x28)));
            Check(source.AutoThreshold is > .2 and < .8, $"The automatic threshold {source.AutoThreshold:0.###} is not between paper and ink");
            var tile = source.Convert(source.AutoThreshold);
            Check(tile.Width == 64 && tile.Height == 64, "A small image changed size");
            for (int y = 0; y < 64; y++)
            {
                int row = y % 16, a = A(tile, 10, y);
                if (row is 4 or 5) Check(a == 255, $"Ink row {y} is not solid ({a})");
                else if (row is 0 or 1 or 9 or 12) Check(a == 0, $"Paper row {y} kept grain ({a})");
            }
            Check(A(tile, 10, 6) is > 20 and < 250, "The antialiased edge row lost its partial coverage: " + A(tile, 10, 6));
            var asset = LinePatterns.Create("스캔", tile);
            var (b, g, r, _) = Px(asset.Pixels, 10, 4);
            Check(LinePatterns.IsCustom(asset) && asset.Tileable && asset.Source == LinePatterns.Source && b == 0x26 && g == 0x26 && r == 0x26 && A(asset.Pixels, 10, 4) == 255,
                "The asset does not store the default ink with coverage alpha");
            // Ink on a transparent image converts the same way; colored ink on colored paper too.
            var clear = new Raster(32, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) if (y % 8 < 2) { int i = (y * 32 + x) * 4; clear.Data[i + 3] = 255; }
            var fromAlpha = LinePatternSource.From(clear); var clearTile = fromAlpha.Convert(fromAlpha.AutoThreshold);
            Check(A(clearTile, 3, 0) == 255 && A(clearTile, 3, 4) == 0, "Lines on a transparent image did not convert");
            var blue = LinePatternSource.From(Scan(48, 48, Color.FromRgb(0xFF, 0xF4, 0xC8), Color.FromRgb(0x10, 0x30, 0xA0)));
            var blueTile = blue.Convert(blue.AutoThreshold);
            Check(A(blueTile, 3, 4) == 255 && A(blueTile, 3, 0) == 0, "Blue lines on yellow paper did not convert");
            // The slider moves the threshold: a high threshold keeps only the darkest pixels.
            static double Ink(Raster t) => t.Data.Where((_, i) => i % 4 == 3).Sum(v => (double)v);
            Check(Ink(source.Convert(.8)) < Ink(tile) && Ink(source.Convert(.1)) > Ink(tile), "The threshold does not change the amount of ink");
        });

        test("line pattern conversion rejects blank and flat images and keeps tiles bounded", () =>
        {
            foreach (var (image, label) in new[] { (Raster.Solid(40, 40, Colors.White), "white"), (Raster.Solid(40, 40, Color.FromRgb(120, 120, 120)), "flat grey"), (new Raster(40, 40), "transparent") })
            {
                string? message = null;
                try { var s = LinePatternSource.From(image); s.Convert(s.AutoThreshold); } catch (InvalidDataException e) { message = e.Message; }
                Check(message != null, $"A {label} image became a pattern");
            }
            string? tiny = null; try { LinePatternSource.From(new Raster(3, 40)); } catch (InvalidDataException e) { tiny = e.Message; }
            Check(tiny != null, "A 3 px image was accepted");
            var large = LinePatternSource.From(Scan(3000, 1500, Colors.White, Colors.Black, 60));
            Check(large.Width == LinePatterns.MaxSide && large.Height == 512, $"A large image was not reduced to {LinePatterns.MaxSide} px: {large.Width}×{large.Height}");
            var reduced = large.Convert(large.AutoThreshold);
            Check(reduced.Width == 1024 && reduced.Height == 512 && reduced.Data.Where((_, i) => i % 4 == 3).Any(v => v == 255), "The reduced tile lost its lines");
            bool rejected = false;
            try { LinePatterns.Create("큰 타일", new Raster(LinePatterns.MaxSide + 1, 8)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "An oversized tile became a pattern");
            var oversized = new MaterialAsset(Guid.NewGuid(), "큰", new Raster(LinePatterns.MaxSide + 1, 8), LinePatterns.Source, true);
            Check(!LinePatterns.IsCustom(oversized) && !LinePatterns.IsCustom(new MaterialAsset(Guid.NewGuid(), "a", new Raster(8, 8), LinePatterns.Source, false))
                && !LinePatterns.IsCustom(HatchPatternRenderer.Create(HatchPattern.Brick) with { Source = LinePatterns.Source }), "An oversized, non-tiling or built-in-ID asset was treated as a line pattern");
            // Trimming crops blank paper around the drawing.
            var framed = Raster.Solid(80, 60, Colors.White);
            for (int y = 20; y < 40; y++) for (int x = 30; x < 50; x++) if ((x + y) % 5 == 0) { int i = (y * 80 + x) * 4; framed.Data[i] = framed.Data[i + 1] = framed.Data[i + 2] = 0; }
            var frame = LinePatternSource.From(framed);
            var trimmed = frame.Convert(frame.AutoThreshold, trim: true); var whole = frame.Convert(frame.AutoThreshold);
            Check(whole.Width == 80 && whole.Height == 60 && trimmed.Width <= 21 && trimmed.Height <= 21 && trimmed.Width >= 18, $"Trimming did not crop the margins: {trimmed.Width}×{trimmed.Height}");
        });

        test("line patterns tile without seams at any resampled size", () =>
        {
            var asset = Stripes();
            foreach (int size in new[] { 48, 64, 96, 23 })
            {
                var tile = Raster.FromBitmap(LinePatternRenderer.Tile(asset, size, size, 1, 0));
                Check(tile.Width == size && tile.Height == size, "Tile size differs");
                // Period 8 of 64 → size/8 px: every column equals the one a period later, across the wrap too.
                double period = size / 8d; if (period != Math.Floor(period)) continue;
                for (int x = 0; x < size; x++)
                    Check(Math.Abs(A(tile, x, size / 2) - A(tile, (x + (int)period) % size, size / 2)) <= 2, $"{size} px: column {x} breaks the period at the seam");
            }
            var stored = Raster.FromBitmap(LinePatternRenderer.Tile(asset, 64, 64, 1, 0));
            for (int x = 0; x < 64; x++) Check(A(stored, x, 7) == A(asset.Pixels, x, 7), "The tile at its own size differs from the stored coverage");
            // A rendered fill of three repeats: the seams between repeats match the inside of a repeat.
            var image = MaterialRenderer.Render(Fill(asset, 144, 48, 48, 48));
            for (int x = 0; x < 136; x++) Check(Math.Abs(A(image, x, 20) - A(image, x + 6, 20)) <= 3, $"The rendered fill steps at x={x}");
            Check(Math.Abs(MaterialEditing.Stretch(Fill(asset, 10, 10, 30, 30)) - 1) < 1e-9 && MaterialEditing.RepeatScale(asset) == 1, "Ratio or repeat scale of a square tile changed");
        });

        test("line patterns take ink color and line weight like built-in patterns", () =>
        {
            var asset = Stripes(16, 64, 3);
            double stroke = LinePatternRenderer.Stroke(asset);
            Check(Math.Abs(stroke - 3) < .01, "The stroke estimate is not the drawn width: " + stroke);
            var red = MaterialRenderer.Render(Fill(asset, 64, 64, 64, 64) with { Ink = 0xFFC03020 });
            var (b, g, r, a) = Px(red, 1, 10);
            Check(a == 255 && r == 0xC0 && g == 0x30 && b == 0x20 && Px(red, 8, 10).A == 0, "The ink color was not used or paper is not clear");
            double Coverage(double weight) => MaterialRenderer.Render(Fill(asset, 64, 64, 64, 64) with { LineWeight = weight }).Data.Where((_, i) => i % 4 == 3).Sum(v => (double)v);
            double one = Coverage(1), two = Coverage(2), half = Coverage(.5);
            Check(two / one is >= 1.6 and <= 2.4 && half / one is >= .35 and <= .7, $"Line weight 2 covers {two / one:0.##}× and 0.5 covers {half / one:0.##}× of weight 1");
            // A thickened tile still repeats seamlessly: the line at x=0 grows to both sides of the wrap.
            var thick = Raster.FromBitmap(LinePatternRenderer.Tile(asset, 64, 64, 2, 0));
            Check(A(thick, 63, 5) > 100 && A(thick, 3, 5) > 100 && A(thick, 8, 5) == 0, "The thicker line did not wrap around the tile edge");
            // Thumbnails and swatches work for user patterns.
            var fill = Fill(asset, 40, 40, 20, 20); Check(MaterialRenderer.PatternThumbnail(fill) is { PixelWidth: 56 } && LinePatternRenderer.Swatch(asset, 96).PixelWidth == 96, "Thumbnail or swatch failed");
        });

        test("pattern background color fills the region under the lines and is clear by default", () =>
        {
            foreach (var asset in new[] { HatchPatternRenderer.Create(HatchPattern.Lines), Stripes() })
            {
                string triangle = "M0,0 L64,0 L0,64 Z";
                var plain = MaterialRenderer.Render(Fill(asset, 64, 64, 32, 32, triangle));
                var yellow = MaterialRenderer.Render(Fill(asset, 64, 64, 32, 32, triangle) with { Background = 0xFFFFE070 });
                int paperX = -1, paperY = 10;
                for (int x = 2; x < 40 && paperX < 0; x++) if (A(plain, x, paperY) == 0) paperX = x;
                Check(paperX > 0, "No clear paper pixel in the plain fill");
                var (b, g, r, a) = Px(yellow, paperX, paperY);
                Check(a == 255 && r == 0xFF && g == 0xE0 && b == 0x70, $"{asset.Name}: the background color is not under the lines: {r},{g},{b},{a}");
                Check(Px(yellow, 60, 60).A == 0 && Px(plain, 60, 60).A == 0, "The background leaked outside the region");
                // Line pixels stay ink: darker than the background.
                int inkX = 0, inkY = 0;
                for (int y = 0; y < 30; y++) for (int x = 0; x < 30; x++) if (A(plain, x, y) > A(plain, inkX, inkY)) { inkX = x; inkY = y; }
                Check(A(plain, inkX, inkY) > 120 && Px(yellow, inkX, inkY).R < 0xD0, "The lines are not drawn over the background");
                // A transparent pick and image materials have no background: pixels as before.
                Check(MaterialRenderer.Render(Fill(asset, 64, 64, 32, 32, triangle) with { Background = 0x00FFE070 }).Data.AsSpan().SequenceEqual(plain.Data), "An alpha-0 background changed the pixels");
            }
            var image = new MaterialAsset(Guid.NewGuid(), "Bricks", Raster.Solid(8, 8, Color.FromArgb(128, 200, 30, 30)), "bricks.png", true);
            Check(MaterialRenderer.Render(Fill(image, 32, 32, 8, 8) with { Background = 0xFF00FF00 }).Data.AsSpan().SequenceEqual(MaterialRenderer.Render(Fill(image, 32, 32, 8, 8)).Data),
                "An image material painted a pattern background");
        });

        test("pattern background renders through multiply, zoomed vector marks and document export", () =>
        {
            var doc = new Document { Width = 100, Height = 80 };
            doc.Add(new Layer { Name = "Paper", Pixels = Raster.Solid(100, 80, Colors.White) });
            doc.Add(new Layer { Name = "Blue", Pixels = Raster.Solid(50, 80, Color.FromRgb(0x40, 0x80, 0xFF)), X = 50 });
            var region = MaterialEditing.Region(doc, "Yard", MaterialEditing.Polygon([[new(0, 0), new(100, 0), new(100, 80), new(0, 80)]]), "polygon"); doc.MaterialRegions.Add(region);
            doc.Materials.Add(HatchPatternRenderer.Create(HatchPattern.DotsSparse));
            // A 50 px repeat: x and x + 50 (over the blue half) are the same spot of the pattern.
            var layer = MaterialEditing.Apply(doc, HatchPatterns.StableId(HatchPattern.DotsSparse), region.Id, 50, 50);
            Check(layer.Blend == BlendMode.Multiply && layer.Material!.Background == 0, "A new pattern layer is not a clear multiply fill");
            layer.Material = layer.Material! with { Background = 0xFFFFE070 }; layer.Pixels = MaterialRenderer.Render(layer.Material); doc.Add(layer);
            var flat = Imaging.Render(doc);
            // Clear paper of the pattern: white × yellow = yellow; blue × yellow = darker green-blue.
            int x0 = -1, y0 = -1;
            var clear = MaterialRenderer.Render(layer.Material with { Background = 0 });
            for (int y = 0; y < 40 && x0 < 0; y++) for (int x = 0; x < 40; x++) if (A(clear, x, y) == 0 && A(clear, x + 50, y) == 0) { x0 = x; y0 = y; break; }
            var left = Px(flat, x0, y0); var right = Px(flat, x0 + 50, y0);
            Check(left.R == 0xFF && left.G == 0xE0 && left.B == 0x70, $"Multiply over white is not the background color: {left}");
            Check(Math.Abs(right.R - 0x40 * 0xFF / 255) <= 2 && Math.Abs(right.G - 0x80 * 0xE0 / 255) <= 2 && Math.Abs(right.B - 0xFF * 0x70 / 255) <= 2, $"Multiply over blue is wrong: {right}");
            // The export path (document renderer) matches the flattened layers.
            var exported = DesignRenderer.Render(doc, new Rect(0, 0, 100, 80), 100, 80);
            var e = Px(exported, x0, y0);
            Check(Math.Abs(e.R - left.R) <= 2 && Math.Abs(e.G - left.G) <= 2 && Math.Abs(e.B - left.B) <= 2, "The exported pixels differ from the layer");
            // A repeat too large for a tile bitmap draws vector marks, with the background under them.
            var big = new Document { Width = 400, Height = 400 };
            var area = MaterialEditing.Region(big, "Field", MaterialEditing.Polygon([[new(0, 0), new(400, 0), new(400, 400), new(0, 400)]]), "polygon"); big.MaterialRegions.Add(area);
            big.Materials.Add(HatchPatternRenderer.Create(HatchPattern.Lines));
            var mapped = MaterialEditing.Apply(big, HatchPatterns.StableId(HatchPattern.Lines), area.Id, 300, 300); mapped.Blend = BlendMode.Normal;
            mapped.Material = mapped.Material! with { Background = 0xFF3060C0 }; mapped.Pixels = MaterialRenderer.Render(mapped.Material); big.Add(mapped);
            Check(HatchPatternRenderer.NeedsVector(300, 300, 16), "The zoom does not reach the vector path");
            var zoomed = DesignRenderer.Render(big, new Rect(0, 0, 25, 25), 400, 400);
            int blue = 0; for (int y = 0; y < 400; y += 7) for (int x = 0; x < 400; x += 7) { var p = Px(zoomed, x, y); if (p.A == 255 && p.B == 0xC0 && p.R == 0x30) blue++; }
            Check(blue > 2000, $"The zoomed vector view lost the background ({blue} samples)");
        });

        test("line pattern documents round-trip and open without the user's library", () =>
        {
            var source = LinePatternSource.From(Scan(48, 48, Colors.White, Colors.Black));
            var asset = LinePatterns.Create("손그림 벽돌", source.Convert(source.AutoThreshold));
            var doc = new Document { Width = 120, Height = 90 }; doc.Add(new Layer { Name = "Paper", Pixels = Raster.Solid(120, 90, Colors.White) });
            var region = MaterialEditing.Region(doc, "Wall", MaterialEditing.Polygon([[new(5, 5), new(110, 5), new(110, 80), new(5, 80)]]), "polygon"); doc.MaterialRegions.Add(region);
            doc.Materials.Add(asset);
            var layer = MaterialEditing.Apply(doc, asset.Id, region.Id, 30, 30);
            layer.Material = layer.Material! with { Ink = 0xFF3366AA, LineWeight = 1.5, Background = 0xFFF0E0C0, Angle = 20 }; layer.Pixels = MaterialRenderer.Render(layer.Material); doc.Add(layer);
            var before = Imaging.Render(doc); string path = Path.Combine(root, "custom-pattern.moruproj");
            ProjectStore.Save(doc, path);
            LinePatternRenderer.ClearCache();
            var loaded = ProjectStore.Load(path);
            var fill = loaded.Layers.Single(l => l.Kind == LayerKind.Material).Material!;
            Check(fill with { Asset = asset } == layer.Material && LinePatterns.IsCustom(fill.Asset) && fill.Asset.Name == "손그림 벽돌" && fill.Asset.Id == asset.Id
                && fill.Asset.Pixels.Data.AsSpan().SequenceEqual(asset.Pixels.Data), "The pattern fill did not reopen as the same line pattern");
            Check(Imaging.Render(loaded).Data.AsSpan().SequenceEqual(before.Data), "Reopened pixels differ");
            using (var zip = ZipFile.OpenRead(path))
            {
                JsonNode manifest; using (var input = zip.GetEntry("document.json")!.Open()) manifest = JsonNode.Parse(input)!;
                Check(manifest["Materials"]!.AsArray().Any(m => m!["Source"]!.GetValue<string>() == LinePatterns.Source) && zip.GetEntry("materials/0.png") != null
                    && manifest["Layers"]!.AsArray().Any(l => l!["Material"] is JsonObject m && m["Background"]!.GetValue<uint>() == 0xFFF0E0C0), "The pattern tile or background is not stored in the document");
            }
            // An older build reads the tile as an image material: the stored pixels are dark ink on clear paper.
            var fallback = new MaterialAsset(asset.Id, asset.Name, asset.Pixels, "older-build.png", true);
            var old = MaterialRenderer.Render(new MaterialFill(fallback, Guid.NewGuid(), "A", new RegionPath("M0,0 L48,0 L48,48 L0,48 Z"), 48, 48, 48, 48));
            Check(Px(old, 10, 4).A == 255 && Px(old, 10, 4).R < 0x40 && Px(old, 10, 0).A == 0, "The fallback pixels are not dark lines on clear paper");
        });

        test("pattern fills without a background read and render as before", () =>
        {
            var info = ProjectStore.MaterialFillInfo.From(Fill(HatchPatternRenderer.Create(HatchPattern.Sand), 20, 20, 10, 10) with { Background = 0xFF112233 })!;
            var node = JsonSerializer.SerializeToNode(info)!.AsObject();
            Check(node["Background"]!.GetValue<uint>() == 0xFF112233, "The background is not written");
            node.Remove("Background");
            var old = JsonSerializer.Deserialize<ProjectStore.MaterialFillInfo>(node.ToJsonString())!;
            Check(old.Background == 0 && old.TileWidth == 10 && old.Ink == 0 && old.LineWeight == 1, "A fill without a background did not read as clear");
            var fill = Fill(HatchPatternRenderer.Create(HatchPattern.Brick), 64, 64, 32, 32);
            Check(fill.Background == 0 && MaterialRenderer.Render(fill).Data.AsSpan().SequenceEqual(MaterialRenderer.Render(fill with { Background = 0 }).Data), "The default fill is not clear");
            // Swapping keeps the background with the other pattern settings.
            var swapped = MaterialEditing.Swap(fill with { Background = 0xFF00FF00 }, HatchPatternRenderer.Create(HatchPattern.Sand));
            Check(swapped.Background == 0xFF00FF00, "Swapping lost the background");
        });

        test("내 패턴 store keeps patterns, names and order and skips damaged entries", () =>
        {
            string store = Path.Combine(root, "store-" + Guid.NewGuid().ToString("N")[..8]);
            Check(LinePatternStore.Load(store).Count == 0, "A missing store is not empty");
            var first = Stripes(); var second = LinePatterns.Create("점", Stripes(4, 32, 1).Pixels);
            LinePatternStore.Save([first, second], store);
            var loaded = LinePatternStore.Load(store);
            Check(loaded.Count == 2 && loaded[0].Id == first.Id && loaded[0].Name == "세로줄" && loaded[1].Name == "점" && loaded.All(LinePatterns.IsCustom)
                && loaded[0].Pixels.Data.AsSpan().SequenceEqual(first.Pixels.Data), "The store did not round-trip");
            LinePatternStore.Save([second with { Name = "  고운 점\n" }], store);
            loaded = LinePatternStore.Load(store);
            Check(loaded.Count == 1 && loaded[0].Name == "고운 점" && !File.Exists(Path.Combine(store, first.Id.ToString("N") + ".png")), "Rename or removal was not stored");
            File.WriteAllText(Path.Combine(store, second.Id.ToString("N") + ".png"), "not a png");
            Check(LinePatternStore.Load(store).Count == 0, "A damaged tile was not skipped");
            File.WriteAllText(Path.Combine(store, "patterns.json"), "{ broken");
            Check(LinePatternStore.Load(store).Count == 0, "A broken index was not ignored");
            Check(LinePatternStore.CleanName("") == "내 패턴" && LinePatternStore.CleanName(new string('가', 200)).Length == LinePatternStore.MaxNameLength, "Names are not cleaned");
        });

        test("pattern favorites are saved with the workspace layout and sanitized", () =>
        {
            string store = Path.Combine(root, "workspace-" + Guid.NewGuid().ToString("N")[..8] + ".json");
            var custom = LinePatterns.FavoriteKey(Stripes());
            Check(LinePatterns.FavoriteKey(HatchPatternRenderer.Create(HatchPattern.Brick)) == "brick" && custom.StartsWith("custom:", StringComparison.Ordinal) && custom.Length == 39, "Favorite keys changed");
            WorkspaceLayoutStore.Save(new WorkspaceLayout { PatternFavorites = ["brick", custom, "brick", "nonsense", "custom:xyz", "sand"] }, store);
            var loaded = WorkspaceLayoutStore.Load(store)!;
            Check(loaded.PatternFavorites!.SequenceEqual(new[] { "brick", custom, "sand" }), "Favorites were not saved in order or not cleaned: " + string.Join(",", loaded.PatternFavorites!));
            Check(WorkspaceLayoutStore.Sanitize(new WorkspaceLayout())!.PatternFavorites == null, "A layout without favorites gained some");
        });
    }
}
