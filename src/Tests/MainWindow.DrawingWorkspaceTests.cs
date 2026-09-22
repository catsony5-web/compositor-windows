using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunDrawingWorkspaceTests(Action<string, Action> test, string directory)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Layer Line()
        {
            var vector = VectorContent.FromPaths(100, 100, [new VectorPrimitive(new LineGeometry(new Point(10, 10), new Point(90, 90)), Colors.Black, false, 2)]);
            return new Layer { Kind = LayerKind.Vector, Vector = vector, Pixels = new Raster(100, 100), Name = "대각선" };
        }
        test("directional object selection tests ink rather than bounding boxes", () =>
        {
            var doc = new Document { Width = 200, Height = 200 }; var line = Line(); doc.Add(line);
            Check(ObjectSelection.Find(doc, new Rect(45, 45, 10, 10), true).SequenceEqual([line.Id]), "Crossing must select the touched line");
            Check(ObjectSelection.Find(doc, new Rect(45, 45, 10, 10), false).Length == 0, "Window must fully enclose a line");
            Check(ObjectSelection.Find(doc, new Rect(5, 5, 90, 90), false).SequenceEqual([line.Id]), "Window enclosing the ink must select it");
            Check(ObjectSelection.Find(doc, new Rect(10, 80, 10, 10), true).Length == 0, "Empty bounding-box corners must not count as ink");
        });
        test("object selection respects hollow shapes hidden parents and locks", () =>
        {
            var doc = new Document { Width = 200, Height = 200 };
            var group = new Layer { Kind = LayerKind.Group, Pixels = new Raster(200, 200) }; doc.Add(group);
            var shape = VectorShapes.Create(new ShapeSpec { Width = 80, Height = 80, FillEnabled = false, StrokeEnabled = true, StrokeWidth = 3 }, 20, 20); shape.ParentId = group.Id; doc.Add(shape);
            Check(ObjectSelection.Find(doc, new Rect(40, 40, 10, 10), true).Length == 0, "Hollow interior selected");
            Check(ObjectSelection.Find(doc, new Rect(19, 40, 5, 10), true).Contains(shape.Id), "Shape edge not selected");
            group.Locked = true; Check(ObjectSelection.Find(doc, new Rect(0, 0, 200, 200), true).Length == 0, "Locked parent selected");
            group.Locked = false; group.Visible = false; Check(ObjectSelection.Find(doc, new Rect(0, 0, 200, 200), true).Length == 0, "Hidden parent selected");
        });
        test("object selection follows nested transforms masks and clips", () =>
        {
            var doc = new Document { Width = 300, Height = 300 };
            var group = new Layer { Kind = LayerKind.Group, Pixels = new Raster(100, 100), X = 50, Y = 30, Scale = 2 }; doc.Add(group);
            var line = Line(); line.ParentId = group.Id; doc.Add(line);
            Check(ObjectSelection.Find(doc, new Rect(140, 120, 20, 20), true).Contains(line.Id), "Parent transform ignored");
            line.Mask = new byte[10000]; Check(ObjectSelection.Find(doc, new Rect(0, 0, 300, 300), true).Length == 0, "Empty mask selected");
            line.Mask = null; line.Clipped = true;
            Check(ObjectSelection.Find(doc, new Rect(0, 0, 300, 300), true).Length == 0, "Orphan clipping layer selected");
            line.Clipped = false; line.X = 200;
            Check(ObjectSelection.Find(doc, new Rect(0, 0, 1000, 1000), true).Length == 0, "Group clipping boundary ignored");
        });
        test("object selection cancellation leaves the source untouched", () =>
        {
            var doc = new Document(); doc.Add(Line()); var before = doc.Snapshot(); using var cts = new CancellationTokenSource(); cts.Cancel();
            try { ObjectSelection.Find(doc, new Rect(0, 0, 100, 100), true, cts.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Check(SameDocument(doc, before), "Selection mutated document");
        });
        test("drawing containers retain objects moved across artboards in render pick and selection", () =>
        {
            var doc = new Document { Width = 100, Height = 100 };
            var shape = VectorShapes.Create(new ShapeSpec { Width = 10, Height = 10, FillArgb = 0xFFFF0000 }); doc.Add(shape); DrawingLayers.Wrap(doc);
            ArtboardEditing.Set(doc, new Artboard(Guid.Empty, "두 번째", 120, 0, 100, 100), true);
            var child = doc.Layers.Single(l => l.Kind == LayerKind.Shape); child.X = 150; child.Y = 20;
            Check(LayerPicking.Pick(doc, new Point(155, 25))?.Id == child.Id, "Drawing container cropped point picking");
            Check(ObjectSelection.Find(doc, new Rect(148, 18, 14, 14), false).Contains(child.Id), "Drawing container cropped window selection");
            var raster = Imaging.Render(doc); Check(raster.Data[(25 * raster.Width + 155) * 4 + 2] == 255, "Moved shape disappeared from composite");
            var output = ArtboardEditing.ExportDocument(doc, doc.Artboards.Last().Id); var rendered = DesignRenderer.RenderOutput(output);
            Check(rendered.Data[(25 * rendered.Width + 35) * 4 + 3] == 255, "Moved shape disappeared from artboard export");
        });
        test("async object selection rejects changed tabs selections and cancelled requests", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true }; var dispatcher = Dispatcher.CurrentDispatcher; var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            bool Complete(Task<bool> task)
            {
                if (!task.IsCompleted)
                {
                    var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default); Dispatcher.PushFrame(frame);
                }
                return task.GetAwaiter().GetResult();
            }
            try
            {
                var doc = new Document(); doc.Add(Line()); window.AddTab(doc, null);
                Check(Complete(window.SelectObjectsAsync(new Rect(0, 0, 100, 100), true, SelectionCombine.Replace)), "Async selection failed");
                Task<bool> BeginPending(out TaskCompletionSource<Guid[]> completion)
                {
                    var source = new TaskCompletionSource<Guid[]>(TaskCreationOptions.RunContinuationsAsynchronously); completion = source;
                    return window.SelectObjectsAsync(new Rect(0, 0, 100, 100), true, SelectionCombine.Replace, (_, _) => source.Task);
                }
                // Hold the worker result so each state change happens before completion,
                // independently of how quickly this machine can scan a single line.
                var pending = BeginPending(out var completion); window.ApplyObjectSelection([], SelectionCombine.Replace); completion.SetResult([doc.Layers[0].Id]);
                Check(!Complete(pending) && window.selectedLayers.Count == 0, "Stale selection overwrote user change");
                pending = BeginPending(out completion); window.SetTool(Tool.Artboard); completion.SetResult([doc.Layers[0].Id]);
                Check(!Complete(pending) && !window.history.Dirty(doc), "Cancelled selection modified artboard session");
                pending = BeginPending(out completion); window.AddTab(new Document(), null); completion.SetResult([doc.Layers[0].Id]);
                Check(!Complete(pending), "Selection entered another tab");
            }
            finally { window.StopRenderingForShutdown(); SynchronizationContext.SetSynchronizationContext(context); }
        });
        Document Drawing()
        {
            var doc = new Document { Width = 100, Height = 100, Name = "테스트 도면" }; var surface = new Raster(100, 100);
            foreach (var name in new[] { "벽", "기둥", "벽" })
            {
                var group = new Layer { Kind = LayerKind.Group, Category = LayerCategory.Drawing, SourceLayerName = name, Name = name, Pixels = surface }; doc.Add(group);
                var child = Line(); child.ParentId = group.Id; child.X = doc.Layers.Count; doc.Add(child);
            }
            DrawingLayers.Wrap(doc); return doc;
        }
        test("drawing layers aggregate source runs without changing paint order", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var doc = Drawing(); var root = doc.Layers.Single(l => l.ParentId == null); var order = doc.Layers.Select(l => l.Id).ToArray();
                window.AddTab(doc, null); Check(window.layerList.Items.Count == 1, "Imported file should start as one collapsed layer");
                window.collapsedGroups.Remove(root.Id); window.BuildLayers();
                var rows = window.layerList.Items.Cast<LayerListEntry>().ToArray();
                Check(rows.Count(e => e.Layer.SourceLayerName == "벽") == 1, "Repeated source layer names must share a row");
                var wall = rows.Single(e => e.Layer.SourceLayerName == "벽"); Check(wall.GroupMembers?.Length == 2, "Lost source runs");
                window.ToggleSourceLayer(wall.GroupMembers!, false); Check(doc.Layers.Where(l => l.SourceLayerName == "벽").All(l => !l.Visible), "Visibility did not update all runs");
                window.Undo(); window.SelectLayer(doc.Layers.First(l => l.Vector != null).Id);
                Check(window.layerList.Items.Count == 3, "Object click unexpectedly expanded every object");
                Check(order.SequenceEqual(window.doc.Layers.Select(l => l.Id)), "Panel grouping changed paint order");
                Check(!window.history.Dirty(window.doc), "Selection or expansion dirtied document");
            }
            finally { window.StopRenderingForShutdown(); }
        });
        test("drawing and photo tabs keep separate entries and preserve individual selection", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var doc = Drawing(); var photo = new Layer { Pixels = Raster.Solid(10, 10, Colors.Red), Name = "사진" }; doc.Add(photo); window.AddTab(doc, null);
                Check(window.layerCategory == LayerCategory.Photo && window.layerList.Items.Count == 1, "Photo tab mixed drawing entries");
                var child = doc.Layers.First(l => l.Vector != null); window.SelectLayer(child.Id);
                Check(window.layerCategory == LayerCategory.Drawing && window.layerList.Items.Count == 1 && doc.ActiveId == child.Id, "Object must remain individually active under a collapsed drawing");
                window.layerCategoryButtons[LayerCategory.Photo].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.layerCategory == LayerCategory.Photo && doc.ActiveId == child.Id && !window.history.Dirty(doc), "Tab switch modified document or selection");
            }
            finally { window.StopRenderingForShutdown(); }
        });
        test("artboard extension preserves vector coordinates undo and native roundtrip", () =>
        {
            var doc = Drawing(); var before = doc.Snapshot(); var root = doc.Layers.First(); var history = new History(); history.Reset(doc);
            var vector = doc.Layers.First(l => l.Vector != null).Vector;
            var result = ArtboardEditing.Set(doc, new Artboard(Guid.Empty, "왼쪽 대지", -140, -20, 100, 100), true); history.Commit("대지", before, doc);
            Check(doc.Width == 240 && doc.Height == 120 && result.Offset == new Vector(140, 20), "Workspace extension incorrect");
            Check(doc.Layers[0].X == root.X + 140 && doc.Artboards.Count == 2, "Original content was not translated with workspace");
            Check(ReferenceEquals(vector, doc.Layers.First(l => l.Vector != null).Vector), "Artboard extension rasterized vector source");
            doc = history.Undo(doc); Check(SameDocument(doc, before), "Undo failed to restore implicit artboard"); doc = history.Redo(doc);
            string path = Path.Combine(directory, "drawing-artboards.moruproj"); ProjectStore.Save(doc, path); var loaded = ProjectStore.Load(path);
            Check(loaded.Artboards.SequenceEqual(doc.Artboards), "Artboards not persisted");
            Check(loaded.Layers.Select(l => (l.Category, l.SourceLayerName)).SequenceEqual(doc.Layers.Select(l => (l.Category, l.SourceLayerName))), "Drawing metadata not persisted");
        });
        test("artboard export crops without flattening or moving source content", () =>
        {
            var doc = new Document { Width = 200, Height = 100 }; var line = Line(); line.X = 100; doc.Add(line);
            var board = new Artboard(Guid.NewGuid(), "오른쪽", 100, 0, 100, 100); doc.Artboards.Add(board);
            var output = ArtboardEditing.ExportDocument(doc, board.Id);
            Check(output.Width == 100 && output.Height == 100 && output.Layers[0].X == 0 && doc.Layers[0].X == 100, "Export crop moved source document");
            Check(output.Layers[0].Vector == line.Vector && output.Artboards.Count == 0, "Export destroyed vector source");
            ArtboardEditing.Crop(doc, new Rect(110, 10, 50, 50)); doc.Width = doc.Height = 50; doc.Validate();
            Check(doc.Artboards.Single().Bounds == new Rect(0, 0, 50, 50), "Canvas crop did not adjust artboards");
        });
        test("artboard validation rejects duplicate ids invalid extents and removal of last board", () =>
        {
            var doc = new Document(); var board = new Artboard(Guid.NewGuid(), "A", 0, 0, 100, 100); doc.Artboards = [board, board];
            try { doc.Validate(); throw new Exception("Duplicate accepted"); } catch (InvalidDataException) { }
            doc.Artboards = [board];
            try { ArtboardEditing.Remove(doc, board.Id); throw new Exception("Last board removed"); } catch (InvalidOperationException) { }
            try { ArtboardEditing.Set(doc, board with { Width = double.NaN }); throw new Exception("NaN accepted"); } catch (InvalidDataException) { }
        });
        test("Shift O artboard gestures commit once and cancel without document edits", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var doc = new Document { Width = 100, Height = 100 }; doc.Add(Line()); window.AddTab(doc, null);
                window.ExecuteEditorShortcut(Key.O, ModifierKeys.Shift);
                window.canvas.Zoom = 1;
                Check(window.tool == Tool.Artboard && !window.history.Dirty(doc), "Entering artboard tool edited document");
                window.BeginArtboard(new Point(150, 0), new Point(150, 0), false);
                window.MoveArtboard(new Point(250, 80), new Point(250, 80)); window.EndArtboard(new Point(250, 80), new Point(250, 80));
                Check(window.doc.Artboards.Count == 2 && window.doc.Artboards.Last().Bounds == new Rect(150, 0, 100, 80), "Drawn artboard bounds wrong");
                window.Undo(); Check(window.doc.Artboards.Count == 0 && !window.history.CanUndo, "Gesture produced multiple undo steps");
                Check(window.history.CanRedo, "Redo missing");
                window.BeginArtboard(new Point(150, 0), new Point(150, 0), false); window.MoveArtboard(new Point(220, 70), new Point(220, 70)); window.CancelGesture();
                Check(window.doc.Artboards.Count == 0 && window.history.CanRedo && window.canvas.ArtboardDraft == null, "Cancelled draft altered history");
                window.Redo(); var board = window.CurrentArtboard;
                window.BeginArtboard(board.Bounds.BottomRight, board.Bounds.BottomRight, false);
                window.EndArtboard(board.Bounds.BottomRight + new Vector(30, 20), board.Bounds.BottomRight + new Vector(30, 20));
                Check(window.CurrentArtboard.Width == board.Width + 30 && window.CurrentArtboard.Height == board.Height + 20, "Resize handle failed");
            }
            finally { window.StopRenderingForShutdown(); }
        });
        test("object multi-selection combines independently of pixel masks and deletes with undo", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var doc = new Document(); var a = Line(); var b = Line(); doc.Add(a); doc.Add(b); window.AddTab(doc, null);
                var pixels = new Selection(new Rect(1, 1, 5, 5)); window.selection = pixels;
                window.ApplyObjectSelection([a.Id], SelectionCombine.Replace); window.ApplyObjectSelection([b.Id], SelectionCombine.Add);
                Check(window.selectedLayers.Count == 2 && window.selection == pixels && !window.history.Dirty(doc), "Object selection altered pixels or history");
                window.ApplyObjectSelection([a.Id], SelectionCombine.Subtract); Check(window.selectedLayers.SetEquals([b.Id]), "Subtract failed");
                window.ApplyObjectSelection([a.Id], SelectionCombine.Intersect); Check(window.selectedLayers.Count == 0, "Intersect failed");
                window.ApplyObjectSelection([a.Id, b.Id], SelectionCombine.Replace); window.DeleteSelectedObjects();
                Check(window.doc.Layers.Count == 0, "Delete missed selected objects"); window.Undo(); Check(window.doc.Layers.Count == 2, "Delete undo lost objects");
            }
            finally { window.StopRenderingForShutdown(); }
        });
    }
}
