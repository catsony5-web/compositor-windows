using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class BrushTipTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static Layer Layer(Color? color = null) => new() { Pixels = Raster.Solid(40, 40, color ?? Colors.Transparent) };
        static byte Alpha(Layer layer, int x, int y) => layer.Pixels.Data[(y * layer.Pixels.Width + x) * 4 + 3];
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid tip was accepted");
        }

        test("built-in brush silhouettes distinguish round, square, diamond and star", () =>
        {
            Check(BrushTip.Round.Sample(.8, .8, 1) == 0 && BrushTip.Square.Sample(.8, .8, 1) == 1, "Square corners are round");
            Check(BrushTip.Diamond.Sample(.6, .2, 1) == 1 && BrushTip.Diamond.Sample(.6, .6, 1) == 0, "Diamond silhouette is incorrect");
            Check(BrushTip.Star.Sample(0, -.9, 1) == 1 && BrushTip.Star.Sample(0, .9, 1) == 0, "Star lost its top point or bottom notch");
            Check(BrushTip.Square.Sample(.8, 0, .2) > 0 && BrushTip.Square.Sample(.8, 0, .2) < 1, "Shape hardness does not soften its perimeter");
        });
        test("round tip preserves the legacy soft-dab pixel values and default stroke", () =>
        {
            var implicitRound = Layer(); var explicitRound = Layer();
            var old = new BrushStroke(implicitRound, null, Colors.Red, 18, .3, .6, false, false);
            var current = new BrushStroke(explicitRound, null, Colors.Red, 18, .3, .6, false, false, BrushTip.Round);
            old.Point(new Point(20, 20)); current.Point(new Point(20, 20));
            for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++)
            {
                double distance = (new Point(x + .5, y + .5) - new Point(20, 20)).Length / 9;
                double expected = distance > 1 ? 0 : (distance <= .3 ? 1 : BrushStroke.Falloff((distance - .3) / .7)) * .6;
                Check(Alpha(implicitRound, x, y) == Imaging.Byte(expected * 255), "Legacy round-dab falloff changed");
            }
            old.Point(new Point(29, 23)); current.Point(new Point(29, 23));
            Check(implicitRound.Pixels.Data.SequenceEqual(explicitRound.Pixels.Data), "Selecting round changed the default stroke");
        });
        test("custom brush alpha is immutable and preserves transparent light artwork", () =>
        {
            byte[] source = [0, 255, 128, 0]; var tip = BrushTip.FromAlpha("immutable", 2, 2, source); source[1] = 0;
            Check(tip.Sample(.5, -.5, 1) == 1, "Caller mutation changed the stored silhouette");
            var image = Raster.Solid(2, 2, Colors.White); image.Data[3] = 0;
            var white = BrushTip.FromImage("white artwork", image);
            Check(white.Sample(-.5, -.5, 1) == 0 && white.Sample(.5, -.5, 0) == 1, "Transparent white artwork was interpreted as inverse luminance");
        });
        test("opaque custom artwork converts white to empty and black to ink", () =>
        {
            var image = Raster.Solid(2, 2, Colors.White); image.Data[0] = image.Data[1] = image.Data[2] = 0;
            var tip = BrushTip.FromImage("ink", image);
            Check(tip.Sample(-.5, -.5, 1) == 1 && tip.Sample(.5, .5, 1) == 0, "Opaque ink/background conversion is incorrect");
            Reject(() => BrushTip.FromImage("empty", Raster.Solid(2, 2, Colors.White)));
            Reject(() => BrushTip.FromImage("empty alpha", Raster.Solid(2, 2, Colors.Transparent)));
        });
        test("image-tip diameter preserves aspect ratio and rotation", () =>
        {
            var tip = BrushTip.FromAlpha("wide", 8, 2, Enumerable.Repeat((byte)255, 16).ToArray());
            var horizontal = Layer(); var vertical = Layer();
            new BrushStroke(horizontal, null, Colors.Blue, 16, 1, 1, false, false, tip).Point(new Point(20, 20));
            new BrushStroke(vertical, null, Colors.Blue, 16, 1, 1, false, false, tip, .1, 90).Point(new Point(20, 20));
            Check(Alpha(horizontal, 26, 20) > 0 && Alpha(horizontal, 20, 26) == 0, "Image tip lost its aspect ratio");
            Check(Alpha(vertical, 20, 26) > 0 && Alpha(vertical, 26, 20) == 0, "Tip angle did not rotate the silhouette");
            Check(horizontal.Pixels.Data[(20 * 40 + 20) * 4] == 255, "Image tip did not use the foreground ink color");
        });
        test("rotated square bounds include corners outside the unrotated diameter", () =>
        {
            var layer = Layer();
            new BrushStroke(layer, null, Colors.Red, 16, 1, 1, false, false, BrushTip.Square, .1, 45).Point(new Point(20, 20));
            Check(Alpha(layer, 30, 20) == 255 && Alpha(layer, 31, 20) == 0, "Rotated square was clipped to circular bounds");
        });
        test("shape stamp spacing is independent of pointer-event subdivisions", () =>
        {
            var one = Layer(); var many = Layer();
            var a = new BrushStroke(one, null, Colors.Red, 4, 1, 1, false, false, BrushTip.Square, 1.5);
            var b = new BrushStroke(many, null, Colors.Red, 4, 1, 1, false, false, BrushTip.Square, 1.5);
            a.Point(new Point(4, 20)); a.Point(new Point(34, 20));
            for (int x = 4; x <= 34; x++) b.Point(new Point(x, 20));
            Check(one.Pixels.Data.SequenceEqual(many.Pixels.Data), "Stamp spacing depends on pointer event frequency");
            Check(Alpha(one, 7, 20) == 0 && Alpha(one, 10, 20) == 255, "Spacing slider has no visible effect");
        });
        test("custom overlapping stamps preserve whole-stroke opacity and original pixels", () =>
        {
            var tip = BrushTip.FromAlpha("solid", 3, 3, Enumerable.Repeat((byte)255, 9).ToArray());
            var layer = Layer(); var before = layer.Pixels; var beforeBytes = before.Data.ToArray();
            var stroke = new BrushStroke(layer, null, Colors.Red, 12, 1, .5, false, false, tip);
            stroke.Point(new Point(20, 20)); stroke.Point(new Point(24, 20)); stroke.Point(new Point(20, 20));
            Check(Alpha(layer, 20, 20) == 128, "Overlapping custom dabs accumulated opacity");
            Check(!ReferenceEquals(before, layer.Pixels) && before.Data.SequenceEqual(beforeBytes), "Painting mutated immutable history pixels");
        });
        test("shape painting and erasing obey selections without changing history", () =>
        {
            var selected = new Selection(new Rect(0, 0, 20, 40));
            var painted = Layer(); new BrushStroke(painted, selected, Colors.Red, 16, 1, 1, false, false, BrushTip.Square).Point(new Point(20, 20));
            Check(Alpha(painted, 19, 20) == 255 && Alpha(painted, 21, 20) == 0, "Tip paint escaped selection");
            var erased = Layer(Colors.Blue); var before = erased.Pixels;
            new BrushStroke(erased, selected, Colors.Black, 16, 1, 1, true, false, BrushTip.Diamond).Point(new Point(20, 20));
            Check(Alpha(erased, 19, 20) == 0 && Alpha(erased, 21, 20) == 255 && before.Data[3] == 255, "Tip eraser damaged selection or history");
        });
        test("image tip paints masks independently and retains the previous mask", () =>
        {
            var tip = BrushTip.FromAlpha("mask", 2, 2, [255, 255, 255, 255]); var layer = Layer(Colors.Red);
            layer.Mask = Enumerable.Repeat((byte)255, 1600).ToArray(); var before = layer.Mask; var pixels = layer.Pixels;
            new BrushStroke(layer, new Selection(new Rect(0, 0, 20, 40)), Colors.Black, 16, 1, 1, false, true, tip).Point(new Point(20, 20));
            Check(layer.Mask[20 * 40 + 19] == 0 && layer.Mask[20 * 40 + 21] == 255, "Image mask ignored selection");
            Check(before.All(b => b == 255) && ReferenceEquals(pixels, layer.Pixels), "Mask tip changed history or source pixels");
        });
        test("brush image import normalizes large art and preserves its aspect ratio", () =>
        {
            string path = Path.Combine(directory, "brush-import-wide.png");
            using (var stream = File.Create(path)) Raster.Solid(1024, 256, Colors.Black).WritePng(stream);
            var tip = BrushTipStore.Import(path);
            Check(tip.Width == 256 && tip.Height == 64 && tip.IsCustom, "Image-tip decode did not normalize before pixel copying");
            string alphaPath = Path.Combine(directory, "brush-import-alpha.png");
            var light = Raster.Solid(2, 2, Colors.White); light.Data[3] = 0; light.Data[11] = 128;
            using (var stream = File.Create(alphaPath)) light.WritePng(stream);
            var alpha = BrushTipStore.Import(alphaPath);
            Check(alpha.Sample(-.5, -.5, 1) == 0 && alpha.Sample(.5, -.5, 1) == 1
                && Math.Abs(alpha.Sample(-.5, .5, 1) - 128 / 255.0) < .00001, "PNG decode changed alpha or discarded light artwork");
            string empty = Path.Combine(directory, "brush-import-empty.png"); File.WriteAllBytes(empty, []);
            Reject(() => BrushTipStore.Import(empty));
            Reject(() => BrushTipStore.Import(Path.ChangeExtension(path, ".svg")));
        });
        test("custom brush persistence roundtrips alpha and never creates folders during reads", () =>
        {
            string folder = Path.Combine(directory, "brush-presets-" + Guid.NewGuid().ToString("N"));
            Check(BrushTipStore.LoadAll(folder).Count == 0 && !Directory.Exists(folder), "Reading tips created a user preset directory");
            var tip = BrushTip.FromAlpha("사용자 잎", 3, 2, [0, 255, 0, 64, 128, 32]);
            string file = BrushTipStore.Save(tip, folder); Check(new FileInfo(file).Length < 10000, "Small brush unexpectedly retained the source image");
            File.WriteAllText(Path.Combine(folder, "broken.png"), "invalid");
            var loaded = BrushTipStore.LoadAll(folder);
            Check(loaded.Count == 1 && loaded[0].Id == tip.Id && loaded[0].Name == tip.Name, "Brush silhouette/name changed after reloading");
            Check(loaded[0].MaskImage().Data.SequenceEqual(tip.MaskImage().Data), "Persisted tip alpha changed");
            Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Atomic tip save left temporary files");
        });
    }
}
