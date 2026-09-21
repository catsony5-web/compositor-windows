using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class RenderingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("text move preview uses only a topmost simple text layer", () =>
        {
            var document = new Document { Width = 8, Height = 8 };
            document.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue) });
            var text = new Layer { Kind = LayerKind.Text, Text = new TextSpec(), Pixels = Raster.Solid(2, 2, Colors.Red) };
            document.Add(text);
            void Assert(bool value) { if (!value) throw new Exception("Incorrect text move preview eligibility"); }
            Assert(MainWindow.CanPreviewTextMove(document, text, 1));
            Assert(!MainWindow.CanPreviewTextMove(document, text, 2));
            text.Mask = [255, 255, 255, 255]; Assert(!MainWindow.CanPreviewTextMove(document, text, 1)); text.Mask = null;
            text.Blend = BlendMode.Multiply; Assert(!MainWindow.CanPreviewTextMove(document, text, 1)); text.Blend = BlendMode.Normal;
            document.Add(new Layer { Pixels = new Raster(8, 8) });
            Assert(!MainWindow.CanPreviewTextMove(document, text, 1));
        });
        test("text move background excludes the old text location", () =>
        {
            var document = new Document { Width = 8, Height = 8 };
            document.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue) });
            var text = new Layer { Kind = LayerKind.Text, Text = new TextSpec(), Pixels = Raster.Solid(2, 2, Colors.Red), X = 1, Y = 1 };
            document.Add(text);
            var background = document.Snapshot(); background.Layers.RemoveAll(layer => layer.Id == text.Id);
            var oldBackground = Imaging.Render(background);
            text.X = 4;
            var newBackground = Imaging.Render(background);
            if (!oldBackground.Data.AsSpan().SequenceEqual(newBackground.Data) || oldBackground.Data[(1 * 8 + 1) * 4] != 255)
                throw new Exception("Text movement changed the cached background");
            var final = Imaging.Render(document);
            if (final.Data[(1 * 8 + 1) * 4] != 255 || final.Data[(1 * 8 + 4) * 4 + 2] != 255)
                throw new Exception("Final render did not move text to its new position");
        });
        test("canvas text preview shows one moved copy without old-position ghost", () =>
        {
            var document = new Document { Width = 8, Height = 8 };
            document.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue) });
            var text = new Layer { Kind = LayerKind.Text, Text = new TextSpec(), Pixels = Raster.Solid(2, 2, Colors.Red), X = 1, Y = 1 };
            document.Add(text);
            var old = Imaging.Render(document).Bitmap();
            var background = document.Snapshot(); background.Layers.RemoveAll(layer => layer.Id == text.Id);
            text.X = 4;
            var view = new CanvasView
            {
                Document = document, Composite = old, Zoom = 8,
                MovePreviewBackground = Imaging.Render(background).Bitmap(),
                MovePreviewLayer = text.Pixels.Bitmap(), MovePreviewMatrix = text.Matrix
            };
            view.Measure(new Size(200, 200)); view.Arrange(new Rect(0, 0, 200, 200));
            var surface = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32); surface.Render(view);
            var pixels = new byte[200 * 200 * 4]; surface.CopyPixels(pixels, 200 * 4, 0);
            int origin = (200 - 8 * 8) / 2;
            int oldPixel = ((origin + 12) * 200 + origin + 12) * 4;
            int newPixel = ((origin + 12) * 200 + origin + 36) * 4;
            if (pixels[oldPixel] < 200 || pixels[oldPixel + 2] > 60 ||
                pixels[newPixel + 2] < 200 || pixels[newPixel] > 60)
                throw new Exception("Canvas preview retained old text or missed the moved copy");
        });
    }
}
