using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class EngineFeatureTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool ok, string detail = "Assertion failed") { if (!ok) throw new Exception(detail); }
        static void Near(double actual, double expected, double tolerance = 1.01) => Assert(Math.Abs(actual - expected) < tolerance, $"Expected {expected}, got {actual}");
        static Document Single(Color color, int w = 4, int h = 4)
        {
            var doc = new Document { Width = w, Height = h }; doc.Add(new Layer { Pixels = Raster.Solid(w, h, color) }); return doc;
        }
        static void Invalid(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid data was accepted"); }
        static string TempFile() => Path.Combine(Path.GetTempPath(), "lumino-engine-" + Guid.NewGuid().ToString("N") + ".cwproj");

        test("fractional translation splits alpha without a dark fringe", () =>
        {
            var d = Single(Colors.Red, 2, 1); d.Active!.Pixels = Raster.Solid(1, 1, Colors.Red); d.Active.X = .5;
            var p = Imaging.Render(d); Near(p.Data[3], 128); Near(p.Data[7], 128); Assert(p.Data[2] == 255 && p.Data[6] == 255);
        });
        test("nonuniform scale inverse round trip", () =>
        {
            var l = Single(Colors.Red).Active!; l.ScaleX = 2; l.ScaleY = .6; l.Scale = 1.3; l.Rotation = 43; l.FlipY = true;
            var point = new Point(1.5, 2.5); var round = l.Local(l.Document(point)); Near(round.X, point.X, 1e-8); Near(round.Y, point.Y, 1e-8);
        });
        test("strong minification averages fine checker detail", () =>
        {
            var d = Single(Colors.Black, 2, 2); var l = d.Active!; l.Pixels = Raster.Solid(8, 8, Colors.Black); l.Scale = .25;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if ((x + y) % 2 == 0) for (int c = 0; c < 3; c++) l.Pixels.Data[(y * 8 + x) * 4 + c] = 255;
            var r = Imaging.Render(d); for (int i = 0; i < r.Data.Length; i += 4) { Near(r.Data[i], 128); Assert(r.Data[i + 3] == 255); }
        });
        test("perspective corners and inverse", () =>
        {
            var layer = Single(Colors.Red, 8, 8).Active!;
            layer.Warp = new(new(1, 0), new(7, 1), new(8, 8), new(0, 7)); layer.Warp.Validate();
            var corner = layer.Document(new(0, 0)); Near(corner.X, 1, 1e-8); Near(corner.Y, 0, 1e-8);
            foreach (var p in new[] { new Point(0, 0), new Point(8, 8), new Point(3.2, 5.1) })
            { var q = layer.Local(layer.Document(p)); Near(q.X, p.X, 1e-8); Near(q.Y, p.Y, 1e-8); }
        });
        test("crossing and degenerate perspective rejected", () =>
        {
            Invalid(() => new WarpQuad(new(0, 0), new(8, 8), new(8, 0), new(0, 8)).Validate());
            Invalid(() => new WarpQuad(new(0, 0), new(0, 0), new(8, 8), new(0, 8)).Validate());
        });
        test("group visibility opacity and mask affect children", () =>
        {
            var d = Single(Colors.Red); var child = d.Active!; var group = DocumentFeatures.Group(d, [child.Id]); group.Opacity = .5;
            Near(Imaging.Render(d).Data[3], 128); group.Mask = new byte[16]; Assert(Imaging.Render(d).Data[3] == 0);
            group.Mask = null; group.Visible = false; Assert(Imaging.Render(d).Data[3] == 0);
        });
        test("nested group transforms and coordinate adapters", () =>
        {
            var d = Single(Colors.Red, 8, 8); var child = d.Active!; child.Pixels = Raster.Solid(1, 1, Colors.Red);
            var inner = DocumentFeatures.Group(d, [child.Id]); inner.X = 1;
            var outer = DocumentFeatures.Group(d, [inner.Id]); outer.X = 2;
            var r = Imaging.Render(d); Assert(r.Data[3] == 0 && r.Data[3 * 4 + 3] == 255);
            var point = DocumentFeatures.ToDocumentSpace(d, child, new(.5, .5)); Near(point.X, 3.5, 1e-8);
            var parent = DocumentFeatures.ToParentSpace(d, child, point); Near(parent.X, .5, 1e-8);
        });
        test("clipping preserves fractional base alpha", () =>
        {
            var d = Single(Color.FromArgb(128, 255, 0, 0), 2, 1); d.Active!.Pixels.Data[7] = 0;
            d.Add(new Layer { Pixels = Raster.Solid(2, 1, Colors.Blue), Clipped = true });
            var r = Imaging.Render(d); Assert(r.Data[0] == 255 && r.Data[3] == 128 && r.Data[7] == 0);
        });
        test("hidden clipping base hides entire clipping stack", () =>
        {
            var d = Single(Colors.Red); d.Active!.Visible = false; d.Add(new Layer { Pixels = Raster.Solid(4, 4, Colors.Blue), Clipped = true });
            Assert(Imaging.Render(d).Data[3] == 0);
        });
        test("adjustment layer preserves source pixels and alpha", () =>
        {
            var d = Single(Color.FromArgb(128, 64, 64, 64)); var source = d.Active!.Pixels;
            d.Add(DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.Levels, Gamma = 2 }));
            var r = Imaging.Render(d); Near(r.Data[2], 128); Assert(r.Data[3] == 128 && source.Data[2] == 64);
        });
        test("group adjustment affects only that group's lower stack", () =>
        {
            var d = Single(Colors.Red, 2, 1); var blue = new Layer { Pixels = Raster.Solid(1, 1, Colors.Blue) }; d.Add(blue);
            var group = DocumentFeatures.Group(d, [blue.Id]);
            var adjust = DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.GradientMap, DarkColor = 0xFFFFFFFF, LightColor = 0xFFFFFFFF }); adjust.ParentId = group.Id; d.Add(adjust);
            var r = Imaging.Render(d); Assert(r.Data[0] == 255 && r.Data[2] == 255 && r.Data[4] == 0 && r.Data[6] == 255);
        });
        test("adjustment layer mask and opacity interpolate effect", () =>
        {
            var d = Single(Colors.Black, 2, 1); var a = DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.GradientMap, DarkColor = 0xFFFFFFFF, LightColor = 0xFFFFFFFF });
            a.Mask = [255, 0]; a.Opacity = .5; d.Add(a); var r = Imaging.Render(d); Near(r.Data[0], 128); Assert(r.Data[4] == 0 && r.Data[3] == 255);
        });
        test("linear-light exposure matches sRGB reference", () =>
        {
            var r = DocumentFeatures.ApplyAdjustment(Raster.Solid(1, 1, Color.FromRgb(128, 128, 128)), new() { Kind = AdjustmentKind.Exposure, Exposure = 1 });
            Near(r.Data[0], 175, 1.5); Assert(r.Data[3] == 255);
        });
        test("curves identity and monotonicity", () =>
        {
            var src = new Raster(256, 1); for (int x = 0; x < 256; x++) { src.Data[x * 4] = src.Data[x * 4 + 1] = src.Data[x * 4 + 2] = (byte)x; src.Data[x * 4 + 3] = 255; }
            Assert(src.Data.SequenceEqual(DocumentFeatures.ApplyAdjustment(src, new() { Kind = AdjustmentKind.Curves }).Data));
            var r = DocumentFeatures.ApplyAdjustment(src, new() { Kind = AdjustmentKind.Curves, Curve = [new(0, 0), new(.3, .15), new(.7, .9), new(1, 1)] });
            for (int x = 1; x < 256; x++) Assert(r.Data[x * 4] >= r.Data[(x - 1) * 4]);
        });
        test("hue saturation rotates red to green", () =>
        {
            var r = DocumentFeatures.ApplyAdjustment(Raster.Solid(1, 1, Colors.Red), new() { Kind = AdjustmentKind.HueSaturation, Hue = 120 }); Assert(r.Data[1] == 255 && r.Data[2] == 0 && r.Data[3] == 255);
        });
        test("grain deterministic per seed and alpha preserving", () =>
        {
            var src = Raster.Solid(32, 32, Color.FromArgb(128, 128, 128, 128)); var spec = new AdjustmentSpec { Kind = AdjustmentKind.Grain, Amount = .7, Seed = 22 };
            var a = DocumentFeatures.ApplyAdjustment(src, spec); var b = DocumentFeatures.ApplyAdjustment(src, spec);
            Assert(a.Data.SequenceEqual(b.Data) && !a.Data.SequenceEqual(DocumentFeatures.ApplyAdjustment(src, spec with { Seed = 23 }).Data));
            for (int i = 3; i < a.Data.Length; i += 4) Assert(a.Data[i] == 128);
        });
        test("nonseparable blend preserves requested luminance", () =>
        {
            var c = Imaging.BlendRgb(.8, .2, .1, .1, .7, .3, BlendMode.Color);
            Near(.3 * c.R + .59 * c.G + .11 * c.B, .3 * .8 + .59 * .2 + .11 * .1, .00001);
            foreach (var mode in new[] { BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity })
            {
                var v = Imaging.BlendRgb(.8, .1, .4, .3, .9, .2, mode); Assert(v.R >= 0 && v.R <= 1 && v.G >= 0 && v.G <= 1 && v.B >= 0 && v.B <= 1);
            }
        });
        test("editable text rerenders while preserving metadata", () =>
        {
            var l = DocumentFeatures.CreateText(new() { Content = "Hello", FontSize = 24, ColorArgb = 0xFFFF0000 }); var old = l.Pixels;
            DocumentFeatures.UpdateText(l, l.Text! with { Content = "Hello world", Bold = true });
            Assert(l.Kind == LayerKind.Text && l.Text!.Bold && l.Pixels.Width > old.Width && !ReferenceEquals(old, l.Pixels));
            DocumentFeatures.Rasterize(l); Assert(l.Kind == LayerKind.Raster && l.Text == null);
        });
        test("new layer metadata survives history and snapshot isolation", () =>
        {
            var d = Single(Colors.Red); d.Add(DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.Curves }));
            var h = new History(); h.Reset(d); var before = d.Snapshot(); d.Active!.Adjustment!.Curve[0] = new(0, .2); h.Commit("curve", before, d);
            Assert(h.CanUndo && before.Active!.Adjustment!.Curve[0].Y == 0); var undone = h.Undo(d); Assert(undone.Active!.Adjustment!.Curve[0].Y == 0);
            var replayed = h.Redo(undone); Near(replayed.Active!.Adjustment!.Curve[0].Y, .2, 1e-9);
        });
        test("hierarchy validation rejects cycles orphan parents and non-group parents", () =>
        {
            var d = Single(Colors.Red); var child = d.Active!; child.ParentId = Guid.NewGuid(); Invalid(d.Validate); child.ParentId = child.Id; Invalid(d.Validate);
            child.ParentId = null; var group = DocumentFeatures.Group(d, [child.Id]); group.ParentId = group.Id; Invalid(d.Validate);
        });
        test("group remove recursively removes descendants", () =>
        {
            var d = Single(Colors.Red); var group = DocumentFeatures.Group(d, [d.ActiveId]); var outside = new Layer { Pixels = new Raster(1, 1) }; d.Add(outside);
            DocumentFeatures.Remove(d, group.Id); d.Validate(); Assert(d.Layers.Count == 1 && d.ActiveId == outside.Id);
        });
        test("group ungroup preserves stacking and render", () =>
        {
            var d = Single(Colors.Red); var bottom = d.Active!; d.Add(new Layer { Pixels = Raster.Solid(4, 4, Color.FromArgb(128, 0, 0, 255)) }); var top = d.Active!;
            var before = Imaging.Render(d); var group = DocumentFeatures.Group(d, [bottom.Id, top.Id]); DocumentFeatures.Ungroup(d, group.Id);
            d.Validate(); Assert(Imaging.Render(d).Data.SequenceEqual(before.Data) && d.Layers[0].Id == bottom.Id && d.Layers[1].Id == top.Id);
        });
        test("version two package roundtrip preserves all new metadata and pixels", () =>
        {
            var d = Single(Colors.Red, 32, 32); var raster = d.Active!; raster.ScaleX = 1.2; raster.ScaleY = .8;
            raster.Warp = new(new(1, 1), new(30, 0), new(32, 32), new(0, 30));
            var group = DocumentFeatures.Group(d, [raster.Id]); group.Opacity = .7;
            var text = DocumentFeatures.CreateText(new() { Content = "ABC", FontSize = 12, Bold = true }); text.ParentId = group.Id; text.Clipped = true; d.Add(text);
            var adjustment = DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.Curves, Curve = [new(0, .1), new(.4, .6), new(1, 1)] }); d.Add(adjustment);
            string path = TempFile();
            try { ProjectStore.Save(d, path); var read = ProjectStore.Load(path); Assert(read.Layers.Count == d.Layers.Count && Imaging.Render(read).Data.SequenceEqual(Imaging.Render(d).Data)); Assert(read.Layers[0].Warp == raster.Warp && read.Layers[2].Text == text.Text && read.Layers[2].Clipped); }
            finally { File.Delete(path); }
        });
        test("version one package remains readable", () =>
        {
            var d = Single(Colors.Red); string path = TempFile();
            try
            {
                ProjectStore.Save(d, path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                {
                    JsonObject json; using (var stream = zip.GetEntry("document.json")!.Open()) json = JsonNode.Parse(stream)!.AsObject();
                    json["Version"] = 1; foreach (var node in json["Layers"]!.AsArray()) foreach (var field in new[] { "Kind", "ParentId", "Clipped", "ScaleX", "ScaleY", "Text", "Adjustment", "Warp" }) node!.AsObject().Remove(field);
                    zip.GetEntry("document.json")!.Delete(); using var output = zip.CreateEntry("document.json").Open(); JsonSerializer.Serialize(output, json);
                }
                var read = ProjectStore.Load(path); Assert(read.Active!.ScaleX == 1 && read.Active.Kind == LayerKind.Raster && Imaging.Render(read).Data.SequenceEqual(Imaging.Render(d).Data));
            }
            finally { File.Delete(path); }
        });
        test("invalid adjustment and text metadata rejected before save", () =>
        {
            var d = Single(Colors.Red); var l = d.Active!; l.Kind = LayerKind.Text; Invalid(d.Validate); l.Text = new() { FontSize = double.NaN }; Invalid(d.Validate);
            l.Text = null; l.Kind = LayerKind.Adjustment; l.Adjustment = new() { Curve = [new(1, 0), new(0, 1)] }; Invalid(d.Validate);
        });
        test("feathered selection controls pixel adjustment and brush strength", () =>
        {
            var d = Single(Colors.Black, 2, 1); var selection = new Selection(new Rect(0, 0, 2, 1)) { CanvasWidth = 2, CanvasHeight = 1, Coverage = [128, 0] };
            var adjusted = Imaging.Adjust(d.Active!, selection, "invert"); Near(adjusted.Data[0], 128); Assert(adjusted.Data[4] == 0);
            var brush = new BrushStroke(d.Active!, selection, Colors.White, 10, 1, 1, false, false); brush.Point(new(.5, .5)); Near(d.Active!.Pixels.Data[0], 128); Assert(d.Active.Pixels.Data[4] == 0);
        });
        test("render cancellation stops before publication", () =>
        {
            var d = Single(Colors.Red); using var cts = new CancellationTokenSource(); cts.Cancel();
            try { Imaging.Render(d, cts.Token); } catch (OperationCanceledException) { return; } throw new Exception("Canceled render completed");
        });
        test("picker respects transformed ancestors group masks and visibility", () =>
        {
            var d = Single(Colors.Red, 16, 16); var child = d.Active!; child.Pixels = Raster.Solid(2, 2, Colors.Red);
            var group = DocumentFeatures.Group(d, [child.Id]); group.X = 6; group.Y = 3; group.ScaleX = 1.5;
            var p = DocumentFeatures.ToDocumentSpace(d, child, new(1, 1)); Assert(LayerPicking.Pick(d, p)?.Id == child.Id);
            group.Visible = false; Assert(LayerPicking.Pick(d, p) == null); group.Visible = true;
            group.Mask = new byte[256]; Assert(LayerPicking.Pick(d, p) == null); group.Mask = null;
            group.Locked = true; Assert(LayerPicking.Pick(d, p)?.Id == group.Id);
        });
        test("picker skips adjustment and clipped content outside base", () =>
        {
            var d = Single(Colors.Red, 4, 1); var bottom = d.Active!; bottom.Pixels = Raster.Solid(1, 1, Colors.Red);
            var clipped = new Layer { Pixels = Raster.Solid(4, 1, Colors.Blue), Clipped = true }; d.Add(clipped);
            d.Add(DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.Exposure }));
            Assert(LayerPicking.Pick(d, new(.5, .5))?.Id == clipped.Id); Assert(LayerPicking.Pick(d, new(3.5, .5)) == null);
        });
        test("picker honors perspective geometry", () =>
        {
            var d = Single(Colors.Red, 16, 16); var l = d.Active!; l.Pixels = Raster.Solid(4, 4, Colors.Red); l.Warp = new(new(5, 5), new(12, 4), new(10, 12), new(4, 10));
            var p = l.Document(new(2, 2)); Assert(LayerPicking.Pick(d, p)?.Id == l.Id && LayerPicking.Pick(d, new(.5, .5)) == null);
        });
        test("cropping a group retains children outside the new document extents", () =>
        {
            var d = Single(Colors.Transparent, 16, 16); var child = d.Active!; child.Pixels = Raster.Solid(2, 2, Colors.Red); child.X = 12; child.Y = 12;
            var group = DocumentFeatures.Group(d, [child.Id]); group.X = -12; group.Y = -12; d.Width = 4; d.Height = 4;
            var r = Imaging.Render(d); Assert(r.Data[2] == 255 && r.Data[3] == 255 && child.X == 12);
        });
        test("shrinking group canvas scales children exactly once", () =>
        {
            var d = Single(Colors.Transparent, 16, 16); var child = d.Active!; child.Pixels = Raster.Solid(4, 4, Colors.Red); child.X = 12; child.Y = 12;
            var group = DocumentFeatures.Group(d, [child.Id]); group.Scale = .5; d.Width = 8; d.Height = 8;
            var r = Imaging.Render(d); Assert(r.Data[(6 * 8 + 6) * 4 + 3] == 255 && r.Data[(2 * 8 + 2) * 4 + 3] == 0);
        });
        test("grouping incomplete clipping stack is rejected without mutation", () =>
        {
            var d = Single(Colors.Red); d.Add(new Layer { Pixels = Raster.Solid(4, 4, Colors.Blue), Clipped = true }); int count = d.Layers.Count;
            try { DocumentFeatures.Group(d, [d.ActiveId]); } catch (InvalidOperationException) { Assert(d.Layers.Count == count && d.Active!.ParentId == null); return; } throw new Exception("Clipped child lost its external base");
        });
        test("levels apply individual channel before master RGB", () =>
        {
            var src = Raster.Solid(1, 1, Color.FromArgb(177, 64, 64, 64));
            var spec = new AdjustmentSpec { Kind = AdjustmentKind.Levels, Gamma = 2, RedLevels = new() { White = 128 }, BlueLevels = new() { OutputWhite = 128 } };
            var r = DocumentFeatures.ApplyAdjustment(src, spec);
            Near(r.Data[2], Imaging.Level(Imaging.Level(64 / 255.0, 0, 128, 1), 0, 255, 2) * 255, .51);
            Near(r.Data[1], Math.Sqrt(64 / 255.0) * 255, .51); Near(r.Data[0], Imaging.Level(64 / 255.0 * 128 / 255, 0, 255, 2) * 255, .51); Assert(r.Data[3] == 177);
        });
        test("curves apply channel then continuous master without intermediate quantization", () =>
        {
            var src = Raster.Solid(1, 1, Color.FromRgb(64, 64, 64));
            var spec = new AdjustmentSpec { Kind = AdjustmentKind.Curves, RedCurve = [new(0, .25), new(1, .75)], Curve = [new(0, 1), new(1, 0)] };
            var r = DocumentFeatures.ApplyAdjustment(src, spec); Near(r.Data[2], (1 - (.25 + .5 * 64 / 255.0)) * 255, .51); Assert(r.Data[1] == 191 && r.Data[0] == 191);
        });
        test("channel metadata persists and deep snapshots preserve redo semantics", () =>
        {
            var d = Single(Colors.Gray); d.Add(DocumentFeatures.CreateAdjustment(d, new() { Kind = AdjustmentKind.Curves, RedLevels = new() { Gamma = 2 }, GreenCurve = [new(0, .1), new(1, .8)] }));
            var history = new History(); history.Reset(d); var before = d.Snapshot(); d.Active!.Adjustment!.GreenCurve[0] = new(0, .2); history.Commit("green", before, d);
            Assert(before.Active!.Adjustment!.GreenCurve[0].Y == .1); var undone = history.Undo(d); var noOp = undone.Snapshot(); history.Commit("no-op", noOp, undone); Assert(history.CanRedo);
            string path = TempFile();
            try { ProjectStore.Save(d, path); var read = ProjectStore.Load(path); Assert(DocumentFeatures.SameAdjustment(d.Active!.Adjustment, read.Active!.Adjustment)); Assert(Imaging.Render(d).Data.SequenceEqual(Imaging.Render(read).Data)); }
            finally { File.Delete(path); }
            d.Active!.Adjustment = d.Active.Adjustment! with { BlueCurve = [new(0, 0), new(0, 1)] }; Invalid(d.Validate);
        });
        test("resizing editable text preserves its mask and warped footprint through undo and save", () =>
        {
            var d = new Document { Width = 256, Height = 128 };
            var layer = DocumentFeatures.CreateText(new() { Content = "A", FontSize = 24 }); layer.Mask = Enumerable.Repeat((byte)128, layer.Pixels.Width * layer.Pixels.Height).ToArray();
            layer.Warp = new(new(2, 1), new(70, 5), new(80, 60), new(0, 50)); layer.Rotation = 24; layer.ScaleX = 1.4; layer.X = 30; d.Add(layer);
            var oldMask = layer.Mask; var oldPixels = layer.Pixels; var oldCorners = new[] { new Point(0, 0), new Point(oldPixels.Width, 0), new Point(oldPixels.Width, oldPixels.Height), new Point(0, oldPixels.Height) }.Select(layer.Document).ToArray();
            var history = new History(); history.Reset(d); var before = d.Snapshot();
            DocumentFeatures.UpdateText(layer, layer.Text! with { Content = "A much longer line", FontSize = 32 }); history.Commit("text", before, d); d.Validate();
            Assert(layer.Mask != null && layer.Mask.Length == layer.Pixels.Width * layer.Pixels.Height && layer.Mask.All(b => b == 128) && !ReferenceEquals(oldMask, layer.Mask) && layer.Warp != null);
            var corners = new[] { new Point(0, 0), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }.Select(layer.Document).ToArray();
            for (int i = 0; i < 4; i++) { Near(corners[i].X, oldCorners[i].X, 1e-8); Near(corners[i].Y, oldCorners[i].Y, 1e-8); }
            string path = TempFile(); try { ProjectStore.Save(d, path); var read = ProjectStore.Load(path); Assert(read.Active!.Warp == layer.Warp && read.Active.Mask!.SequenceEqual(layer.Mask!)); } finally { File.Delete(path); }
            var undone = history.Undo(d); Assert(ReferenceEquals(undone.Active!.Mask, oldMask) && ReferenceEquals(undone.Active.Pixels, oldPixels) && undone.Active.Text!.Content == "A");
        });
        test("grouping a clipping base without its upper clips is rejected", () =>
        {
            var d = Single(Colors.Red); var baseId = d.ActiveId; d.Add(new Layer { Pixels = Raster.Solid(4, 4, Colors.Blue), Clipped = true });
            try { DocumentFeatures.Group(d, [baseId]); } catch (InvalidOperationException) { Assert(d.Layers.Count == 2 && d.Layers.All(l => l.ParentId == null)); return; } throw new Exception("Grouping changed the external clipping base");
        });
        test("nested group keeps parent-local canvas after document crop", () =>
        {
            var d = Single(Colors.Transparent, 16, 16); var child = d.Active!; child.Pixels = Raster.Solid(2, 2, Colors.Red); child.X = 12; child.Y = 12;
            var outer = DocumentFeatures.Group(d, [child.Id]); outer.X = -12; outer.Y = -12; d.Width = d.Height = 4;
            var before = Imaging.Render(d); var inner = DocumentFeatures.Group(d, [child.Id]); d.Validate();
            Assert(inner.Pixels.Width == 16 && inner.Pixels.Height == 16 && Imaging.Render(d).Data.SequenceEqual(before.Data));
        });
    }
}
