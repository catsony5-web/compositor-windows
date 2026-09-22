using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class PrecisionWandTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static Document Drawing(Rect? box = null)
    {
        var doc = new Document { Width = 120, Height = 100, Name = "Precision wand" };
        doc.Add(new Layer { Name = "Paper", Pixels = Raster.Solid(120, 100, Colors.White) });
        var source = VectorContent.FromPaths(120, 100, [new(new RectangleGeometry(box ?? new Rect(41.2, 31.3, 12.4, 14.7)), Colors.Black, false, .22)]);
        doc.Add(new Layer { Name = "Thin boundary", Kind = LayerKind.Vector, Vector = source, Pixels = Imaging.Draw(120, 100, dc => dc.DrawDrawing(source.Drawing)) });
        return doc;
    }
    public static void Run(Action<string, Action> test)
    {
        test("Wand scanline flood matches an independent four-neighbor search", () =>
        {
            var random = new Random(7301);
            for (int pass = 0; pass < 35; pass++)
            {
                int w = 31, h = 27; var image = new Raster(w, h);
                for (int i = 0; i < w * h; i++) { image.Data[i * 4] = (byte)(random.Next(4) * 75); image.Data[i * 4 + 1] = 140; image.Data[i * 4 + 2] = 80; image.Data[i * 4 + 3] = (byte)(random.Next(5) == 0 ? 0 : 255); }
                int seed = random.Next(w * h); double tolerance = pass % 2 == 0 ? 0 : 48;
                var seen = new bool[w * h]; var expected = new byte[w * h]; var queue = new Queue<int>(); queue.Enqueue(seed);
                while (queue.TryDequeue(out int i))
                {
                    if (seen[i]) continue; seen[i] = true;
                    if (SelectionTools.ColorDistance(image.Data, seed * 4, i * 4) > tolerance) continue;
                    expected[i] = 255; int x = i % w, y = i / w;
                    if (x > 0) queue.Enqueue(i - 1); if (x + 1 < w) queue.Enqueue(i + 1); if (y > 0) queue.Enqueue(i - w); if (y + 1 < h) queue.Enqueue(i + w);
                }
                var actual = SelectionTools.MagicWand(image, new Point(seed % w + .5, seed / w + .5), tolerance);
                Assert(expected.SequenceEqual(actual.Coverage!), "Scanline flood crossed or omitted a component");
            }
        });
        test("Wand antialias adds a soft edge without crossing the blocking line", () =>
        {
            var image = new Raster(7, 1); byte[] colors = [255, 255, 224, 64, 0, 255, 255];
            for (int i = 0; i < colors.Length; i++) { image.Data[i * 4] = image.Data[i * 4 + 1] = image.Data[i * 4 + 2] = colors[i]; image.Data[i * 4 + 3] = 255; }
            var hard = SelectionTools.MagicWand(image, new(.5, .5), 16);
            var soft = SelectionTools.MagicWand(image, new(.5, .5), 16, antialias: true);
            Assert(hard.Coverage![2] == 0 && soft.Coverage![2] is > 128 and < 255, "Mixed edge pixel was not softened");
            Assert(soft.Coverage!.Skip(3).All(v => v == 0), "Soft edge leaked through the barrier or propagated");
            Assert(image.Data[8] == 224, "Sampling changed image pixels");
        });
        test("Vector wand follows fractional thin walls instead of the raster cache", () =>
        {
            var doc = Drawing(); var pixels = doc.Active!.Pixels; var source = doc.Active.Vector;
            var selection = PrecisionWand.Select(doc, new(47, 38), 16, true, true, true, 32);
            var outline = SelectionContours.Create(selection);
            Assert(selection.Contains(41.4, 35) && !selection.Contains(41.1, 35), "Subpixel boundary is misplaced");
            Assert(selection.Contains(50, 45.8) && !selection.Contains(50, 46.2), "Selection crossed the bottom line");
            Assert(Math.Abs(outline.Bounds.Left - 41.31) < .08 && Math.Abs(outline.Bounds.Top - 31.41) < .08, "Contour no longer follows original vector coordinates: " + outline.Bounds);
            Assert(selection.CoverageBounds != null && selection.CanvasWidth / selection.CoverageBounds.Value.Width == 32, "Local detail was reduced to document pixels");
            Assert(ReferenceEquals(pixels, doc.Active.Pixels) && ReferenceEquals(source, doc.Active.Vector), "Selection rasterized the layer");
        });
        test("Vector wand expands past the initial crop and global sampling covers the whole sheet", () =>
        {
            var doc = Drawing(new Rect(10.3, 8.2, 98.4, 80.6));
            var local = PrecisionWand.Select(doc, new(60, 48), 16, true, true, true, 8);
            Assert(local.Contains(12, 10) && local.Contains(107, 87) && !local.Contains(5, 48), "Flood stopped at a sample crop or crossed the wall");
            var global = PrecisionWand.Select(doc, new(60, 48), 16, false, true, true, 8);
            Assert(global.Contains(5, 48) && global.Contains(115, 48) && global.Contains(60, 48), "Global same-color mode was limited to the local crop");
        });
        test("Selection contours retain odd-coordinate islands and holes on large masks", () =>
        {
            int w = 2400, h = 1707; var mask = new byte[w * h];
            for (int y = 65; y < 81; y++) for (int x = 1151; x < 1172; x++) mask[y * w + x] = 255;
            mask[73 * w + 1163] = 0; mask[85 * w + 1175] = 255;
            var selection = SelectionTools.FromMask(mask, w, h); var outline = SelectionContours.Create(selection);
            Assert(outline.FillContains(new Point(1175.5, 85.5)), "Small odd-coordinate island disappeared");
            Assert(!outline.FillContains(new Point(1163.5, 73.5)) && outline.FillContains(new Point(1162.5, 73.5)), "One-pixel hole was skipped");
            Assert(Math.Abs(outline.Bounds.Left - 1151) < .001, "Contour stride shifted the boundary");
        });
        test("Selection contours close all saddle cases without joining diagonal regions", () =>
        {
            for (int code = 0; code < 16; code++)
            {
                byte[] mask = [(byte)((code & 1) != 0 ? 255 : 0), (byte)((code & 2) != 0 ? 255 : 0), (byte)((code & 8) != 0 ? 255 : 0), (byte)((code & 4) != 0 ? 255 : 0)];
                var outline = SelectionContours.Create(SelectionTools.FromMask(mask, 2, 2));
                for (int i = 0; i < 4; i++) Assert(outline.FillContains(new Point(i % 2 + .5, i / 2 + .5)) == (mask[i] != 0), "Wrong contour topology in case " + code);
            }
        });
        test("Precision selection combinations preserve subpixel membership", () =>
        {
            var a = SelectionTools.FromMask(Enumerable.Repeat((byte)255, 128).ToArray(), 16, 8, new Rect(10, 20, 1, .5));
            var b = SelectionTools.FromMask(Enumerable.Repeat((byte)255, 128).ToArray(), 16, 8, new Rect(10.5, 20, 1, .5));
            var added = SelectionTools.Combine(a, b, 100, 100, SelectionCombine.Add);
            var subtracted = SelectionTools.Combine(a, b, 100, 100, SelectionCombine.Subtract);
            var intersected = SelectionTools.Combine(a, b, 100, 100, SelectionCombine.Intersect);
            Assert(added.Contains(11.25, 20.25) && added.Contains(10.25, 20.25), "Addition lost a precise region");
            Assert(subtracted.Contains(10.45, 20.25) && !subtracted.Contains(10.55, 20.25), "Subtraction became a document-pixel mask");
            Assert(intersected.Contains(10.75, 20.25) && !intersected.Contains(10.25, 20.25), "Intersection lost precision");
            Assert(ReferenceEquals(SelectionTools.Combine(a, b, 100, 100, SelectionCombine.Replace), b), "Replacement needlessly resampled the mask");
        });
        test("Photo wand samples original pixels and honors cancellation", () =>
        {
            var doc = Drawing(); var photo = PrecisionWand.Select(doc, new(47, 38), 16, true, true, false, 32);
            var expected = SelectionTools.MagicWand(Imaging.Render(doc), new(47, 38), 16, true, antialias: true);
            Assert(photo.CoverageBounds == null && photo.Coverage!.SequenceEqual(expected.Coverage!), "Photo view was supersampled");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            bool cancelled = false; try { PrecisionWand.Select(doc, new(47, 38), 16, true, true, true, 32, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Cancelled vector sampling continued");
        });
        test("Precision sampling bounds long narrow pages and selection inversion", () =>
        {
            var page = new Rect(0, 0, Raster.MaxDimension, 1);
            double scale = PrecisionWand.FitDensity(page, 64);
            var grid = PrecisionWand.Grid(page, scale, Raster.MaxDimension, 1);
            Assert(grid.Width <= Raster.MaxDimension && grid.Height <= Raster.MaxDimension && (long)grid.Width * grid.Height <= PrecisionWand.MaxSamples, "A narrow page exceeded the renderer dimension limit");
            var selected = SelectionTools.FromMask([255, 255, 255, 255], 2, 2, new Rect(2.25, 3.25, .5, .5));
            var inverse = SelectionTools.Invert(selected, 8, 8);
            Assert(inverse.Contains(2.1, 3.5) && !inverse.Contains(2.5, 3.5) && inverse.Contains(2.9, 3.5), "Inversion lost fractional mask placement");
        });
    }
}
