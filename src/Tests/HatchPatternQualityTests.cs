using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Line quality of hatch patterns: whole-pixel tiles, pixel-centred straight lines, constant tone when
// zoomed out, vector marks for huge repeats, scaled exports, stone repeats, thumbnails and CAD names.
public static class HatchPatternQualityTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static MaterialFill Fill(HatchPattern p, int width, int height, double tileWidth, double tileHeight) =>
            new(HatchPatternRenderer.Create(p), Guid.NewGuid(), "Area", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"), width, height, tileWidth, tileHeight);
        static Document Holder(MaterialFill fill)
        {
            var doc = new Document { Width = fill.Width, Height = fill.Height };
            doc.Add(new Layer { Name = "Fill", Kind = LayerKind.Material, Category = LayerCategory.Photo, Material = fill, Pixels = MaterialRenderer.Render(fill) });
            return doc;
        }
        static byte[] Alpha(Raster image) { var a = new byte[image.Width * image.Height]; for (int i = 0; i < a.Length; i++) a[i] = image.Data[i * 4 + 3]; return a; }
        static byte[] AlphaOf(BitmapSource bitmap) => Alpha(Raster.FromBitmap(bitmap));
        static double Soft(byte[] a) => a.Count(v => v > 20 && v < 230) / (double)Math.Max(1, a.Count(v => v >= 230));
        // Runs of ink along a line of pixels and the darkest alpha of each.
        static List<int> Runs(Func<int, byte> at, int length)
        {
            var peaks = new List<int>(); int peak = -1;
            for (int i = 0; i < length; i++)
            {
                byte v = at(i);
                if (v > 20) peak = Math.Max(peak, v);
                else if (peak >= 0) { peaks.Add(peak); peak = -1; }
            }
            if (peak >= 0) peaks.Add(peak);
            return peaks;
        }

        test("hatch tiles are drawn at whole device pixels, so layer pixels and 1:1 views are not resampled", () =>
        {
            Check(HatchPatternRenderer.TileSize(64, 64, 1) == (64, 64) && HatchPatternRenderer.TileSize(900 / 14d, 900 / 14d, 1) == (64, 64)
                && HatchPatternRenderer.TileSize(76.8, 76.8, 1) == (77, 77) && HatchPatternRenderer.TileSize(72, 36, 1) == (72, 36) && HatchPatternRenderer.TileSize(64, 64, .5) == (32, 32),
                "Tiles are not sized to the nearest device pixel: " + HatchPatternRenderer.TileSize(76.8, 76.8, 1));
            // A 72 px repeat (not a power of two) in the layer pixels is the tile itself, copied 1:1.
            var pixels = Alpha(MaterialRenderer.Render(Fill(HatchPattern.Flagstone, 144, 144, 72, 72)));
            var tile = AlphaOf(HatchPatternRenderer.Tile(HatchPattern.Flagstone, 72, 72, 1, 0));
            int worst = 0;
            for (int y = 0; y < 144; y++) for (int x = 0; x < 144; x++) worst = Math.Max(worst, Math.Abs(pixels[y * 144 + x] - tile[y % 72 * 72 + x % 72]));
            Check(worst <= 3, $"Layer pixels of a 72 px repeat were resampled (worst difference {worst})");
            HatchPatternRenderer.ClearCache();
            DesignRenderer.Render(Holder(Fill(HatchPattern.Gravel, 256, 256, 64, 64)), new Rect(0, 0, 256, 256), 263, 263);
            Check(HatchPatternRenderer.LargestCachedSide == 66, "The screen tile is not the exact device size: " + HatchPatternRenderer.LargestCachedSide);
        });

        test("thin straight hatch lines are one crisp pixel row at 1:1, not two grey rows", () =>
        {
            var lines = AlphaOf(HatchPatternRenderer.Tile(HatchPattern.Lines, 64, 64, 1, 0));
            var grid = AlphaOf(HatchPatternRenderer.Tile(HatchPattern.Grid, 64, 64, 1, 0));
            var brick = AlphaOf(HatchPatternRenderer.Tile(HatchPattern.Brick, 64, 64, 1, 0));
            foreach (var (name, runs, count) in new (string, List<int>, int)[] {
                ("Lines rows", Runs(y => lines[y * 64 + 32], 64), 10), ("Grid rows", Runs(y => grid[y * 64 + 3], 64), 4),
                ("Grid columns", Runs(x => grid[3 * 64 + x], 64), 4), ("Brick courses", Runs(y => brick[y * 64 + 5], 64), 4) })
                Check(runs.Count == count && runs.All(v => v >= 165), $"{name}: {runs.Count} lines, peaks {string.Join(" ", runs)} (a line split across two pixels peaks near 90)");
        });

        test("zoomed-out hatch views keep the tone of 100% and the exported pixels", () =>
        {
            foreach (var p in new[] { HatchPattern.Sand, HatchPattern.Stipple, HatchPattern.Meadow, HatchPattern.GrassSparse, HatchPattern.Gravel })
            {
                var doc = Holder(Fill(p, 684, 684, 171, 171));
                double Tone(int size) => Alpha(DesignRenderer.Render(doc, new Rect(0, 0, 684, 684), size, size)).Average(a => (double)a);
                double ratio = Tone(274) / Tone(684);
                Check(ratio is > .75 and < 1.33, $"{p}: the 40% view has {ratio:0.##}x the ink of 100%");
            }
        });

        test("very large hatch repeats are drawn as vector marks and stay sharp past the tile limit", () =>
        {
            HatchPatternRenderer.ClearCache();
            var doc = Holder(Fill(HatchPattern.Lines, 400, 400, 3000, 3000));
            Check(HatchPatternRenderer.NeedsVector(3000, 3000, 8) && !HatchPatternRenderer.NeedsVector(320, 320, 4), "The vector threshold changed");
            // The first line lies at y = 150; at 800% its 0.7 px pen is 5.6 device px.
            var alpha = Alpha(DesignRenderer.Render(doc, new Rect(0, 125, 50, 50), 400, 400));
            var column = Enumerable.Range(0, 400).Select(y => alpha[y * 400 + 200]).ToArray();
            int solid = column.Count(v => v >= 230), soft = column.Count(v => v > 20 && v < 230);
            Check(solid >= 4 && soft <= 3, $"The zoomed line is soft: {solid} solid and {soft} soft rows");
            Check(HatchPatternRenderer.CacheStats.Entries == 0, "A capped bitmap tile was built for a huge repeat");
            var turned = Holder(Fill(HatchPattern.Grid, 300, 300, 2500, 2500) with { Angle = 30, OffsetX = -200, OffsetY = -200 });
            Check(Alpha(turned.Layers[0].Pixels).Any(v => v > 100), "A rotated huge repeat drew nothing");
        });

        test("scaled image exports redraw hatch patterns at the output size instead of resizing 1x pixels", () =>
        {
            var doc = Holder(Fill(HatchPattern.Lines, 128, 128, 64, 64));
            var settings = new ExportSettings(Scale: 4);
            var sharp = settings.Render(doc);
            Check(sharp.Width == 512 && sharp.Height == 512 && settings.Prepare(sharp, atOutputSize: true).Width == 512 && settings.Prepare(DesignRenderer.RenderOutput(doc)).Width == 512,
                "The output size is wrong or a rendered output was scaled twice");
            var resized = ImportExport.Resize(DesignRenderer.RenderOutput(doc), 512, 512);
            Check(Soft(Alpha(sharp)) * 2 <= Soft(Alpha(resized)), $"The 4x export is not sharper than resizing: {Soft(Alpha(sharp)):0.###} vs {Soft(Alpha(resized)):0.###}");
            var layer = doc.Layers[0].Id;
            var selected = SelectedLayerExport.Render(doc, [layer], true, default, settings);
            Check(selected.Image.Width == settings.OutputSize(selected.CanvasBounds.Width, selected.CanvasBounds.Height).Width && Soft(Alpha(selected.Image)) * 2 <= Soft(Alpha(resized)),
                "The selected-layer export was not redrawn at its output size");
            // A photo-only document keeps the resize path.
            var photo = new Document { Width = 20, Height = 10 }; photo.Add(new Layer { Name = "Photo", Pixels = Raster.Solid(20, 10, Colors.SteelBlue) });
            var two = new ExportSettings(Scale: 2).Render(photo);
            Check(two.Width == 40 && two.Data.AsSpan().SequenceEqual(ImportExport.Resize(Imaging.Render(photo), 40, 20).Data), "A photo export changed");
        });

        test("large stone patterns repeat over twice the distance with four times the cells", () =>
        {
            Check(HatchPatterns.RepeatScale(HatchPattern.Flagstone) == 2 && HatchPatterns.RepeatScale(HatchPattern.Cobble) == 2 && HatchPatterns.RepeatScale(HatchPattern.GrassSparse) == 1, "Repeat scales changed");
            Check(HatchPatternRenderer.Build(HatchPattern.Flagstone).Marks.Length >= 110 && HatchPatternRenderer.Build(HatchPattern.Cobble).Marks.Length >= 300,
                $"Stone repeats hold too few cells ({HatchPatternRenderer.Build(HatchPattern.Flagstone).Marks.Length} flagstone edges)");
            var flag = HatchPatternRenderer.Create(HatchPattern.Flagstone); var lawn = HatchPatternRenderer.Create(HatchPattern.GrassSparse);
            Check(MaterialEditing.DefaultTile(900, 600, flag) == 2 * 900 / 14d && MaterialEditing.DefaultTile(900, 600, lawn) == 900 / 14d, "Default stone repeat is not doubled");
            var fill = Fill(HatchPattern.GrassSparse, 100, 100, 50, 75);
            var stones = MaterialEditing.Swap(fill, flag); var back = MaterialEditing.Swap(stones, lawn);
            Check(stones.TileWidth == 100 && Math.Abs(MaterialEditing.Stretch(stones) - 1.5) < 1e-9 && back.TileWidth == 50 && back.TileHeight == 75, "A swap did not keep the relative size and ratio: " + stones);
        });

        test("pattern layer thumbnails show the pattern on paper instead of washed-out pixels", () =>
        {
            var fill = Fill(HatchPattern.GrassSparse, 900, 685, 64, 64);
            var thumb = Raster.FromBitmap(MaterialRenderer.PatternThumbnail(fill)!);
            int marks = 0;
            for (int i = 0; i < thumb.Data.Length; i += 4)
            {
                double a = thumb.Data[i + 3] / 255d, lum = (thumb.Data[i] + thumb.Data[i + 1] + thumb.Data[i + 2]) / 3d * a + 255 * (1 - a);
                if (lum < 180) marks++;
            }
            Check(marks >= 25, $"Only {marks} pattern pixels are visible in the thumbnail");
            Check(ReferenceEquals(MaterialRenderer.PatternThumbnail(fill), MaterialRenderer.PatternThumbnail(fill)), "The thumbnail is drawn again for the same fill");
            Check(MaterialRenderer.PatternThumbnail(fill with { Asset = MaterialPresets.Create(MaterialKind.Wood) }) == null, "An image material got a pattern thumbnail");
        });

        test("CAD layer names choose line patterns by whole words, with wood and stone names", () =>
        {
            foreach (var (pattern, layer, role, expected) in new (string, string, DrawingRole, HatchPattern?)[] {
                ("AR-PARQ1", "A-FLOR-PATT", DrawingRole.Hatch, HatchPattern.Lines), ("AR-HBONE", "A-FLOR", DrawingRole.Other, HatchPattern.Lines),
                ("", "A-FLOOR", DrawingRole.Other, HatchPattern.Lines), ("", "합판마감", DrawingRole.Other, HatchPattern.Lines),
                ("MARBLE", "A-FLOR", DrawingRole.Other, HatchPattern.Flagstone), ("", "A-화강석", DrawingRole.Other, HatchPattern.Flagstone),
                ("USER", "A-점토벽", DrawingRole.Other, HatchPattern.Diagonal), ("USER", "상점", DrawingRole.Other, HatchPattern.Diagonal),
                ("USER", "A-OUTLINE", DrawingRole.Other, HatchPattern.Diagonal), ("USER", "A-LINE", DrawingRole.Other, HatchPattern.Lines),
                ("USER", "점무늬", DrawingRole.Other, HatchPattern.Dots), ("USER", "L-PLNT", DrawingRole.Other, HatchPattern.GrassSparse) })
                Check(DrawingCleanup.SuggestPattern(pattern, layer, role) == expected, $"{pattern}/{layer} suggested {DrawingCleanup.SuggestPattern(pattern, layer, role)}, not {expected}");
            foreach (var (pattern, layer) in new[] { ("AR-PARQ1", "A-FLOR-PATT"), ("MARBLE", "A-FLOR") })
            {
                var material = DrawingCleanup.Suggest(pattern, layer, DrawingRole.Other); var line = DrawingCleanup.SuggestPattern(pattern, layer, DrawingRole.Other);
                Check(material == MaterialKind.Wood && line == HatchPattern.Lines || material == MaterialKind.Stone && line == HatchPattern.Flagstone, $"{pattern}: material and line suggestions disagree ({material}, {line})");
            }
        });
    }
}
