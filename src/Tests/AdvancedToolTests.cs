using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class AdvancedToolTests
{
    public static void Run(Action<string, Action> test)
    {
        void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
        void Near(double value, double expected, double tolerance = 1.1) => Assert(Math.Abs(value - expected) < tolerance, $"Expected {expected}, got {value}");
        Layer Solid(Color color, int w = 16, int h = 16) => new() { Pixels = Raster.Solid(w, h, color) };
        void Pixel(Raster r, int x, int y, Color c) { int i = (y * r.Width + x) * 4; r.Data[i] = c.B; r.Data[i + 1] = c.G; r.Data[i + 2] = c.R; r.Data[i + 3] = c.A; }
        test("lasso polygon concave coverage and antialiased edge", () =>
        {
            var s = SelectionTools.Polygon(12, 12, [new(1, 1), new(10, 1), new(10, 4), new(4, 4), new(4, 10), new(1, 10)]);
            Assert(s.Contains(2, 8) && s.Contains(8, 2) && !s.Contains(8, 8));
            var diagonal = SelectionTools.Polygon(8, 8, [new(0, 0), new(8, 0), new(0, 8)]); Assert(diagonal.Coverage!.Any(v => v > 0 && v < 255));
        });
        test("selection empty polygon retains explicit empty coverage", () =>
        { var s = SelectionTools.Polygon(8, 8, [new(1, 1), new(2, 2)]); Assert(!s.Contains(0, 0) && !s.Contains(1, 1)); });
        test("magic wand distinguishes connected and global color", () =>
        {
            var r = Raster.Solid(9, 3, Colors.White); for (int y = 0; y < 3; y++) Pixel(r, 4, y, Colors.Black);
            var connected = SelectionTools.MagicWand(r, new Point(.5, .5), 0); var global = SelectionTools.MagicWand(r, new Point(.5, .5), 0, false);
            Assert(connected.Contains(2, 1) && !connected.Contains(7, 1)); Assert(global.Contains(7, 1) && !global.Contains(4, 1));
        });
        test("magic wand tolerance and transparent hidden RGB", () =>
        {
            var r = new Raster(3, 1); Pixel(r, 0, 0, Color.FromArgb(0, 255, 0, 0)); Pixel(r, 1, 0, Color.FromArgb(0, 0, 255, 0)); Pixel(r, 2, 0, Colors.Black);
            var s = SelectionTools.MagicWand(r, new Point(.5, .5), 0); Assert(s.Contains(1.5, .5) && !s.Contains(2.5, .5));
            r = Raster.Solid(2, 1, Color.FromRgb(100, 100, 100)); Pixel(r, 1, 0, Color.FromRgb(110, 110, 110));
            Assert(!SelectionTools.MagicWand(r, new(.5, .5), 9).Contains(1.5, .5)); Assert(SelectionTools.MagicWand(r, new(.5, .5), 10).Contains(1.5, .5));
        });
        test("selection combine soft coverage preserves inputs", () =>
        {
            var a = SelectionTools.FromMask([255, 128, 0], 3, 1); var b = SelectionTools.FromMask([128, 255, 255], 3, 1);
            Assert(SelectionTools.Combine(a, b, 3, 1, SelectionCombine.Add).Coverage!.SequenceEqual(new byte[] { 255, 255, 255 }));
            Assert(SelectionTools.Combine(a, b, 3, 1, SelectionCombine.Subtract).Coverage!.SequenceEqual(new byte[] { 127, 0, 0 }));
            Assert(SelectionTools.Combine(a, b, 3, 1, SelectionCombine.Intersect).Coverage!.SequenceEqual(new byte[] { 128, 128, 0 }));
            Assert(a.Coverage![0] == 255 && SelectionTools.Invert(a, 3, 1).Coverage!.SequenceEqual(new byte[] { 0, 127, 255 }));
        });
        test("feather creates soft symmetric coverage without wraparound", () =>
        {
            var s = SelectionTools.Feather(new Selection(new Rect(6, 6, 5, 5)), 17, 17, 3);
            Near(s.Weight(5.5, 8.5), s.Weight(11.5, 8.5), .00001); Assert(s.Weight(5.5, 8.5) > 0 && s.Weight(5.5, 8.5) < s.Weight(8.5, 8.5)); Assert(s.Weight(.5, 8.5) < .01);
        });
        test("expand uses circular footprint and contract handles canvas edge", () =>
        {
            var dot = new Selection(new Rect(5, 5, 1, 1)); var grown = SelectionTools.Expand(dot, 11, 11, 2);
            Assert(grown.Contains(7.5, 5.5) && !grown.Contains(7.5, 7.5));
            var shrunk = SelectionTools.Contract(new Selection(new Rect(0, 0, 11, 11)), 11, 11, 2); Assert(!shrunk.Contains(.5, .5) && shrunk.Contains(5.5, 5.5));
        });
        test("alpha to selection accounts for transform and layer mask", () =>
        {
            var l = Solid(Color.FromArgb(128, 200, 30, 40), 2, 2); l.X = 3; l.Y = 4; l.Mask = [128, 255, 255, 255]; var s = SelectionTools.FromAlpha(l, 8, 8);
            Near(s.Weight(3.5, 4.5) * 255, 64); Near(s.Weight(4.5, 4.5) * 255, 128); Assert(!s.Contains(.5, .5));
        });
        test("circular morphology agrees with direct footprint on mixed masks", () =>
        {
            var random = new Random(43);
            foreach (bool soft in new[] { false, true })
            {
                int w = 13, h = 9; var mask = Enumerable.Range(0, w * h).Select(_ => soft ? (byte)random.Next(256) : random.Next(2) == 0 ? (byte)0 : (byte)255).ToArray();
                var s = SelectionTools.FromMask(mask, w, h);
                foreach (bool expand in new[] { false, true })
                {
                    var result = (expand ? SelectionTools.Expand(s, w, h, 2) : SelectionTools.Contract(s, w, h, 2)).Coverage!;
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int expected = expand ? 0 : 255;
                        for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                        {
                            if (dx * dx + dy * dy > 4) continue;
                            int v = x + dx < 0 || y + dy < 0 || x + dx >= w || y + dy >= h ? 0 : mask[(y + dy) * w + x + dx];
                            expected = expand ? Math.Max(expected, v) : Math.Min(expected, v);
                        }
                        Assert(result[y * w + x] == expected, $"Morphology mismatch at {x},{y}, soft={soft}, expand={expand}");
                    }
                }
            }
        });
        test("wide feather maintains symmetry and soft coverage", () =>
        {
            var s = SelectionTools.Feather(new Selection(new Rect(24, 24, 17, 17)), 65, 65, 12);
            Near(s.Weight(20.5, 32.5), s.Weight(44.5, 32.5), .00001); Assert(s.Weight(20.5, 32.5) > 0 && s.Weight(20.5, 32.5) < s.Weight(32.5, 32.5));
        });
        test("clone stamp source alignment alpha and immutable snapshot", () =>
        {
            var l = Solid(Colors.Transparent); Pixel(l.Pixels, 2, 2, Color.FromArgb(128, 240, 20, 10)); var original = (byte[])l.Pixels.Data.Clone();
            var result = RetouchTools.Clone(l, l.Pixels, new(2.5, 2.5), new(10.5, 10.5), 1, 1, 1);
            int i = (10 * 16 + 10) * 4; Assert(result.Data[i + 2] == 240 && result.Data[i + 3] == 128); Assert(l.Pixels.Data.SequenceEqual(original));
        });
        test("clone applies feather selection and layer transformation", () =>
        {
            var l = Solid(Colors.Transparent, 8, 8); Pixel(l.Pixels, 1, 1, Colors.Red); l.X = 4; l.Y = 7; l.Scale = 2;
            var m = new byte[32 * 32]; var target = l.Document(new Point(5.5, 5.5)); m[(int)target.Y * 32 + (int)target.X] = 128;
            var result = RetouchTools.Clone(l, l.Pixels, l.Document(new Point(1.5, 1.5)), target, 2, 1, 1, SelectionTools.FromMask(m, 32, 32));
            Near(result.Data[(5 * 8 + 5) * 4 + 3], 128); Assert(result.Data[(5 * 8 + 4) * 4 + 3] == 0);
        });
        test("healing transfers texture with destination tone and fixed alpha", () =>
        {
            var l = Solid(Color.FromArgb(180, 180, 180, 180), 24, 12);
            for (int y = 0; y < 12; y++) for (int x = 0; x < 10; x++) Pixel(l.Pixels, x, y, Color.FromArgb(180, 60, 60, 60));
            Pixel(l.Pixels, 4, 5, Color.FromArgb(180, 100, 100, 100));
            var result = RetouchTools.Heal(l, l.Pixels, new(4.5, 5.5), new(18.5, 5.5), 3, 1, 1);
            Assert(result.Data[(5 * 24 + 18) * 4 + 2] > 185 && result.Data[(5 * 24 + 18) * 4 + 3] == 180);
        });
        test("smudge transfers paint along movement without touching exterior", () =>
        {
            var l = Solid(Colors.Black); Pixel(l.Pixels, 5, 8, Colors.Red);
            var result = RetouchTools.Smudge(l, new(5.5, 8.5), new(8.5, 8.5), 2, 1);
            Assert(result.Data[(8 * 16 + 8) * 4 + 2] == 255); Assert(result.Data[(2 * 16 + 2) * 4 + 2] == 0); Assert(l.Pixels.Data[(8 * 16 + 8) * 4 + 2] == 0);
        });
        test("liquify backward warp preserves alpha and selection exterior", () =>
        {
            var l = Solid(Color.FromArgb(128, 0, 0, 0)); Pixel(l.Pixels, 5, 8, Color.FromArgb(128, 255, 0, 0));
            var result = RetouchTools.Liquify(l, new(5.5, 8.5), new(8.5, 8.5), 3, 1, new Selection(new Rect(7, 7, 3, 3)));
            Assert(result.Data[(8 * 16 + 8) * 4 + 2] == 255 && result.Data[(8 * 16 + 8) * 4 + 3] == 128); Assert(result.Data[(8 * 16 + 5) * 4 + 2] == 255);
        });
        test("local blur matches gaussian only within brush and keeps input", () =>
        {
            var l = Solid(Colors.Transparent, 25, 25); var random = new Random(19);
            for (int y = 0; y < 25; y++) for (int x = 0; x < 25; x++) Pixel(l.Pixels, x, y, Color.FromArgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            var original = (byte[])l.Pixels.Data.Clone(); var full = Imaging.Blur(l, 3, null); var local = RetouchTools.Blur(l, new(12.5, 12.5), 5, 1, 1, null, 3);
            for (int y = 0; y < 25; y++) for (int x = 0; x < 25; x++)
            {
                int i = (y * 25 + x) * 4;
                if ((new Point(x + .5, y + .5) - new Point(12.5, 12.5)).Length < 5)
                    for (int c = 0; c < 4; c++) Near(local.Data[i + c], full.Data[i + c]);
                else Assert(local.Data.AsSpan(i, 4).SequenceEqual(original.AsSpan(i, 4)));
            }
            Assert(l.Pixels.Data.SequenceEqual(original));
            var soft = RetouchTools.Blur(l, new(12.5, 12.5), 5, .2, .5, new Selection(new Rect(12, 0, 13, 25)), 3);
            Assert(soft.Data.AsSpan((12 * 25 + 11) * 4, 4).SequenceEqual(original.AsSpan((12 * 25 + 11) * 4, 4)));
        });
        test("batched retouch matches sequential dabs with immutable input", () =>
        {
            var initial = Solid(Colors.Transparent, 32, 24); var random = new Random(123);
            for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++) Pixel(initial.Pixels, x, y, Color.FromArgb((byte)random.Next(80, 256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            var original = (byte[])initial.Pixels.Data.Clone(); Point origin = new(5.5, 6.5), start = new(15.5, 12.5); Point[] samples = [new(16.5, 12.5), new(18.5, 13.5), new(20.5, 13.5)];
            var selection = new Selection(new Rect(12, 6, 15, 12));
            foreach (var kind in Enum.GetValues<RetouchKind>())
            {
                var layer = initial.Snapshot(); var previous = start;
                foreach (var point in samples)
                {
                    layer.Pixels = kind switch
                    {
                        RetouchKind.Clone => RetouchTools.Clone(layer, initial.Pixels, origin + (point - start), point, 5, .6, .7, selection),
                        RetouchKind.Heal => RetouchTools.Heal(layer, initial.Pixels, origin + (point - start), point, 5, .6, .7, selection),
                        RetouchKind.Smudge => RetouchTools.Smudge(layer, previous, point, 5, .7 * .65, selection),
                        RetouchKind.Liquify => RetouchTools.Liquify(layer, previous, point, 5, .7 * .7, selection),
                        _ => RetouchTools.Blur(layer, point, 5, .6, .7, selection, 1)
                    };
                    previous = point;
                }
                var batch = RetouchTools.ApplyStroke(initial, kind, initial.Pixels, origin, start, start, samples, 5, .6, .7, selection);
                Assert(batch.Data.SequenceEqual(layer.Pixels.Data), $"Batched {kind} changed stroke semantics");
                Assert(initial.Pixels.Data.SequenceEqual(original));
            }
        });
        test("content aware fill removes object using intact texture patches", () =>
        {
            var l = Solid(Colors.Black, 24, 24);
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++) Pixel(l.Pixels, x, y, (x + y) % 2 == 0 ? Colors.White : Colors.Black);
            for (int y = 8; y < 14; y++) for (int x = 8; x < 14; x++) Pixel(l.Pixels, x, y, Colors.Red);
            var original = (byte[])l.Pixels.Data.Clone(); var result = RetouchTools.ContentAwareFill(l, new Selection(new Rect(8, 8, 6, 6)));
            var filled = new HashSet<byte>();
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
            {
                int i = (y * 24 + x) * 4;
                if (x >= 8 && x < 14 && y >= 8 && y < 14) { Assert(result.Data[i] == result.Data[i + 2], "Original object leaked into fill"); filled.Add(result.Data[i]); }
                else Assert(result.Data.AsSpan(i, 4).SequenceEqual(original.AsSpan(i, 4)), "Unselected pixel changed");
            }
            Assert(filled.SetEquals(new byte[] { 0, 255 }), "Texture was averaged instead of sampled"); Assert(l.Pixels.Data.SequenceEqual(original));
        });
        test("content aware fill rejects selection without donor pixels", () =>
        {
            bool threw = false; try { _ = RetouchTools.ContentAwareFill(Solid(Colors.Red, 3, 3), new Selection(new Rect(0, 0, 3, 3))); } catch (InvalidOperationException) { threw = true; } Assert(threw);
        });
        test("background extraction retains disconnected matching interior", () =>
        {
            var l = Solid(Colors.White, 20, 20);
            for (int y = 4; y < 16; y++) for (int x = 4; x < 16; x++) Pixel(l.Pixels, x, y, Colors.Red);
            Pixel(l.Pixels, 10, 10, Colors.White); var result = RetouchTools.RemoveBackgroundByColor(l, 10, 0);
            Assert(result.Data[3] == 0 && result.Data[(5 * 20 + 5) * 4 + 3] == 255 && result.Data[(10 * 20 + 10) * 4 + 3] == 255); Assert(l.Pixels.Data[3] == 255);
        });
        test("background feather has no retained canvas-edge fringe", () =>
        {
            var l = Solid(Colors.White); var result = RetouchTools.RemoveBackgroundByColor(l, 10, 2); Assert(Enumerable.Range(0, 256).All(i => result.Data[i * 4 + 3] == 0));
        });
        test("motion blur alpha-correct color without transparent dark halo", () =>
        {
            var l = Solid(Colors.Transparent, 11, 3); Pixel(l.Pixels, 5, 1, Colors.Red); var result = AdvancedFilters.MotionBlur(l, 0, 4);
            Assert(result.Data[(1 * 11 + 4) * 4 + 2] == 255 && result.Data[(1 * 11 + 4) * 4 + 3] > 0); Assert(result.Data[(0 * 11 + 5) * 4 + 3] == 0);
            Assert(AdvancedFilters.MotionBlur(l, 0, 0).Data.SequenceEqual(l.Pixels.Data));
        });
        test("noise deterministic monochrome and keeps alpha selection", () =>
        {
            var l = Solid(Color.FromArgb(128, 100, 100, 100)); var selection = new Selection(new Rect(0, 0, 8, 16));
            var a = AdvancedFilters.AddNoise(l, 30, 15, true, selection); var b = AdvancedFilters.AddNoise(l, 30, 15, true, selection);
            Assert(a.Data.SequenceEqual(b.Data) && !a.Data.SequenceEqual(l.Pixels.Data));
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) { int i = (y * 16 + x) * 4; Assert(a.Data[i] == a.Data[i + 1] && a.Data[i + 3] == 128); if (x >= 8) Assert(a.Data[i] == 100); }
        });
        test("lens correction identity and transparent outside sampling", () =>
        {
            var l = Solid(Color.FromArgb(128, 10, 20, 200)); Assert(AdvancedFilters.LensCorrection(l, 0).Data.SequenceEqual(l.Pixels.Data));
            var distorted = AdvancedFilters.LensCorrection(l, 1); Assert(distorted.Data[3] == 0); Near(distorted.Data[(8 * 16 + 8) * 4 + 3], 128);
        });
        test("hue saturation identities primaries desaturation and alpha", () =>
        {
            var r = Raster.Solid(1, 1, Color.FromArgb(77, 255, 0, 0)); Assert(AdvancedFilters.HueSaturation(r, 0, 0).Data.SequenceEqual(r.Data));
            var green = AdvancedFilters.HueSaturation(r, 120, 0); Assert(green.Data[1] == 255 && green.Data[2] == 0 && green.Data[3] == 77);
            var gray = AdvancedFilters.HueSaturation(r, 0, -100); Assert(gray.Data[0] == gray.Data[1] && gray.Data[1] == gray.Data[2]);
            var neutral = Raster.Solid(1, 1, Colors.Gray); Assert(AdvancedFilters.HueSaturation(neutral, 70, 100).Data.SequenceEqual(neutral.Data), "Saturation introduced a color cast into neutral gray");
        });
        test("curves honors endpoints intermediate points and identity", () =>
        {
            var r = new Raster(256, 1); for (int x = 0; x < 256; x++) Pixel(r, x, 0, Color.FromArgb(120, (byte)x, (byte)x, (byte)x));
            Assert(AdvancedFilters.Curves(r, [new(0, 0), new(255, 255)]).Data.SequenceEqual(r.Data));
            var curve = AdvancedFilters.Curves(r, [new(0, 0), new(128, 200), new(255, 255)]); Assert(curve.Data[128 * 4] == 200 && curve.Data[128 * 4 + 3] == 120);
        });
        test("gradient map luminance endpoints and alpha retention", () =>
        {
            var r = Raster.Solid(2, 1, Colors.Black); Pixel(r, 1, 0, Color.FromArgb(100, 255, 255, 255)); var mapped = AdvancedFilters.GradientMap(r, Colors.Red, Colors.Blue);
            Assert(mapped.Data[2] == 255 && mapped.Data[0] == 0 && mapped.Data[4] == 255 && mapped.Data[7] == 100);
        });
        test("filter selection feather uses premultiplied interpolation", () =>
        {
            var l = Solid(Colors.Red, 1, 1); var filtered = Raster.Solid(1, 1, Colors.Blue); var result = AdvancedFilters.ApplySelection(l, filtered, SelectionTools.FromMask([128], 1, 1));
            Near(result.Data[0], 128); Near(result.Data[2], 127); Assert(result.Data[3] == 255 && l.Pixels.Data[2] == 255);
        });
        test("expensive operations observe cancellation without mutating input", () =>
        {
            var l = Solid(Colors.White, 40, 40); var original = (byte[])l.Pixels.Data.Clone(); using var cts = new CancellationTokenSource(); cts.Cancel();
            int canceled = 0;
            foreach (Action action in new Action[] { () => SelectionTools.MagicWand(l.Pixels, new(.5, .5), 10, true, cts.Token), () => SelectionTools.Feather(new Selection(new Rect(5, 5, 10, 10)), 40, 40, 2, cts.Token), () => RetouchTools.ContentAwareFill(l, new Selection(new Rect(10, 10, 10, 10)), cts.Token), () => RetouchTools.RemoveBackgroundByColor(l, 20, 2, cts.Token), () => AdvancedFilters.MotionBlur(l, 45, 20, null, cts.Token) })
            { try { action(); } catch (OperationCanceledException) { canceled++; } }
            Assert(canceled == 5 && l.Pixels.Data.SequenceEqual(original));
        });
    }
}
