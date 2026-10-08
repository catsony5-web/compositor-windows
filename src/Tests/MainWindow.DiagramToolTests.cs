using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 선 · 곡선 and 지시선 tools in a headless window: drawing, point editing, shortcuts, the options bar,
// the properties panel in both screen styles, and the AI connection commands.
public sealed partial class MainWindow
{
    internal static void RunDiagramToolTests(Action<string, Action> test)
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        static MainWindow Open(bool design = true)
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var board = new Document { Width = 400, Height = 300, Name = "보드" };
            board.Add(new Layer { Name = "종이", Pixels = Raster.Solid(400, 300, Colors.White) });
            w.AddTab(board, null); if (design) w.SetWorkspaceMode(true);
            // A headless canvas has no size; work at 100% so handle and closing distances are real.
            w.foreground = Color.FromRgb(20, 30, 40); w.canvas.Zoom = 1;
            return w;
        }
        static Point Screen(double x) => new(x * 10, 0);
        static IEnumerable<DependencyObject> Tree(DependencyObject root)
        {
            yield return root;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
            if (count == 0 && root is ContentControl { Content: DependencyObject content }) foreach (var child in Tree(content)) yield return child;
            if (count == 0 && root is Panel panel) foreach (UIElement child in panel.Children) foreach (var item in Tree(child)) yield return item;
            if (count == 0 && root is Decorator { Child: { } inner }) foreach (var item in Tree(inner)) yield return item;
        }
        void Window(string name, Action<MainWindow> body, bool design = true) => test("diagram tools: " + name, () =>
        {
            var w = Open(design);
            try { body(w); }
            finally { w.history.MarkSaved(w.doc); w.StopRenderingForShutdown(); }
        });
        static Layer Draw(MainWindow w, params Point[] points)
        {
            w.SelectLineTool(false);
            for (int i = 0; i < points.Length; i++) { w.LineDown(points[i], Screen(i * 3), 1); w.DiagramUp(points[i], Screen(i * 3)); }
            w.DiagramKey(Key.Enter);
            return w.doc.Active!;
        }

        Window("shortcuts, rail, palette and options bar", w =>
        {
            Check(w.ExecuteEditorShortcut(Key.P, ModifierKeys.None) && w.tool == Tool.Line && !w.lineCurve, "P must choose the straight line tool");
            Check(w.ExecuteEditorShortcut(Key.P, ModifierKeys.Shift) && w.tool == Tool.Line && w.lineCurve, "Shift+P must choose curve mode");
            Check(w.lineModeChoice!.Selected && w.diagramOptions.Visibility == Visibility.Visible && w.lineOnlyOptions.All(o => o.Visibility == Visibility.Visible)
                && w.calloutOnlyOptions.All(o => o.Visibility == Visibility.Collapsed), "The line options bar is not shown for the line tool");
            Check(w.ExecuteEditorShortcut(Key.N, ModifierKeys.None) && w.tool == Tool.Callout, "N must choose the callout tool");
            Check(w.calloutOnlyOptions.All(o => o.Visibility == Visibility.Visible) && w.lineOnlyOptions.All(o => o.Visibility == Visibility.Collapsed), "The callout options bar is wrong");
            w.SetTool(Tool.Move); Check(w.diagramOptions.Visibility == Visibility.Collapsed, "Diagram options must hide for other tools");
            var keys = w.toolShortcuts.Values.Select(v => v.Key).ToArray();
            Check(keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Length, "Two tools share a shortcut: " + string.Join(", ", keys));
            foreach (bool design in new[] { true, false })
                Check(w.CurrentToolGroups(design).SelectMany(g => g.Tools).Count(t => t is Tool.Line or Tool.Callout) == 2, "Diagram tools are missing from a rail");
            var commands = w.BuildCommandRegistry();
            Check(commands.Any(c => c.Id == "tool:Line" && c.Shortcut == "P") && commands.Any(c => c.Id == "tool:Callout" && c.Shortcut == "N") && commands.Any(c => c.Id == "tool:Curve" && c.Shortcut == "Shift+P"), "Ctrl+K entries are missing");
            w.RunCommand(commands.Single(c => c.Id == "tool:Curve"));
            Check(w.tool == Tool.Line && w.lineCurve, "The curve command did not choose curve mode");
            // Options set what the next line gets.
            w.lineDash = StrokeDash.Dotted; w.lineEnd = LineMark.Arrow; w.lineWidth = 3;
            var line = Draw(w, new(40, 40), new(200, 60));
            Check(line.Shape is { Kind: ShapeKind.Line, Smooth: false, Dash: StrokeDash.Dotted, EndMark: LineMark.Arrow, StrokeWidth: 3 }, "Tool options did not reach the new line");
            Check(w.DiagramCursor(new Point(300, 250)) == Cursors.Cross, "The line tool shows a cross cursor");
        });

        Window("clicks draw lines, curves and closed shapes as single undo steps", w =>
        {
            int layers = w.doc.Layers.Count;
            var line = Draw(w, new(40, 40), new(120, 90), new(200, 40));
            Check(w.doc.Layers.Count == layers + 1 && line.Shape is { Kind: ShapeKind.Line, Points.Count: 3 } && w.history.UndoLabel == "선 그리기", "Three clicks and Enter must make one polyline");
            var at = line.Shape!.Points!.Select(line.Document).ToArray();
            Check((at[1] - new Point(120, 90)).Length < 1e-6, "A clicked point moved");
            Check(w.selectedLayers.SetEquals([line.Id]) && w.doc.ActiveId == line.Id, "The new line is not selected");
            // Drag: one straight segment, in curve mode a curve layer.
            w.SelectLineTool(true);
            w.LineDown(new(50, 200), Screen(0), 1); w.DiagramMove(new(250, 260), Screen(30)); w.DiagramUp(new(250, 260), Screen(30));
            Check(w.doc.Active!.Shape is { Smooth: true, Points.Count: 2 } && w.history.UndoLabel == "곡선 그리기", "A drag must draw one line");
            // Double-click finishes; a click on the first point closes.
            w.SelectLineTool(false);
            w.LineDown(new(300, 100), Screen(0), 1); w.DiagramUp(new(300, 100), Screen(0));
            w.LineDown(new(360, 100), Screen(3), 1); w.DiagramUp(new(360, 100), Screen(3));
            w.LineDown(new(360, 100), Screen(3), 2);
            Check(!w.lineDrawing && w.doc.Active!.Shape is { Points.Count: 2, Closed: false }, "Double-click must finish the line");
            foreach (var p in new Point[] { new(250, 150), new(330, 150), new(330, 220) }) { w.LineDown(p, Screen(p.X), 1); w.DiagramUp(p, Screen(p.X)); }
            w.LineDown(new(250.5, 150.5), Screen(1), 1);
            Check(w.doc.Active!.Shape is { Closed: true, Points.Count: 3 } && w.doc.Active.Name == Loc.T("닫힌 선"), "A click on the first point must close the line");
            // Backspace drops the last point; a single point and Enter makes nothing.
            int count = w.doc.Layers.Count;
            w.LineDown(new(10, 10), Screen(0), 1); w.DiagramUp(new(10, 10), Screen(0)); w.LineDown(new(60, 10), Screen(5), 1); w.DiagramUp(new(60, 10), Screen(5));
            Check(w.DiagramKey(Key.Back) && w.lineDraft.Count == 1, "Backspace must remove the last point");
            w.DiagramKey(Key.Enter);
            Check(w.doc.Layers.Count == count && !w.lineDrawing, "One point must not make a line");
            w.LineDown(new(10, 10), Screen(0), 1); w.DiagramUp(new(10, 10), Screen(0));
            w.CancelGesture(); w.ResetInteractionTransient();
            Check(!w.lineDrawing && w.lineDraft.Count == 0, "Esc must drop the line being drawn");
            w.Undo(); w.Undo(); w.Undo(); w.Undo();
            Check(w.doc.Layers.Count == layers, "Each drawn line must be one undo step");
        });

        Window("handles move, add and delete points; Esc puts a drag back", w =>
        {
            var line = Draw(w, new(40, 40), new(120, 90), new(200, 40));
            w.SetTool(Tool.Move);
            Check(w.canvas.DiagramHandles is { Count: 3 }, "The selected line must show its three handles");
            Check(w.DiagramCursor(new Point(120, 90)) == Cursors.SizeAll, "A handle must show the move cursor");
            Check(w.TryBeginPointDrag(new Point(121, 91), Screen(0), false), "Pressing a handle must begin a point drag");
            w.ContinuePointDrag(new Point(150, 160), Screen(40));
            Check(!line.Visible && w.canvas.DiagramPreview != null, "The edited line must preview over the canvas");
            w.EndPointDrag();
            var at = w.doc.Active!.Shape!.Points!.Select(w.doc.Active.Document).ToArray();
            Check(w.doc.Active.Visible && (at[1] - new Point(150, 160)).Length < 1e-6 && (at[0] - new Point(40, 40)).Length < 1e-6 && w.history.UndoLabel == "선 점 이동", "The handle drag did not move only its point");
            // A click on a handle selects it without an edit.
            var revision = w.doc.Revision;
            w.TryBeginPointDrag(new Point(40, 40), Screen(0), false); w.EndPointDrag();
            Check(w.doc.Revision == revision && w.selectedShapePoint?.Index == 0, "A handle click must only select the point");
            Check(w.DiagramKey(Key.Delete) && w.doc.Active.Shape!.Points!.Count == 2 && w.history.UndoLabel == "선 점 삭제", "Delete must remove the selected point");
            Check(!w.DiagramKey(Key.Delete), "Without a selected point Delete is not a point command");
            // Double-click on the line adds a point there.
            var a = w.doc.Active.Document(w.doc.Active.Shape!.Points![0]); var b = w.doc.Active.Document(w.doc.Active.Shape.Points[1]);
            var middle = a + (b - a) / 2;
            Check(w.TryInsertShapePoint(w.doc.Active, middle) && w.doc.Active.Shape!.Points!.Count == 3 && w.history.UndoLabel == "선 점 추가", "Double-click on the line must add a point");
            Check(!w.TryInsertShapePoint(w.doc.Active, new Point(390, 290)), "A double-click away from the line must not add a point");
            // Alt+click deletes; a two-point line refuses to lose more.
            Check(w.TryBeginPointDrag(middle, Screen(0), true) && w.doc.Active.Shape!.Points!.Count == 2, "Alt+click must delete the point");
            w.TryBeginPointDrag(a, Screen(0), true);
            Check(w.doc.Active.Shape!.Points!.Count == 2 && w.status.Text.Length > 0, "A line must keep two points");
            // Esc during a drag restores everything.
            var before = w.doc.Active.Shape;
            w.TryBeginPointDrag(a, Screen(0), false); w.ContinuePointDrag(new Point(10, 280), Screen(50)); w.CancelGesture();
            Check(w.doc.Active!.Shape == before && w.doc.Active.Visible && w.pointDrag == null, "Esc must put the drag back");
            // Locked lines show no handles.
            w.doc.Active.Locked = true; w.Refresh(false);
            Check(w.canvas.DiagramHandles == null && !w.TryBeginPointDrag(a, Screen(0), false), "A locked line must not be edited");
        });

        Window("callout tool drags from target to label; each handle moves on its own", w =>
        {
            w.SetTool(Tool.Callout);
            w.CalloutDown(new Point(100, 220), Screen(0)); w.DiagramMove(new Point(220, 120), Screen(30)); w.DiagramUp(new Point(220, 120), Screen(30));
            var callout = w.doc.Active!;
            Check(callout.Shape is { Kind: ShapeKind.Callout, Leader: CalloutLeader.Elbow, StartMark: LineMark.Dot } && callout.Shape.Label!.Content == Loc.T("라벨") && w.history.UndoLabel == "지시선 추가", "The callout was not created");
            var points = callout.Shape!.Points!.Select(callout.Document).ToArray();
            Check((points[0] - new Point(100, 220)).Length < 1e-6 && (points[1] - new Point(100, 120)).Length < 1e-6 && (points[2] - new Point(220, 120)).Length < 1e-6, "Target, bend or label point is wrong");
            Check(w.canvas.DiagramHandles is { Count: 3 } handles && handles.Select(h => h.Kind).SequenceEqual([DiagramHandleKind.Anchor, DiagramHandleKind.Elbow, DiagramHandleKind.Label]), "Callout handles");
            Check(w.calloutLabelPanel != null, "The label text rows are missing");
            // Label handle: the bend's height follows.
            w.TryBeginPointDrag(new Point(220, 120), Screen(0), false); w.ContinuePointDrag(new Point(260, 80), Screen(40)); w.EndPointDrag();
            points = w.doc.Active!.Shape!.Points!.Select(w.doc.Active.Document).ToArray();
            Check((points[2] - new Point(260, 80)).Length < 1e-6 && Math.Abs(points[1].Y - 80) < 1e-6 && Math.Abs(points[1].X - 100) < 1e-6 && (points[0] - new Point(100, 220)).Length < 1e-6, "The label move broke the leader");
            // Target handle alone.
            w.TryBeginPointDrag(new Point(100, 220), Screen(0), false); w.ContinuePointDrag(new Point(80, 250), Screen(40)); w.EndPointDrag();
            points = w.doc.Active!.Shape!.Points!.Select(w.doc.Active.Document).ToArray();
            Check((points[0] - new Point(80, 250)).Length < 1e-6 && (points[2] - new Point(260, 80)).Length < 1e-6, "The target move changed the label");
            Check(w.history.UndoLabel == "지시선 편집", "Callout edits are labeled");
            // A click without a drag puts the label at a default offset.
            w.SetTool(Tool.Callout); int count = w.doc.Layers.Count;
            w.CalloutDown(new Point(300, 250), Screen(0)); w.DiagramUp(new Point(300, 250), Screen(0));
            Check(w.doc.Layers.Count == count + 1 && w.doc.Active!.Shape!.Points![2] != w.doc.Active.Shape.Points[0], "A click must make a callout with its label beside the target");
            // The text tool edits a callout's label instead of adding text.
            w.SetTool(Tool.Text); count = w.doc.Layers.Count;
            var labelCenter = w.doc.Active.Document(new Point(ShapeGeometry.LabelRect(w.doc.Active.Shape).X + 4, ShapeGeometry.LabelRect(w.doc.Active.Shape).Y + 4));
            w.EditTextAt(labelCenter);
            Check(w.doc.Layers.Count == count && w.calloutLabelPanel != null, "The text tool added text over a callout label");
            // The label text rows apply to the callout.
            w.calloutLabelPanel!.EditorForTesting.Text = "로비\nLobby";
            Check(w.calloutLabelPanel.TryApply() && w.doc.Active!.Shape!.Label!.Content == "로비\nLobby" && w.history.UndoLabel == "지시선 라벨", "Label text did not apply");
        });

        Window("properties panel edits line, callout and rectangle strokes in both screen styles", w =>
        {
            var line = Draw(w, new(40, 40), new(200, 120), new(300, 60));
            foreach (bool compact in new[] { false, true })
            {
                w.SetScreenStyle(compact); w.SelectLayer(line.Id); w.BuildProperties();
                var tree = Tree(w.properties).ToArray();
                var dash = tree.OfType<SegmentedChoice<StrokeDash>>().Single();
                var marks = tree.OfType<ComboBox>().Where(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is LineMark)).ToArray();
                Check(marks.Length == 2 && tree.OfType<ComboBox>().Any(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is StrokeCap)) && tree.OfType<SegmentedChoice<bool>>().Any(), $"Line rows are missing (compact {compact})");
                dash.Select(compact ? StrokeDash.DashDot : StrokeDash.Dashed);
                Check(w.doc.Active!.Shape!.Dash == (compact ? StrokeDash.DashDot : StrokeDash.Dashed) && w.history.UndoLabel == "선 모양", "Dash choice did not apply");
                tree = Tree(w.properties).ToArray();
                tree.OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is LineMark)).SelectedIndex = 1;
                Check(w.doc.Active!.Shape!.StartMark == LineMark.Arrow, "Start mark did not apply");
                line = w.doc.Active;
            }
            w.SetScreenStyle(false);
            // Curve switch and direction.
            w.SelectLayer(line.Id); w.BuildProperties();
            Tree(w.properties).OfType<SegmentedChoice<bool>>().First().Select(true);
            Check(w.doc.Active!.Shape!.Smooth && w.history.UndoLabel == "곡선으로 바꾸기", "Curve switch did not apply");
            var first = w.doc.Active.Document(w.doc.Active.Shape.Points![0]);
            w.EditLayer("선 방향 바꾸기", l => VectorShapes.Update(l, DiagramEditing.Reverse(l.Shape!)));
            Check(w.doc.Active!.Shape!.EndMark == LineMark.Arrow && (w.doc.Active.Document(w.doc.Active.Shape.Points![^1]) - first).Length < 1e-6, "Reverse did not swap the ends");
            // Callout rows include the text rows; a rectangle gains the dash rows.
            w.SetTool(Tool.Callout); w.CalloutDown(new(100, 250), Screen(0)); w.DiagramMove(new(200, 200), Screen(30)); w.DiagramUp(new(200, 200), Screen(30));
            foreach (bool compact in new[] { false, true })
            {
                w.SetScreenStyle(compact); w.BuildProperties();
                var tree = Tree(w.properties).ToArray();
                Check(tree.OfType<TextPropertiesPanel>().Count() == 1 && tree.OfType<SegmentedChoice<CalloutLeader>>().Count() == 1, $"Callout rows are missing (compact {compact})");
                tree.OfType<SegmentedChoice<CalloutLeader>>().Single().Select(compact ? CalloutLeader.Elbow : CalloutLeader.Straight);
                Check(w.doc.Active!.Shape!.Leader == (compact ? CalloutLeader.Elbow : CalloutLeader.Straight), "Leader choice did not apply");
            }
            w.SetScreenStyle(false);
            w.SetTool(Tool.Rectangle);
            var box = VectorShapes.Create(new ShapeSpec { Width = 60, Height = 40, StrokeEnabled = true, StrokeWidth = 2 }, 20, 200);
            w.Edit("도형", () => w.doc.Add(box)); w.SelectLayer(box.Id); w.BuildProperties();
            Tree(w.properties).OfType<SegmentedChoice<StrokeDash>>().Single().Select(StrokeDash.Dotted);
            Check(w.doc.Active!.Shape!.Dash == StrokeDash.Dotted && ShapeGeometry.UsesDrawing(w.doc.Active.Shape), "A rectangle outline did not become dotted");
        });

        // The AI connection: add_shape lines and curves, add_callout and update_shape (also in apply_batch).
        test("diagram automation: lines, curves and callouts through add_shape, add_callout and update_shape", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try
            {
                JsonObject Call(string command, JsonObject? arguments = null)
                {
                    var task = w.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments ?? new JsonObject() }, default);
                    var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Send, w.Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
                    timer.Tick += (_, _) => frame.Continue = false;
                    _ = task.ContinueWith(_ => w.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)), TaskScheduler.Default);
                    if (!task.IsCompleted) { timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); }
                    return task.GetAwaiter().GetResult();
                }
                JsonObject Ok(JsonObject reply) { Check(reply["ok"]?.GetValue<bool>() == true, "Command failed: " + reply.ToJsonString()); return reply["result"]!.AsObject(); }
                string Code(JsonObject reply) { Check(reply["ok"]?.GetValue<bool>() == false, "Command was accepted: " + reply.ToJsonString()); return reply["error"]!["code"]!.GetValue<string>(); }
                JsonObject Write(params (string Key, JsonNode? Value)[] values)
                {
                    var state = Ok(Call("get_state", new JsonObject { ["includeLayers"] = false }));
                    var documentId = state["activeDocumentId"]!.GetValue<string>();
                    var document = state["documents"]!.AsArray().OfType<JsonObject>().Single(d => d["documentId"]!.GetValue<string>() == documentId);
                    var args = new JsonObject { ["documentId"] = documentId, ["expectedRevision"] = document["revision"]!.GetValue<string>(), ["includeLayers"] = false };
                    foreach (var (key, value) in values) args[key] = value;
                    return args;
                }
                static JsonArray Points(params (double X, double Y)[] points) => new(points.Select(p => (JsonNode?)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray());
                Ok(Call("new_document", new JsonObject { ["name"] = "다이어그램", ["width"] = 400, ["height"] = 300, ["background"] = "#FFFFFF" }));
                var capabilities = Ok(Call("get_capabilities"));
                Check(capabilities["commands"]!.AsArray().Count == 41 && capabilities["batch"]!["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "add_callout")
                    && capabilities["batch"]!["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "update_shape"), "Capabilities do not list the diagram commands");

                var lineId = Guid.Parse(Ok(Call("add_shape", Write(("shape", "curve"), ("points", Points((40, 200), (140, 60), (260, 150))), ("stroke", "#C0392B"), ("strokeWidth", 3),
                    ("dash", "dashed"), ("dashScale", 1.5), ("endMark", "arrow"), ("startMark", "dot"), ("name", "동선"))))["layerId"]!.GetValue<string>());
                var line = w.doc.Layers.Single(l => l.Id == lineId);
                Check(line.Shape is { Kind: ShapeKind.Line, Smooth: true, Dash: StrokeDash.Dashed, DashScale: 1.5, StartMark: LineMark.Dot, EndMark: LineMark.Arrow, StrokeWidth: 3 } && line.Name == "동선", "add_shape curve fields");
                Check((line.Document(line.Shape!.Points![1]) - new Point(140, 60)).Length < 1e-6, "add_shape points are document pixels");
                var documentId = Ok(Call("get_state", new JsonObject { ["includeLayers"] = false }))["activeDocumentId"]!.GetValue<string>();
                var layerInfo = Ok(Call("get_layer", new JsonObject { ["documentId"] = documentId, ["layerId"] = lineId.ToString() }))["layer"]!;
                Check(layerInfo["documentPoints"]!.AsArray().Count == 3 && Math.Abs(layerInfo["documentPoints"]![2]!["x"]!.GetValue<double>() - 260) < 1e-6
                    && layerInfo["editing"]!["shapeDefinitionViaMcp"]!.GetValue<bool>(), "get_layer must report the canvas points and allow shape edits");
                Ok(Call("update_shape", Write(("layerId", lineId.ToString()), ("points", Points((40, 200), (140, 60), (260, 150), (360, 100))), ("curve", false), ("dash", "dotted"), ("endMark", "open_arrow"))));
                line = w.doc.Layers.Single(l => l.Id == lineId);
                Check(line.Shape is { Smooth: false, Dash: StrokeDash.Dotted, EndMark: LineMark.OpenArrow, Points.Count: 4 } && (line.Document(line.Shape.Points![3]) - new Point(360, 100)).Length < 1e-6, "update_shape line fields");
                Check(Code(Call("add_shape", Write(("shape", "line")))) == "invalid_arguments", "A line without points must be refused");
                Check(Code(Call("add_shape", Write(("shape", "rectangle"), ("width", 10), ("height", 10), ("endMark", "arrow")))) == "invalid_arguments", "A rectangle with end marks must be refused");
                Check(Code(Call("add_shape", Write(("shape", "line"), ("points", Points((0, 0), (5, 5))), ("width", 10)))) == "invalid_arguments", "A line with a width must be refused");
                Check(Code(Call("add_shape", Write(("shape", "line"), ("points", Points((0, 0), (5, 5))), ("closed", true)))) == "invalid_arguments", "A closed two-point line must be refused");
                Check(Code(Call("update_shape", Write(("layerId", lineId.ToString()), ("labelX", 10)))) == "invalid_arguments", "Callout fields on a line must be refused");
                var rectangle = Guid.Parse(Ok(Call("add_shape", Write(("shape", "rectangle"), ("width", 80), ("height", 50), ("x", 10), ("y", 10), ("stroke", "#000000"), ("dash", "dash_dot"))))["layerId"]!.GetValue<string>());
                Check(w.doc.Layers.Single(l => l.Id == rectangle).Shape!.Dash == StrokeDash.DashDot, "A rectangle outline can be dashed");
                Check(Code(Call("update_shape", Write(("layerId", rectangle.ToString()), ("points", Points((0, 0), (5, 5)))))) == "invalid_arguments", "Points on a rectangle must be refused");

                var calloutId = Guid.Parse(Ok(Call("add_callout", Write(("anchorX", 120), ("anchorY", 220), ("labelX", 220), ("labelY", 80), ("text", "주출입구"), ("fontSize", 18), ("dash", "dotted"), ("fill", "#FFFFFF"))))["layerId"]!.GetValue<string>());
                var callout = w.doc.Layers.Single(l => l.Id == calloutId);
                var cp = callout.Shape!.Points!.Select(callout.Document).ToArray();
                Check(callout.Shape is { Kind: ShapeKind.Callout, Leader: CalloutLeader.Elbow, StartMark: LineMark.Dot, Dash: StrokeDash.Dotted, FillEnabled: true } && callout.Shape.Label!.Content == "주출입구" && callout.Shape.Label.FontSize == 18, "add_callout fields");
                Check((cp[1] - new Point(120, 80)).Length < 1e-6 && (cp[2] - new Point(220, 80)).Length < 1e-6, "add_callout default bend");
                Ok(Call("update_shape", Write(("layerId", calloutId.ToString()), ("labelX", 300), ("labelY", 60), ("text", "로비"), ("labelMark", "dot"), ("leader", "elbow"))));
                callout = w.doc.Layers.Single(l => l.Id == calloutId); cp = callout.Shape!.Points!.Select(callout.Document).ToArray();
                Check((cp[2] - new Point(300, 60)).Length < 1e-6 && Math.Abs(cp[1].Y - 60) < 1e-6 && Math.Abs(cp[1].X - 120) < 1e-6 && (cp[0] - new Point(120, 220)).Length < 1e-6
                    && callout.Shape.Label!.Content == "로비" && callout.Shape.EndMark == LineMark.Dot, "update_shape callout fields");
                Check(Code(Call("update_shape", Write(("layerId", calloutId.ToString()), ("points", Points((0, 0), (5, 5)))))) == "invalid_arguments", "Line points on a callout must be refused");
                Check(Code(Call("add_callout", Write(("anchorX", 1), ("anchorY", 1), ("labelX", 5)))) == "invalid_arguments", "A callout needs its label point and text");

                // One undo step for a batch that draws and edits.
                int layers = w.doc.Layers.Count;
                Ok(Call("apply_batch", Write(("operationId", Guid.NewGuid().ToString()), ("steps", new JsonArray(
                    new JsonObject { ["command"] = "add_shape", ["ref"] = "arrow", ["arguments"] = new JsonObject { ["shape"] = "line", ["points"] = Points((10, 10), (90, 90)), ["endMark"] = "arrow" } },
                    new JsonObject { ["command"] = "update_shape", ["arguments"] = new JsonObject { ["layerId"] = "@arrow", ["dash"] = "dashed", ["strokeWidth"] = 4 } },
                    new JsonObject { ["command"] = "add_callout", ["arguments"] = new JsonObject { ["anchorX"] = 50, ["anchorY"] = 50, ["labelX"] = 120, ["labelY"] = 20, ["text"] = "A" } })))));
                Check(w.doc.Layers.Count == layers + 2 && w.doc.Layers[^2].Shape is { Dash: StrokeDash.Dashed, StrokeWidth: 4 }, "The batch did not draw and edit");
                Ok(Call("undo", Write()));
                Check(w.doc.Layers.Count == layers, "The batch must be one undo step");
            }
            finally
            {
                w.history.MarkSaved(w.doc); foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                w.StopRenderingForShutdown(); w.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

}
