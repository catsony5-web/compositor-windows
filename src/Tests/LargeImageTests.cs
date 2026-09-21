using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class LargeImageTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        test("large image validation accepts the BGRA array boundary without allocating it", () =>
        {
            Assert(Raster.MaxPixels == Array.MaxLength / 4, "Pixel ceiling must fit the BGRA byte array");
            int lastHeight = Raster.MaxPixels / Raster.MaxDimension;
            Assert((long)Raster.MaxDimension * lastHeight <= Raster.MaxPixels);
            Assert((long)Raster.MaxDimension * (lastHeight + 1) > Raster.MaxPixels);
            Raster.ValidateSize(Raster.MaxDimension, lastHeight);
            Raster.ValidateSize(lastHeight, Raster.MaxDimension);
            Raster.ValidateSize(8192, 8192);
            Raster.ValidateSize(10_000, 2_000);
            Reject(() => Raster.ValidateSize(Raster.MaxDimension, lastHeight + 1));
            Reject(() => Raster.ValidateSize(lastHeight + 1, Raster.MaxDimension));
            Reject(() => Raster.ValidateSize(Raster.MaxDimension + 1, 1));
            Reject(() => Raster.ValidateSize(1, Raster.MaxDimension + 1));
        });

        test("large image validation rejects integer overflow and invalid dimensions", () =>
        {
            foreach (var (width, height) in new[]
            {
                (0, 1), (1, 0), (-1, 1), (1, -1),
                (int.MaxValue, int.MaxValue), (int.MinValue, int.MinValue),
                (int.MaxValue, 4), (4, int.MaxValue),
                (Raster.MaxDimension, Raster.MaxDimension)
            })
                Reject(() => new Raster(width, height));
            Reject(() => new Raster(10_000, 2_000, new byte[4]));
        });

        test("10000 by 2000 PNG opens exports and reopens as a project with exact sample pixels", () =>
        {
            const int width = 10_000, height = 2_000;
            string fixture = Path.Combine(directory, "large-image-source.png");
            // Independent WIC fixture bypasses the application's size guard. Its decoded
            // pixels occupy 80 MB, while sparse content keeps saved test artifacts small.
            WriteWicFixture(fixture, width, height);
            var imported = ImportExport.LoadImage(fixture);
            AssertSamples(imported, width, height);

            string png = Path.Combine(directory, "large-image-export.png");
            using (var stream = File.Create(png)) ImportExport.Write(imported, stream, "png");
            AssertSamples(ImportExport.LoadImage(png), width, height);

            var document = new Document { Width = width, Height = height, Name = "큰 이미지", Dpi = 144 };
            document.Add(new Layer { Name = "큰 원본", Pixels = imported });
            string project = Path.Combine(directory, "large-image-roundtrip.moruproj");
            ProjectStore.Save(document, project);
            var reopened = ProjectStore.Load(project);
            Assert(reopened.Width == width && reopened.Height == height && reopened.Dpi == 144);
            Assert(reopened.Layers.Count == 1 && reopened.Active!.Name == "큰 원본");
            AssertSamples(reopened.Active!.Pixels, width, height);

            // Six layers share immutable pixels, exercising 480 MB of the logical
            // document budget without allocating six large backing arrays.
            for (int layer = 1; layer < 6; layer++)
                document.Add(new Layer { Name = $"큰 원본 {layer + 1}", Pixels = imported });
            document.Validate();
            Assert(document.Layers.Count == 6);
        });

        foreach (bool vertical in new[] { false, true })
        {
            int width = vertical ? 2 : 65_535, height = vertical ? 65_535 : 2;
            test($"{width} by {height} image decodes and renders through WPF", () =>
            {
                var expected = Color.FromRgb(29, 113, 207);
                var source = Raster.Solid(width, height, expected);
                using var encoded = new MemoryStream();
                ImportExport.Write(source, encoded, "png");
                encoded.Position = 0;
                var loaded = ImportExport.LoadImage(encoded);
                Assert(loaded.Width == width && loaded.Height == height);
                Assert(loaded.Data.AsSpan().SequenceEqual(source.Data));

                var bitmap = loaded.Bitmap();
                Assert(bitmap.PixelWidth == width && bitmap.PixelHeight == height);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawImage(bitmap, new Rect(0, 0, 64, 64));
                var surface = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                surface.Render(visual);
                var rendered = Raster.FromBitmap(surface);
                AssertColor(rendered, 32, 32, expected, 1);

                var thumbnail = Raster.FromBitmap(loaded.Thumbnail(68));
                Assert(thumbnail.Width == (vertical ? 1 : 68) && thumbnail.Height == (vertical ? 68 : 1));
                AssertColor(thumbnail, 0, 0, expected);
                AssertColor(thumbnail, thumbnail.Width - 1, thumbnail.Height - 1, expected);
            });
        }

        test("canvas fit and cursor anchored zoom support a 65535 pixel image", () =>
        {
            foreach (bool vertical in new[] { false, true })
            {
                var document = new Document { Width = vertical ? 2 : 65_535, Height = vertical ? 65_535 : 2 };
                var view = new CanvasView { Document = document, Pan = new Vector(21, -13) };
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                view.Fit();
                Assert(view.Zoom > 0 && view.Zoom < .03, "Fit still clips images below 3% zoom");
                Assert(document.Width * view.Zoom <= 904.000001 && document.Height * view.Zoom <= 704.000001);
                Assert(view.Pan == new Vector());
                Assert(view.Origin.X >= 47.999999 && view.Origin.Y >= 47.999999);

                double fittedZoom = view.Zoom;
                var cursor = new Point(420, 300);
                var before = view.ToDocument(cursor);
                view.ZoomAt(.5, cursor);
                var after = view.ToDocument(cursor);
                Assert(Math.Abs(view.Zoom - fittedZoom * .5) < 1e-12, "Zoom jumps back to the previous minimum");
                Assert((after - before).Length < 1e-7, "Zoom moved the document position under the cursor");
            }
        });
    }

    static (int X, int Y, Color Color)[] Samples(int width, int height) =>
    [
        (0, 0, Color.FromArgb(255, 231, 17, 43)),
        (width - 1, 0, Color.FromArgb(255, 19, 211, 71)),
        (0, height - 1, Color.FromArgb(255, 41, 67, 239)),
        (width - 1, height - 1, Color.FromArgb(153, 173, 83, 29)),
        (width / 2, height / 2, Color.FromArgb(101, 59, 137, 223))
    ];

    static void WriteWicFixture(string path, int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        foreach (var (x, y, color) in Samples(width, height))
        {
            int i = (y * width + x) * 4;
            pixels[i] = color.B; pixels[i + 1] = color.G;
            pixels[i + 2] = color.R; pixels[i + 3] = color.A;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static void AssertSamples(Raster actual, int width, int height)
    {
        Assert(actual.Width == width && actual.Height == height, "Image dimensions changed");
        Assert(actual.Data.LongLength == (long)width * height * 4, "Image data was truncated");
        foreach (var (x, y, color) in Samples(width, height)) AssertColor(actual, x, y, color);
        AssertColor(actual, width / 3, height / 3, Color.FromArgb(0, 0, 0, 0));
    }

    static void AssertColor(Raster actual, int x, int y, Color color, int tolerance = 0)
    {
        int i = (y * actual.Width + x) * 4;
        Assert(Math.Abs(actual.Data[i] - color.B) <= tolerance &&
            Math.Abs(actual.Data[i + 1] - color.G) <= tolerance &&
            Math.Abs(actual.Data[i + 2] - color.R) <= tolerance &&
            Math.Abs(actual.Data[i + 3] - color.A) <= tolerance,
            $"Pixel ({x}, {y}) differs from {color}");
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException before allocating image pixels");
    }

    static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
