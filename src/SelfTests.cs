using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class SelfTests
{
    public static int Run(string output)
    {
        var results = new List<string>(); int failed = 0;
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(directory);
        void Test(string name, Action test)
        {
            try { test(); results.Add("PASS " + name); } catch (Exception e) { failed++; results.Add("FAIL " + name + ": " + e); }
        }
        void Assert(bool truth, string detail = "Assertion failed") { if (!truth) throw new Exception(detail); }
        void Near(double a, double b, double tolerance = .01) => Assert(Math.Abs(a - b) < tolerance, $"Expected {b}, got {a}");
        Document Single(Color color, int size = 8) { var d = new Document { Width = size, Height = size }; d.Add(new Layer { Name = "base", Pixels = Raster.Solid(size, size, color) }); return d; }
        Test("straight-alpha source-over", () => { var d = Single(Colors.Blue); d.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Red), Opacity = .5 }); var r = Imaging.Render(d); Near(r.Data[0], 128, 1); Near(r.Data[2], 128, 1); Assert(r.Data[3] == 255); });
        Test("transparent backdrop retains source color", () => { var d = Single(Colors.Transparent); d.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Red), Opacity = .5, Blend = BlendMode.Multiply }); var r = Imaging.Render(d); Assert(r.Data[2] == 255 && r.Data[3] == 128); });
        Test("multiply", () => { Near(Imaging.Blend(.4, .5, BlendMode.Multiply), .2); });
        Test("screen", () => { Near(Imaging.Blend(.4, .5, BlendMode.Screen), .7); });
        Test("dodge and burn endpoints", () => { Near(Imaging.Blend(0, 1, BlendMode.ColorDodge), 0); Near(Imaging.Blend(1, 0, BlendMode.ColorBurn), 1); });
        Test("all blend modes bounded", () => { foreach (var mode in Enum.GetValues<BlendMode>()) for (int i = 0; i <= 10; i++) for (int j = 0; j <= 10; j++) { double v = Imaging.Blend(i / 10.0, j / 10.0, mode); Assert(v >= 0 && v <= 1 && double.IsFinite(v)); } });
        Test("layer order and visibility", () => { var d = Single(Colors.Red); d.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue), Visible = false }); Assert(Imaging.Render(d).Data[2] == 255); d.Active!.Visible = true; Assert(Imaging.Render(d).Data[0] == 255); });
        Test("layer mask", () => { var d = Single(Colors.Red); d.Active!.Mask = new byte[64]; Assert(Imaging.Render(d).Data[3] == 0); d.Active.Mask[0] = 128; Assert(Imaging.Render(d).Data[3] == 128); });
        Test("translation", () => { var d = Single(Colors.Red); d.Active!.X = 2; var r = Imaging.Render(d); Assert(r.Data[3] == 0 && r.Data[2 * 4 + 3] == 255); });
        Test("transform round trip", () => { var l = Single(Colors.Red).Active!; l.X = 15; l.Y = 20; l.Rotation = 37; l.Scale = 1.8; l.FlipX = true; var p = new Point(3, 5); var result = l.Local(l.Matrix.Transform(p)); Near(result.X, p.X); Near(result.Y, p.Y); });
        Test("90 degree rotation and flip", () => { var d = Single(Colors.Transparent, 2); d.Active!.Pixels.Data[2] = 255; d.Active.Pixels.Data[3] = 255; d.Active.Rotation = 90; var r = Imaging.Render(d); Assert(r.Data[7] == 255 && r.Data[3] == 0); d.Active.Rotation = 0; d.Active.FlipX = true; Assert(Imaging.Render(d).Data[7] == 255); });
        Test("levels identity across all 256 values", () => { for (int v = 0; v < 256; v++) Near(Imaging.Level(v / 255.0, 0, 255, 1), v / 255.0, .00001); });
        Test("levels reference gamma and clipping", () => { Near(Imaging.Level(.5, 0, 255, 2), Math.Sqrt(.5), .00001); Near(Imaging.Level(0, 20, 220, 1), 0); Near(Imaging.Level(1, 20, 220, 1), 1); Assert(double.IsFinite(Imaging.Level(.5, double.NaN, double.NaN, double.NaN))); });
        Test("soft brush reference falloff", () => { Near(BrushStroke.Falloff(0), 1); Near(BrushStroke.Falloff(1), 0); Assert(BrushStroke.Falloff(.5) > BrushStroke.Falloff(.75)); });
        Test("brush overlap capped per stroke", () => { var d = Single(Colors.Transparent, 32); var l = d.Active!; var brush = new BrushStroke(l, null, Colors.Red, 12, 1, .5, false, false); brush.Point(new Point(16, 16)); brush.Point(new Point(17, 16)); brush.Point(new Point(16, 16)); Assert(l.Pixels.Data[(16 * 32 + 16) * 4 + 3] == 128); });
        Test("eraser preserves history pixels", () => { var d = Single(Colors.Red, 32); var original = d.Active!.Pixels; var brush = new BrushStroke(d.Active, null, Colors.Black, 12, 1, 1, true, false); brush.Point(new Point(16, 16)); Assert(d.Active.Pixels.Data[(16 * 32 + 16) * 4 + 3] == 0); Assert(original.Data[(16 * 32 + 16) * 4 + 3] == 255); });
        Test("brush respects selection", () => { var d = Single(Colors.Transparent, 16); var b = new BrushStroke(d.Active!, new Selection(new Rect(0, 0, 8, 16)), Colors.Red, 24, 1, 1, false, false); b.Point(new Point(8, 8)); Assert(d.Active!.Pixels.Data[(8 * 16 + 7) * 4 + 3] == 255); Assert(d.Active.Pixels.Data[(8 * 16 + 9) * 4 + 3] == 0); });
        Test("mask brush non-destructive", () => { var d = Single(Colors.Red, 16); d.Active!.Mask = Enumerable.Repeat((byte)255, 256).ToArray(); var originalMask = d.Active.Mask; var b = new BrushStroke(d.Active, null, Colors.Black, 10, 1, 1, false, true); b.Point(new Point(8, 8)); Assert(d.Active.Mask[8 * 16 + 8] == 0 && originalMask[8 * 16 + 8] == 255); Assert(d.Active.Pixels.Data[3] == 255); });
        Test("ellipse selection boundaries", () => { var s = new Selection(new Rect(0, 0, 10, 10), true); Assert(s.Contains(5, 5) && !s.Contains(0, 0)); });
        Test("adjustment alpha and selection", () => { var d = Single(Color.FromArgb(128, 255, 0, 0)); var r = Imaging.Adjust(d.Active!, new Selection(new Rect(0, 0, 4, 8)), "invert"); Assert(r.Data[0] == 255 && r.Data[2] == 0 && r.Data[3] == 128); Assert(r.Data[7 * 4 + 2] == 255); });
        Test("blur keeps flat color and transparency", () => { var d = Single(Color.FromArgb(128, 255, 20, 5)); var r = Imaging.Blur(d.Active!, 3, null); Near(r.Data[0], 5, 1); Near(r.Data[2], 255, 1); Near(r.Data[3], 128, 1); });
        Test("history undo redo and saved revision", () => { var d = Single(Colors.Red); var h = new History(); h.Reset(d); var before = d.Snapshot(); d.Active!.Pixels = Imaging.Adjust(d.Active, null, "invert"); h.Commit("invert", before, d); Assert(h.Dirty(d)); d = h.Undo(d); Assert(!h.Dirty(d) && d.Active!.Pixels.Data[2] == 255); d = h.Redo(d); Assert(h.Dirty(d) && d.Active!.Pixels.Data[2] == 0); h.MarkSaved(d); Assert(!h.Dirty(d)); });
        Test("new edit invalidates redo", () => { var d = Single(Colors.Red); var h = new History(); h.Reset(d); var b = d.Snapshot(); d.Active!.X = 2; h.Commit("move", b, d); d = h.Undo(d); b = d.Snapshot(); d.Active!.Y = 2; h.Commit("move2", b, d); Assert(!h.CanRedo); });
        Test("project complete round trip", () => { var d = Single(Colors.Red); d.Add(new Layer { Name = "테스트", Pixels = Raster.Solid(3, 2, Color.FromArgb(155, 10, 20, 30)), Mask = [0, 50, 100, 150, 200, 255], Scale = 1.5, Rotation = 22, X = 1, Y = 2, FlipX = true, Opacity = .6, Blend = BlendMode.Screen, Locked = true }); string file = Path.Combine(directory, "roundtrip.cwproj"); ProjectStore.Save(d, file); var read = ProjectStore.Load(file); Assert(Imaging.Render(d).Data.SequenceEqual(Imaging.Render(read).Data)); Assert(read.Active!.Locked && read.Active.Name == "테스트" && read.Active.Mask!.SequenceEqual(d.Active!.Mask!)); });
        Test("PNG exact pixel round trip", () => { var d = Single(Color.FromArgb(128, 200, 50, 10)); string file = Path.Combine(directory, "alpha.png"); ProjectStore.Export(d, file); var read = Raster.Load(file); Assert(read.Data.SequenceEqual(Imaging.Render(d).Data)); });
        Test("JPEG flattens transparency to white", () => { var d = Single(Colors.Transparent); string file = Path.Combine(directory, "white.jpg"); ProjectStore.Export(d, file); var r = Raster.Load(file); Assert(r.Data[0] >= 253 && r.Data[2] >= 253 && r.Data[3] == 255); });
        Test("atomic write preserves file on failure", () => { string file = Path.Combine(directory, "atomic.txt"); File.WriteAllText(file, "original"); try { ProjectStore.AtomicWrite(file, s => { s.WriteByte(1); throw new IOException("injected"); }); } catch (IOException) { } Assert(File.ReadAllText(file) == "original"); });
        Test("invalid project version rejected", () => { string file = Path.Combine(directory, "invalid.cwproj"); using (var fileStream = File.Create(file)) using (var z = new ZipArchive(fileStream, ZipArchiveMode.Create)) using (var w = new StreamWriter(z.CreateEntry("document.json").Open())) w.Write("{\"Version\":999}"); bool rejected = false; try { ProjectStore.Load(file); } catch (InvalidDataException) { rejected = true; } Assert(rejected); });
        Test("oversize allocation rejected", () => { bool rejected = false; try { _ = new Raster(8192, 8192); } catch (InvalidDataException) { rejected = true; } Assert(rejected); });
        Test("demo render and project export", () => { var d = Demo.Create(); ProjectStore.Save(d, Path.Combine(directory, "sample.moruproj")); ProjectStore.Export(d, Path.Combine(directory, "sample.png")); var read = ProjectStore.Load(Path.Combine(directory, "sample.moruproj")); Assert(read.Layers.Count == d.Layers.Count && read.Layers.Any(l => l.Kind == LayerKind.Text) && Imaging.Render(read).Data.SequenceEqual(Imaging.Render(d).Data)); });
        PersistenceTests.Run(Test);
        LayerPickingTests.Run(Test);
        EngineFeatureTests.Run(Test);
        AdvancedToolTests.Run(Test);
        ImportExportTests.Run(Test);
        MainWindow.RunCommandTests(Test, directory);
        CmykExportTests.Run(Test);
        EditingDialogTests.Run(Test);
        results.Add($"\n{results.Count - failed}/{results.Count} passed; {failed} failed. {DateTimeOffset.Now:O}");
        File.WriteAllLines(output, results); return failed == 0 ? 0 : 1;
    }
}
