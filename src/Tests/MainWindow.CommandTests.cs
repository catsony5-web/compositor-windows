using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Offscreen command tests exercise actual UI transactions without scheduling
    // dispatcher work or constructing native windows.
    bool headlessTesting;

    internal static void RunCommandTests(Action<string, Action> test, string directory)
    {
        var window = new MainWindow(null) { headlessTesting = true };
        void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
        void Reset()
        {
            window.renderCts?.Cancel(); window.jobCts?.Cancel();
            window.doc = new Document { Width = 16, Height = 16, Name = "명령 테스트" };
            window.doc.Add(new Layer { Name = "아래", Pixels = Raster.Solid(16, 16, Colors.Blue) });
            window.doc.Add(new Layer { Name = "위", Pixels = Raster.Solid(16, 16, Colors.Transparent) });
            window.history = new History(); window.history.Reset(window.doc);
            window.tabs.Clear(); window.InitializeWorkspace(); window.activeTab = 0;
            window.selectedLayers.Clear(); window.selectedLayers.Add(window.doc.ActiveId);
            window.selection = null; window.maskEditing = false; window.foreground = Colors.Red;
            window.backgroundColor = Colors.White; window.gradientToBackground = false; window.brushSize = 42;
            window.resizingBrush = false; window.canvas.BrushHud = null; window.cloneAnchorLocal = null;
            window.tool = Tool.Move; window.dragging = false; window.panning = false; window.beforeGesture = null;
            window.stroke = null; window.projectPath = null; window.canvas.Document = window.doc; window.canvas.Zoom = 1;
            window.snapping = false; window.ResetInteractionTransient();
        }
        void Case(string name, Action action) => test("UI command: " + name, () => { Reset(); action(); });

        Case("Delete fill shortcuts choose foreground and background without deleting", () =>
        {
            window.foreground = Colors.Red; window.backgroundColor = Colors.Blue;
            window.selection = new Selection(new Rect(0, 0, 8, 16));
            Assert(window.ExecuteEditorShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.Alt));
            Assert(window.doc.Active!.Pixels.Data[2] == 255 && window.doc.Active.Pixels.Data[3] == 255);
            Assert(window.doc.Active.Pixels.Data[12 * 4 + 3] == 0, "Foreground fill escaped selection");
            Assert(window.ExecuteEditorShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.Control));
            Assert(window.doc.Active.Pixels.Data[0] == 255 && window.doc.Active.Pixels.Data[2] == 0);
            window.Undo(); Assert(window.doc.Active.Pixels.Data[2] == 255);
            window.Undo(); Assert(!window.history.Dirty(window.doc) && window.doc.Active.Pixels.Data[3] == 0);
            window.ExecuteEditorShortcut(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.Alt);
            Assert(window.doc.Active.Pixels.Data[2] == 255, "Backspace alias failed");
            window.ExecuteEditorShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None);
            Assert(window.doc.Active.Pixels.Data[3] == 0, "Plain Delete must still erase");
        });

        Case("Delete fill shortcuts respect locked layers and bucket shortcut changes tool", () =>
        {
            window.doc.Active!.Locked = true; var pixels = window.doc.Active.Pixels;
            window.ExecuteEditorShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.Alt);
            Assert(ReferenceEquals(pixels, window.doc.Active.Pixels) && !window.history.Dirty(window.doc));
            window.ExecuteEditorShortcut(System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.None);
            Assert(window.tool == Tool.Bucket);
            window.ExecuteEditorShortcut(System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.Shift);
            Assert(window.tool == Tool.Gradient && !window.history.Dirty(window.doc));
        });

        Case("empty fill and erase preserve clean history and redo", () =>
        {
            window.Fill(); window.Undo(); var pixels = window.doc.Active!.Pixels;
            window.foreground = Colors.Transparent; window.Fill(); window.ClearPixels();
            Assert(ReferenceEquals(pixels, window.doc.Active.Pixels) && !window.history.Dirty(window.doc) && window.history.CanRedo);
            window.foreground = Colors.Red; window.selection = new Selection(new Rect(30, 30, 1, 1)); window.Fill();
            Assert(ReferenceEquals(pixels, window.doc.Active.Pixels) && window.history.CanRedo);
        });

        Case("Alt resize changes diameter without painting or destroying redo", () =>
        {
            window.Fill(); window.Undo(); var pixels = window.doc.Active!.Pixels; window.tool = Tool.Brush;
            Assert(!window.BeginBrushResize(new Point(), new Point(8, 8), System.Windows.Input.MouseButton.Left, false));
            Assert(window.BeginBrushResize(new Point(100, 100), new Point(8, 8), System.Windows.Input.MouseButton.Left, true));
            window.MoveBrushResize(new Point(120, 100)); Assert(window.brushSize == 82 && window.canvas.BrushRadius == 41);
            window.EndBrushResize(false);
            Assert(ReferenceEquals(pixels, window.doc.Active.Pixels) && window.history.CanRedo && !window.history.Dirty(window.doc));
            Assert(window.canvas.BrushHud == null && !window.dragging && window.stroke == null);
        });
        Case("resize clamps both directions and cancel restores original diameter", () =>
        {
            window.tool = Tool.Eraser;
            window.BeginBrushResize(new Point(), new Point(8, 8), System.Windows.Input.MouseButton.Right, true);
            window.MoveBrushResize(new Point(-2000, 0)); Assert(window.brushSize == 1);
            window.MoveBrushResize(new Point(2000, 0)); Assert(window.brushSize == MaxBrushSize);
            window.CancelGesture(); Assert(window.brushSize == 42 && !window.resizingBrush && window.canvas.BrushHud == null);
        });
        Case("clone Alt click samples but Alt drag preserves existing source", () =>
        {
            foreach (var t in new[] { Tool.CloneStamp, Tool.Heal })
            {
                window.tool = t; window.BeginBrushResize(new Point(), new Point(3, 5), System.Windows.Input.MouseButton.Left, true);
                window.MoveBrushResize(new Point(2, 1)); window.EndBrushResize(false);
                Assert(window.cloneAnchorLocal == new Point(3, 5));
                window.BeginBrushResize(new Point(), new Point(9, 9), System.Windows.Input.MouseButton.Left, true);
                window.MoveBrushResize(new Point(20, 0)); window.EndBrushResize(false);
                Assert(window.cloneAnchorLocal == new Point(3, 5) && !window.history.Dirty(window.doc));
            }
        });
        Case("tool changes cancel resizing and selections retain Alt semantics", () =>
        {
            window.tool = Tool.RectangleSelect; Assert(!window.BeginBrushResize(new Point(), new Point(), System.Windows.Input.MouseButton.Left, true));
            window.tool = Tool.BlurBrush; window.BeginBrushResize(new Point(), new Point(), System.Windows.Input.MouseButton.Left, true);
            window.MoveBrushResize(new Point(30, 0)); window.SetTool(Tool.Move);
            Assert(window.brushSize == 42 && !window.resizingBrush && window.canvas.BrushHud == null);
            Assert(window.brushOptions.Visibility == Visibility.Collapsed);
        });
        Case("foreground background swap reset and selected fill preserve alpha", () =>
        {
            var color = Color.FromArgb(128, 40, 80, 120); window.backgroundColor = color; window.SwapColors();
            Assert(window.foreground == color && window.backgroundColor == Colors.Red);
            window.SwapColors(); window.selection = new Selection(new Rect(0, 0, 1, 1)); window.FillBackground();
            var p = window.doc.Active!.Pixels.Data; Assert(p[0] == 120 && p[1] == 80 && p[2] == 40 && p[3] == 128 && p[7] == 0);
            window.Undo(); Assert(!window.history.Dirty(window.doc)); window.ResetColors();
            Assert(window.foreground == Colors.Black && window.backgroundColor == Colors.White);
        });
        Case("two color gradient interpolates premultiplied alpha and retains selection", () =>
        {
            window.foreground = Colors.Red; window.backgroundColor = Color.FromArgb(128, 0, 0, 255); window.brushOpacity = 1; window.gradientToBackground = true;
            window.selection = new Selection(new Rect(0, 0, 16, 1)); window.AddGradient(new Point(.5, .5), new Point(14.5, .5));
            var p = window.doc.Active!.Pixels.Data; Assert(p[2] == 255 && p[3] == 255 && p[14 * 4] == 255 && p[14 * 4 + 3] == 128);
            Assert(p[7 * 4 + 2] > p[7 * 4] && p[7 * 4 + 3] is >= 191 and <= 192 && p[16 * 4 + 3] == 0);
        });
        Case("color picker HSV conversion retains RGB and alpha across gamut", () =>
        {
            foreach (byte r in new byte[] { 0, 37, 128, 255 }) foreach (byte g in new byte[] { 0, 73, 200, 255 }) foreach (byte b in new byte[] { 0, 49, 175, 255 })
            {
                var initial = Color.FromArgb(91, r, g, b); var hsv = ColorValues.ToHsv(initial); var converted = ColorValues.FromHsv(hsv.H, hsv.S, hsv.V, initial.A);
                Assert(Math.Abs(converted.R - r) <= 1 && Math.Abs(converted.G - g) <= 1 && Math.Abs(converted.B - b) <= 1 && converted.A == 91);
            }
        });

        Case("group duplicate subtree delete and undo preserve structure", () =>
        {
            foreach (var layer in window.doc.Layers) window.selectedLayers.Add(layer.Id);
            window.GroupSelected(); var originalGroup = window.doc.ActiveId;
            Assert(window.doc.Layers.Count == 3 && window.doc.Active!.Kind == LayerKind.Group);
            Assert(window.doc.Layers.Count(l => l.ParentId == originalGroup) == 2);
            window.Duplicate(); var copyGroup = window.doc.ActiveId;
            Assert(copyGroup != originalGroup && window.doc.Layers.Count == 6 && window.doc.Layers.Count(l => l.ParentId == copyGroup) == 2);
            Assert(window.doc.Layers.Select(l => l.Id).Distinct().Count() == 6);
            window.DeleteLayer(); Assert(window.doc.Layers.Count == 3 && window.doc.Layers.All(l => l.ParentId != copyGroup));
            window.Undo(); Assert(window.doc.Layers.Count == 6); window.Undo(); Assert(window.doc.Layers.Count == 3);
            window.Undo(); Assert(window.doc.Layers.Count == 2 && window.doc.Layers.All(l => l.ParentId == null));
            window.Redo(); Assert(window.doc.Layers.Count == 3); window.doc.Validate();
        });
        Case("empty group command inserts group and remains undoable", () =>
        {
            window.doc.ActiveId = Guid.Empty; window.selectedLayers.Clear(); window.GroupSelected();
            Assert(window.doc.Layers.Count == 3 && window.doc.Active!.Kind == LayerKind.Group);
            window.Undo(); Assert(window.doc.Layers.Count == 2);
        });
        Case("tabs retain independent document history selection and zoom", () =>
        {
            window.selection = new Selection(new Rect(0, 0, 8, 16)); window.Fill();
            var firstPixels = (byte[])window.doc.Active!.Pixels.Data.Clone(); var firstSelection = window.selection; window.canvas.Zoom = .75;
            var next = new Document { Width = 8, Height = 8, Name = "두 번째" }; next.Add(new Layer { Pixels = Raster.Solid(8, 8, Colors.Green) });
            window.AddTab(next, null); Assert(window.tabs.Count == 2 && !window.history.Dirty(window.doc));
            window.NudgeSelected(new Vector(2, 3)); Assert(window.doc.Active!.X == 2 && window.history.Dirty(window.doc));
            window.SwitchTab(0); Assert(window.doc.Active!.Pixels.Data.SequenceEqual(firstPixels) && ReferenceEquals(window.selection, firstSelection));
            Assert(window.history.Dirty(window.doc) && window.canvas.Zoom == .75); window.Undo(); Assert(!window.history.Dirty(window.doc));
            window.SwitchTab(1); Assert(window.doc.Active!.X == 2 && window.doc.Active.Y == 3); window.Undo(); Assert(window.doc.Active!.X == 0 && !window.history.Dirty(window.doc));
        });
        Case("clipping command masks to base alpha and undoes", () =>
        {
            var lower = window.doc.Layers[0]; lower.Pixels = new Raster(16, 16); lower.Pixels.Data[3] = 255; lower.Pixels.Data[0] = 255;
            window.doc.Active!.Pixels = Raster.Solid(16, 16, Colors.Red);
            window.ToggleClipping(); var result = Imaging.Render(window.doc);
            Assert(window.doc.Active!.Clipped && result.Data[2] == 255 && result.Data[3] == 255 && result.Data[7] == 0);
            window.Undo(); Assert(!window.doc.Active!.Clipped && Imaging.Render(window.doc).Data[7] == 255);
        });
        Case("soft fill clear mask and undo retain coverage", () =>
        {
            window.doc.Active!.Pixels = Raster.Solid(16, 16, Colors.Blue);
            var coverage = new byte[256]; coverage[0] = 128; window.selection = SelectionTools.FromMask(coverage, 16, 16);
            var original = window.doc.Active.Pixels; window.Fill(); var filled = window.doc.Active.Pixels;
            Assert(filled.Data[0] == 127 && filled.Data[2] == 128 && filled.Data[3] == 255 && filled.Data[4] == 255);
            Assert(original.Data[0] == 255 && original.Data[2] == 0);
            window.ClearPixels(); Assert(window.doc.Active.Pixels.Data[3] == 127 && window.doc.Active.Pixels.Data[7] == 255);
            window.Undo(); Assert(window.doc.Active!.Pixels.Data.SequenceEqual(filled.Data));
            window.selection = SelectionTools.FromMask(coverage, 16, 16);
            window.AddMask(); Assert(window.doc.Active.Mask![0] == 128 && window.doc.Active.Mask[1] == 0 && window.maskEditing);
            var beforeMask = window.doc.Active.Mask; window.foreground = Colors.Black; window.Fill();
            Assert(window.doc.Active.Mask![0] == 64 && beforeMask[0] == 128 && window.doc.Active.Pixels.Data.SequenceEqual(filled.Data));
            window.Undo(); Assert(window.doc.Active!.Mask![0] == 128);
        });
        Case("multilayer move threshold cancel and document nudge", () =>
        {
            foreach (var layer in window.doc.Layers) window.selectedLayers.Add(layer.Id);
            window.beforeGesture = window.doc.Snapshot(); window.dragging = true; window.start = new Point(4, 4); window.screenStart = new Point(0, 0);
            window.ContinueMove(new Point(5, 4), new Point(1, 0)); Assert(window.doc.Layers.All(l => l.X == 0));
            window.ContinueMove(new Point(16, 4), new Point(12, 0)); Assert(window.doc.Layers.All(l => l.X == 12));
            window.CancelGesture(); Assert(window.doc.Layers.All(l => l.X == 0) && !window.history.Dirty(window.doc));
            window.NudgeSelected(new Vector(5, 2)); Assert(window.doc.Layers.All(l => l.X == 5 && l.Y == 2));
            window.Undo(); Assert(window.doc.Layers.All(l => l.X == 0 && l.Y == 0));
        });
        Case("selected group child moves once and parent-space motion maps", () =>
        {
            var child = window.doc.Active!; var group = DocumentFeatures.CreateGroup(window.doc); window.doc.Add(group); child.ParentId = group.Id;
            window.doc.ActiveId = child.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(group.Id); window.selectedLayers.Add(child.Id);
            window.NudgeSelected(new Vector(4, 3)); Assert(group.X == 4 && group.Y == 3 && child.X == 0 && child.Y == 0);
            window.Undo(); child = window.doc.Layers.Single(l => l.Id == child.Id); group = window.doc.Layers.Single(l => l.Id == group.Id);
            group.Scale = 2; window.doc.ActiveId = child.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(child.Id);
            window.NudgeSelected(new Vector(4, 2)); Assert(child.X == 2 && child.Y == 1);
            Assert(!window.CanPaintActiveLayer(), "Painting inside a transformed parent must be guarded");
        });
        Case("gesture cancel restores pixels and clears all previews", () =>
        {
            var original = window.doc.Active!.Pixels; window.beforeGesture = window.doc.Snapshot(); window.dragging = true;
            window.doc.Active.Pixels = Raster.Solid(16, 16, Colors.White); window.lassoPoints.Add(new Point(1, 1)); window.polygonInProgress = true;
            window.canvas.GesturePoints = [new Point(1, 1), new Point(2, 2)]; window.canvas.GestureBounds = new Rect(1, 1, 2, 2);
            window.CancelGesture(); Assert(ReferenceEquals(window.doc.Active!.Pixels, original) && !window.history.CanUndo);
            Assert(!window.polygonInProgress && window.lassoPoints.Count == 0 && window.canvas.GesturePoints == null && window.canvas.GestureBounds == null);
        });
        Case("gradient and shape commands respect soft selection and alpha", () =>
        {
            var coverage = Enumerable.Repeat((byte)128, 256).ToArray(); window.selection = SelectionTools.FromMask(coverage, 16, 16);
            window.foreground = Color.FromArgb(128, 255, 0, 0); window.brushOpacity = 1;
            window.AddGradient(new Point(.5, .5), new Point(15.5, .5)); Assert(window.doc.Active!.Pixels.Data[3] == 64 && window.doc.Active.Pixels.Data[15 * 4 + 3] == 0);
            window.AddShape(new Rect(2, 2, 4, 4), false);
            var shape = window.doc.Active!; var shapeOnly = new Raster(window.doc.Width, window.doc.Height); Imaging.Composite(shapeOnly, shape);
            Assert(shapeOnly.Data[(2 * window.doc.Width + 2) * 4 + 3] == 64, "Shape coverage must combine foreground alpha and the soft selection once");
            Assert(shape.Kind == LayerKind.Shape && shape.Pixels.Data[3] == 128 && shape.Mask![0] == 128, "Editable shapes retain the source fill alpha and a separate selection mask");
            window.Undo(); Assert(window.doc.Active!.Name == "그라데이션");
        });
        Case("editable types groups save reopen and saved undo lifecycle", () =>
        {
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "A", FontSize = 8, ColorArgb = 0xFFFFFFFF }, 2, 2);
            window.Edit("텍스트 추가", () => window.doc.Add(text));
            var adjustment = DocumentFeatures.CreateAdjustment(window.doc, new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = .2 });
            window.Edit("조정 추가", () => window.doc.Add(adjustment));
            window.selectedLayers.Clear(); foreach (var layer in window.doc.Layers) window.selectedLayers.Add(layer.Id); window.GroupSelected();
            var expected = Imaging.Render(window.doc); string path = Path.Combine(directory, "command-lifecycle.moruproj"); ProjectStore.Save(window.doc, path); window.history.MarkSaved(window.doc);
            var savedRevision = window.doc.Revision; window.NudgeSelected(new Vector(2, 1)); Assert(window.history.Dirty(window.doc));
            window.Undo(); Assert(window.doc.Revision == savedRevision && !window.history.Dirty(window.doc) && Imaging.Render(window.doc).Data.SequenceEqual(expected.Data));
            window.Redo(); Assert(window.history.Dirty(window.doc));
            window.OpenProject(path); Assert(window.tabs.Count == 2 && !window.history.CanUndo && !window.history.Dirty(window.doc));
            Assert(window.doc.Layers.Any(l => l.Kind == LayerKind.Text && l.Text!.Content == "A") && window.doc.Layers.Any(l => l.Kind == LayerKind.Adjustment) && window.doc.Layers.Any(l => l.Kind == LayerKind.Group));
            Assert(Imaging.Render(window.doc).Data.SequenceEqual(expected.Data)); window.doc.Validate();
        });
        Case("duplicate project opens existing dirty tab and Save As cannot overwrite another tab", () =>
        {
            string path = Path.Combine(directory, "tab-conflict.moruproj"); ProjectStore.Save(window.doc, path);
            window.OpenProject(path); int owner = window.activeTab; var opened = window.doc;
            window.NudgeSelected(new Vector(3, 0)); var revision = window.doc.Revision;
            window.SwitchTab(0); window.OpenProject(Path.Combine(directory, ".", "tab-conflict.moruproj"));
            Assert(window.tabs.Count == 2 && window.activeTab == owner && ReferenceEquals(window.doc, opened));
            Assert(window.doc.Revision == revision && window.history.Dirty(window.doc));
            window.SwitchTab(0); bool refused = false;
            try { window.EnsureSavePathAvailable(path); } catch (InvalidOperationException) { refused = true; }
            Assert(refused && ProjectStore.Load(path).Active!.X == 0, "Save As must not overwrite another open document");
        });
        Case("async filters and AI removal honor ancestor lock before starting work", () =>
        {
            var child = window.doc.Active!; var group = DocumentFeatures.CreateGroup(window.doc); group.Locked = true; window.doc.Add(group); child.ParentId = group.Id; window.doc.ActiveId = child.Id;
            bool called = false; var pixels = child.Pixels; var mask = child.Mask;
            window.RunRasterJob("test", (layer, selected, ct) => { called = true; return Raster.Solid(16, 16, Colors.Red); });
            window.RemoveAiBackground();
            Assert(!called && window.jobCts == null && ReferenceEquals(pixels, child.Pixels) && ReferenceEquals(mask, child.Mask));
        });
        window.headlessTesting = true; window.history.MarkSaved(window.doc);
        foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
        window.renderCts?.Cancel(); window.jobCts?.Cancel(); window.Close();
    }
}
