using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Screentones in the hatch pattern library: tone screens by ink coverage, black poché, and the dot and
// stipple gradients whose density runs across the region (catalog, coverage, seams, ink, zoom, gradient
// direction and edges, seeds, saving, exports and thumbnails).
public static class ScreentoneTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "screentones"); Directory.CreateDirectory(root);
        static MaterialFill Fill(HatchPattern p, double width, double height, double tile, ToneGradient? gradient = null) =>
            new(HatchPatternRenderer.Create(p), Guid.NewGuid(), "Area", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"), (int)Math.Ceiling(width), (int)Math.Ceiling(height), tile, tile, Gradient: gradient);
        static Document Holder(MaterialFill fill)
        {
            var doc = new Document { Width = fill.Width, Height = fill.Height };
            doc.Add(new Layer { Name = "Fill", Kind = LayerKind.Material, Category = LayerCategory.Photo, Material = fill, Pixels = MaterialRenderer.Render(fill) });
            return doc;
        }
        static double Mean(Raster image) { double sum = 0; for (int i = 3; i < image.Data.Length; i += 4) sum += image.Data[i]; return sum / (image.Width * (double)image.Height) / 255; }
        static byte[] Alpha(Raster image) { var a = new byte[image.Width * image.Height]; for (int i = 0; i < a.Length; i++) a[i] = image.Data[i * 4 + 3]; return a; }
        var screens = HatchPatterns.All.Where(p => HatchPatterns.Coverage(p) is not null).ToArray();
        var tones = HatchPatterns.All.Where(HatchPatterns.IsScreentone).ToArray();

        test("screentone catalog: 13 tone screens in their own group after the line patterns, with stable keys, IDs and versioned sources", () =>
        {
            Check(tones.Length == 13 && tones.Select(p => (int)p).SequenceEqual(Enumerable.Range(19, 13)) && HatchPatterns.All.Count(p => HatchPatterns.Group(p) == HatchGroup.Line) == 19,
                "The screentones are not the 13 patterns appended after the 19 line patterns");
            Check(tones.Select(HatchPatterns.Key).SequenceEqual(new[] { "dot-screen-10", "dot-screen-20", "dot-screen-30", "dot-screen-45", "dot-screen-60", "dot-screen-75",
                "line-screen-20", "line-screen-35", "line-screen-50", "grid-screen-30", "solid-black", "dot-gradient", "stipple-gradient" }), "Screentone keys changed");
            Check(HatchPatterns.Name(HatchPattern.SolidBlack) == "검정 채움" && HatchPatterns.Name(HatchPattern.DotGradient) == "점 그라데이션" && HatchPatterns.Name(HatchPattern.StippleGradient) == "점묘 그라데이션"
                && HatchPatterns.Name(HatchPattern.DotScreen30) == "점 스크린 30%", "Korean names changed");
            Check(HatchPatterns.Source(HatchPattern.DotScreen10) == "morupixel:pattern/dot-screen-10@1" && HatchPatterns.StableId(HatchPattern.StippleGradient) == Guid.Parse("4d6f7275-7069-7865-6c70-6174746e011f"),
                "A screentone source or ID is not in the reserved, versioned form");
            Check(tones.All(p => System.Text.RegularExpressions.Regex.IsMatch(HatchPatterns.Key(p), AutomationCatalog.PatternIdPattern) && HatchPatterns.TryParseKey(HatchPatterns.Key(p), out var back) && back == p
                && LinePatterns.IsFavoriteKey(HatchPatterns.Key(p)) && LinePatterns.IsPattern(HatchPatternRenderer.Create(p))), "A screentone key is not a valid patternId, favorite or pattern");
            Check(HatchPatterns.All.Where(HatchPatterns.IsGradient).SequenceEqual(new[] { HatchPattern.DotGradient, HatchPattern.StippleGradient }) && HatchPatterns.Coverage(HatchPattern.DotGradient) == null
                && HatchPatterns.Coverage(HatchPattern.SolidBlack) == 1 && HatchPatterns.Coverage(HatchPattern.Brick) == null, "Gradient or coverage classification changed");
            foreach (var surface in Enum.GetValues<SurfaceHint>())
                Check(SelectionMaterials.PatternOrder(surface).TakeLast(13).SequenceEqual(tones), $"{surface}: screentones do not follow the line patterns in catalog order");
            Check(tones.All(p => HatchPatternRenderer.Geometry(p).TileUnits && HatchPatternRenderer.Create(p).Tileable), "Screentones are not sized to their repeat");
        });

        test("screentones ink their nominal coverage within 3% at default sizes, other sizes, rotation and huge repeats", () =>
        {
            foreach (var p in screens)
            {
                double nominal = HatchPatterns.Coverage(p)!.Value;
                foreach (var (width, height) in new[] { (900, 600), (2400, 1700), (600, 420) })
                {
                    double tile = MaterialEditing.DefaultTile(width, height, HatchPatternRenderer.Create(p));
                    double mean = Mean(MaterialRenderer.Render(Fill(p, 512, 512, tile)));
                    Check(Math.Abs(mean - nominal) <= .03, $"{HatchPatterns.Key(p)}: {mean:P1} ink at the default repeat of a {width}×{height} document, not {nominal:P0}");
                }
                // Other sizes down to the smallest default repeat (40 px; finer lines meet the rasterizer's 1/8 px steps).
                foreach (double tile in new[] { 40, 48, 128 })
                {
                    double mean = Mean(MaterialRenderer.Render(Fill(p, 512, 512, tile)));
                    Check(Math.Abs(mean - nominal) <= .03, $"{HatchPatterns.Key(p)}: {mean:P1} ink at a {tile} px repeat");
                }
                var turned = MaterialRenderer.Render(Fill(p, 512, 512, 64) with { Angle = 30 });
                Check(Math.Abs(Mean(turned) - nominal) <= .03, $"{HatchPatterns.Key(p)}: {Mean(turned):P1} ink when rotated");
            }
            // A repeat too large for a tile (8x zoom of a 300 px repeat) is drawn as vector marks with the same coverage.
            foreach (var p in new[] { HatchPattern.DotScreen20, HatchPattern.DotScreen75, HatchPattern.LineScreen35 })
            {
                var doc = Holder(Fill(p, 300, 300, 300));
                Check(HatchPatternRenderer.NeedsVector(300, 300, 8), "The vector threshold changed");
                double mean = Mean(DesignRenderer.Render(doc, new Rect(0, 0, 300, 300), 2400, 2400));
                Check(Math.Abs(mean - HatchPatterns.Coverage(p)!.Value) <= .02, $"{HatchPatterns.Key(p)}: the vector repeat inks {mean:P1}");
            }
        });

        test("screentone tiles repeat without seams and take the ink, background and line weight", () =>
        {
            foreach (var p in tones)
                foreach (int size in new[] { 64, 77 })
                {
                    var tile = Alpha(Raster.FromBitmap(HatchPatternRenderer.Tile(p, size, size, 1, 0)));
                    var reference = Alpha(Raster.FromBitmap(HatchPatternRenderer.ReferenceTile(p, size, size, 1)));
                    int worst = tile.Zip(reference).Max(x => Math.Abs(x.First - x.Second));
                    Check(worst <= 4, $"{HatchPatterns.Key(p)}: a {size} px tile differs from the wrapped reference by {worst}");
                }
            // Ink and background: red dots on a yellow region.
            var tinted = MaterialRenderer.Render(Fill(HatchPattern.DotScreen30, 128, 128, 128) with { Ink = 0xFFD02010, Background = 0xFFFFE070 });
            int red = 0, yellow = 0;
            for (int i = 0; i < tinted.Data.Length; i += 4)
            {
                if (tinted.Data[i + 3] < 250) continue;
                if (tinted.Data[i + 2] > 180 && tinted.Data[i + 1] < 60) red++;
                if (tinted.Data[i + 2] > 240 && tinted.Data[i + 1] > 200 && tinted.Data[i] < 140) yellow++;
            }
            Check(red > 128 * 128 / 6 && yellow > 128 * 128 / 3 && Mean(tinted) > .99, $"The ink or background was not used ({red} red, {yellow} yellow)");
            // Heavier lines mean more ink: dots grow, holes shrink, lines thicken.
            foreach (var (p, expected) in new[] { (HatchPattern.DotScreen10, .4), (HatchPattern.DotScreen75, 1 - .25 / 4), (HatchPattern.LineScreen20, .4) })
            {
                double light = Mean(MaterialRenderer.Render(Fill(p, 256, 256, 128) with { LineWeight = .5 })), heavy = Mean(MaterialRenderer.Render(Fill(p, 256, 256, 128) with { LineWeight = 2 }));
                Check(light < HatchPatterns.Coverage(p) && Math.Abs(heavy - expected) <= .03, $"{HatchPatterns.Key(p)}: line weight 50% inks {light:P1}, 200% inks {heavy:P1} (expected {expected:P1})");
            }
        });

        test("screentones stay crisp when zoomed and keep their tone when zoomed out", () =>
        {
            var fill = Fill(HatchPattern.DotScreen45, 256, 256, 64);
            var zoomed = Alpha(DesignRenderer.Render(Holder(fill), new Rect(0, 0, 64, 64), 512, 512));
            var copy = new MaterialAsset(Guid.NewGuid(), "Copy", HatchPatternRenderer.Create(HatchPattern.DotScreen45).Pixels, "copy.png", true);
            var blurry = Alpha(DesignRenderer.Render(Holder(fill with { Asset = copy }), new Rect(0, 0, 64, 64), 512, 512));
            static double Soft(byte[] a) => a.Count(v => v > 20 && v < 230) / (double)Math.Max(1, a.Count(v => v >= 230));
            Check(zoomed.Count(v => v >= 250) > zoomed.Length / 4 && Soft(zoomed) * 2 <= Soft(blurry), $"Zoomed dots are not sharp: soft/ink {Soft(zoomed):0.###} vs image {Soft(blurry):0.###}");
            foreach (var p in new[] { HatchPattern.DotScreen10, HatchPattern.DotScreen75, HatchPattern.GridScreen30, HatchPattern.DotGradient, HatchPattern.StippleGradient })
            {
                var doc = Holder(Fill(p, 640, 640, 40, new ToneGradient(0, .3, .3)));
                double full = Mean(DesignRenderer.Render(doc, new Rect(0, 0, 640, 640), 640, 640)), small = Mean(DesignRenderer.Render(doc, new Rect(0, 0, 640, 640), 160, 160));
                Check(Math.Abs(small - full) <= .04, $"{HatchPatterns.Key(p)}: the 25% view inks {small:P1}, 100% inks {full:P1}");
            }
        });

        test("black poché fills the region with the ink at any size and over its background", () =>
        {
            var ring = new RegionPath("M0,0 L100,0 L100,80 L0,80 Z M20,20 L80,20 L80,60 L20,60 Z");
            var fill = new MaterialFill(HatchPatternRenderer.Create(HatchPattern.SolidBlack), Guid.NewGuid(), "Wall", ring, 100, 80, 7, 7, Angle: 33, Ink: 0xFF101820);
            var pixels = MaterialRenderer.Render(fill);
            byte At(int x, int y, int c) => pixels.Data[(y * 100 + x) * 4 + c];
            Check(At(5, 5, 3) == 255 && At(5, 5, 2) == 0x10 && At(5, 5, 0) == 0x20 && At(50, 40, 3) == 0 && At(95, 75, 3) == 255, "The poché does not fill the ring with the ink");
            var zoomed = DesignRenderer.Render(Holder(fill), new Rect(0, 0, 25, 25), 400, 400);
            Check(Alpha(zoomed).Take(400 * 300).Count(v => v == 255) > 400 * 290, "The zoomed poché is not solid");
            var thumbnail = Raster.FromBitmap(MaterialRenderer.PatternThumbnail(fill)!);
            Check(thumbnail.Data[(10 * 56 + 6) * 4] < 60, "The poché thumbnail does not show the ink");
        });

        // Ink coverage in `strips` bands along the gradient direction over the pixels wholly inside the region,
        // and what the gradient asks for there (ToneGradientRenderer.CoverageAt, inlined).
        static (double[] Measured, double[] Expected) Strips(MaterialFill fill, Raster pixels, int strips)
        {
            var sum = new double[strips]; var expected = new double[strips]; var count = new int[strips];
            var gradient = ToneGradient.Of(fill); var (min, max) = ToneGradientRenderer.Extent(fill.Boundary.Geometry, gradient.Angle);
            var inside = Imaging.Draw(pixels.Width, pixels.Height, dc => dc.DrawGeometry(Brushes.Black, null, fill.Boundary.Geometry));
            double radians = gradient.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            for (int y = 0; y < pixels.Height; y++)
                for (int x = 0; x < pixels.Width; x++)
                {
                    int i = (y * pixels.Width + x) * 4;
                    if (inside.Data[i + 3] != 255) continue;
                    double t = Math.Clamp(((x + .5) * cos + (y + .5) * sin - min) / (max - min), 0, 1);
                    int s = Math.Min(strips - 1, (int)(t * strips)); sum[s] += pixels.Data[i + 3] / 255d; expected[s] += gradient.Start + (gradient.End - gradient.Start) * t; count[s]++;
                }
            return (sum.Select((v, i) => v / Math.Max(1, count[i])).ToArray(), expected.Select((v, i) => v / Math.Max(1, count[i])).ToArray());
        }

        test("dot and stipple gradients run monotonically from the start to the end density along their direction", () =>
        {
            foreach (var p in new[] { HatchPattern.DotGradient, HatchPattern.StippleGradient })
            {
                double tolerance = p == HatchPattern.DotGradient ? .02 : .05, noise = p == HatchPattern.DotGradient ? .005 : .03;
                foreach (var gradient in new[] { new ToneGradient(0, .1, .9), new ToneGradient(90, .05, .95, 3), new ToneGradient(180, .2, .8), new ToneGradient(-45, .9, .1) })
                {
                    var fill = Fill(p, 480, 320, 64, gradient);
                    var (measured, expected) = Strips(fill, MaterialRenderer.Render(fill), 10);
                    for (int s = 0; s < 10; s++)
                        Check(Math.Abs(measured[s] - expected[s]) <= tolerance, $"{HatchPatterns.Key(p)} {gradient}: strip {s} inks {measured[s]:P1}, expected {expected[s]:P1}");
                    double sign = Math.Sign(gradient.End - gradient.Start);
                    for (int s = 1; s < 10; s++)
                        Check(sign * (measured[s] - measured[s - 1]) >= -noise, $"{HatchPatterns.Key(p)} {gradient}: the density turns back between strips {s - 1} and {s}");
                    // The edges hold the start and end densities.
                    var (edges, wanted) = Strips(fill, MaterialRenderer.Render(fill), 40);
                    Check(Math.Abs(edges[0] - wanted[0]) <= tolerance + .02 && Math.Abs(edges[39] - wanted[39]) <= tolerance + .02 && Math.Abs(wanted[0] - gradient.Start) < .02 && Math.Abs(wanted[39] - gradient.End) < .02,
                        $"{HatchPatterns.Key(p)} {gradient}: the edges ink {edges[0]:P1} and {edges[39]:P1}");
                }
                // A region of another shape: the density spans its own extent along a diagonal.
                var oval = new MaterialFill(HatchPatternRenderer.Create(p), Guid.NewGuid(), "Oval", new RegionPath("M10,150 A190,140 0 1 1 390,150 A190,140 0 1 1 10,150 Z"), 400, 300, 56, 56, Gradient: new ToneGradient(45, 0, 1));
                var (ovalMeasured, ovalExpected) = Strips(oval, MaterialRenderer.Render(oval), 8);
                for (int s = 0; s < 8; s++)
                    Check(Math.Abs(ovalMeasured[s] - ovalExpected[s]) <= tolerance + .01 && (s == 0 || ovalMeasured[s] >= ovalMeasured[s - 1] - noise), $"{HatchPatterns.Key(p)}: oval strip {s} inks {ovalMeasured[s]:P1}, expected {ovalExpected[s]:P1}");
                Check(MaterialRenderer.Render(Fill(p, 64, 64, 32, new ToneGradient(0, 0, 0))).Data.All(b => b == 0) && Mean(MaterialRenderer.Render(Fill(p, 64, 64, 32, new ToneGradient(0, 1, 1)))) > .99,
                    "0% and 100% are not empty and solid");
            }
        });

        test("gradients follow the repeat size and rotation, keep dots round and stay crisp when zoomed", () =>
        {
            // The dot lattice matches the uniform dot screens at the same size and rotation.
            var screen = MaterialRenderer.Render(Fill(HatchPattern.DotScreen30, 256, 256, 64) with { Angle = 20 });
            var even = MaterialRenderer.Render(Fill(HatchPattern.DotGradient, 256, 256, 64, new ToneGradient(0, .3, .3)) with { Angle = 20 });
            int differ = Alpha(screen).Zip(Alpha(even)).Count(x => Math.Abs(x.First - x.Second) > 100);
            Check(differ < 256 * 256 / 50, $"The flat dot gradient does not line up with the 30% dot screen ({differ} pixels differ)");
            var doc = Holder(Fill(HatchPattern.DotGradient, 400, 240, 64, new ToneGradient(0, .1, .9)));
            var zoomed = Alpha(DesignRenderer.Render(doc, new Rect(180, 100, 40, 30), 640, 480));
            Check(zoomed.Count(v => v == 255) > zoomed.Length / 4 && zoomed.Count(v => v > 20 && v < 235) < zoomed.Length / 12, "The zoomed dot gradient is soft");
            var grain = Alpha(DesignRenderer.Render(Holder(Fill(HatchPattern.StippleGradient, 400, 240, 64, new ToneGradient(0, .1, .9))), new Rect(180, 100, 40, 30), 640, 480));
            Check(grain.Count(v => v == 255) > grain.Length / 4 && grain.Count(v => v > 20 && v < 235) < grain.Length / 8, "The zoomed stipple is soft");
        });

        test("stipple gradients are deterministic per seed and another seed moves the dots at the same density", () =>
        {
            ToneGradientRenderer.ClearCache();
            var a = MaterialRenderer.Render(Fill(HatchPattern.StippleGradient, 320, 200, 64, new ToneGradient(0, .2, .8, 5)));
            ToneGradientRenderer.ClearCache();
            var b = MaterialRenderer.Render(Fill(HatchPattern.StippleGradient, 320, 200, 64, new ToneGradient(0, .2, .8, 5)));
            var c = MaterialRenderer.Render(Fill(HatchPattern.StippleGradient, 320, 200, 64, new ToneGradient(0, .2, .8, 6)));
            Check(a.Data.AsSpan().SequenceEqual(b.Data), "The same seed drew different dots");
            int moved = Alpha(a).Zip(Alpha(c)).Count(x => Math.Abs(x.First - x.Second) > 128);
            Check(moved > 320 * 200 / 8 && Math.Abs(Mean(a) - Mean(c)) <= .02, $"Another seed did not move the dots at the same density ({moved} moved, {Mean(a):P1} vs {Mean(c):P1})");
            Check(ToneGradientRenderer.GrainThreshold(.5) is > -.2 and < .2 && ToneGradientRenderer.GrainThreshold(0) == double.PositiveInfinity && ToneGradientRenderer.DotThreshold(1) < -1,
                "The density tables are not centred or do not reach 0% and 100%");
        });

        test("gradient fills save and reopen; files without them keep their exact shape and older files open", () =>
        {
            var doc = new Document { Width = 160, Height = 120 }; doc.Add(new Layer { Name = "Paper", Pixels = Raster.Solid(160, 120, Colors.White) });
            var region = MaterialEditing.Region(doc, "Room", MaterialEditing.Polygon([[new(10, 10), new(150, 10), new(150, 110), new(10, 110)]]), "polygon"); doc.MaterialRegions.Add(region);
            var asset = HatchPatternRenderer.Create(HatchPattern.StippleGradient); doc.Materials.Add(asset);
            var layer = MaterialEditing.Apply(doc, asset.Id, region.Id, 40, 40);
            Check(layer.Material!.Gradient == null && ToneGradient.Of(layer.Material) == ToneGradient.Default, "A new gradient fill does not start from the defaults");
            layer.Material = layer.Material with { Gradient = new ToneGradient(30, .2, .7, 11), Ink = 0xFF203040 }; layer.Pixels = MaterialRenderer.Render(layer.Material); doc.Add(layer);
            doc.Materials.Add(HatchPatternRenderer.Create(HatchPattern.SolidBlack));
            doc.Add(MaterialEditing.Apply(doc, HatchPatterns.StableId(HatchPattern.SolidBlack), region.Id, 40, 40));
            var before = Imaging.Render(doc); string path = Path.Combine(root, "gradient.moruproj");
            ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            var fills = loaded.Layers.Where(l => l.Kind == LayerKind.Material).Select(l => l.Material!).ToArray();
            Check(fills[0] == layer.Material && fills[0].Gradient == new ToneGradient(30, .2, .7, 11) && ReferenceEquals(fills[0].Asset, asset) && fills[1].Gradient == null, "The gradient did not reopen");
            Check(Imaging.Render(loaded).Data.AsSpan().SequenceEqual(before.Data), "Reopened pixels differ");
            JsonNode Manifest(string file) { using var zip = ZipFile.OpenRead(file); using var input = zip.GetEntry("document.json")!.Open(); return JsonNode.Parse(input)!; }
            var materials = Manifest(path)["Layers"]!.AsArray().Select(l => l!["Material"]).OfType<JsonObject>().ToArray();
            Check(materials[0]["Gradient"]!["Seed"]!.GetValue<int>() == 11 && !materials[1].ContainsKey("Gradient") && Manifest(path)["Version"]!.GetValue<int>() == 6,
                "Gradient is not written only where a fill has one, or the format version changed");
            // A fill without a gradient writes exactly the fields older builds wrote.
            var node = JsonSerializer.SerializeToNode(ProjectStore.MaterialFillInfo.From(Fill(HatchPattern.Sand, 20, 20, 10))!)!.AsObject();
            Check(!node.ContainsKey("Gradient") && node.Select(p => p.Key).SequenceEqual(new[] { "MaterialId", "SourceRegionId", "RegionName", "Boundary", "Width", "Height", "TileWidth", "TileHeight", "Angle", "OffsetX", "OffsetY", "Ink", "LineWeight", "Background" }),
                "The fill JSON of a document without gradients changed: " + string.Join(", ", node.Select(p => p.Key)));
            var info = ProjectStore.MaterialFillInfo.From(Fill(HatchPattern.DotGradient, 20, 20, 10, new ToneGradient(10, .3, .6, 2)))!;
            var text = JsonSerializer.Serialize(info); var back = JsonSerializer.Deserialize<ProjectStore.MaterialFillInfo>(text)!;
            Check(back.Gradient == new ToneGradient(10, .3, .6, 2), "The fill info does not round-trip the gradient");
            var old = JsonNode.Parse(text)!.AsObject(); old.Remove("Gradient");
            Check(JsonSerializer.Deserialize<ProjectStore.MaterialFillInfo>(old.ToJsonString())!.Gradient == null, "A fill written without a gradient does not read as none");
        });

        test("gradient fills draw the same in layer pixels, design views, scaled exports and thumbnails", () =>
        {
            foreach (var p in new[] { HatchPattern.DotGradient, HatchPattern.StippleGradient })
            {
                var fill = Fill(p, 240, 160, 64, new ToneGradient(0, .05, .95)) with { Background = 0xFFF0E8D0 };
                var doc = Holder(fill);
                var output = DesignRenderer.RenderOutput(doc); var layer = doc.Layers[0].Pixels;
                int worst = 0; for (int i = 0; i < layer.Data.Length; i++) worst = Math.Max(worst, Math.Abs(layer.Data[i] - output.Data[i]));
                Check(worst <= 2, $"{HatchPatterns.Key(p)}: the document output differs from the layer pixels by {worst}");
                // A 2x export draws the same density twice as finely, and is redrawn (hard mark edges), not the 1x pixels resized.
                var plain = Holder(fill with { Background = 0 });
                var scaled = new ExportSettings(Scale: 2).Render(plain);
                var (measured, expected) = Strips(Fill(p, 480, 320, 128, new ToneGradient(0, .05, .95)), scaled, 6);
                Check(scaled.Width == 480 && scaled.Height == 320 && measured.Zip(expected).All(x => Math.Abs(x.First - x.Second) <= .05),
                    $"{HatchPatterns.Key(p)}: the 2x export inks {string.Join(" ", measured.Select(v => v.ToString("P0")))}");
                var resized = ImportExport.Resize(DesignRenderer.RenderOutput(plain), 480, 320);
                static double Soft(byte[] a) => a.Count(v => v > 20 && v < 230) / (double)Math.Max(1, a.Length);
                Check(Soft(Alpha(scaled)) < Soft(Alpha(resized)), $"{HatchPatterns.Key(p)}: the 2x export is a resized 1x image");
                var thumbnail = Raster.FromBitmap(MaterialRenderer.PatternThumbnail(fill with { Background = 0 })!);
                double Luma(int x0, int x1) { double sum = 0; int n = 0; for (int y = 22; y < 34; y++) for (int x = x0; x < x1; x++) { int i = (y * 56 + x) * 4; sum += thumbnail.Data[i + 1]; n++; } return sum / n; }
                Check(Luma(4, 14) > Luma(42, 52) + 80, $"{HatchPatterns.Key(p)}: the thumbnail does not show the ramp ({Luma(4, 14):0} → {Luma(42, 52):0})");
            }
        });

        test("pattern thumbnails keep a region stored in document pixels in place", () =>
        {
            // Regions from polygons (AI connection points, CAD hatches) keep document coordinates and an offset into the layer.
            foreach (var p in new[] { HatchPattern.DotScreen30, HatchPattern.Brick, HatchPattern.DotGradient })
            {
                var placed = new RegionPath("M600,400 L800,400 L800,560 L600,560 Z", OffsetX: -600, OffsetY: -400);
                var fill = new MaterialFill(HatchPatternRenderer.Create(p), Guid.NewGuid(), "Room", placed, 200, 160, 40, 40, Gradient: new ToneGradient(0, .6, .9));
                var thumbnail = Raster.FromBitmap(MaterialRenderer.PatternThumbnail(fill)!);
                int drawn = 0, ink = 0;
                for (int i = 0; i < thumbnail.Data.Length; i += 4)
                {
                    if (thumbnail.Data[i + 3] < 250) continue;
                    drawn++; if (thumbnail.Data[i + 1] < 150) ink++;
                }
                // The 200×160 region fills 56×45 icon pixels.
                Check(drawn > 2000 && ink > 100, $"{HatchPatterns.Key(p)}: the thumbnail of a placed region shows {drawn} drawn and {ink} ink pixels");
            }
        });

        test("gradient values are validated and kept through swaps", () =>
        {
            var fill = Fill(HatchPattern.DotGradient, 32, 32, 16); var pixels = new Raster(32, 32);
            foreach (var bad in new[] { new ToneGradient(Start: -.1), new ToneGradient(End: 1.1), new ToneGradient(double.NaN), new ToneGradient(Angle: 40000), new ToneGradient(Seed: -1), new ToneGradient(Seed: ToneGradient.MaxSeed + 1) })
            {
                bool rejected = false;
                try { MaterialEditing.ValidateFill(fill with { Gradient = bad }, pixels); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "An invalid gradient was accepted: " + bad);
            }
            MaterialEditing.ValidateFill(fill with { Gradient = new ToneGradient(-180, 0, 1, ToneGradient.MaxSeed) }, pixels);
            var tuned = fill with { Gradient = new ToneGradient(15, .3, .6, 4) };
            var swapped = MaterialEditing.Swap(tuned, HatchPatternRenderer.Create(HatchPattern.DotScreen30));
            Check(swapped.Gradient == tuned.Gradient && MaterialEditing.Swap(swapped, HatchPatternRenderer.Create(HatchPattern.StippleGradient)).Gradient == tuned.Gradient, "A swap lost the gradient");
        });

        test("gradient performance: a large stipple region renders a screen view quickly and repeats reuse it", () =>
        {
            ToneGradientRenderer.ClearCache();
            var doc = new Document { Width = 6000, Height = 4000 };
            var fill = new MaterialFill(HatchPatternRenderer.Create(HatchPattern.StippleGradient), Guid.NewGuid(), "Field", new RegionPath("M0,0 L5000,0 L5000,3400 L0,3400 Z"), 5000, 3400,
                MaterialEditing.DefaultTile(6000, 4000), MaterialEditing.DefaultTile(6000, 4000), Gradient: new ToneGradient(30, .05, .9));
            doc.Layers.Add(new Layer { Name = "Field", Kind = LayerKind.Material, Category = LayerCategory.Photo, Material = fill, X = 500, Y = 300, Pixels = new Raster(5000, 3400) });
            var clock = Stopwatch.StartNew();
            DesignRenderer.Render(doc, new Rect(0, 0, 6000, 4000), 1480, 987);
            DesignRenderer.Render(doc, new Rect(2000, 1500, 1480, 987), 1480, 987);
            Check(clock.Elapsed < TimeSpan.FromSeconds(4), $"Two cold views took {clock.Elapsed.TotalMilliseconds:0} ms");
            clock.Restart(); DesignRenderer.Render(doc, new Rect(2000, 1500, 1480, 987), 1480, 987);
            Check(clock.Elapsed < TimeSpan.FromSeconds(1), $"The same view again took {clock.Elapsed.TotalMilliseconds:0} ms");
        });
    }
}
