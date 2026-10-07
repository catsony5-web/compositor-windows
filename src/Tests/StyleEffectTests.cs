using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// 한계값 · 망점 · 종이·인쇄 질감 · 빛 번짐 adjustment layers: pixel rules, determinism, resolution
// independence, persistence, undo, layered export and the automation catalog.
public static class StyleEffectTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static AdjustmentSpec Threshold(ThresholdSpec spec) => new() { Kind = AdjustmentKind.Threshold, Threshold = spec };
        static AdjustmentSpec Halftone(HalftoneSpec spec) => new() { Kind = AdjustmentKind.Halftone, Halftone = spec };
        static AdjustmentSpec Paper(PaperTextureSpec spec) => new() { Kind = AdjustmentKind.PaperTexture, Paper = spec };
        static AdjustmentSpec Glow(GlowSpec spec) => new() { Kind = AdjustmentKind.Glow, Glow = spec };
        static Raster Gray(int width, int height, byte value) => Raster.Solid(width, height, Color.FromRgb(value, value, value));
        static Raster Photo(int width, int height)
        {
            var raster = new Raster(width, height);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                raster.Data[i] = (byte)(x * 255 / Math.Max(1, width - 1)); raster.Data[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                raster.Data[i + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2)); raster.Data[i + 3] = 255;
            }
            return raster;
        }
        static Document Holder(Raster pixels) { var doc = new Document { Width = pixels.Width, Height = pixels.Height }; doc.Add(new Layer { Name = "사진", Pixels = pixels }); return doc; }
        static double Ink(Raster raster) { double sum = 0; for (int i = 0; i < raster.Data.Length; i += 4) sum += raster.Data[i + 1]; return 1 - sum / (raster.Width * raster.Height * 255.0); }
        static int MaxDifference(Raster a, Raster b) { int max = 0; for (int i = 0; i < a.Data.Length; i++) max = Math.Max(max, Math.Abs(a.Data[i] - b.Data[i])); return max; }
        static Raster Crop(Raster source, int x, int y, int width, int height)
        {
            var output = new Raster(width, height);
            for (int row = 0; row < height; row++) Buffer.BlockCopy(source.Data, ((y + row) * source.Width + x) * 4, output.Data, row * width * 4, width * 4);
            return output;
        }
        static AdjustmentSpec[] Samples() =>
        [
            Threshold(new() { Level = 120, Smoothness = 6, KeepAlpha = false }),
            Halftone(new() { CellSize = 7, Angle = 30, Shape = HalftoneShape.Line, InkArgb = 0xFF1B2A6B, PaperArgb = 0x00FFFFFF }),
            Paper(new() { Seed = 42, Scale = 1.5, Toner = .4, Streaks = .3, Edges = .6, EdgeArgb = 0xFFFFFFFF }),
            Glow(new() { Threshold = .55, Radius = 9, Intensity = 1.6, TintArgb = 0x80FFB050 })
        ];

        test("threshold is exact at the level, black below it and soft only below it", () =>
        {
            var ramp = new Raster(256, 1);
            for (int v = 0; v < 256; v++) { ramp.Data[v * 4] = ramp.Data[v * 4 + 1] = ramp.Data[v * 4 + 2] = (byte)v; ramp.Data[v * 4 + 3] = 255; }
            foreach (int level in new[] { 0, 1, 64, 128, 200, 255 })
            {
                var output = DocumentFeatures.ApplyAdjustment(ramp, Threshold(new() { Level = level }));
                for (int v = 0; v < 256; v++)
                {
                    byte expected = v >= level ? (byte)255 : (byte)0;
                    Check(output.Data[v * 4] == expected && output.Data[v * 4 + 1] == expected && output.Data[v * 4 + 2] == expected && output.Data[v * 4 + 3] == 255,
                        $"Level {level}: gray {v} became {output.Data[v * 4]}");
                }
            }
            // Color uses Rec. 709 luminance: pure green (182.4) is white at 182 and black at 183.
            var green = Raster.Solid(1, 1, Color.FromRgb(0, 255, 0));
            Check(DocumentFeatures.ApplyAdjustment(green, Threshold(new() { Level = 182 })).Data[0] == 255 && DocumentFeatures.ApplyAdjustment(green, Threshold(new() { Level = 183 })).Data[0] == 0, "Color luminance boundary is wrong");
            var soft = DocumentFeatures.ApplyAdjustment(ramp, Threshold(new() { Level = 128, Smoothness = 16 }));
            Check(soft.Data[128 * 4] == 255 && soft.Data[112 * 4] == 0 && soft.Data[120 * 4] is > 40 and < 215, "Smoothness did not ramp just below the level");
            for (int v = 1; v < 256; v++) Check(soft.Data[v * 4] >= soft.Data[(v - 1) * 4], "Soft threshold is not monotonic");
        });

        test("threshold keeps or hardens transparency as chosen", () =>
        {
            var source = new Raster(4, 1, [0, 0, 0, 0, 0, 0, 0, 90, 255, 255, 255, 128, 40, 40, 40, 255]);
            var kept = DocumentFeatures.ApplyAdjustment(source, Threshold(new() { KeepAlpha = true }));
            var hard = DocumentFeatures.ApplyAdjustment(source, Threshold(new() { KeepAlpha = false }));
            Check(kept.Data[7] == 90 && kept.Data[11] == 128 && kept.Data[3] == 0, "Kept transparency changed");
            Check(hard.Data[3] == 0 && hard.Data[7] == 0 && hard.Data[11] == 255 && hard.Data[15] == 255, "Hard edges did not turn alpha into 0 or 255");
            // Through the compositor the layer's coverage decides how far alpha moves, like color.
            var doc = new Document { Width = 4, Height = 1 }; doc.Add(new Layer { Pixels = source });
            var layer = DocumentFeatures.CreateAdjustment(doc, Threshold(new() { KeepAlpha = false })); layer.Mask = [255, 255, 255, 0]; doc.Add(layer);
            var rendered = Imaging.Render(doc);
            Check(rendered.Data[7] == 0 && rendered.Data[11] == 255 && rendered.Data[15] == 255 && rendered.Data[12] == 40, "Masked hard-edge threshold composited wrongly");
        });

        test("halftone ink coverage follows darkness for flat grays", () =>
        {
            foreach (var shape in Enum.GetValues<HalftoneShape>())
                foreach (var (cell, angle) in new[] { (8.0, 45.0), (12.0, 15.0), (6.0, 0.0) })
                    for (int percent = 10; percent <= 90; percent += 10)
                    {
                        byte value = (byte)Math.Round(255 * (1 - percent / 100.0));
                        var output = DocumentFeatures.ApplyAdjustment(Gray(240, 240, value), Halftone(new() { CellSize = cell, Angle = angle, Shape = shape }));
                        double nominal = 1 - value / 255.0, ink = Ink(output);
                        Check(Math.Abs(ink - nominal) < .03, $"{shape} {cell}px {angle}°: {percent}% gray printed {ink:P1} ink");
                    }
        });

        test("halftone screen follows its angle and is deterministic", () =>
        {
            // At atan(3/4) a 10 px screen repeats every (8, 6) and (-6, 8) pixels, but not every (10, 0).
            var spec = Halftone(new() { CellSize = 10, Angle = Math.Atan2(3, 4) * 180 / Math.PI });
            var output = DocumentFeatures.ApplyAdjustment(Gray(120, 120, 140), spec);
            int Same(int dx, int dy)
            {
                int same = 0, total = 0;
                for (int y = 10; y < 100; y++) for (int x = 10; x < 100; x++, total++) if (output.Data[(y * 120 + x) * 4] == output.Data[((y + dy) * 120 + x + dx) * 4]) same++;
                return same * 100 / total;
            }
            Check(Same(8, 6) >= 99 && Same(-6, 8) >= 99, "The screen does not repeat along its rotated lattice");
            Check(Same(10, 0) < 70, "The rotated screen still repeats along the image axes");
            var level = DocumentFeatures.ApplyAdjustment(Gray(120, 120, 140), Halftone(new() { CellSize = 10, Angle = 0 }));
            Check(Enumerable.Range(10, 90).All(y => level.Data[(y * 120 + 20) * 4] == level.Data[(y * 120 + 30) * 4]), "A 0° screen does not repeat every cell along x");
            Check(output.Data.SequenceEqual(DocumentFeatures.ApplyAdjustment(Gray(120, 120, 140), spec).Data), "Halftone is not deterministic");
            var lines = DocumentFeatures.ApplyAdjustment(Gray(120, 120, 128), Halftone(new() { CellSize = 10, Angle = 90, Shape = HalftoneShape.Line }));
            Check(Enumerable.Range(0, 120).All(x => lines.Data[(5 * 120 + x) * 4] == lines.Data[(60 * 120 + x) * 4]) &&
                Enumerable.Range(0, 120).Select(x => lines.Data[(5 * 120 + x) * 4]).Distinct().Count() > 1, "A 90° line screen is not vertical");
        });

        test("halftone keeps its look at any zoom and in scaled output", () =>
        {
            var doc = Holder(Photo(160, 120)); doc.Add(DocumentFeatures.CreateAdjustment(doc, Halftone(new() { CellSize = 8 })));
            var full = DesignRenderer.RenderOutput(doc); var doubled = DesignRenderer.Render(doc, new Rect(0, 0, 160, 120), 320, 240);
            Check(Math.Abs(Ink(full) - Ink(doubled)) < .015, "2x output changed the ink coverage");
            var reduced = ImportExport.Resize(doubled, 160, 120); double difference = 0;
            for (int i = 0; i < full.Data.Length; i += 4) difference += Math.Abs(full.Data[i] - reduced.Data[i]);
            Check(difference / (160 * 120) < 24, $"2x output is not the same screen drawn finer ({difference / (160 * 120):0.#})");
            // A view where each cell spans two device pixels shows the mean tone instead of moiré.
            var flat = Holder(Gray(400, 400, 128)); flat.Add(DocumentFeatures.CreateAdjustment(flat, Halftone(new() { CellSize = 8 })));
            var far = DesignRenderer.Render(flat, new Rect(0, 0, 400, 400), 100, 100);
            Check(far.Data.Where((_, i) => i % 4 == 0).All(v => Math.Abs(v - 128) <= 2), "A zoomed-out screen shows aliasing instead of its tone");
            var viewport = DesignRenderer.Render(doc, new Rect(40, 30, 50, 40), 50, 40);
            Check(MaxDifference(viewport, Crop(full, 40, 30, 50, 40)) <= 1, "A viewport does not match the full image");
        });

        test("paper texture is deterministic per seed and leaves the image alone at zero", () =>
        {
            var source = Photo(180, 140);
            var first = DocumentFeatures.ApplyAdjustment(source, Paper(new() { Seed = 7, Toner = .5, Streaks = .5, Edges = .5 }));
            Check(first.Data.SequenceEqual(DocumentFeatures.ApplyAdjustment(source, Paper(new() { Seed = 7, Toner = .5, Streaks = .5, Edges = .5 })).Data), "The same seed drew a different texture");
            Check(!first.Data.SequenceEqual(DocumentFeatures.ApplyAdjustment(source, Paper(new() { Seed = 8, Toner = .5, Streaks = .5, Edges = .5 })).Data), "A different seed drew the same texture");
            var neutral = new PaperTextureSpec { Tint = 0, Grain = 0, Fibers = 0 };
            Check(DocumentFeatures.ApplyAdjustment(source, Paper(neutral)).Data.SequenceEqual(source.Data), "Zero strengths changed pixels");
            var doc = Holder(source); var before = Imaging.Render(doc); doc.Add(DocumentFeatures.CreateAdjustment(doc, Paper(neutral)));
            Check(Imaging.Render(doc).Data.SequenceEqual(before.Data), "A neutral paper layer changed the composite");
            foreach (var single in new[] { neutral with { Grain = .6 }, neutral with { Fibers = .8 }, neutral with { Toner = .8 }, neutral with { Streaks = .9 }, neutral with { Edges = .9 }, neutral with { Tint = 1 } })
                Check(!DocumentFeatures.ApplyAdjustment(Gray(160, 160, 235), Paper(single)).Data.SequenceEqual(Gray(160, 160, 235).Data), "A paper component had no effect: " + single);
            var alpha = new Raster(3, 1, [10, 20, 30, 0, 200, 210, 220, 77, 250, 250, 250, 255]);
            Check(Enumerable.Range(0, 3).All(p => DocumentFeatures.ApplyAdjustment(alpha, Paper(new() { Toner = 1, Edges = 1 })).Data[p * 4 + 3] == alpha.Data[p * 4 + 3]), "Paper texture changed transparency");
        });

        test("paper texture has no repeating tile", () =>
        {
            var texture = DocumentFeatures.ApplyAdjustment(Gray(1024, 1024, 220), Paper(new() { Seed = 3, Tint = 0, Grain = 1, Fibers = 1, Toner = .6 }));
            var luma = new double[1024 * 1024]; double mean = 0;
            for (int p = 0; p < luma.Length; p++) mean += luma[p] = texture.Data[p * 4 + 1];
            mean /= luma.Length; double variance = luma.Sum(v => (v - mean) * (v - mean)) / luma.Length;
            Check(variance > 1, "The texture is flat");
            double Correlation(int dx, int dy)
            {
                double sum = 0; int count = 0;
                for (int y = 0; y + dy < 1024; y += 2) for (int x = 0; x + dx < 1024; x += 2, count++) sum += (luma[y * 1024 + x] - mean) * (luma[(y + dy) * 1024 + x + dx] - mean);
                return sum / count / variance;
            }
            Check(Correlation(1, 0) > .1, "The texture has no structure at all");
            foreach (var (dx, dy) in new[] { (128, 0), (256, 0), (384, 0), (512, 0), (0, 128), (0, 256), (0, 512), (256, 256) })
            {
                double rho = Correlation(dx, dy);
                Check(Math.Abs(rho) < .2, $"The texture repeats at ({dx}, {dy}): correlation {rho:0.00}");
            }
        });

        test("paper edges, specks and streaks stay where they belong", () =>
        {
            var page = Gray(300, 200, 240);
            var edges = DocumentFeatures.ApplyAdjustment(page, Paper(new() { Tint = 0, Grain = 0, Fibers = 0, Edges = 1, EdgeWidth = .1, EdgeArgb = 0xFF000000 }));
            double border = Enumerable.Range(0, 300).Average(x => (edges.Data[x * 4] + edges.Data[(199 * 300 + x) * 4]) / 2.0);
            Check(edges.Data[(100 * 300 + 150) * 4] == 240 && border < 150, $"Rough edges did not darken only the border ({border:0})");
            var specks = DocumentFeatures.ApplyAdjustment(page, Paper(new() { Tint = 0, Grain = 0, Fibers = 0, Toner = 1 }));
            int dark = Enumerable.Range(0, 300 * 200).Count(p => specks.Data[p * 4] < 140);
            Check(dark > 20 && dark < 300 * 200 / 10, $"Toner specks cover {dark} pixels");
            var solid = DocumentFeatures.ApplyAdjustment(Gray(300, 200, 10), Paper(new() { Tint = 0, Grain = 0, Fibers = 0, Toner = 1 }));
            Check(Enumerable.Range(0, 300 * 200).Count(p => solid.Data[p * 4] > 60) > 5, "Toner dropouts are missing in solid ink");
            var streaks = DocumentFeatures.ApplyAdjustment(Gray(600, 300, 245), Paper(new() { Tint = 0, Grain = 0, Fibers = 0, Streaks = 1 }));
            double Column(int x) => Enumerable.Range(0, 300).Average(y => streaks.Data[(y * 600 + x) * 4]);
            var columns = Enumerable.Range(0, 600).Select(Column).ToArray();
            Check(columns.Max() - columns.Min() > 8, "Streaks do not vary across the page");
        });

        test("glow spreads only from bright areas, stays bounded and is identity at zero", () =>
        {
            var night = Gray(120, 120, 25);
            for (int y = 57; y < 63; y++) for (int x = 57; x < 63; x++) { int i = (y * 120 + x) * 4; night.Data[i] = night.Data[i + 1] = night.Data[i + 2] = 255; }
            var spec = new GlowSpec { Threshold = .7, Radius = 18, Intensity = 2 };
            var lit = DocumentFeatures.ApplyAdjustment(night, Glow(spec));
            byte At(int x) => lit.Data[(60 * 120 + x) * 4];
            Check(At(64) > 100 && At(70) > 30, $"No glow near the light ({At(64)}, {At(70)})");
            Check(At(64) > At(68) && At(68) > At(72) && At(72) > At(76) && At(76) >= At(84), "Glow does not fall off with distance");
            for (int p = 0; p < 120 * 120; p++)
            {
                int x = p % 120, y = p / 120; double distance = Math.Max(Math.Abs(x - 59.5) - 3, 0) + Math.Max(Math.Abs(y - 59.5) - 3, 0);
                for (int c = 0; c < 3; c++) Check(lit.Data[p * 4 + c] >= night.Data[p * 4 + c], "Glow darkened a pixel");
                if (distance > 30) Check(lit.Data[p * 4] == 25 && lit.Data[p * 4 + 2] == 25, $"Light reached ({x}, {y}) beyond the radius");
            }
            Check(DocumentFeatures.ApplyAdjustment(night, Glow(spec with { Intensity = 0 })).Data.SequenceEqual(night.Data), "Zero intensity changed pixels");
            Check(DocumentFeatures.ApplyAdjustment(Gray(64, 64, 150), Glow(spec)).Data.SequenceEqual(Gray(64, 64, 150).Data), "Light spread from areas below the threshold");
            // The added light never exceeds what the bright pixels emit, times the intensity.
            static double Linear(byte v) { double e = v / 255.0; return e <= .04045 ? e / 12.92 : Math.Pow((e + .055) / 1.055, 2.4); }
            double added = 0; for (int p = 0; p < 120 * 120; p++) added += Linear(lit.Data[p * 4 + 1]) - Linear(night.Data[p * 4 + 1]);
            Check(added <= 2 * 36 * 1.02 && added > 2, $"Glow added {added:0.0} units of light for 72 emitted");
            var white = Gray(50, 50, 255); Check(DocumentFeatures.ApplyAdjustment(white, Glow(spec with { Intensity = 4 })).Data.SequenceEqual(white.Data), "Glow blew past white");
            var red = Raster.Solid(40, 40, Color.FromRgb(10, 10, 10)); for (int i = (20 * 40 + 20) * 4, n = 0; n < 2; n++, i += 4) { red.Data[i + 2] = 255; }
            var redGlow = DocumentFeatures.ApplyAdjustment(red, Glow(spec with { Radius = 8 }));
            Check(redGlow.Data[(20 * 40 + 23) * 4 + 2] > redGlow.Data[(20 * 40 + 23) * 4], "A saturated red light does not glow red");
        });

        test("glow views, tiles and exports agree with the whole image", () =>
        {
            var night = Gray(260, 180, 20);
            foreach (var (cx, cy) in new[] { (30, 30), (130, 90), (240, 160), (200, 20) })
                for (int y = cy - 3; y <= cy + 3; y++) for (int x = cx - 3; x <= cx + 3; x++) { int i = (y * 260 + x) * 4; night.Data[i] = 200; night.Data[i + 1] = 230; night.Data[i + 2] = 255; }
            var doc = Holder(night); doc.Add(DocumentFeatures.CreateAdjustment(doc, Glow(new() { Threshold = .6, Radius = 40, Intensity = 1.5 })));
            var full = DesignRenderer.RenderOutput(doc);
            foreach (var area in new[] { new Int32Rect(100, 60, 70, 50), new Int32Rect(0, 0, 64, 64), new Int32Rect(180, 120, 80, 60) })
            {
                var view = DesignRenderer.Render(doc, new Rect(area.X, area.Y, area.Width, area.Height), area.Width, area.Height);
                Check(MaxDifference(view, Crop(full, area.X, area.Y, area.Width, area.Height)) <= 1, $"View {area} lost light from outside it");
            }
            Check(StyleEffects.Spreads(doc) && !StyleEffects.Spreads(Holder(night)), "Spreading effects are not recognized");
            Check(Imaging.Render(doc).Data.SequenceEqual(full.Data), "The photo compositor and the design renderer differ");
        });

        test("style effects save, load, undo and keep masks, clipping and opacity", () =>
        {
            var doc = Holder(Photo(64, 48));
            doc.Add(new Layer { Name = "기준", Pixels = Raster.Solid(30, 20, Colors.White), X = 10, Y = 10 });
            var layers = Samples().Select(spec => DocumentFeatures.CreateAdjustment(doc, spec)).ToArray();
            layers[0].Mask = Enumerable.Range(0, 64 * 48).Select(i => (byte)(i % 64 < 32 ? 255 : 0)).ToArray();
            layers[1].Clipped = true; layers[2].Opacity = .7; layers[3].Blend = BlendMode.Screen;
            doc.Layers.Insert(2, layers[1]); doc.Add(layers[0]); doc.Add(layers[2]); doc.Add(layers[3]);
            Check(layers.Select(l => l.Name).SequenceEqual(["한계값", "망점", "종이·인쇄 질감", "빛 번짐"]), "Default layer names changed");
            string path = Path.Combine(directory, "style-effects.moruproj"); ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            for (int i = 0; i < doc.Layers.Count; i++)
                Check(DocumentFeatures.SameAdjustment(doc.Layers[i].Adjustment, loaded.Layers[i].Adjustment) && doc.Layers[i].Clipped == loaded.Layers[i].Clipped, "A setting was lost on save: " + doc.Layers[i].Name);
            Check(Imaging.Render(loaded).Data.SequenceEqual(Imaging.Render(doc).Data), "The saved project renders differently");
            var history = new History(); history.Reset(doc); var before = doc.Snapshot();
            var glow = doc.Layers.Single(l => l.Adjustment?.Kind == AdjustmentKind.Glow);
            glow.Adjustment = glow.Adjustment! with { Glow = glow.Adjustment.Glow with { Radius = 20 } }; history.Commit("빛 번짐 설정", before, doc);
            var undone = history.Undo(doc);
            Check(undone.Layers.Single(l => l.Id == glow.Id).Adjustment!.Glow.Radius == 9, "Undo did not restore the glow settings");
            Check(history.Redo(undone).Layers.Single(l => l.Id == glow.Id).Adjustment!.Glow.Radius == 20, "Redo did not reapply the glow settings");
            // Old projects without the new settings still load with defaults.
            var legacy = System.Text.Json.JsonSerializer.Deserialize<AdjustmentSpec>("{\"Kind\":2}")!;
            Check(legacy.Threshold == new ThresholdSpec() && legacy.Glow == new GlowSpec() && legacy.Paper == new PaperTextureSpec() && legacy.Halftone == new HalftoneSpec(), "Missing settings did not default");
        });

        test("style effect settings reject invalid values", () =>
        {
            void Rejects(AdjustmentSpec spec, string label) { bool rejected = false; try { spec.Validate(); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Accepted " + label); }
            Rejects(Threshold(new() { Level = 256 }), "level 256"); Rejects(Threshold(new() { Smoothness = double.NaN }), "NaN smoothness");
            Rejects(Halftone(new() { CellSize = 1 }), "1 px cells"); Rejects(Halftone(new() { Shape = (HalftoneShape)9 }), "unknown dot shape"); Rejects(Halftone(new() { Angle = double.PositiveInfinity }), "infinite angle");
            Rejects(Paper(new() { Grain = 1.5 }), "grain 1.5"); Rejects(Paper(new() { Scale = .1 }), "scale 0.1"); Rejects(Paper(new() { EdgeWidth = .9 }), "edge width 0.9");
            Rejects(Glow(new() { Radius = 0 }), "radius 0"); Rejects(Glow(new() { Intensity = 5 }), "intensity 5"); Rejects(Glow(new() { Threshold = -.1 }), "threshold -0.1");
            Rejects(new AdjustmentSpec { Kind = AdjustmentKind.Glow, Glow = null! }, "missing glow settings");
            Rejects(new AdjustmentSpec { Kind = AdjustmentKind.Levels, Paper = null! }, "missing paper settings on another kind");
            foreach (var spec in Samples()) spec.Validate();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            foreach (var spec in Samples())
            {
                bool canceled = false; try { StyleEffects.Apply(Photo(40, 40), spec, Matrix.Identity, 40, 40, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, spec.Kind + " ignored cancellation");
            }
        });

        test("layered .psd stores style effects as their adjusted result", () =>
        {
            var doc = Holder(Photo(48, 36)); doc.Name = "효과";
            foreach (var spec in Samples().Skip(1)) doc.Add(DocumentFeatures.CreateAdjustment(doc, spec));
            Check(PsdLayerExport.Build(doc).Adjustments == 3, "The .psd plan does not count the effect layers");
            string path = Path.Combine(directory, "style-effects.psd"); ProjectStore.AtomicWrite(path, s => PsdCompatibility.Write(doc, s, true));
            var back = PsdCompatibility.Read(path, true).Document;
            Check(back.Layers.Count == 4 && back.Layers.Skip(1).All(l => l.Kind == LayerKind.Raster), "Effect layers were not exported as results");
            Check(MaxDifference(DesignRenderer.RenderOutput(doc), DesignRenderer.RenderOutput(back)) <= 2, "The exported .psd looks different");
            bool rejected = false; try { CompositorPackage.Export(doc, Path.Combine(directory, "style-effects.comp")); } catch (NotSupportedException) { rejected = true; }
            Check(rejected, ".comp export silently dropped an effect it cannot store");
        });

        test("automation catalog accepts style effect parameters per kind", () =>
        {
            JsonObject Args(params (string Key, JsonNode? Value)[] values)
            {
                var args = new JsonObject { ["documentId"] = Guid.NewGuid().ToString(), ["expectedRevision"] = Guid.NewGuid().ToString() };
                foreach (var (key, value) in values) args[key] = value; return args;
            }
            void Accept(JsonObject args) => AutomationCatalog.Validate("add_adjustment", args);
            void Reject(JsonObject args) { bool rejected = false; try { Accept(args); } catch (ArgumentException) { rejected = true; } Check(rejected, "Accepted " + args.ToJsonString()); }
            Accept(Args(("kind", "threshold"), ("level", 140), ("smoothness", 4), ("keepAlpha", false)));
            Accept(Args(("kind", "halftone"), ("cellSize", 10), ("angle", -15), ("dotShape", "line"), ("ink", "#FF1B1464"), ("paper", "transparent")));
            Accept(Args(("kind", "paper_texture"), ("seed", 12), ("textureSize", 3), ("paperTint", .4), ("paperColor", "#EFE6D2"), ("grain", .5), ("fibers", .2), ("toner", .3), ("streaks", .1), ("edges", .6), ("edgeWidth", .12), ("edgeColor", "#FFFFFF")));
            Accept(Args(("kind", "glow"), ("threshold", .6), ("radius", 80), ("intensity", 2.5), ("glowColor", "#80FFB060")));
            Accept(Args(("kind", "glow")));
            Reject(Args(("kind", "halftone"), ("radius", 10)));
            Reject(Args(("kind", "glow"), ("level", 100)));
            Reject(Args(("kind", "halftone"), ("cellSize", 1)));
            Reject(Args(("kind", "halftone"), ("dotShape", "star")));
            Reject(Args(("kind", "paper_texture"), ("edgeColor", "brown")));
            Reject(Args(("kind", "paper_texture"), ("seed", 1.5)));
            Reject(Args(("kind", "threshold"), ("keepAlpha", "yes")));
            Reject(Args(("kind", "glow"), ("intensity", 4.5)));
            Reject(Args(("kind", "photo_develop"), ("grain", .5)));
            Reject(Args(("kind", "sharpen")));
            var tools = AutomationCatalog.Tools().OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "morupixel_add_adjustment");
            var kinds = tools["inputSchema"]!["properties"]!["kind"]!["enum"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
            Check(kinds.SequenceEqual(["exposure", "levels", "hue_saturation", "photo_develop", "threshold", "halftone", "paper_texture", "glow"]), "Kind choices changed: " + string.Join(", ", kinds));
        });

        test("style effects stay interactive on a full HD image", () =>
        {
            // Generous bounds (the machine may be busy): a guard against accidental per-pixel blowups, not a benchmark.
            var image = Photo(1920, 1080);
            foreach (var spec in Samples().Append(Paper(new() { Toner = .6, Streaks = .6, Edges = .6, Grain = .6, Fibers = .6 })).Append(Glow(new() { Radius = 300 })))
            {
                DocumentFeatures.ApplyAdjustment(image, spec);
                var watch = Stopwatch.StartNew(); DocumentFeatures.ApplyAdjustment(image, spec);
                Check(watch.Elapsed.TotalSeconds < 4, $"{spec.Kind} took {watch.Elapsed.TotalMilliseconds:0} ms on 1920×1080");
            }
        });
    }
}
