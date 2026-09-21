using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class ImportExportTests
{
    public static void Run(Action<string, Action> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Morupixel-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
        static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        try
        {
            test("EXIF all eight orientations pixel fixtures", () =>
            {
                var source = new Raster(2, 3);
                for (int i = 0; i < 6; i++) { source.Data[i * 4] = (byte)(i + 1); source.Data[i * 4 + 3] = 255; }
                byte[][] expected = [[1, 2, 3, 4, 5, 6], [2, 1, 4, 3, 6, 5], [6, 5, 4, 3, 2, 1], [5, 6, 3, 4, 1, 2], [1, 3, 5, 2, 4, 6], [5, 3, 1, 6, 4, 2], [6, 4, 2, 5, 3, 1], [2, 4, 6, 1, 3, 5]];
                for (int orientation = 1; orientation <= 8; orientation++)
                {
                    var actual = ImportExport.Orient(source, orientation);
                    Assert(actual.Width == (orientation >= 5 ? 3 : 2));
                    Assert(Enumerable.Range(0, 6).Select(i => actual.Data[i * 4]).SequenceEqual(expected[orientation - 1]), "EXIF " + orientation);
                }
            });
            test("JPEG EXIF orientation decoded from metadata", () =>
            {
                var source = Raster.Solid(12, 24, Colors.Red);
                var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
                var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
                encoder.Frames.Add(BitmapFrame.Create(source.Bitmap(), null, metadata, null));
                using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
                var actual = ImportExport.LoadImage(stream); Assert(actual.Width == 24 && actual.Height == 12);
            });
            test("embedded sRGB profile import conversion", () =>
            {
                var source = Raster.Solid(4, 4, Color.FromRgb(20, 100, 220));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source.Bitmap(), null, null, new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>([new ColorContext(PixelFormats.Bgra32)])));
                using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
                var actual = ImportExport.LoadImage(stream);
                Assert(Math.Abs(actual.Data[0] - 220) <= 1 && Math.Abs(actual.Data[1] - 100) <= 1 && Math.Abs(actual.Data[2] - 20) <= 1);
            });
            test("PNG and TIFF preserve RGBA fixture", () =>
            {
                var source = Raster.Solid(7, 5, Color.FromArgb(153, 220, 100, 20));
                foreach (string format in new[] { "png", "tiff" })
                {
                    using var stream = new MemoryStream(); ImportExport.Write(source, stream, format); stream.Position = 0;
                    Assert(Raster.Load(stream).Data.SequenceEqual(source.Data), format);
                }
            });
            test("JPEG quality changes bytes and white alpha matte", () =>
            {
                var source = new Raster(64, 64); var random = new Random(12); random.NextBytes(source.Data);
                for (int i = 3; i < source.Data.Length; i += 4) source.Data[i] = 255;
                using var low = new MemoryStream(); using var high = new MemoryStream();
                ImportExport.Write(source, low, "jpg", 10); ImportExport.Write(source, high, "jpg", 95); Assert(high.Length > low.Length * 1.5);
                using var alpha = new MemoryStream(); ImportExport.Write(Raster.Solid(8, 8, Colors.Transparent), alpha, "jpg"); alpha.Position = 0;
                var white = Raster.Load(alpha); Assert(white.Data[0] >= 254 && white.Data[1] >= 254 && white.Data[2] >= 254 && white.Data[3] == 255);
                Reject<NotSupportedException>(() => ImportExport.Write(source, new MemoryStream(), "psd"));
            });
            test("Lanczos reduction suppresses checker aliasing", () =>
            {
                var source = new Raster(64, 64);
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    int i = (y * 64 + x) * 4; source.Data[i] = source.Data[i + 1] = source.Data[i + 2] = (byte)((x + y) % 2 * 255); source.Data[i + 3] = 255;
                }
                var small = ImportExport.Resize(source, 8, 8);
                Assert(Enumerable.Range(0, 64).All(i => Math.Abs(small.Data[i * 4] - 127.5) < 8));
            });
            test("Lanczos alpha edge does not bleed hidden blue", () =>
            {
                var source = new Raster(2, 1, [0, 0, 255, 255, 255, 0, 0, 0]);
                var resized = ImportExport.Resize(source, 12, 5);
                for (int i = 0; i < resized.Data.Length; i += 4) if (resized.Data[i + 3] > 1) Assert(resized.Data[i] == 0 && resized.Data[i + 2] == 255);
            });
            test("Compositor hand-authored v7 raster fixture", () =>
            {
                string package = Fixture(directory, "fixture.comp");
                var imported = CompositorPackage.Import(package).Document;
                Assert(imported.Width == 2 && imported.Height == 2 && imported.Layers.Count == 1 && imported.Active!.Name == "Reference");
                Assert(imported.Active!.Pixels.Data[2] == 255);
                string export = Path.Combine(directory, "roundtrip.comp"); CompositorPackage.Export(imported, export);
                var roundTrip = CompositorPackage.Import(export).Document;
                Assert(Imaging.Render(imported).Data.SequenceEqual(Imaging.Render(roundTrip).Data));
                Reject<IOException>(() => CompositorPackage.Export(imported, export));
            });
            test("Compositor import refuses unsupported effects and traversal", () =>
            {
                string effects = Fixture(directory, "effects.comp", ", \"effects\": { \"shadow\": { \"blur\": 20 } }");
                Reject<NotSupportedException>(() => CompositorPackage.Import(effects));
                string traversal = Fixture(directory, "traversal.comp"); var path = Path.Combine(traversal, "manifest.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace("11111111-1111-1111-1111-111111111111.png", "../../outside.png"));
                Reject<InvalidDataException>(() => CompositorPackage.Import(traversal));
            });
            test("Compositor five supported adjustment kinds preserve rendering", () =>
            {
                foreach (var spec in new[] {
                    new AdjustmentSpec { Kind = AdjustmentKind.Levels, Black = 12, White = 240, Gamma = 1.5,
                        RedLevels = new LevelsRange { Black = 20 }, GreenLevels = new LevelsRange { White = 220 }, BlueLevels = new LevelsRange { Gamma = .8 } },
                    new AdjustmentSpec { Kind = AdjustmentKind.Curves, Curve = [new(0, 0), new(.4, .65), new(1, 1)],
                        RedCurve = [new(0, .1), new(.4, .7), new(1, .9)], GreenCurve = [new(0, 0), new(.7, .4), new(1, 1)], BlueCurve = [new(0, .3), new(1, 1)] },
                    new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = .7, Offset = .03, ExposureGamma = 1.2 },
                    new AdjustmentSpec { Kind = AdjustmentKind.GradientMap, DarkColor = 0xFF202050, LightColor = 0xFFFFC070 },
                    new AdjustmentSpec { Kind = AdjustmentKind.Grain, Amount = .35, GrainSize = 1.7, GrainRoughness = .4, Seed = -7 } })
                {
                    var doc = new Document { Width = 8, Height = 8 };
                    doc.Add(new Layer { Pixels = Raster.Solid(8, 8, Color.FromArgb(177, 50, 120, 200)) });
                    doc.Add(DocumentFeatures.CreateAdjustment(doc, spec, "Adjustment"));
                    string path = Path.Combine(directory, spec.Kind + ".comp");
                    CompositorPackage.Export(doc, path); var imported = CompositorPackage.Import(path).Document;
                    Assert(Imaging.Render(doc).Data.SequenceEqual(Imaging.Render(imported).Data), spec.Kind.ToString());
                    if (spec.Kind == AdjustmentKind.Levels)
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
                        var ranges = json.RootElement.GetProperty("layers")[1].GetProperty("adjustment").GetProperty("levels").GetProperty("ranges");
                        Assert(ranges[1].GetProperty("black").GetDouble() == 20 && ranges[2].GetProperty("white").GetDouble() == 220 && ranges[3].GetProperty("gamma").GetDouble() == .8);
                        Assert(imported.Active!.Adjustment!.RedLevels.Black == 20 && imported.Active.Adjustment.BlueLevels.Gamma == .8);
                    }
                    if (spec.Kind == AdjustmentKind.Curves) Assert(imported.Active!.Adjustment!.RedCurve.SequenceEqual(spec.RedCurve) && imported.Active.Adjustment.BlueCurve.SequenceEqual(spec.BlueCurve));
                }
            });
            test("Compositor group text and grayscale mask preserve metadata", () =>
            {
                var doc = new Document { Width = 20, Height = 20 }; var group = DocumentFeatures.CreateGroup(doc, "Folder"); doc.Add(group);
                var text = new Layer { Kind = LayerKind.Text, Text = new TextSpec { Content = "A", FontSize = 12 }, Pixels = Raster.Solid(8, 8, Colors.White), ParentId = group.Id, Mask = Enumerable.Repeat((byte)128, 64).ToArray() };
                doc.Add(text); string path = Path.Combine(directory, "text.comp"); CompositorPackage.Export(doc, path);
                var imported = CompositorPackage.Import(path).Document;
                Assert(imported.Active!.Text!.Content == "A" && imported.Active.ParentId == group.Id && imported.Active.Mask!.SequenceEqual(text.Mask));
                Assert(Imaging.Render(imported).Data.SequenceEqual(Imaging.Render(doc).Data));
                text.Blend = BlendMode.Multiply;
                Reject<NotSupportedException>(() => CompositorPackage.Export(doc, Path.Combine(directory, "unsupported-group.comp")));
            });
            test("ONNX missing model and mask composition", () =>
            {
                Reject<FileNotFoundException>(() => BackgroundRemoval.CreateMask(new Raster(1, 1), Path.Combine(directory, "missing.onnx")));
                Assert(BackgroundRemoval.CombineMasks([0, 128, 255], [255, 128, 128]).SequenceEqual(new byte[] { 0, 64, 128 }));
            });
            test("real ONNX CPU inference through generated ReduceMean fixture", () =>
            {
                string model = Path.Combine(directory, "reduce-mean.onnx"); File.WriteAllBytes(model, MakeOnnxFixture());
                var image = Raster.Solid(320, 320, Colors.Black);
                for (int y = 0; y < 320; y++) for (int x = 160; x < 320; x++) for (int c = 0; c < 3; c++) image.Data[(y * 320 + x) * 4 + c] = 255;
                var mask = BackgroundRemoval.CreateMask(image, model);
                Assert(mask.Length == 320 * 320 && mask[100 * 320 + 30] == 0 && mask[100 * 320 + 280] == 255);
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                Reject<OperationCanceledException>(() => BackgroundRemoval.CreateMask(image, model, cancellation.Token));
            });
            test("bundled U2NetP weights checksum and real inference", () =>
            {
                string model = BackgroundRemoval.DefaultModelPath;
                Assert(File.Exists(model), "Bundled model is missing");
                using var stream = File.OpenRead(model);
                string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                Assert(sha == "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8", "Model digest differs");
                var sample = Raster.Solid(64, 64, Colors.LightBlue);
                for (int y = 12; y < 54; y++) for (int x = 20; x < 45; x++) { int i = (y * 64 + x) * 4; sample.Data[i] = 30; sample.Data[i + 1] = 70; sample.Data[i + 2] = 210; }
                var mask = BackgroundRemoval.CreateMask(sample);
                Assert(mask.Length == 4096 && mask.Max() > mask.Min(), "Model did not produce a varying mask");
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    static string Fixture(string root, string name, string extra = "")
    {
        string directory = Path.Combine(root, name); Directory.CreateDirectory(Path.Combine(directory, "images"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), """
            {"format":"com.compositor.project","version":7,"colorSpace":"sRGB","resolution":96,
             "documentID":"22222222-2222-2222-2222-222222222222","width":2,"height":2,"activeLayerID":"11111111-1111-1111-1111-111111111111",
             "layers":[{"id":"11111111-1111-1111-1111-111111111111","name":"Reference","isVisible":true,
               "transform":{"origin":[0,0],"size":[2,2],"rotation":0,"flipX":false,"flipY":false,"sampling":"High quality"},
               "imageFile":"11111111-1111-1111-1111-111111111111.png","opacity":1,"blendMode":"Normal"
            """ + extra + "}]}");
        using var stream = File.Create(Path.Combine(directory, "images", "11111111-1111-1111-1111-111111111111.png")); Raster.Solid(2, 2, Colors.Red).WritePng(stream);
        return directory;
    }

    // An independently generated ONNX protobuf model, not downloaded weights. Tests the actual native
    // runtime, tensor layout, normalization, output validation and mask resampling, not AI visual quality.
    static byte[] MakeOnnxFixture()
    {
        static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
        static byte[] Var(ulong value) { var list = new List<byte>(); do { byte b = (byte)(value & 127); value >>= 7; list.Add((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0); return list.ToArray(); }
        static byte[] Int(int field, ulong value) => Join(Var((ulong)field << 3), Var(value));
        static byte[] Bytes(int field, byte[] bytes) => Join(Var(((ulong)field << 3) | 2), Var((ulong)bytes.Length), bytes);
        static byte[] Str(int field, string text) => Bytes(field, Encoding.UTF8.GetBytes(text));
        static byte[] Value(string name, params int[] shape) => Join(Str(1, name), Bytes(2, Bytes(1, Join(Int(1, 1), Bytes(2, Join(shape.Select(d => Bytes(1, Int(1, (ulong)d))).ToArray()))))));
        var axes = Join(Str(1, "axes"), Int(8, 1), Int(20, 7));
        var node = Join(Str(1, "X"), Str(2, "Y"), Str(4, "ReduceMean"), Bytes(5, axes));
        var graph = Join(Bytes(1, node), Str(2, "Morupixel test fixture"), Bytes(11, Value("X", 1, 3, 320, 320)), Bytes(12, Value("Y", 1, 1, 320, 320)));
        return Join(Int(1, 8), Str(2, "Morupixel self-test"), Bytes(7, graph), Bytes(8, Int(2, 13)));
    }
}
