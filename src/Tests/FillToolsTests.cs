using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class FillToolsTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value, string message = "Assertion failed")
        { if (!value) throw new InvalidOperationException(message); }
        static Color Pixel(Raster raster, int x, int y)
        {
            int i = (y * raster.Width + x) * 4;
            return Color.FromArgb(raster.Data[i + 3], raster.Data[i + 2], raster.Data[i + 1], raster.Data[i]);
        }
        static void Set(Raster raster, int x, int y, Color color)
        {
            int i = (y * raster.Width + x) * 4;
            raster.Data[i] = color.B; raster.Data[i + 1] = color.G;
            raster.Data[i + 2] = color.R; raster.Data[i + 3] = color.A;
        }
        static Document Blank(int width, int height)
        {
            var document = new Document { Width = width, Height = height };
            document.Add(new Layer { Pixels = new Raster(width, height) });
            return document;
        }

        test("bucket fills only the connected area sampled from visible line art", () =>
        {
            var document = new Document { Width = 9, Height = 3 };
            var lineArt = Raster.Solid(9, 3, Colors.White);
            for (int y = 0; y < 3; y++) Set(lineArt, 4, y, Colors.Black);
            document.Add(new Layer { Pixels = lineArt });
            document.Add(new Layer { Pixels = new Raster(9, 3) });
            var target = document.Active!; var sample = Imaging.Render(document);
            var connected = FillTools.Fill(document, target.Id, null, sample, new Point(.5, .5), Colors.Red, 0)!;
            Assert(Pixel(connected, 2, 1) == Colors.Red && Pixel(connected, 7, 1).A == 0 && Pixel(connected, 4, 1).A == 0);
            Assert(Pixel(target.Pixels, 2, 1).A == 0, "Bucket mutated the original layer before commit.");
            var global = FillTools.Fill(document, target.Id, null, sample, new Point(.5, .5), Colors.Blue, 0, false)!;
            Assert(Pixel(global, 2, 1) == Colors.Blue && Pixel(global, 7, 1) == Colors.Blue && Pixel(global, 4, 1).A == 0);
        });
        test("bucket tolerance treats hidden RGB in transparent pixels as transparent", () =>
        {
            var document = Blank(3, 1); var sample = new Raster(3, 1);
            Set(sample, 0, 0, Color.FromArgb(0, 255, 0, 0));
            Set(sample, 1, 0, Color.FromArgb(0, 0, 255, 0));
            Set(sample, 2, 0, Color.FromArgb(255, 20, 20, 20));
            var result = FillTools.Fill(document, document.ActiveId, null, sample, new Point(.5, .5), Colors.Green, 0)!;
            Assert(Pixel(result, 0, 0) == Colors.Green && Pixel(result, 1, 0) == Colors.Green && Pixel(result, 2, 0).A == 0);
            var shades = Raster.Solid(3, 1, Color.FromRgb(100, 100, 100));
            Set(shades, 1, 0, Color.FromRgb(110, 110, 110)); Set(shades, 2, 0, Colors.Black);
            var low = FillTools.Fill(document, document.ActiveId, null, shades, new Point(.5, .5), Colors.Red, 9)!;
            var high = FillTools.Fill(document, document.ActiveId, null, shades, new Point(.5, .5), Colors.Red, 10)!;
            Assert(Pixel(low, 1, 0).A == 0 && Pixel(high, 1, 0) == Colors.Red);
        });
        test("bucket selection blocks flood paths and feather scales source alpha", () =>
        {
            var document = Blank(5, 2); var sample = Raster.Solid(5, 2, Colors.White);
            var mask = new byte[] { 255, 255, 0, 128, 128, 0, 0, 0, 0, 0 };
            var selection = SelectionTools.FromMask(mask, 5, 2);
            var result = FillTools.Fill(document, document.ActiveId, selection, sample, new Point(.5, .5), Colors.Red, 0, true, .5)!;
            Assert(Pixel(result, 0, 0).A == 128 && Pixel(result, 1, 0).A == 128);
            Assert(Pixel(result, 3, 0).A == 0, "Flood crossed the selection gap.");
            var global = FillTools.Fill(document, document.ActiveId, selection, sample, new Point(.5, .5), Colors.Red, 0, false, .5)!;
            Assert(Math.Abs(Pixel(global, 3, 0).A - 64) <= 1, "Soft selection or opacity was lost.");
        });
        test("bucket maps document coordinates through layer and parent transforms", () =>
        {
            var document = new Document { Width = 8, Height = 8 };
            var group = new Layer { Kind = LayerKind.Group, Pixels = new Raster(8, 8), X = 1, Y = 1 };
            document.Add(group);
            var target = new Layer { Pixels = new Raster(2, 2), ParentId = group.Id, X = 2, Y = 2 };
            document.Add(target);
            var sample = Raster.Solid(8, 8, Colors.White);
            var result = FillTools.Fill(document, target.Id, null, sample, new Point(3.5, 3.5), Colors.Blue, 0)!;
            Assert(Pixel(result, 0, 0) == Colors.Blue && Pixel(result, 1, 1) == Colors.Blue);
            var selection = new Selection(new Rect(3, 3, 1, 1));
            result = FillTools.Fill(document, target.Id, selection, sample, new Point(3.5, 3.5), Colors.Blue, 0)!;
            Assert(Pixel(result, 0, 0) == Colors.Blue && Pixel(result, 1, 0).A == 0);
        });
        test("bucket rejects protected layers and returns null for no-op", () =>
        {
            var document = Blank(2, 2); var layer = document.Active!; var sample = new Raster(2, 2);
            Assert(FillTools.Fill(document, layer.Id, null, sample, new Point(.5, .5), Colors.Transparent) == null);
            Assert(FillTools.Fill(document, layer.Id, null, sample, new Point(-1, 0), Colors.Red) == null);
            layer.Pixels = Raster.Solid(2, 2, Colors.Red);
            Assert(FillTools.Fill(document, layer.Id, null, sample, new Point(.5, .5), Colors.Red) == null);
            layer.Locked = true;
            try { FillTools.Fill(document, layer.Id, null, sample, new Point(.5, .5), Colors.Blue); throw new Exception("Expected locked layer to fail."); }
            catch (InvalidOperationException) { }
            layer.Locked = false; layer.Visible = false;
            try { FillTools.Fill(document, layer.Id, null, sample, new Point(.5, .5), Colors.Blue); throw new Exception("Expected hidden layer to fail."); }
            catch (InvalidOperationException) { }
        });
        test("bucket cancellation leaves the source raster untouched", () =>
        {
            var document = Blank(4, 4); var original = document.Active!.Pixels;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                FillTools.Fill(document, document.ActiveId, null, new Raster(4, 4), new Point(.5, .5), Colors.Blue, token: cancelled.Token);
                throw new Exception("Expected cancellation.");
            }
            catch (OperationCanceledException) { }
            Assert(ReferenceEquals(original, document.Active.Pixels));
        });
    }
}
