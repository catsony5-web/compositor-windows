using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunTransformCursorTests(Action<string, Action> test)
    {
        static void Expect(Cursor actual, Cursor expected, string context)
        {
            if (!ReferenceEquals(actual, expected)) throw new Exception($"{context}: expected {expected}, got {actual}");
        }
        static Cursor[] Upright() => [Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW,
            Cursors.SizeNS, Cursors.SizeWE, Cursors.SizeNS, Cursors.SizeWE];
        static Cursor[] QuarterTurn() => [Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE,
            Cursors.SizeWE, Cursors.SizeNS, Cursors.SizeWE, Cursors.SizeNS];
        static void CheckHandles(Document document, Layer layer, Cursor[] expected, string context)
        {
            for (int handle = 0; handle < 8; handle++)
                Expect(TransformHandles.CursorForHandle(document, layer, handle, 1), expected[handle], $"{context} handle {handle}");
            Expect(TransformHandles.CursorForHandle(document, layer, 8, 1), Cursors.Hand, context + " rotation");
        }

        test("transform cursor corners stay diagonal on very wide and tall layers", () =>
        {
            foreach (var (width, height) in new[] { (1200, 80), (80, 1200) })
            {
                var document = new Document { Width = width, Height = height };
                var layer = new Layer { Pixels = new Raster(width, height) }; document.Add(layer);
                CheckHandles(document, layer, Upright(), $"{width}×{height}");
            }
        });

        test("transform cursor follows rotation flips parents and screen hit tolerance", () =>
        {
            var document = new Document { Width = 600, Height = 400 };
            var layer = new Layer { Pixels = new Raster(320, 120), X = 40, Y = 50, Rotation = 90 }; document.Add(layer);
            CheckHandles(document, layer, QuarterTurn(), "rotated layer");
            layer.Rotation = 0; layer.FlipX = true;
            var reflected = Upright();
            for (int i = 0; i < 4; i++) reflected[i] = i % 2 == 0 ? Cursors.SizeNESW : Cursors.SizeNWSE;
            CheckHandles(document, layer, reflected, "horizontal reflection");
            layer.FlipX = false;
            var group = DocumentFeatures.CreateGroup(document); group.Rotation = 90; group.ScaleX = 2; group.ScaleY = .5;
            document.Add(group); layer.ParentId = group.Id;
            CheckHandles(document, layer, QuarterTurn(), "rotated and scaled parent");
            layer.ParentId = null;
            foreach (double zoom in new[] { .25, 1, 4 })
            {
                var points = TransformHandles.Points(document, layer, zoom);
                if (TransformHandles.HitTest(document, layer, points[0] + new Vector(-6 / zoom, 0), zoom) != 0)
                    throw new Exception($"Corner was not hit at a 6-DIP distance with zoom {zoom}");
                if (TransformHandles.HitTest(document, layer, points[0] + new Vector(-8 / zoom, 0), zoom) != -1)
                    throw new Exception($"Corner hit extended beyond 7 DIP with zoom {zoom}");
                if (TransformHandles.HitTest(document, layer, points[8], zoom) != 8)
                    throw new Exception("Rotation handle hit testing changed");
            }
        });

        test("transform hover respects editability and gesture priority without document changes", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.doc = new Document { Width = 600, Height = 400 };
                var group = DocumentFeatures.CreateGroup(window.doc); window.doc.Add(group);
                window.doc.Add(new Layer { Pixels = new Raster(320, 180), X = 40, Y = 60, ParentId = group.Id });
                window.history.Reset(window.doc); window.tabs.Clear(); window.selectedLayers.Clear(); window.InitializeWorkspace();
                window.NudgeSelected(new Vector(1, 0)); window.Undo();
                window.tool = Tool.Move; window.canvas.ShowLayerBounds = true; window.canvas.Zoom = 1; window.canvas.Cursor = Cursors.SizeAll;
                var layer = window.doc.Active!; group = window.doc.Layers.Single(item => item.Id == group.Id);
                var revision = window.doc.Revision; var pixels = layer.Pixels;
                var points = TransformHandles.Points(window.doc, layer, 1);
                var center = points[0] + (points[2] - points[0]) * .5;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeNWSE, "corner hover");
                Expect(window.CanvasCursorAt(center), Cursors.SizeAll, "layer center");
                layer.Locked = true;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeAll, "locked layer");
                layer.Locked = false; group.Locked = true;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeAll, "locked parent");
                group.Locked = false; layer.Kind = LayerKind.Adjustment;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeAll, "adjustment layer");
                layer.Kind = LayerKind.Raster; window.canvas.ShowLayerBounds = false;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeAll, "hidden bounds");
                window.canvas.ShowLayerBounds = true;
                window.jobCts = new CancellationTokenSource();
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeAll, "active background edit");
                window.jobCts.Dispose(); window.jobCts = null;
                Expect(window.CanvasCursorAt(points[0], panModifier: true), Cursors.Hand, "pan modifier");
                window.panning = true;
                Expect(window.CanvasCursorAt(points[0]), Cursors.Hand, "active pan");
                window.panning = false; window.resizingBrush = true;
                Expect(window.CanvasCursorAt(points[0]), Cursors.SizeWE, "brush diameter gesture");
                window.resizingBrush = false;
                window.handleGesture = new HandleGesture(0, false, layer.Snapshot(), new Point(), new Point(), new Point());
                window.dragging = true;
                Expect(window.CanvasCursorAt(new Point(5000, 5000)), Cursors.SizeNWSE, "active handle outside layer");
                window.handleGesture = null; window.dragging = false;
                Expect(window.CanvasCursorAt(new Point(5000, 5000)), Cursors.SizeAll, "finished handle gesture");
                window.tool = Tool.Brush; window.canvas.Cursor = Cursors.Cross;
                Expect(window.CanvasCursorAt(points[0]), Cursors.Cross, "brush tool over transform corner");
                window.tool = Tool.Hand;
                // WPF may already handle QueryCursor from the element's Cursor
                // property. Exercise the actual registration with that state.
                var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = Mouse.QueryCursorEvent, Cursor = Cursors.Cross, Handled = true
                };
                window.canvas.RaiseEvent(query);
                Expect(query.Cursor, Cursors.Hand, "routed prehandled cursor query");
                window.tool = Tool.Move; window.canvas.Cursor = Cursors.SizeAll;
                if (layer.X != 40 || layer.Y != 60 || layer.ScaleX != 1 || layer.ScaleY != 1 || layer.Rotation != 0
                    || !ReferenceEquals(pixels, layer.Pixels) || window.doc.Revision != revision
                    || window.history.CanUndo || !window.history.CanRedo || window.history.Dirty(window.doc)
                    || !ReferenceEquals(window.canvas.Cursor, Cursors.SizeAll))
                    throw new Exception("Cursor queries changed document geometry, pixels, history or the persistent tool cursor");
            }
            finally
            {
                window.handleGesture = null; window.dragging = false; window.panning = false; window.resizingBrush = false;
                window.renderCts?.Cancel(); window.jobCts?.Cancel(); window.jobCts?.Dispose(); window.jobCts = null;
            }
        });
    }
}
