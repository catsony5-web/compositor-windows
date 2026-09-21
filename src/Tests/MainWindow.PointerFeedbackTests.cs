using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunPointerFeedbackTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static MainWindow Window()
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.doc = new Document { Width = 500, Height = 320, Name = "포인터 상호작용" };
            window.doc.Add(new Layer { Name = "배경", Pixels = Raster.Solid(500, 320, Colors.White), Locked = true });
            window.doc.Add(new Layer { Name = "도면 선", Pixels = Raster.Solid(2, 120, Colors.DarkBlue), X = 100, Y = 140 });
            window.doc.Add(VectorShapes.Create(new ShapeSpec { Width = 80, Height = 60, FillArgb = 0xFFBBDEEF }, 240, 180));
            window.doc.Add(VectorShapes.Create(new ShapeSpec { Width = 60, Height = 40, FillArgb = 0xFFEA9B75 }, 40, 50));
            window.history.Reset(window.doc); window.InitializeWorkspace();
            window.canvas.Document = window.doc; window.canvas.Zoom = 1; window.canvas.ShowLayerBounds = true;
            window.tool = Tool.Move; window.canvas.Cursor = Cursors.Arrow; window.autoSelectToggle.IsChecked = true;
            window.snapping = true;
            return window;
        }
        void Case(string name, Action<MainWindow> action) => test("pointer feedback: " + name, () =>
        {
            var window = Window();
            try { action(window); }
            finally { window.dragging = false; window.panning = false; window.StopRenderingForShutdown(); }
        });

        Case("preselection and click use the same thin-line target without changing history", window =>
        {
            window.NudgeSelected(new Vector(1, 0)); window.Undo();
            var active = window.doc.ActiveId; var revision = window.doc.Revision; var pixels = window.doc.Active!.Pixels;
            var point = new Point(104, 200);
            window.UpdatePointerHover(point);
            var line = window.doc.Layers.Single(l => l.Name == "도면 선");
            Check(window.canvas.HoveredLayerId == line.Id && window.PickMoveTarget(point)?.Id == line.Id, "Thin-line target disagreed with hover");
            Check(window.CanvasCursorAt(point) == Cursors.Arrow, "Idle selection showed the move cursor");
            Check(window.doc.ActiveId == active && window.doc.Revision == revision && ReferenceEquals(window.doc.Active.Pixels, pixels) &&
                !window.history.CanUndo && window.history.CanRedo && !window.history.Dirty(window.doc), "Hover altered editing state");
            window.UpdatePointerHover(new Point(400, 70));
            Check(window.canvas.HoveredLayerId == null, "Locked page background received a distracting hover outline");
            window.UpdatePointerHover(point); window.autoSelectToggle.IsChecked = false;
            Check(window.canvas.HoveredLayerId == null, "Disabling auto-selection retained a stale target");
        });

        Case("cursor changes only after drag threshold and modifiers preserve transform priority", window =>
        {
            var center = new Point(70, 70); window.start = center; window.screenStart = center;
            window.beforeGesture = window.doc.Snapshot(); window.dragging = true; window.moveStarted = false;
            window.ContinueMove(center + new Vector(1, 1), center + new Vector(1, 1), ModifierKeys.None);
            Check(window.CanvasCursorAt(center) == Cursors.Arrow && window.doc.Active!.X == 40, "A click jitter started a move");
            window.ContinueMove(center + new Vector(15, 18), center + new Vector(15, 18), ModifierKeys.Alt);
            Check(window.CanvasCursorAt(center) == Cursors.SizeAll && window.moveStarted, "Dragging did not show the movement cursor");
            window.CancelGesture();
            Check(window.CanvasCursorAt(center) == Cursors.Arrow && window.doc.Active!.X == 40 && window.doc.Active.Y == 50, "Cancel failed to restore the object and cursor");
            var corner = TransformHandles.Points(window.doc, window.doc.Active!, 1)[0];
            Check(window.CanvasCursorAt(corner) == Cursors.SizeNWSE, "Resize corner lost its diagonal cursor");
            Check(window.CanvasCursorAt(corner, true) == Cursors.Hand, "Space pan lost priority");
        });

        Case("real move command uses edge snapping hysteresis Alt and Shift without polluting history", window =>
        {
            window.start = new Point(70, 70); window.screenStart = window.start;
            window.beforeGesture = window.doc.Snapshot(); window.dragging = true;
            void Move(double x, double y, ModifierKeys modifiers) => window.ContinueMove(window.start + new Vector(x, y), window.screenStart + new Vector(x, y), modifiers);
            Move(136, 76, ModifierKeys.None);
            Check(window.doc.Active!.X == 180 && window.canvas.SnapGuides.Any(g => g.Vertical && g.Position == 240), "Right edge did not attach to the target edge");
            Move(147, 76, ModifierKeys.None);
            Check(window.doc.Active.X == 180, "Minor pointer movement broke the magnetic latch");
            Move(147, 76, ModifierKeys.Alt);
            Check(window.doc.Active.X == 187 && window.canvas.SnapGuides.Count == 0, "Alt did not bypass snapping");
            Move(136, 20, ModifierKeys.Shift);
            Check(window.doc.Active.X == 180 && window.doc.Active.Y == 50, "Shift added orthogonal motion");
            Check(!window.history.CanUndo && !window.history.Dirty(window.doc), "Pointer frames added undo entries");
            window.CancelGesture();
            Check(window.canvas.SnapGuides.Count == 0 && window.moveSnapSession == null && window.doc.Active!.X == 40, "Cancel retained snap state");
        });

        Case("tool and document changes clear preselection and transient guides", window =>
        {
            window.UpdatePointerHover(new Point(104, 200));
            Check(window.canvas.HoveredLayerId != null, "Missing initial target");
            window.SetTool(Tool.Brush);
            Check(window.canvas.HoveredLayerId == null && window.canvas.Cursor == Cursors.Cross, "Brush inherited selection feedback");
            window.SetTool(Tool.Move); window.UpdatePointerHover(new Point(104, 200));
            window.AddTab(new Document { Width = 20, Height = 20 }, null);
            Check(window.canvas.HoveredLayerId == null && window.canvas.SnapGuides.Count == 0, "New tab inherited old feedback");
        });

        Case("hover and magnetic guides render offscreen over the correct image", window =>
        {
            string output = Path.Combine(directory, "pointer-feedback"); Directory.CreateDirectory(output);
            window.canvas.Width = 660; window.canvas.Height = 440; window.canvas.Zoom = 1;
            window.canvas.Measure(new Size(660, 440)); window.canvas.Arrange(new Rect(0, 0, 660, 440));
            void Save(string name)
            {
                // A fresh unattached surface captures this exact state without a deferred
                // layout pass from the unshown parent Window restoring an older visual.
                var view = new CanvasView { Document = window.doc, Composite = Imaging.Render(window.doc).Bitmap(),
                    Zoom = 1, ShowLayerBounds = true, HoveredLayerId = window.canvas.HoveredLayerId, SnapGuides = window.canvas.SnapGuides };
                view.Measure(new Size(660, 440)); view.Arrange(new Rect(0, 0, 660, 440)); view.UpdateLayout();
                var image = new RenderTargetBitmap(660, 440, 96, 96, PixelFormats.Pbgra32); image.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
                var bytes = new byte[660 * 440 * 4]; image.CopyPixels(bytes, 660 * 4, 0);
                int white = ((int)(view.Origin.Y + 10) * 660 + (int)(view.Origin.X + 10)) * 4;
                Check(bytes[white] > 245 && bytes[white + 1] > 245 && bytes[white + 2] > 245, "Document image was missing from the capture");
            }
            window.UpdatePointerHover(new Point(104, 200)); Save("hover.png");
            window.start = new Point(70, 70); window.screenStart = window.start;
            window.beforeGesture = window.doc.Snapshot(); window.dragging = true;
            window.ContinueMove(new Point(206, 146), new Point(206, 146), ModifierKeys.None);
            Save("magnetic-move.png");
        });
    }
}
