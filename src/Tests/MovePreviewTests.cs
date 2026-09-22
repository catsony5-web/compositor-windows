using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunMovePreviewTests(Action<string, Action> test)
    {
        static void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static (Document Document, Layer Moving) Scene()
        {
            var document = new Document { Width = 8, Height = 8 };
            document.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Blue), Locked = true });
            var moving = new Layer { Pixels = Raster.Solid(3, 3, Colors.Red), X = 1, Y = 1 };
            document.Add(moving);
            document.Add(new Layer { Pixels = Raster.Solid(1, 4, Colors.Lime), X = 5, Y = 1 });
            document.ActiveId = moving.Id;
            return (document, moving);
        }

        test("move preview accepts simple image text shape and CAD layers below other objects", () =>
        {
            var (document, moving) = Scene();
            foreach (var kind in new[] { LayerKind.Raster, LayerKind.Text, LayerKind.Shape, LayerKind.Vector })
            {
                moving.Kind = kind;
                Expect(CanPreviewLayerMove(document, moving, 1), $"Simple {kind} below another object was not eligible");
            }
            Expect(!CanPreviewLayerMove(document, moving, 2), "Multi-selection used single-layer preview");
            moving.Locked = true;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Locked moving layer was eligible");
        });

        test("move preview falls back for interdependent layers masks warps and excessive cache memory", () =>
        {
            var (document, moving) = Scene(); var upper = document.Layers[^1];
            upper.Blend = BlendMode.Multiply;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Upper blend interaction was flattened independently");
            upper.Blend = BlendMode.Normal; upper.Clipped = true;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Clipped upper layer was detached from its base");
            upper.Clipped = false; upper.Kind = LayerKind.Adjustment;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Adjustment was detached from moving content");
            upper.Kind = LayerKind.Group;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Group stack entered simple preview");
            upper.Kind = LayerKind.Raster; moving.ParentId = upper.Id;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Parent transform was omitted from preview");
            moving.ParentId = null; moving.Mask = new byte[9];
            Expect(!CanPreviewLayerMove(document, moving, 1), "Moving mask was omitted from preview");
            moving.Mask = null;
            moving.Warp = new(new Point(0, 0), new Point(3, 0), new Point(2.5, 3), new Point(0, 3));
            Expect(!CanPreviewLayerMove(document, moving, 1), "Warped layer entered affine-only preview");
            moving.Warp = null; document.Width = document.Height = 65535;
            Expect(!CanPreviewLayerMove(document, moving, 1), "Huge document requested unbounded cache copies");
        });

        test("move preview fixed stacks preserve alpha order and remove the original moving position", () =>
        {
            var (document, moving) = Scene();
            moving.Opacity = .7;
            document.Layers[^1].Opacity = .6;
            document.Add(new Layer { Pixels = Raster.Solid(2, 3, Color.FromArgb(140, 255, 220, 30)), X = 4, Y = 2 });
            document.ActiveId = moving.Id;
            var (below, above) = CreateLayerMovePreviewStacks(document, moving.Id);
            Expect(document.Layers.Count == 4 && document.ActiveId == moving.Id, "Preparing caches mutated the source document");
            Expect(below.Layers.Count == 1 && above.Layers.Count == 2 &&
                below.Layers.All(layer => layer.Id != moving.Id) && above.Layers.All(layer => layer.Id != moving.Id),
                "Moving layer leaked into a fixed stack");
            var background = Imaging.Render(below);
            var foreground = Imaging.Render(above);
            moving.X = 4;
            var combined = background.Clone();
            Imaging.Composite(combined, moving);
            Imaging.Composite(combined, new Layer { Pixels = foreground });
            var expected = Imaging.Render(document);
            for (int i = 0; i < combined.Data.Length; i++)
                Expect(Math.Abs(combined.Data[i] - expected.Data[i]) <= 2, $"Cached alpha/order differs at byte {i}");
            int original = (1 * document.Width + 1) * 4;
            Expect(combined.Data[original] == 255 && combined.Data[original + 2] == 0, "Old position retained a moving-layer ghost");
            document.Layers[0].X = 200;
            Expect(below.Layers[0].X == 0, "Fixed cache snapshot shared mutable layer geometry");
        });

        test("canvas move preview draws fixed foreground above the moving image without ghosts", () =>
        {
            // Probe solid interiors: at 8x zoom WPF correctly interpolates a
            // one-pixel upper line into its neighboring screen pixels.
            var document = new Document { Width = 16, Height = 12 };
            document.Add(new Layer { Pixels = Raster.Solid(16, 12, Colors.Blue) });
            var moving = new Layer { Pixels = Raster.Solid(6, 6, Colors.Red), X = 1, Y = 2 };
            document.Add(moving);
            document.Add(new Layer { Pixels = Raster.Solid(3, 8, Colors.Lime), X = 9, Y = 1 });
            document.ActiveId = moving.Id;
            var old = Imaging.Render(document).Bitmap();
            var (below, above) = CreateLayerMovePreviewStacks(document, moving.Id);
            moving.X = 6;
            var view = new CanvasView
            {
                Document = document, Composite = old, Zoom = 8,
                MovePreviewBackground = Imaging.Render(below).Bitmap(),
                MovePreviewLayer = moving.Pixels.Bitmap(), MovePreviewMatrix = moving.Matrix,
                MovePreviewForeground = Imaging.Render(above).Bitmap()
            };
            view.Measure(new Size(200, 200)); view.Arrange(new Rect(0, 0, 200, 200));
            var surface = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
            surface.Render(view);
            var pixels = new byte[200 * 200 * 4]; surface.CopyPixels(pixels, 800, 0);
            int Pixel(int x, int y) => ((int)(view.Origin.Y + (y + .5) * view.Zoom) * 200 +
                (int)(view.Origin.X + (x + .5) * view.Zoom)) * 4;
            int original = Pixel(2, 3), moved = Pixel(7, 4), occluded = Pixel(10, 4);
            Expect(pixels[original] > 240 && pixels[original + 2] < 10, "Original position retained a red ghost");
            Expect(pixels[moved + 2] > 240 && pixels[moved] < 10,
                $"Moving image did not follow its matrix: BGRA {string.Join(',', pixels.Skip(moved).Take(4))}; foreground {string.Join(',', pixels.Skip(occluded).Take(4))}; origin {view.Origin}; matrix {moving.Matrix}");
            Expect(pixels[occluded + 1] > 240 && pixels[occluded + 2] < 10, "Moving image jumped in front of the green upper layer");
        });

        test("move preview cleanup invalidates pending work and clears all three display planes", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var source = new CancellationTokenSource();
            try
            {
                var image = Raster.Solid(2, 2, Colors.Red).Bitmap();
                window.textPreviewCts = source; window.textPreviewDocument = window.doc;
                window.textPreviewLayerId = Guid.NewGuid(); window.textPreviewBitmap = image;
                window.canvas.MovePreviewBackground = image; window.canvas.MovePreviewLayer = image;
                window.canvas.MovePreviewForeground = image;
                long generation = window.textPreviewGeneration;
                window.ClearTextMovePreview();
                Expect(source.IsCancellationRequested && window.textPreviewGeneration > generation,
                    "Pending cache can still publish after cleanup");
                Expect(window.textPreviewDocument == null && window.textPreviewLayerId == null &&
                    window.textPreviewBitmap == null && window.canvas.MovePreviewBackground == null &&
                    window.canvas.MovePreviewLayer == null && window.canvas.MovePreviewForeground == null,
                    "Canceled drag retained a display plane or layer reference");
            }
            finally { window.StopRenderingForShutdown(); source.Dispose(); }
        });
    }
}
