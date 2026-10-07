using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// 스케치 사진 정리 (SketchCleanup) on synthetic phone photos (SyntheticSketch): finding the sheet, the
// flattened sheet's proportions and straightness, even lighting, line extraction (ruled lines, specks,
// edge strips, boldness) and line colors, plus the shared line-art core (LineArt).
public static class SketchCleanupTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        SyntheticSketch.Sketch? cached = null;
        SyntheticSketch.Sketch Sample() => cached ??= SyntheticSketch.Photo(1200, 900);
        static int Alpha(Raster r, int x, int y) => r.Data[(y * r.Width + x) * 4 + 3];
        // The strongest pixel in a small square around a sheet position of the result.
        static (int A, int B, int G, int R) Strongest(Raster lines, Point uv, int radius = 3)
        {
            int cx = (int)(uv.X * lines.Width), cy = (int)(uv.Y * lines.Height); (int A, int B, int G, int R) best = default;
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(lines.Height - 1, cy + radius); y++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(lines.Width - 1, cx + radius); x++)
                {
                    int i = (y * lines.Width + x) * 4;
                    if (lines.Data[i + 3] > best.A) best = (lines.Data[i + 3], lines.Data[i], lines.Data[i + 1], lines.Data[i + 2]);
                }
            return best;
        }
        // Opaque pixels in a rectangle of sheet coordinates.
        static int Opaque(Raster lines, double u0, double v0, double u1, double v1)
        {
            int count = 0;
            for (int y = (int)(v0 * lines.Height); y < (int)(v1 * lines.Height); y++)
                for (int x = (int)(u0 * lines.Width); x < (int)(u1 * lines.Width); x++) if (Alpha(lines, x, y) > 0) count++;
            return count;
        }
        static double Luma(Raster image, Point p) { int i = ((int)p.Y * image.Width + (int)p.X) * 4; return (.0722 * image.Data[i] + .7152 * image.Data[i + 1] + .2126 * image.Data[i + 2]) / 255; }

        test("sketch cleanup finds a photographed sheet's corners within a pixel and a half, tilted, straight on or turned", () =>
        {
            foreach (var (sketch, label) in new[]
            {
                (Sample(), "tilted"),
                (SyntheticSketch.Photo(900, 700, tilt: 0, turn: 0, roll: 0, pen: false), "straight on"),
                (SyntheticSketch.Photo(700, 940, tilt: 18, turn: -10, roll: -6, fill: .7), "portrait, turned")
            })
            {
                var found = SketchCleanup.DetectSheet(sketch.Photo);
                Check(found.Found && found.Confidence >= .8, $"{label}: the sheet was not found (confidence {found.Confidence:0.###})");
                for (int k = 0; k < 4; k++)
                {
                    double error = (found.Corners[k] - sketch.Corners[k]).Length;
                    Check(error <= 1.5, $"{label}: corner {SketchCleanupDialog.CornerNames[k]} is {error:0.##} px off");
                }
            }
        });

        test("sketch cleanup keeps the whole image when a scanned page fills the frame", () =>
        {
            var scan = SyntheticSketch.Scan(700, 990);
            var found = SketchCleanup.DetectSheet(scan);
            Check(!found.Found && found.Confidence < SketchCleanup.MinConfidence, $"A full-frame scan was taken for a photographed sheet ({found.Confidence:0.###})");
            Check(found.Corners.SequenceEqual(SketchCleanup.ImageCorners(700, 990)), "Low confidence did not fall back to the whole image");
            var result = SketchCleanup.Clean(scan, null, new SketchOptions());
            Check(result.Corners == null && result.Lines.Width == 700 && result.Lines.Height == 990, $"The scan was flattened or resized: {result.Lines.Width}×{result.Lines.Height}");
            Check(Strongest(result.Lines, SyntheticSketch.BlueSample).A >= 230 && Opaque(result.Lines, .2, .95, .4, .99) == 0, "The scan's lines or paper were not cleaned");
            var whole = SketchCleanup.Clean(Sample().Photo, null, new SketchOptions { Flatten = false });
            Check(whole.Corners == null && whole.Detection == null && whole.Lines.Width == 1200 && whole.Lines.Height == 900, "Not flattening did not keep the whole photo");
        });

        test("flattening restores the sheet's A4 proportions and keeps a ruler line straight", () =>
        {
            var sketch = Sample();
            var result = SketchCleanup.Clean(sketch.Photo, null, new SketchOptions());
            var lines = result.Lines;
            double ratio = lines.Height / (double)lines.Width;
            Check(Math.Abs(ratio - Math.Sqrt(2)) <= 1.5 / lines.Width, $"The flattened sheet is {lines.Width}×{lines.Height} ({ratio:0.####}), not A4");
            // The ruler line's center in each column across the middle of the sheet.
            var centers = new List<(double X, double Y)>();
            int top = (int)((SyntheticSketch.RulerV - .02) * lines.Height), bottom = (int)((SyntheticSketch.RulerV + .02) * lines.Height);
            for (int x = (int)(.15 * lines.Width); x < (int)(.85 * lines.Width); x++)
            {
                double sum = 0, weight = 0;
                for (int y = top; y <= bottom; y++) { double a = Alpha(lines, x, y); sum += a * y; weight += a; }
                if (weight > 0) centers.Add((x, sum / weight));
            }
            Check(centers.Count > lines.Width * .65, "The ruler line is broken");
            double mx = centers.Average(c => c.X), my = centers.Average(c => c.Y);
            double slope = centers.Sum(c => (c.X - mx) * (c.Y - my)) / centers.Sum(c => (c.X - mx) * (c.X - mx));
            double deviation = centers.Max(c => Math.Abs(c.Y - (my + slope * (c.X - mx))));
            Check(Math.Abs(slope) < .004 && deviation < 1, $"The ruler line is not straight and level after flattening (slope {slope:0.####}, deviation {deviation:0.##} px)");
            Check(Math.Abs(my / lines.Height - SyntheticSketch.RulerV) < .006, $"The ruler line moved to v = {my / lines.Height:0.####}");
        });

        test("the sheet's proportions come from the camera, not from averaging the foreshortened sides", () =>
        {
            // A 3:2 sheet seen by a phone camera tilted 35° toward it, and turned and rolled a little.
            int width = 1600, height = 1200; double focal = .6 * Math.Sqrt(width * width + height * height);
            Point Project(double x, double y, double tilt, double turn, double roll)
            {
                double a = tilt * Math.PI / 180, b = turn * Math.PI / 180, c = roll * Math.PI / 180;
                double y1 = y * Math.Cos(a), z1 = y * Math.Sin(a), x2 = x * Math.Cos(b) + z1 * Math.Sin(b), z2 = -x * Math.Sin(b) + z1 * Math.Cos(b);
                double x3 = x2 * Math.Cos(c) - y1 * Math.Sin(c), y3 = x2 * Math.Sin(c) + y1 * Math.Cos(c), z3 = z2 + 900;
                return new Point(width / 2.0 + focal * x3 / z3, height / 2.0 + focal * y3 / z3);
            }
            foreach (var (tilt, turn, roll) in new[] { (35.0, 0.0, 0.0), (30.0, 12.0, 5.0) })
            {
                Point[] quad = [Project(-150, -100, tilt, turn, roll), Project(150, -100, tilt, turn, roll), Project(150, 100, tilt, turn, roll), Project(-150, 100, tilt, turn, roll)];
                double aspect = SketchCleanup.Aspect(quad, width, height);
                double averaged = ((quad[1] - quad[0]).Length + (quad[2] - quad[3]).Length) / ((quad[3] - quad[0]).Length + (quad[2] - quad[1]).Length);
                Check(Math.Abs(aspect - 1.5) < .015, $"Tilt {tilt}°/{turn}°/{roll}°: aspect {aspect:0.####} instead of 1.5");
                Check(Math.Abs(averaged - 1.5) > .05, "The averaged sides were already right; the check does not test foreshortening");
                var (w, h) = SketchCleanup.FlatSize(quad, width, height);
                Check(Math.Abs(w / (double)h - 1.5) < .02, $"FlatSize ignored the measured aspect: {w}×{h}");
            }
            // A parallelogram (no perspective) keeps its sides' ratio; a near-A4 ratio snaps to √2.
            Point[] flat = [new(100, 100), new(700, 100), new(700, 950), new(100, 950)];
            Check(Math.Abs(SketchCleanup.Aspect(flat, 800, 1000) - 600 / 850.0) < 1e-9, "A straight-on rectangle changed its ratio");
            var (sw, sh) = SketchCleanup.FlatSize(flat, 800, 1000);
            Check(Math.Abs(sh / (double)sw - Math.Sqrt(2)) <= 1.5 / sw, $"A ratio within 3% of A4 did not snap: {sw}×{sh}");
        });

        test("even lighting: shadowed paper becomes clear while pen lines stay solid and the blue pen stays blue", () =>
        {
            var sketch = Sample();
            Check(Luma(sketch.Photo, sketch.ToPhoto(SyntheticSketch.ShadowPaper)) < .7 * Luma(sketch.Photo, sketch.ToPhoto(new Point(.6, .03))), "The synthetic shadow is too weak to test");
            var lines = SketchCleanup.Clean(sketch.Photo, null, new SketchOptions()).Lines;
            Check(Opaque(lines, .2, .95, .4, .99) == 0 && Opaque(lines, .05, .02, .95, .08) == 0, "Paper in the shadow or in the light kept coverage");
            var blue = Strongest(lines, SyntheticSketch.BlueSample);
            Check(blue.A >= 230 && blue.B > blue.R + 50 && blue.B > blue.G + 30, $"The blue pen line lost its color or coverage: {blue}");
            var black = Strongest(lines, SyntheticSketch.BlackSample);
            Check(black.A >= 230 && Math.Max(black.R, Math.Max(black.G, black.B)) < 110 && Math.Abs(black.B - black.R) < 40, $"The black line is not dark and neutral: {black}");
        });

        test("ruled lines and faint marks drop out at the automatic threshold and return when it is lowered", () =>
        {
            var sketch = Sample();
            var (w, h) = SketchCleanup.FlatSize(SketchCleanup.DetectSheet(sketch.Photo).Corners, sketch.Photo.Width, sketch.Photo.Height);
            var sheet = SketchCleanup.Prepare(SketchCleanup.Flatten(sketch.Photo, SketchCleanup.DetectSheet(sketch.Photo).Corners, w, h));
            Check(sheet.AutomaticThreshold is >= SketchCleanup.MinAutoThreshold and <= SketchCleanup.MaxAutoThreshold, $"Automatic threshold {sheet.AutomaticThreshold:0.###} is out of range");
            var automatic = SketchCleanup.Extract(sheet, new SketchOptions(), out double used, out int speck);
            Check(Math.Abs(used - sheet.AutomaticThreshold) < 1e-9 && speck == sheet.AutomaticSpeckSize, "The automatic values were not used");
            Check(Strongest(automatic, SyntheticSketch.RuleSample, 4).A == 0, "A ruled line survived the automatic threshold");
            var low = SketchCleanup.Extract(sheet, new SketchOptions { Threshold = .08, SpeckSize = 0 });
            Check(Strongest(low, SyntheticSketch.RuleSample, 4).A > 0, "A low threshold did not keep the faint ruled line");
            string? message = null;
            try { SketchCleanup.Extract(sheet, new SketchOptions { Threshold = .99 }); } catch (InvalidDataException e) { message = e.Message; }
            Check(message != null, "A threshold above every line did not report that no line was found");
        });

        test("line colors: black and a chosen color keep the same coverage", () =>
        {
            var sketch = Sample();
            var corners = SketchCleanup.DetectSheet(sketch.Photo).Corners;
            var original = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions()).Lines;
            var black = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions { LineColor = SketchLineColor.Black }).Lines;
            var custom = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions { LineColor = SketchLineColor.Custom, CustomColor = 0xFFC03020 }).Lines;
            bool sameCoverage = true, blackRgb = true, customRgb = true;
            for (int i = 0; i < black.Data.Length; i += 4)
            {
                sameCoverage &= black.Data[i + 3] == original.Data[i + 3] && custom.Data[i + 3] == original.Data[i + 3];
                if (black.Data[i + 3] > 0) blackRgb &= black.Data[i] == 0 && black.Data[i + 1] == 0 && black.Data[i + 2] == 0;
                if (custom.Data[i + 3] > 0) customRgb &= custom.Data[i] == 0x20 && custom.Data[i + 1] == 0x30 && custom.Data[i + 2] == 0xC0;
            }
            Check(sameCoverage && blackRgb && customRgb, "The line colors changed coverage or did not paint their color");
            var half = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions { LineColor = SketchLineColor.Custom, CustomColor = 0x80C03020 }).Lines;
            var solid = Strongest(half, SyntheticSketch.BlackSample);
            Check(solid.A is >= 120 and <= 130, $"A translucent chosen color did not halve the coverage ({solid.A})");
        });

        test("speck cleanup removes dust smaller than its size and keeps strokes and larger marks", () =>
        {
            var page = Raster.Solid(400, 300, Color.FromRgb(240, 236, 226));
            void Paint(int x0, int y0, int w, int h) { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) { int i = (y * 400 + x) * 4; page.Data[i] = page.Data[i + 1] = page.Data[i + 2] = 30; } }
            Paint(40, 150, 320, 3);                                   // a stroke
            for (int k = 0; k < 12; k++) Paint(30 + k * 28, 60, 2, 2); // dust
            Paint(200, 220, 6, 6);                                    // a dot made on purpose
            var sheet = SketchCleanup.Prepare(page);
            var kept = SketchCleanup.Extract(sheet, new SketchOptions { SpeckSize = 0 });
            var cleaned = SketchCleanup.Extract(sheet, new SketchOptions { SpeckSize = 10 });
            Check(Enumerable.Range(0, 12).All(k => Alpha(kept, 31 + k * 28, 61) > 0), "Speck size 0 removed marks");
            Check(Enumerable.Range(0, 12).All(k => Alpha(cleaned, 31 + k * 28, 61) == 0), "Dust smaller than the speck size stayed");
            Check(Alpha(cleaned, 203, 223) == 255 && Alpha(cleaned, 200, 151) == 255, "The stroke or the larger dot was removed");
            Check(SketchCleanup.AutomaticSpeckSize(2000, 1414) == 16 && SketchCleanup.AutomaticSpeckSize(4000, 2828) == 64 && SketchCleanup.AutomaticSpeckSize(300, 200) == 3, "The automatic speck size does not follow the result size");
        });

        test("boldness makes faint coverage more solid without touching paper", () =>
        {
            var sketch = Sample();
            var corners = SketchCleanup.DetectSheet(sketch.Photo).Corners;
            var plain = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions()).Lines;
            var bold = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions { Boldness = 1 }).Lines;
            long a = 0, b = 0; bool paper = true;
            for (int i = 3; i < plain.Data.Length; i += 4) { a += plain.Data[i]; b += bold.Data[i]; paper &= (plain.Data[i] == 0) == (bold.Data[i] == 0); }
            Check(b > a * 1.08 && paper, $"Boldness did not raise coverage ({a} → {b}) or changed which pixels are paper");
        });

        test("corners placed a little outside the sheet leave no table strip along its sides", () =>
        {
            var sketch = SyntheticSketch.Photo(1200, 900, pen: false);
            var center = new Point(sketch.Corners.Average(p => p.X), sketch.Corners.Average(p => p.Y));
            foreach (double grow in new[] { 3.0, 8.0 })
            {
                var corners = sketch.Corners.Select(p => { var v = p - center; v.Normalize(); return p + v * grow; }).ToArray();
                var lines = SketchCleanup.Clean(sketch.Photo, corners, new SketchOptions()).Lines;
                int edge = 0, band = 4;
                for (int y = 0; y < lines.Height; y++) for (int x = 0; x < lines.Width; x++)
                    if ((x < band || y < band || x >= lines.Width - band || y >= lines.Height - band) && Alpha(lines, x, y) > 32) edge++;
                Check(edge == 0, $"Corners {grow} px outside left {edge} dark pixels along the sides");
                Check(Strongest(lines, SyntheticSketch.BlackSample).A >= 230, "The drawing was lost with the strips");
            }
            // The dialog previews from a reduced copy: its softer sheet border meets the faint ruled lines
            // in short dashes, which the outer ring keeps out of the lines.
            var reduced = PreviewScaling.Fit(sketch.Photo, 1050); double scale = reduced.Width / (double)sketch.Photo.Width;
            var (w, h) = SketchCleanup.FlatSize(sketch.Corners, sketch.Photo.Width, sketch.Photo.Height);
            var preview = SketchCleanup.Extract(SketchCleanup.Prepare(SketchCleanup.Flatten(reduced, sketch.Corners.Select(p => new Point(p.X * scale, p.Y * scale)).ToArray(), w, h)), new SketchOptions());
            int border = 0;
            for (int y = 0; y < preview.Height; y++) for (int x = 0; x < preview.Width; x++)
                if ((x < 2 || y < 2 || x >= preview.Width - 2 || y >= preview.Height - 2) && Alpha(preview, x, y) > 0) border++;
            Check(border == 0, $"The preview's sheet border left {border} marks");
        });

        test("results use the photo's full resolution within 16.7 megapixels, beyond the line pattern size", () =>
        {
            var (w, h) = SketchCleanup.WholeSize(6000, 4000);
            Check((long)w * h <= SketchCleanup.MaxPixels && Math.Max(w, h) <= SketchCleanup.MaxSide && Math.Abs(w / (double)h - 1.5) < .002, $"An 24 MP photo was not kept within the limits: {w}×{h}");
            var (fw, fh) = SketchCleanup.FlatSize([new(0, 0), new(9000, 0), new(9000, 7000), new(0, 7000)], 9000, 7000);
            Check((long)fw * fh <= SketchCleanup.MaxPixels && Math.Max(fw, fh) <= SketchCleanup.MaxSide, "A huge sheet exceeded the result limits");
            var sketch = SyntheticSketch.Photo(2000, 1500, shadow: false);
            var result = SketchCleanup.Clean(sketch.Photo, null, new SketchOptions());
            Check(Math.Max(result.Lines.Width, result.Lines.Height) > LinePatterns.MaxSide, $"The sheet was reduced like a pattern tile: {result.Lines.Width}×{result.Lines.Height}");
            Check(Strongest(result.Lines, SyntheticSketch.BlueSample, 5).A >= 230, "The full-resolution sheet lost its lines");
        });

        test("corner checks turn a mirrored order around and refuse a crossed quadrilateral; work can be cancelled", () =>
        {
            Point[] clockwise = [new(10, 10), new(200, 12), new(190, 300), new(14, 280)];
            var mirrored = SketchCleanup.Corners([clockwise[0], clockwise[3], clockwise[2], clockwise[1]], 220, 320);
            Check(mirrored.SequenceEqual(clockwise), "A counter-clockwise order was not turned around");
            foreach (var bad in new[] { new Point[] { new(10, 10), new(190, 300), new(200, 12), new(14, 280) }, [new(10, 10), new(10, 10), new(11, 10), new(10, 11)] })
            {
                bool refused = false;
                try { SketchCleanup.Corners(bad, 220, 320); } catch (InvalidDataException) { refused = true; }
                Check(refused, "A crossed or degenerate quadrilateral was accepted");
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { SketchCleanup.Clean(Sample().Photo, null, new SketchOptions(), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "A cancelled cleanup kept running");
        });

        test("the shared line-art core counts in parallel exactly and keeps the pattern converter's band", () =>
        {
            var random = new Random(5); var values = new float[700_001];
            for (int i = 0; i < values.Length; i++) values[i] = (float)(i % 7 == 0 ? .7 + random.NextDouble() * .3 : random.NextDouble() * .1);
            var histogram = LineArt.Histogram(values);
            Check(histogram.Sum() == values.Length && histogram[Math.Clamp((int)(values[3] * 255 + .5), 0, 255)] > 0, "The parallel histogram lost or misplaced values");
            double threshold = LineArt.Otsu(values);
            Check(threshold > .1 && threshold < .7, $"Otsu did not separate paper from ink ({threshold:0.###})");
            var band = LineArt.Measure(values, threshold);
            Check(band.Low < threshold && band.High > threshold && band.Coverage(band.Low) == 0 && band.Coverage(band.High) == 1, "The coverage band is not around the threshold");
            var source = LinePatternSource.From(SyntheticSketch.Scan(300, 424));
            var tile = source.Convert(source.AutoThreshold);
            var expected = LineArt.Measure(source.Darkness, source.AutoThreshold);
            Check(Enumerable.Range(0, source.Darkness.Length).All(i => tile.Data[i * 4 + 3] == (byte)Math.Round(expected.Coverage(source.Darkness[i]) * 255)), "The pattern converter no longer uses the shared band");
        });
    }
}
