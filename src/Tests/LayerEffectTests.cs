using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>레이어 효과: stroke widths, glows, overlays, order, toggles, storage, renderer and export parity, speed.</summary>
public static class LayerEffectTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static Document Board(int width = 40, int height = 40, Color? background = null)
        {
            var document = new Document { Width = width, Height = height, Name = "효과" };
            if (background is { } color) document.Add(new Layer { Name = "배경", Pixels = Raster.Solid(width, height, color) });
            return document;
        }
        static Layer Square(Document document, int size, Color color, double x, double y, params LayerEffect[] effects)
        {
            var layer = new Layer { Name = "사각형", Pixels = Raster.Solid(size, size, color), X = x, Y = y, Effects = LayerEffects.Normalize(effects) };
            document.Add(layer); return layer;
        }
        static (byte B, byte G, byte R, byte A) Px(Raster raster, int x, int y) { int i = (y * raster.Width + x) * 4; return (raster.Data[i], raster.Data[i + 1], raster.Data[i + 2], raster.Data[i + 3]); }
        static LayerEffect Stroke(double size, StrokePosition position, uint color = 0xFFFFFFFF, double opacity = 1, bool rounded = true) =>
            LayerEffect.Default(LayerEffectKind.Stroke) with { Size = size, Position = position, ColorArgb = color, Opacity = opacity, Rounded = rounded };
        static double Difference(Raster a, Raster b)
        {
            Check(a.Width == b.Width && a.Height == b.Height, "Sizes differ");
            double total = 0;
            for (int i = 0; i < a.Data.Length; i += 4)
                for (int c = 0; c < 4; c++) total += Math.Abs(a.Data[i + c] * (c == 3 ? 1 : a.Data[i + 3] / 255d) - b.Data[i + c] * (c == 3 ? 1 : b.Data[i + 3] / 255d));
            return total / a.Data.Length;
        }
        static bool White((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 250 && p.G > 250 && p.B > 250;
        static bool Red((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 250 && p.G < 5 && p.B < 5;

        test("layer effects: outside, centre and inside strokes cover their widths around a known square", () =>
        {
            Raster Draw(LayerEffect stroke) { var d = Board(); Square(d, 10, Colors.Red, 15, 15, stroke); return Imaging.Render(d); }
            var outside = Draw(Stroke(4, StrokePosition.Outside));
            Check(White(Px(outside, 14, 20)) && White(Px(outside, 11, 20)) && Px(outside, 10, 20).A == 0 && Red(Px(outside, 15, 20)) && Red(Px(outside, 20, 20)),
                $"Outside stroke: {Px(outside, 14, 20)} {Px(outside, 11, 20)} {Px(outside, 10, 20)} {Px(outside, 15, 20)}");
            Check(Px(outside, 11, 11).A == 0 && White(Px(outside, 13, 12)), "A rounded outside stroke must round the square's corners");
            var square = Draw(Stroke(4, StrokePosition.Outside, rounded: false));
            Check(White(Px(square, 11, 11)) && Px(square, 10, 10).A == 0, "A square stroke must keep square corners");
            var center = Draw(Stroke(4, StrokePosition.Center));
            Check(White(Px(center, 13, 20)) && Px(center, 12, 20).A == 0 && White(Px(center, 16, 20)) && Red(Px(center, 17, 20)), "Centre stroke must straddle the edge by half its width on each side");
            var inside = Draw(Stroke(4, StrokePosition.Inside));
            Check(White(Px(inside, 15, 20)) && White(Px(inside, 18, 20)) && Red(Px(inside, 19, 20)) && Px(inside, 14, 20).A == 0, "Inside stroke must line the edge within the shape");
            var tinted = Draw(Stroke(4, StrokePosition.Outside, 0xFF0000FF, .5));
            var p = Px(tinted, 13, 20);
            Check(p.B == 255 && p.R == 0 && Math.Abs(p.A - 128) <= 1, $"Stroke color or opacity is wrong: {p}");
        });

        test("layer effects: outer glow is strong at the edge, fades out and stays within its reach", () =>
        {
            var d = Board(70, 40); Square(d, 10, Colors.Red, 40, 15, LayerEffect.Default(LayerEffectKind.OuterGlow) with { Size = 10, Spread = 0, Opacity = 1, ColorArgb = 0xFFFFFFFF });
            var r = Imaging.Render(d);
            Check(Px(r, 39, 20).A > 160, $"The glow is weak at the edge: {Px(r, 39, 20)}");
            for (int x = 39; x > 0; x--) Check(Px(r, x - 1, 20).A <= Px(r, x, 20).A, $"The glow does not fade outward at x={x}");
            double reach = d.Layers[0].Effects![0].Reach;
            Check(Px(r, 40 - (int)Math.Ceiling(reach) - 2, 20).A == 0 && Red(Px(r, 45, 20)), $"The glow reaches past {reach}px or covers the layer");
            Check(Math.Abs(reach - 14) < 1e-9 && LayerEffectRenderer.LocalBounds(d.Layers[0]).Left <= -reach, "The glow's reach and the layer's effect bounds disagree");
        });

        test("layer effects: inner glow stays inside the shape and fades toward its middle", () =>
        {
            var d = Board(); var plain = Imaging.Render(Board().Also(b => Square(b, 20, Colors.Red, 10, 10)));
            Square(d, 20, Colors.Red, 10, 10, LayerEffect.Default(LayerEffectKind.InnerGlow) with { Size = 6, Opacity = 1, ColorArgb = 0xFFFFFFFF });
            var r = Imaging.Render(d);
            Check(Px(r, 10, 20).G > 150 && Px(r, 20, 20).G < 5 && Px(r, 9, 20).A == 0, $"Inner glow: edge {Px(r, 10, 20)}, middle {Px(r, 20, 20)}");
            for (int i = 3; i < r.Data.Length; i += 4) Check(r.Data[i] == plain.Data[i], "An inner glow changed the layer's coverage");
        });

        test("layer effects: color overlay recolors and keeps the layer's alpha; blend modes mix with its colors", () =>
        {
            var pixels = new Raster(10, 10);
            for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) { int i = (y * 10 + x) * 4; pixels.Data[i] = pixels.Data[i + 1] = pixels.Data[i + 2] = 200; pixels.Data[i + 3] = (byte)(x * 28); }
            Document Make(params LayerEffect[] effects) { var d = Board(20, 20); d.Add(new Layer { Name = "아이콘", Pixels = pixels, X = 5, Y = 5, Effects = LayerEffects.Normalize(effects) }); return d; }
            var plain = Imaging.Render(Make());
            var red = Imaging.Render(Make(LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFFFF0000 }));
            for (int i = 0; i < red.Data.Length; i += 4)
            {
                Check(red.Data[i + 3] == plain.Data[i + 3], "The overlay changed the alpha");
                if (red.Data[i + 3] > 0) Check(red.Data[i + 2] >= 254 && red.Data[i + 1] <= 1 && red.Data[i] <= 1, $"The overlay did not recolor byte {i}");
            }
            var multiply = Imaging.Render(Make(LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFFFF8000, Blend = BlendMode.Multiply }));
            var p = Px(multiply, 14, 10);
            Check(Math.Abs(p.R - 200) <= 1 && Math.Abs(p.G - 100) <= 1 && p.B <= 1, $"Multiply overlay: {p}");
            var half = Imaging.Render(Make(LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFF000000, Opacity = .5 }));
            Check(Math.Abs(Px(half, 14, 10).R - 100) <= 1, "Overlay opacity does not mix the color halfway");
        });

        test("layer effects: gradient overlay runs along its angle across the layer and turns with it", () =>
        {
            Document Make(double angle, double rotation = 0)
            {
                var d = Board(40, 40);
                d.Add(new Layer { Name = "판", Pixels = Raster.Solid(20, 20, Colors.Gray), X = 10, Y = 10, Rotation = rotation,
                    Effects = [LayerEffect.Default(LayerEffectKind.GradientOverlay) with { ColorArgb = 0xFF000000, EndColorArgb = 0xFFFFFFFF, Angle = angle }] });
                return d;
            }
            var across = Imaging.Render(Make(0));
            Check(Px(across, 11, 20).R < 30 && Px(across, 28, 20).R > 225 && Px(across, 20, 12).R is > 100 and < 155, $"Left to right: {Px(across, 11, 20)} {Px(across, 28, 20)}");
            var down = Imaging.Render(Make(90));
            Check(Px(down, 20, 11).R < 30 && Px(down, 20, 28).R > 225, "Top to bottom failed");
            var turned = Imaging.Render(Make(0, 90));
            Check(Px(turned, 20, 11).R < 30 && Px(turned, 20, 28).R > 225, "A rotated layer's gradient must turn with the layer");
        });

        test("layer effects: list order stacks effects, later ones on top", () =>
        {
            Raster Draw(params LayerEffect[] effects) { var d = Board(); Square(d, 10, Colors.Red, 15, 15, effects); return Imaging.Render(d); }
            var whiteUnder = Draw(Stroke(6, StrokePosition.Outside), Stroke(3, StrokePosition.Outside, 0xFF000000));
            Check(Px(whiteUnder, 13, 20) is { R: < 5, A: 255 } && White(Px(whiteUnder, 10, 20)), "The later (black) stroke must sit on top of the earlier one");
            var whiteOver = Draw(Stroke(3, StrokePosition.Outside, 0xFF000000), Stroke(6, StrokePosition.Outside));
            Check(White(Px(whiteOver, 13, 20)) && White(Px(whiteOver, 10, 20)), "The later (white) stroke must cover the earlier one");
            var overlays = Draw(LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFF00FF00 }, LayerEffect.Default(LayerEffectKind.ColorOverlay) with { ColorArgb = 0xFF0000FF });
            Check(Px(overlays, 20, 20) is { B: 255, G: 0 }, "The later overlay must win");
            Check(LayerEffects.Add([LayerEffect.Default(LayerEffectKind.Stroke)], LayerEffect.Default(LayerEffectKind.DropShadow))[0].Kind == LayerEffectKind.DropShadow &&
                LayerEffects.Add([LayerEffect.Default(LayerEffectKind.DropShadow)], LayerEffect.Default(LayerEffectKind.Stroke))[1].Kind == LayerEffectKind.Stroke,
                "New effects must go to their usual place (shadows at the back, strokes on top)");
        });

        test("layer effects: an effect turned off draws the plain layer; groups and adjustments cannot hold effects", () =>
        {
            var plain = Board(); Square(plain, 10, Colors.Red, 15, 15);
            var off = Board(); Square(off, 10, Colors.Red, 15, 15, Stroke(4, StrokePosition.Outside) with { Enabled = false });
            Check(Imaging.Render(plain).Data.AsSpan().SequenceEqual(Imaging.Render(off).Data), "A disabled effect changed the render");
            Check(!LayerEffects.Any(off.Layers[0]) && LayerEffectRenderer.LocalBounds(off.Layers[0]) == new Rect(0, 0, 10, 10), "A disabled effect still counts");
            var group = DocumentFeatures.CreateGroup(off); group.Effects = [Stroke(2, StrokePosition.Outside)];
            bool rejected = false; try { off.Add(group); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A group accepted effects");
            var bad = Board(); Square(bad, 4, Colors.Red, 0, 0); bad.Layers[0].Effects = [Stroke(2, StrokePosition.Outside) with { Opacity = 2 }];
            rejected = false; try { bad.Validate(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "An invalid effect passed validation");
        });

        test("layer effects: a drop shadow falls along its angle in the parent space, also on rotated and flipped layers", () =>
        {
            foreach (var (rotation, flip) in new[] { (0d, false), (90d, false), (0d, true), (37d, true) })
            {
                var d = Board(60, 40);
                d.Add(new Layer { Name = "판", Pixels = Raster.Solid(10, 10, Colors.Red), X = 15, Y = 15, Rotation = rotation, FlipX = flip,
                    Effects = [LayerEffect.Default(LayerEffectKind.DropShadow) with { Distance = 12, Angle = 0, Size = 0, Opacity = 1, ColorArgb = 0xFF000000 }] });
                var r = Imaging.Render(d);
                var right = Px(r, rotation == 37 ? 33 : 32, 20); var left = Px(r, 8, 20);
                Check(right.A > 200 && right.R < 20 && left.A == 0, $"rotation {rotation}, flip {flip}: shadow at right {right}, left {left}");
            }
        });

        test("layer effects: lengths stay in parent pixels on a scaled layer", () =>
        {
            var d = Board(); d.Add(new Layer { Name = "작은 판", Pixels = Raster.Solid(5, 5, Colors.Red), X = 15, Y = 15, Scale = 2, Effects = [Stroke(4, StrokePosition.Outside)] });
            var r = Imaging.Render(d);
            Check(White(Px(r, 13, 20)) && White(Px(r, 11, 20)) && Px(r, 10, 20).A == 0 && Red(Px(r, 20, 20)), $"Scaled stroke: {Px(r, 11, 20)} {Px(r, 10, 20)}");
        });

        test("layer effects: projects keep effects; files without them open unchanged and the format version stays", () =>
        {
            var d = Board(40, 40, Colors.White);
            var layer = Square(d, 10, Colors.Red, 15, 15, LayerEffect.Default(LayerEffectKind.DropShadow), Stroke(3, StrokePosition.Center, 0xFF102030) with { Rounded = false },
                LayerEffect.Default(LayerEffectKind.ColorOverlay) with { Enabled = false, Blend = BlendMode.Color });
            string path = Path.Combine(directory, "layer-effects.moruproj"); ProjectStore.Save(d, path);
            var back = ProjectStore.Load(path);
            Check(LayerEffects.Same(back.Layers[1].Effects, layer.Effects) && back.Layers[0].Effects == null, "Effects changed in the round trip");
            Check(Imaging.Render(back).Data.AsSpan().SequenceEqual(Imaging.Render(d).Data), "The reopened project renders differently");
            using (var zip = ZipFile.OpenRead(path))
            {
                using var reader = new StreamReader(zip.GetEntry("document.json")!.Open()); string json = reader.ReadToEnd();
                Check(json.Split("\"Effects\"").Length == 2 && json.Contains("\"Version\": 2"), "Only the layer with effects may carry the field, and the version must stay readable by older versions");
                // An older version reads the manifest without the field: the same layers, the plain look.
                var manifest = System.Text.Json.JsonSerializer.Deserialize<ProjectStore.Manifest>(json)!;
                Check(manifest.Layers!.Count == 2, "The manifest no longer reads");
            }
            var plain = Board(40, 40, Colors.White); Square(plain, 10, Colors.Red, 15, 15);
            string old = Path.Combine(directory, "layer-effects-none.moruproj"); ProjectStore.Save(plain, old);
            Check(ProjectStore.Load(old).Layers.All(l => l.Effects == null), "A project without effects gained some");
        });

        test("layer effects: the pixel and design renderers, viewport tiles and dirty redraws agree", () =>
        {
            var d = Board(80, 60, Color.FromRgb(230, 235, 240));
            var cut = new Raster(30, 24); for (int y = 0; y < 24; y++) for (int x = 0; x < 30; x++)
            { double dx = (x + .5 - 15) / 15, dy = (y + .5 - 12) / 12; int i = (y * 30 + x) * 4; cut.Data[i + 1] = 120; cut.Data[i + 2] = 200; cut.Data[i + 3] = Imaging.Byte(Math.Clamp((1 - Math.Sqrt(dx * dx + dy * dy)) * 12, 0, 1) * 255); }
            d.Add(new Layer { Name = "오린 사진", Pixels = cut, X = 20, Y = 14, Rotation = 12, Effects = [LayerEffect.Default(LayerEffectKind.DropShadow), Stroke(4, StrokePosition.Outside), LayerEffect.Default(LayerEffectKind.InnerGlow)] });
            var shape = VectorShapes.Create(new ShapeSpec { Width = 18, Height = 12, FillArgb = 0xFF3060C0 }, 52, 30);
            shape.Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow) with { Size = 8 }]; d.Add(shape);
            var photo = Imaging.Render(d); var design = DesignRenderer.Render(d, new Rect(0, 0, 80, 60), 80, 60);
            double difference = Difference(photo, design);
            Check(difference < 1.2, $"Pixel and design renderers differ ({difference:0.###})");
            var region = new Int32Rect(40, 20, 30, 25);
            var tile = DesignRenderer.RenderRegion(d, new Rect(0, 0, 80, 60), 80, 60, region);
            var dirty = DesignRenderer.RenderDirty(d, new Rect(0, 0, 80, 60), 80, 60, region);
            for (int y = 0; y < region.Height; y++) for (int x = 0; x < region.Width; x++)
            {
                var full = Px(design, region.X + x, region.Y + y);
                Check(Px(tile, x, y) == full, $"Viewport tile differs at {x},{y}");
                Check(Px(dirty, region.X + x, region.Y + y) == full, $"Dirty redraw differs at {x},{y}");
            }
            // A dirty rectangle that only the glow reaches still redraws it.
            var glowOnly = new Int32Rect(46, 30, 4, 4);
            var reached = DesignRenderer.RenderDirty(d, new Rect(0, 0, 80, 60), 80, 60, glowOnly);
            Check(Px(reached, 47, 31) == Px(design, 47, 31) && Px(design, 47, 31) != Px(DesignRenderer.Render(Board(80, 60, Color.FromRgb(230, 235, 240)), new Rect(0, 0, 80, 60), 80, 60), 47, 31),
                "A dirty redraw beside the shape lost its glow");
        });

        test("layer effects: exports match the screen and scale with the output size", () =>
        {
            var d = Board(40, 40, Colors.Black); Square(d, 10, Colors.Red, 15, 15, Stroke(4, StrokePosition.Outside));
            string png = Path.Combine(directory, "layer-effects.png"); ImportExport.Export(d, png);
            Check(Raster.Load(png).Data.AsSpan().SequenceEqual(Imaging.Render(d).Data), "The PNG differs from the screen render");
            var twice = new ExportSettings(Scale: 2).Render(d);
            Check(twice.Width == 80 && White(Px(twice, 23, 40)) && White(Px(twice, 22, 40)) && Px(twice, 21, 40) is { R: < 40 } && Red(Px(twice, 31, 40)),
                $"A 2x export must draw the stroke twice as wide: {Px(twice, 23, 40)} {Px(twice, 22, 40)} {Px(twice, 21, 40)}");
            var selected = SelectedLayerExport.Render(d, [d.Layers[1].Id]);
            Check(selected.Image.Width == 18 && selected.CanvasBounds.X == 11, $"The selected-layer export must include the stroke: {selected.CanvasBounds}");
        });

        test("layer effects: layered .psd draws effects into the layer and PDF keeps text strokes as vectors", () =>
        {
            var d = Board(60, 40, Colors.White);
            var mask = new byte[100]; for (int i = 0; i < 100; i++) mask[i] = (byte)(i % 10 < 5 ? 255 : 0);
            var masked = Square(d, 10, Colors.Red, 10, 10, Stroke(4, StrokePosition.Outside, 0xFF000000)); masked.Mask = mask;
            var plan = PsdLayerExport.Build(d);
            Check(plan.Effects == 1, "The .psd plan does not count the layer with effects");
            string psd = Path.Combine(directory, "layer-effects.psd"); ProjectStore.AtomicWrite(psd, s => CompatibilityExport.Write(d, CompatibilityExportFormat.PsdLayers, s));
            var back = PsdCompatibility.Read(psd, true).Document; var layer = back.Layers.Single(l => l.Name == "사각형");
            Check(layer.Mask == null && layer.Pixels.Width == 13 && layer.Pixels.Height == 18 && layer.X == 6, $"The .psd layer must hold the masked shape with its stroke: {layer.Pixels.Width}×{layer.Pixels.Height} at {layer.X}");
            double difference = Difference(DesignRenderer.RenderOutput(d), DesignRenderer.RenderOutput(back));
            Check(difference < .6, $"The .psd composite differs ({difference:0.###})");
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "효과", FontFamily = "Malgun Gothic", FontSize = 20, ColorArgb = 0xFF202020 }, 30, 8);
            text.Effects = [Stroke(3, StrokePosition.Outside)]; d.Add(text);
            var pdf = PdfLayerExport.Build(d, false);
            Check(pdf.VectorParts == 1, $"A stroked text must stay a vector part (vector {pdf.VectorParts}, image {pdf.ImageParts})");
            text.Effects = [LayerEffect.Default(LayerEffectKind.OuterGlow)];
            Check(PdfLayerExport.Build(d, false).VectorParts == 0, "A glowing text must become an image part");
            text.Effects = [Stroke(3, StrokePosition.Outside)];
            string path = Path.Combine(directory, "layer-effects.pdf"); ProjectStore.AtomicWrite(path, s => PdfLayerExport.Write(d, s, false));
            Check(new FileInfo(path).Length > 0 && VectorPdfExport.DrawsEffectsAsVectors(text), "The PDF was not written");
        });

        test("layer effects: a 4K cut-out with stroke, shadow and glow renders and moves interactively (timings)", () =>
        {
            const int W = 3840, H = 2160;
            var cut = new Raster(W, H);
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    double dx = (x + .5 - W / 2.0) / (W * .42), dy = (y + .5 - H / 2.0) / (H * .42), r = Math.Sqrt(dx * dx + dy * dy) + .06 * Math.Sin(Math.Atan2(dy, dx) * 7);
                    int i = (y * W + x) * 4; cut.Data[i] = 90; cut.Data[i + 1] = 140; cut.Data[i + 2] = 60; cut.Data[i + 3] = Imaging.Byte(Math.Clamp((1 - r) * 300, 0, 1) * 255);
                }
            });
            var d = Board(W, H, Colors.White);
            var layer = new Layer { Name = "4K 오린 사진", Pixels = cut, Effects = [LayerEffect.Default(LayerEffectKind.DropShadow) with { Size = 30, Distance = 24 }, LayerEffect.Default(LayerEffectKind.OuterGlow) with { Size = 24 }, Stroke(12, StrokePosition.Outside)] };
            d.Add(layer);
            LayerEffectRenderer.ClearCache();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var first = Imaging.Render(d); double cold = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); layer.X += 37; Imaging.Render(d); double moved = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); layer.Effects = LayerEffects.Replace(layer.Effects!, 2, layer.Effects![2] with { ColorArgb = 0xFFFFE000 }); Imaging.Render(d); double recolored = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); layer.Effects = LayerEffects.Replace(layer.Effects!, 2, layer.Effects![2] with { Size = 16 }); Imaging.Render(d); double resized = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var plain = Board(W, H, Colors.White); plain.Add(new Layer { Name = "4K", Pixels = cut, X = 37 }); Imaging.Render(plain); double baseline = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var view = DesignRenderer.Render(d, new Rect(0, 0, W, H), 1600, 900); double fitView = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var (mover, _) = LayerEffectRenderer.Flatten(layer); double flatten = watch.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(directory, "layer-effects-timings.txt"),
                $"4K cut-out (3840×2160) with shadow 30px, glow 24px, stroke 12px\nfirst render (effect grid and planes): {cold:0} ms\nrender after a move (cached): {moved:0} ms\nrender after a color change (cached planes): {recolored:0} ms\nrender after a stroke size change (new plane): {resized:0} ms\nsame render without effects: {baseline:0} ms\nfit view 1600×900 (design renderer, new grid): {fitView:0} ms\nmove preview image of the layer: {flatten:0} ms ({mover.Width}×{mover.Height})\n");
            Check(Px(first, W / 2, H / 2).A == 255 && view.Width == 1600, "The 4K render failed");
            Check(moved < cold && moved < 4000 && recolored < 4000, $"Cached effect renders are not fast enough: moved {moved:0} ms, recolored {recolored:0} ms (first {cold:0} ms)");
        });
    }

    static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
