using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ShadowTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static Document Board(int width = 400, int height = 400) => new() { Width = width, Height = height, Name = "그림자 검사" };
        static Layer Block(int w, int h, double x, double y, string name = "물체") => new() { Name = name, Pixels = Raster.Solid(w, h, Colors.Black), X = x, Y = y };
        // Alpha of an untransformed shadow layer at a document pixel.
        static int Alpha(Layer shadow, int x, int y)
        {
            int lx = x - (int)shadow.X, ly = y - (int)shadow.Y;
            return lx < 0 || ly < 0 || lx >= shadow.Pixels.Width || ly >= shadow.Pixels.Height ? 0 : shadow.Pixels.Data[(ly * shadow.Pixels.Width + lx) * 4 + 3];
        }
        static (double X, double Y) Centroid(Layer shadow)
        {
            double sum = 0, sx = 0, sy = 0;
            for (int y = 0; y < shadow.Pixels.Height; y++) for (int x = 0; x < shadow.Pixels.Width; x++)
            {
                double a = shadow.Pixels.Data[(y * shadow.Pixels.Width + x) * 4 + 3];
                sum += a; sx += a * (shadow.X + x + .5); sy += a * (shadow.Y + y + .5);
            }
            return (sx / sum, sy / sum);
        }
        // Pixels in the soft band (10%–90% of the peak) along a column or a row.
        static int Column(Layer shadow, int x, int from, int to, int peak) => Enumerable.Range(from, to - from).Count(y => Alpha(shadow, x, y) > peak * .1 && Alpha(shadow, x, y) < peak * .9);
        static int Row(Layer shadow, int y, int from, int to, int peak) => Enumerable.Range(from, to - from).Count(x => Alpha(shadow, x, y) > peak * .1 && Alpha(shadow, x, y) < peak * .9);
        // Steepest step between neighbors along a row: a crisp edge jumps, a soft one ramps.
        static int Steepest(Layer shadow, int y, int from, int to) => Enumerable.Range(from, to - from).Max(x => Math.Abs(Alpha(shadow, x + 1, y) - Alpha(shadow, x, y)));
        static Layer Make(Document doc, Layer source, ShadowSpec spec) => ShadowRenderer.Create(doc, spec with { Sources = [source.Id] });

        test("shadow direction and distance offset a drop shadow from its object", () =>
        {
            var doc = Board(); var block = Block(40, 40, 100, 100); doc.Add(block);
            var spec = new ShadowSpec { Projection = ShadowProjection.Drop, Softness = 0, Opacity = 1, Distance = 30 };
            var right = Centroid(Make(doc, block, spec with { Angle = 0 }));
            var down = Centroid(Make(doc, block, spec with { Angle = 90 }));
            var upLeft = Centroid(Make(doc, block, spec with { Angle = 225, Distance = 20 }));
            Check(Math.Abs(right.X - 150) < .6 && Math.Abs(right.Y - 120) < .6, $"0° moved the shadow to {right}");
            Check(Math.Abs(down.X - 120) < .6 && Math.Abs(down.Y - 150) < .6, $"90° moved the shadow to {down}");
            Check(Math.Abs(upLeft.X - (120 - 20 / Math.Sqrt(2))) < .8 && Math.Abs(upLeft.Y - (120 - 20 / Math.Sqrt(2))) < .8, $"225° moved the shadow to {upLeft}");
        });

        test("shadow shape keeps a hard edge while a realistic shadow falls off softly", () =>
        {
            var doc = Board(); var block = Block(40, 40, 100, 100); doc.Add(block);
            var spec = new ShadowSpec { Projection = ShadowProjection.Drop, Angle = 0, Distance = 30, Softness = 20, Opacity = 1 };
            var soft = Make(doc, block, spec); var hard = Make(doc, block, spec with { Style = ShadowStyle.Shape, Opacity = 1 });
            int softBand = Row(soft, 120, 150, 200, 255), hardBand = Row(hard, 120, 150, 200, 255);
            Check(softBand >= 8, $"The realistic edge is not soft ({softBand}px)");
            Check(hardBand <= 1, $"The shape edge is not crisp ({hardBand}px)");
            Check(Alpha(hard, 150, 120) == 255 && Alpha(hard, 175, 120) == 0, "The shape shadow is not a solid silhouette");
        });

        test("plan shadows are sharp at contact and soften with distance", () =>
        {
            // A thin post: its shadow edge is cast from the contact point outward.
            var doc = Board(); var block = Block(8, 40, 60, 180); doc.Add(block);
            // 45° sun: the sweep is as long as the height.
            var spec = new ShadowSpec { Projection = ShadowProjection.Plan, Angle = 0, Elevation = 45, Height = 240, Softness = 24, Opacity = 1 };
            var shadow = Make(doc, block, spec);
            int near = Column(shadow, 74, 160, 200, 255), far = Column(shadow, 290, 160, 200, 255);
            Check(near <= 3 && far >= 6 && far > near, $"Softness did not grow with distance (near {near}px, far {far}px)");
            Check(Alpha(shadow, 200, 200) > 240 && Alpha(shadow, 300, 200) > 60 && Alpha(shadow, 390, 200) < 5, "The swept length does not match height ÷ tan(sun height)");
            Check(Alpha(shadow, 200, 150) == 0 && Alpha(shadow, 200, 250) == 0, "The sweep spread sideways");
            var form = Make(doc, block, spec with { Style = ShadowStyle.Shape });
            Check(Column(form, 74, 160, 200, 255) <= 1 && Column(form, 290, 160, 200, 255) <= 1, "The shape sweep lost its hard edge");
            Check(Alpha(form, 307, 200) >= 250 && Alpha(form, 309, 200) <= 5, "The shape sweep ends at the wrong length");
        });

        test("ground shadows lie from the contact line along the sun direction", () =>
        {
            var doc = Board(); var figure = Block(20, 80, 100, 100); doc.Add(figure);
            // 330°: back and to the right; 45° sun: the shadow is as long as the figure is tall.
            var spec = new ShadowSpec { Projection = ShadowProjection.Ground, Angle = 330, Elevation = 45, Style = ShadowStyle.Shape, Opacity = 1 };
            var form = Make(doc, figure, spec);
            double cos = Math.Cos(330 * Math.PI / 180), sin = Math.Sin(330 * Math.PI / 180);
            int topX = (int)(110 + 78 * cos), topY = (int)(180 + 78 * sin);
            Check(Alpha(form, topX, topY) > 200, $"The far end is not at ({topX}, {topY})");
            Check(Alpha(form, 110, 178) > 200, "The shadow does not start at the contact line");
            Check(Enumerable.Range(0, 400).All(x => Alpha(form, x, 183) == 0), "The shadow fell below the contact line");
            var soft = Make(doc, figure, spec with { Style = ShadowStyle.Realistic, Softness = 20 });
            int near = Steepest(soft, 176, 116, 140), far = Steepest(soft, 146, 150, 260);
            Check(near >= 50 && far <= 20, $"The ground shadow is not sharper at contact (steepest step near {near}, far {far})");
        });

        test("shadow color and opacity are baked into the shadow pixels", () =>
        {
            var doc = Board(); var block = Block(40, 40, 100, 100); doc.Add(block);
            var shadow = Make(doc, block, new ShadowSpec { Angle = 0, Distance = 10, Softness = 0, Opacity = .4, ColorArgb = 0xFF204060 });
            int i = ((120 - (int)shadow.Y) * shadow.Pixels.Width + 125 - (int)shadow.X) * 4;
            Check(shadow.Pixels.Data[i] == 0x60 && shadow.Pixels.Data[i + 1] == 0x40 && shadow.Pixels.Data[i + 2] == 0x20, "The shadow color is wrong");
            Check(shadow.Pixels.Data[i + 3] == Imaging.Byte(.4 * 255) && shadow.Pixels.Data.Where((_, n) => n % 4 == 3).Max() == Imaging.Byte(.4 * 255), "The opacity was not applied");
            Check(shadow.Blend == BlendMode.Multiply && shadow.Kind == LayerKind.Raster && shadow.Opacity == 1, "The shadow is not a multiply image layer");
            Check(shadow.Pixels.Width < 80 && shadow.Pixels.Height < 60, "The shadow was not trimmed to its coverage");
        });

        test("shadow layers go directly below their sources in the same group", () =>
        {
            var doc = Board(); doc.Add(Block(400, 400, 0, 0, "배경"));
            var group = DocumentFeatures.CreateGroup(doc, "무리"); doc.Add(group);
            var a = Block(30, 30, 50, 50, "가"); a.ParentId = group.Id; doc.Add(a);
            var b = Block(30, 30, 150, 50, "나"); b.ParentId = group.Id; doc.Add(b);
            var shadow = ShadowRenderer.Insert(doc, new ShadowSpec { Sources = [b.Id] });
            var siblings = doc.Layers.Where(l => l.ParentId == group.Id).ToList();
            Check(siblings.Select(l => l.Id).SequenceEqual([a.Id, shadow.Id, b.Id]), "The shadow is not directly below its source");
            Check(shadow.ParentId == group.Id && shadow.Name == "그림자 · 나" && shadow.Shadow!.Sources.SequenceEqual([b.Id]), "The shadow lost its group or source");
            var pair = ShadowRenderer.Insert(doc, new ShadowSpec { Sources = ShadowRenderer.ResolveSources(doc, [b.Id, a.Id, group.Id]) });
            Check(pair.ParentId == null && doc.Layers.IndexOf(pair) + 1 == doc.Layers.IndexOf(group) && pair.Shadow!.Sources.SequenceEqual([group.Id]), "A selected group did not include its children");
            var both = ShadowRenderer.Insert(doc, new ShadowSpec { Sources = ShadowRenderer.ResolveSources(doc, [b.Id, a.Id]) });
            Check(both.Name == "그림자 · 가 +1" && doc.Layers.Where(l => l.ParentId == group.Id).First() == both, "Several sources did not share one shadow below the lowest");
            // A clipped source keeps its clipping run intact.
            var root = Board(); var clipBase = Block(60, 60, 20, 20, "기준"); root.Add(clipBase);
            var clipped = Block(60, 60, 40, 40, "클립"); clipped.Clipped = true; root.Add(clipped);
            var clipShadow = ShadowRenderer.Insert(root, new ShadowSpec { Sources = [clipped.Id] });
            Check(root.Layers.Select(l => l.Id).SequenceEqual([clipShadow.Id, clipBase.Id, clipped.Id]), "The shadow broke a clipping run");
            bool mixed = false, adjustment = false;
            var other = Block(10, 10, 0, 0); doc.Add(other);
            try { ShadowRenderer.ResolveSources(doc, [a.Id, other.Id]); } catch (InvalidOperationException) { mixed = true; }
            var adjust = DocumentFeatures.CreateAdjustment(doc, new AdjustmentSpec { Kind = AdjustmentKind.Exposure }); doc.Add(adjust);
            try { ShadowRenderer.ResolveSources(doc, [adjust.Id, shadow.Id]); } catch (InvalidOperationException) { adjustment = true; }
            Check(mixed && adjustment, "Sources from different groups, adjustments or shadows were accepted");
        });

        test("shadow settings survive project save and load and regenerate after the source moves", () =>
        {
            var doc = Board(); doc.Add(Block(400, 400, 0, 0, "배경")); var block = Block(40, 40, 100, 100); doc.Add(block);
            var spec = new ShadowSpec { Projection = ShadowProjection.Plan, Style = ShadowStyle.Shape, Angle = 30, Height = 50, Elevation = 50, Opacity = .35, ColorArgb = 0xFF102030, OutlineOnly = true, OutlineWidth = 3, FillClosed = true, Sources = [block.Id] };
            var shadow = ShadowRenderer.Insert(doc, spec);
            string file = Path.Combine(directory, "shadow-roundtrip.moruproj"); ProjectStore.Save(doc, file);
            string json; using (var zip = ZipFile.OpenRead(file)) using (var reader = new StreamReader(zip.GetEntry("document.json")!.Open())) json = reader.ReadToEnd();
            // Only the shadow layer carries the optional entry; the manifest version is unchanged.
            Check(json.Split("\"Shadow\"").Length == 2 && JsonNode.Parse(json)!["Version"]!.GetValue<int>() == 2, "The shadow entry is not an optional, version-neutral addition");
            var read = ProjectStore.Load(file); var loaded = read.Layers.Single(l => l.Id == shadow.Id);
            Check(loaded.Shadow == shadow.Shadow && loaded.Kind == LayerKind.Raster && loaded.Blend == BlendMode.Multiply, "The shadow settings were not restored");
            Check(loaded.Pixels.Data.SequenceEqual(shadow.Pixels.Data) && loaded.X == shadow.X && loaded.Y == shadow.Y, "The shadow pixels changed on load");
            Check(Imaging.Render(read).Data.SequenceEqual(Imaging.Render(doc).Data), "The loaded document renders differently");
            var moved = read.Layers.Single(l => l.Id == block.Id); moved.X += 60;
            double before = loaded.X; ShadowRenderer.Regenerate(read, loaded.Id);
            Check(Math.Abs(loaded.X - before - 60) <= 1 && loaded.Shadow == shadow.Shadow, "Regenerating did not follow the moved source");
            // Older projects have no shadow entry; their layers load without one.
            Check(ProjectStore.Load(file).Layers.Count(l => l.Shadow != null) == 1 && read.Layers.Count(l => l.Shadow == null) == 2, "Plain layers gained shadow settings");
        });

        test("shadow validation rejects out-of-range settings and misplaced shadow data", () =>
        {
            var id = Guid.NewGuid(); var ok = new ShadowSpec { Sources = [id] }; ok.Validate();
            ShadowSpec[] bad =
            [
                ok with { Angle = double.NaN }, ok with { Angle = 720 }, ok with { Distance = -1 }, ok with { Height = 1e9 }, ok with { Elevation = 2 },
                ok with { Elevation = 89 }, ok with { Softness = 501 }, ok with { Opacity = 1.5 }, ok with { OutlineWidth = 0 }, ok with { Style = (ShadowStyle)9 },
                ok with { Projection = (ShadowProjection)7 }, ok with { Sources = [] }, ok with { Sources = null! }, ok with { Sources = [Guid.Empty] }, ok with { Sources = [id, id] },
                ok with { Sources = Enumerable.Range(0, ShadowSpec.MaxSources + 1).Select(_ => Guid.NewGuid()).ToArray() }
            ];
            foreach (var spec in bad)
            {
                bool rejected = false; try { spec.Validate(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "An invalid shadow was accepted: " + spec);
            }
            var text = DocumentFeatures.CreateText(new TextSpec()); text.Shadow = ok;
            var self = new Layer { Pixels = new Raster(1, 1) }; self.Shadow = ok with { Sources = [self.Id] };
            foreach (var layer in new[] { text, self })
            {
                bool rejected = false; try { Document.ValidateLayer(layer); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Misplaced shadow data was accepted");
            }
            // A tampered project is rejected before it is opened.
            var doc = Board(); var block = Block(20, 20, 10, 10); doc.Add(block); ShadowRenderer.Insert(doc, ok with { Sources = [block.Id] });
            string file = Path.Combine(directory, "shadow-invalid.moruproj"); ProjectStore.Save(doc, file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("document.json")!; JsonNode manifest;
                using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
                foreach (var layer in manifest["Layers"]!.AsArray()) if (layer!["Shadow"] is JsonObject shadow) shadow["Opacity"] = 5;
                entry.Delete(); using var output = new StreamWriter(zip.CreateEntry("document.json").Open()); output.Write(manifest.ToJsonString());
            }
            bool refused = false; try { ProjectStore.Load(file); } catch (InvalidDataException) { refused = true; }
            Check(refused, "A project with an invalid shadow was opened");
        });

        test("closed outlines can cast filled shadows and shape shadows can be outlines", () =>
        {
            var doc = Board(); var frame = VectorShapes.Create(new ShapeSpec { Width = 100, Height = 100, FillEnabled = false, StrokeEnabled = true, StrokeArgb = 0xFF000000, StrokeWidth = 3 }, 100, 100); doc.Add(frame);
            var spec = new ShadowSpec { Style = ShadowStyle.Shape, Angle = 0, Distance = 20, Opacity = 1, Sources = [frame.Id] };
            var open = ShadowRenderer.Create(doc, spec); var filled = ShadowRenderer.Create(doc, spec with { FillClosed = true });
            Check(Alpha(open, 170, 150) == 0 && Alpha(filled, 170, 150) == 255, "Closed outlines were not filled");
            var block = Block(80, 80, 100, 250); doc.Add(block);
            var outline = ShadowRenderer.Create(doc, spec with { Sources = [block.Id], OutlineOnly = true, OutlineWidth = 3 });
            Check(Alpha(outline, 160, 290) == 0 && Alpha(outline, 121, 290) == 255 && Alpha(outline, 199, 290) == 255, "The outline is not an edge stroke of the cast form");
        });

        test("large canvas shadows work only around the object and can be canceled", () =>
        {
            var doc = Board(8000, 6000); var block = Block(300, 300, 3800, 2800); doc.Add(block);
            var spec = new ShadowSpec { Projection = ShadowProjection.Plan, Angle = 315, Elevation = 45, Height = 400, Softness = 40, Sources = [block.Id] };
            long canvasBytes = 8000L * 6000 * 4;
            long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
            var shadow = ShadowRenderer.Insert(doc, spec);
            clock.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Check(allocated < canvasBytes / 8, $"A small object's shadow allocated {allocated / 1048576.0:0} MiB");
            Check(shadow.Pixels.Data.LongLength < canvasBytes / 40 && clock.ElapsedMilliseconds < 5000, $"The shadow took {clock.ElapsedMilliseconds} ms / {shadow.Pixels.Data.Length} bytes");
            // A canvas-wide source stays within a few canvas-sized buffers.
            var wide = Board(3000, 2000); var pattern = new Raster(3000, 2000);
            for (int y = 0; y < 2000; y++) for (int x = 0; x < 3000; x++) if ((x / 200 + y / 200) % 2 == 0 && x % 200 < 120) pattern.Data[(y * 3000 + x) * 4 + 3] = 255;
            var source = new Layer { Name = "패턴", Pixels = pattern }; wide.Add(source);
            allocated = GC.GetTotalAllocatedBytes(true); clock.Restart();
            ShadowRenderer.Create(wide, spec with { Sources = [source.Id], Height = 150, Softness = 30 });
            clock.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Check(allocated < 3000L * 2000 * 4 * 5 && clock.ElapsedMilliseconds < 15000, $"A canvas-wide shadow used {allocated / 1048576.0:0} MiB in {clock.ElapsedMilliseconds} ms");
            var silhouette = ShadowRenderer.Silhouette(wide, [source.Id], .25, false);
            Check(silhouette.SpaceWidth == 750 && silhouette.Items.Single().Width <= 750, "The preview silhouette was not reduced");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool stopped = false; try { ShadowRenderer.Cast(silhouette, spec with { Sources = [source.Id] }, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "A canceled shadow kept working");
        });

        static bool SamePixels(Layer a, Layer b) => a.X == b.X && a.Y == b.Y && a.Pixels.Width == b.Pixels.Width && a.Pixels.Height == b.Pixels.Height && a.Pixels.Data.SequenceEqual(b.Pixels.Data);

        test("a group's shadow ignores shadow layers inside the group", () =>
        {
            var groupSpec = new ShadowSpec { Projection = ShadowProjection.Plan, Angle = 45, Elevation = 30, Height = 60, Softness = 8, Opacity = 1 };
            (Document Doc, Layer Group, Layer Building) Scene(bool childShadow)
            {
                var doc = Board(); var group = DocumentFeatures.CreateGroup(doc, "건물"); doc.Add(group);
                var a = Block(30, 30, 60, 60, "가"); a.ParentId = group.Id; doc.Add(a);
                var b = Block(30, 30, 160, 60, "나"); b.ParentId = group.Id; doc.Add(b);
                if (childShadow) ShadowRenderer.Insert(doc, new ShadowSpec { Projection = ShadowProjection.Plan, Angle = 0, Elevation = 20, Height = 60, Softness = 0, Opacity = 1, Sources = [b.Id] });
                return (doc, group, b);
            }
            var plain = Scene(false); var nested = Scene(true);
            Check(nested.Doc.Layers.Any(l => l.Shadow != null && l.ParentId == nested.Group.Id), "The child shadow was not placed inside the group");
            var expected = ShadowRenderer.Create(plain.Doc, groupSpec with { Sources = [plain.Group.Id] });
            var actual = ShadowRenderer.Create(nested.Doc, groupSpec with { Sources = [nested.Group.Id] });
            Check(SamePixels(expected, actual), "A shadow inside the group cast a shadow of its own");
            // A group's shadow dragged into the group does not take in its own pixels on regeneration.
            var own = ShadowRenderer.Insert(nested.Doc, groupSpec with { Sources = [nested.Group.Id] });
            nested.Doc.Layers.Remove(own); own.ParentId = nested.Group.Id; nested.Doc.Layers.Insert(nested.Doc.Layers.IndexOf(nested.Group) + 1, own); nested.Doc.Validate();
            ShadowRenderer.Regenerate(nested.Doc, own.Id); ShadowRenderer.Regenerate(nested.Doc, own.Id);
            Check(SamePixels(expected, own), "Regenerating a group's shadow inside the group grew the shadow");
        });

        test("clipped sources cast the shadow of their clipped, visible shape", () =>
        {
            var doc = Board(); var person = Block(20, 60, 100, 100, "사람"); doc.Add(person);
            // A wide texture clipped to the person: only its top 20 rows are visible, on the person.
            var texture = Block(120, 30, 60, 90, "무늬"); texture.Clipped = true; doc.Add(texture);
            var spec = new ShadowSpec { Projection = ShadowProjection.Drop, Style = ShadowStyle.Shape, Angle = 0, Distance = 30, Opacity = 1 };
            var alone = ShadowRenderer.Create(doc, spec with { Sources = [texture.Id] });
            Check(alone.X == 130 && alone.Y == 100 && alone.Pixels.Width == 20 && alone.Pixels.Height == 20, $"A clipped layer cast its unclipped shape ({alone.X}, {alone.Y}, {alone.Pixels.Width} × {alone.Pixels.Height})");
            var both = ShadowRenderer.Create(doc, spec with { Sources = [person.Id, texture.Id] });
            var personOnly = ShadowRenderer.Create(doc, spec with { Sources = [person.Id] });
            Check(both.X == 130 && both.Y == 100 && both.Pixels.Width == 20 && both.Pixels.Height == 60 && SamePixels(both, personOnly), "A clipping run did not cast its base's shape");
            var ground = spec with { Projection = ShadowProjection.Ground, Angle = 330, Elevation = 45 };
            Check(SamePixels(ShadowRenderer.Create(doc, ground with { Sources = [person.Id, texture.Id] }), ShadowRenderer.Create(doc, ground with { Sources = [person.Id] })),
                "A clipped layer cast its own ground shadow beside its base's");
        });

        test("shadows in a placed drawing folder reach past the folder's page but stay on the canvas", () =>
        {
            var doc = Board(800, 600);
            // A drawing placed into a board: its folder is offset and scaled, its page surface is 400 × 300.
            var folder = new Layer { Name = "도면", Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = new Raster(400, 300), X = 100, Y = 100, Scale = .5 };
            doc.Add(folder);
            var near = Block(20, 20, 370, 100, "가"); near.ParentId = folder.Id; doc.Add(near);
            var moved = Block(20, 20, 600, 100, "나"); moved.ParentId = folder.Id; doc.Add(moved);
            var edge = Block(20, 20, 1370, 100, "다"); edge.ParentId = folder.Id; doc.Add(edge);
            var spec = new ShadowSpec { Projection = ShadowProjection.Drop, Style = ShadowStyle.Shape, Angle = 0, Distance = 60, Opacity = 1 };
            var shadow = ShadowRenderer.Create(doc, spec with { Sources = [near.Id] });
            Check(shadow.ParentId == folder.Id && shadow.X == 430 && shadow.Pixels.Width == 20 && shadow.Pixels.Height == 20, $"The shadow was cut at the folder's page ({shadow.X}, {shadow.Pixels.Width} px)");
            var outside = ShadowRenderer.Create(doc, spec with { Sources = [moved.Id] });
            Check(outside.X == 660 && outside.Pixels.Width == 20, "An object moved off the folder's page cast no shadow");
            // The canvas (800 px wide) ends at folder x = (800 - 100) / .5 = 1400.
            var clipped = ShadowRenderer.Create(doc, spec with { Sources = [edge.Id], Distance = 20 });
            Check(clipped.X == 1390 && clipped.Pixels.Width == 10, $"The shadow was not limited to the canvas ({clipped.X}, {clipped.Pixels.Width} px)");
            // An ordinary transformed group still crops its children to its own surface.
            var group = new Layer { Name = "그룹", Kind = LayerKind.Group, Pixels = new Raster(400, 300), X = 100, Y = 100, Scale = .5 }; doc.Add(group);
            var inside = Block(20, 20, 370, 100, "라"); inside.ParentId = group.Id; doc.Add(inside);
            var cropped = ShadowRenderer.Create(doc, spec with { Sources = [inside.Id] });
            Check(cropped.Pixels.Data.Where((_, i) => i % 4 == 3).All(a => a == 0), "A cropping group's shadow reached past its surface");
        });
    }
}
