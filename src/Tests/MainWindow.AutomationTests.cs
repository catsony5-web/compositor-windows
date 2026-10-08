using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetAutomationTestWindowEnabled(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    public static void RunAutomationCommandTests(Action<string, Action> test, string directory)
    {
        string files = Path.Combine(Path.GetFullPath(directory), "automation-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing string in automation result: " + key);
        static JsonObject DocumentState(JsonObject state, string? id = null)
        {
            id ??= Text(state, "activeDocumentId");
            return state["documents"]!.AsArray().OfType<JsonObject>().Single(item => Text(item, "documentId") == id);
        }
        static JsonObject Arguments(string id, string revision, params (string Key, JsonNode? Value)[] values)
        {
            var arguments = new JsonObject { ["documentId"] = id, ["expectedRevision"] = revision };
            foreach (var (key, value) in values) arguments[key] = value;
            return arguments;
        }
        static JsonObject Success(JsonObject response)
        {
            Check(response["ok"]?.GetValue<bool>() == true, "Automation command failed: " + response.ToJsonString());
            return response["result"]?.AsObject() ?? throw new InvalidOperationException("Successful command has no result object");
        }
        static void Failure(JsonObject response, string? expectedCode = null)
        {
            Check(response["ok"]?.GetValue<bool>() == false, "Invalid command was accepted: " + response.ToJsonString());
            var error = response["error"]?.AsObject() ?? throw new InvalidOperationException("Failure has no error object");
            Check(!string.IsNullOrWhiteSpace(Text(error, "code")) && !string.IsNullOrWhiteSpace(Text(error, "message")), "Failure lacks actionable error details");
            if (expectedCode != null) Check(Text(error, "code") == expectedCode, $"Expected {expectedCode}, received {response.ToJsonString()}");
        }
        static JsonObject FailureError(JsonObject response)
        {
            Failure(response);
            return response["error"]!.AsObject();
        }
        static T Await<T>(Task<T> task)
        {
            if (!task.IsCompleted)
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
                timeout.Tick += (_, _) => frame.Continue = false;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                timeout.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timeout.Stop(); }
                if (!task.IsCompleted) throw new TimeoutException("Automation command did not complete while pumping the STA dispatcher.");
            }
            return task.GetAwaiter().GetResult();
        }
        static JsonObject Call(MainWindow window, string command, JsonObject? arguments = null, CancellationToken token = default)
            => Await(window.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() }, token));
        static JsonObject State(MainWindow window) => Success(Call(window, "get_state"));
        static JsonObject Write(MainWindow window, params (string Key, JsonNode? Value)[] values)
        {
            var current = DocumentState(State(window));
            return Arguments(Text(current, "documentId"), Text(current, "revision"), values);
        }
        static JsonObject New(MainWindow window, string name, int width = 64, int height = 48)
            => Success(Call(window, "new_document", new JsonObject { ["name"] = name, ["width"] = width, ["height"] = height, ["dpi"] = 96, ["background"] = "transparent" }));
        static Guid LayerId(JsonObject result)
        {
            Check(Guid.TryParse(Text(result, "layerId"), out var id) && id != Guid.Empty, "Mutation did not return the created layer identifier");
            return id;
        }
        static void Unchanged(MainWindow window, Document before, bool undo, bool redo)
        {
            Check(window.doc.Revision == before.Revision && window.doc.Layers.Count == before.Layers.Count,
                "Rejected command changed the document revision or layer count");
            Check(window.history.CanUndo == undo && window.history.CanRedo == redo, "Rejected command changed undo/redo availability");
            Check(Imaging.Render(window.doc).Data.SequenceEqual(Imaging.Render(before).Data), "Rejected command changed rendered pixels");
            Check(window.doc.Layers.Select(layer => (layer.Id, layer.Name, layer.X, layer.Y, layer.Kind, layer.Locked))
                .SequenceEqual(before.Layers.Select(layer => (layer.Id, layer.Name, layer.X, layer.Y, layer.Kind, layer.Locked))), "Rejected command partially changed layer properties");
        }
        void Case(string name, Action<MainWindow> action) => test("automation UI: " + name, () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try { action(window); }
            finally
            {
                window.renderCts?.Cancel(); window.jobCts?.Cancel();
                window.history.MarkSaved(window.doc);
                foreach (var tab in window.tabs) tab.History.MarkSaved(tab.Document);
                window.Close();
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        Case("empty state and new document expose usable identifiers without a window", window =>
        {
            var empty = State(window);
            Check(empty["documents"]!.AsArray().Count == 0 && empty["activeDocumentId"] == null && !window.HasDocument,
                "Inspecting an empty workspace created a document or a visible startup sample");
            var created = New(window, "자동화 새 문서", 72, 40);
            var state = State(window); var document = DocumentState(state);
            Check(state["documents"]!.AsArray().Count == 1 && window.tabs.Count == 1 && window.HasDocument, "New document was not registered as the active tab");
            Check(Text(document, "name") == "자동화 새 문서" && document["width"]!.GetValue<int>() == 72 && document["height"]!.GetValue<int>() == 40,
                "State dimensions or document name differ from the requested document");
            Check(Guid.TryParse(Text(document, "documentId"), out var id) && id != Guid.Empty && Guid.TryParse(Text(document, "revision"), out _), "State identifiers cannot be reused by a client");
            Check(Text(created, "documentId") == Text(document, "documentId") && Text(created, "revision") == Text(document, "revision"), "Mutation acknowledgement does not match fresh state");
            Check(document["layers"] is JsonArray && !window.IsVisible, "Layer inventory is absent or automation displayed a window");
        });

        Case("editable text and shape edits participate in real undo and redo", window =>
        {
            New(window, "편집 기록"); int initialLayers = window.doc.Layers.Count; var initialRevision = window.doc.Revision;
            var text = LayerId(Success(Call(window, "add_text", Write(window, ("text", "처음\nText"), ("fontSize", 12),
                ("fontFamily", "Malgun Gothic"), ("color", "#CC2277DD"), ("x", 3), ("y", 4), ("name", "편집할 글자")))));
            var rectangle = LayerId(Success(Call(window, "add_shape", Write(window, ("shape", "rectangle"), ("width", 18), ("height", 12),
                ("x", 20), ("y", 18), ("fill", "#F08030"), ("stroke", "#112233"), ("strokeWidth", 2), ("cornerRadius", 3)))));
            var textLayer = window.doc.Layers.Single(layer => layer.Id == text);
            var shapeLayer = window.doc.Layers.Single(layer => layer.Id == rectangle);
            Check(textLayer.Kind == LayerKind.Text && textLayer.Text?.Content == "처음\nText" && textLayer.Text.ColorArgb == 0xCC2277DD,
                "Text automation rasterized or lost content and alpha");
            Check(shapeLayer.Kind == LayerKind.Shape && shapeLayer.Shape != null && window.history.CanUndo, "Shape automation is not editable or did not record history");
            Success(Call(window, "update_text", Write(window, ("layerId", text.ToString()), ("text", "수정한 제목"), ("bold", true), ("fontSize", 14), ("alignment", "Center"))));
            Check(window.doc.Layers.Single(layer => layer.Id == text).Text is { Content: "수정한 제목", Bold: true }, "Text update did not target the returned layer identifier");
            Success(Call(window, "undo", Write(window)));
            Check(window.doc.Layers.Single(layer => layer.Id == text).Text!.Content == "처음\nText" && window.doc.Layers.Any(layer => layer.Id == rectangle), "Undo did not reverse exactly the latest text edit");
            Success(Call(window, "redo", Write(window)));
            Check(window.doc.Layers.Single(layer => layer.Id == text).Text!.Content == "수정한 제목", "Redo did not restore editable text");
            for (int i = 0; i < 3; i++) Success(Call(window, "undo", Write(window)));
            Check(window.doc.Layers.Count == initialLayers && window.doc.Revision == initialRevision && !window.history.CanUndo,
                "Automation history did not return to the original new-document state");
        });

        Case("unchanged text updates preserve pixel identity revision and the redo branch", window =>
        {
            New(window, "변경 없는 문자 요청");
            string layerId = Text(Success(Call(window, "add_text", Write(window, ("text", "원래 내용"), ("fontSize", 12),
                ("fontFamily", "Malgun Gothic"), ("color", "#804477AA"), ("name", "문자 레이어")))), "layerId");
            Success(Call(window, "update_text", Write(window, ("layerId", layerId), ("text", "다시 실행할 내용"))));
            Success(Call(window, "undo", Write(window)));
            var original = window.doc.Layers.Single(layer => layer.Id.ToString() == layerId);
            var pixels = original.Pixels; var revision = window.doc.Revision;
            bool dirty = window.history.Dirty(window.doc);
            Check(window.history.CanRedo && original.Text!.Content == "원래 내용", "No-op regression fixture has no redo branch");
            var acknowledgement = Success(Call(window, "update_text", Write(window, ("layerId", layerId), ("text", "원래 내용"),
                ("fontSize", 12), ("fontFamily", "Malgun Gothic"), ("color", "#804477AA"), ("bold", false),
                ("italic", false), ("alignment", "Left"), ("lineHeight", 0), ("tracking", 0))));
            Check(ReferenceEquals(pixels, window.doc.Layers.Single(layer => layer.Id.ToString() == layerId).Pixels),
                "Identical text formatting regenerated the layer raster");
            Check(window.doc.Revision == revision && Text(acknowledgement, "revision") == revision.ToString() &&
                window.history.CanRedo && window.history.Dirty(window.doc) == dirty, "Identical text formatting added history or destroyed redo");
            Success(Call(window, "update_text", Write(window, ("layerId", layerId))));
            Check(ReferenceEquals(pixels, window.doc.Layers.Single(layer => layer.Id.ToString() == layerId).Pixels) &&
                window.doc.Revision == revision && window.history.CanRedo, "An empty text update was treated as a document edit");
            Success(Call(window, "redo", Write(window)));
            Check(window.doc.Layers.Single(layer => layer.Id.ToString() == layerId).Text!.Content == "다시 실행할 내용",
                "The preserved redo branch no longer restores the pending text edit");
        });

        Case("native-disabled owner HWND blocks mutations while managed IsEnabled remains true", window =>
        {
            New(window, "네이티브 모달 보호");
            var before = window.doc.Snapshot();
            string layerId = window.doc.ActiveId.ToString();
            // EnsureHandle creates only this test window's hidden HWND. No Show,
            // ShowDialog, activation, desktop input or user-owned handle is used.
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            Check(handle != IntPtr.Zero && IsWindowEnabled(handle) && !window.IsVisible, "Native owner fixture was not created hidden and enabled");
            try
            {
                SetAutomationTestWindowEnabled(handle, false);
                Check(window.IsEnabled && !IsWindowEnabled(handle) && !window.IsVisible,
                    "Native modal disable must be distinguished from WPF IsEnabled or a shown dialog");
                Check(State(window)["busy"]!.GetValue<bool>(), "Native modal owner did not report the busy state");
                Failure(Call(window, "set_layer", Write(window, ("layerId", layerId), ("name", "모달 중 변경"))), "editor_busy");
                Failure(Call(window, "new_document", new JsonObject { ["name"] = "모달 중 새 문서", ["width"] = 8, ["height"] = 8, ["dpi"] = 96 }), "editor_busy");
                Unchanged(window, before, false, false);
                Check(window.tabs.Count == 1 && !window.IsVisible, "A rejected native-modal request changed the workspace or displayed the window");
            }
            finally { SetAutomationTestWindowEnabled(handle, true); }
            Check(IsWindowEnabled(handle) && !State(window)["busy"]!.GetValue<bool>(), "Native owner was not restored after modal blocking");
            Success(Call(window, "set_layer", Write(window, ("layerId", layerId), ("name", "모달 종료 후 변경"))));
            Check(window.doc.Active!.Name == "모달 종료 후 변경" && !window.IsVisible, "Restored owner did not accept a normal edit without showing the window");
        });

        Case("stale revisions and inactive documents reject writes without switching tabs", window =>
        {
            var first = New(window, "첫 문서"); string firstId = Text(first, "documentId"), oldRevision = Text(first, "revision");
            var added = Success(Call(window, "add_shape", Arguments(firstId, oldRevision, ("shape", "ellipse"), ("width", 12), ("height", 12), ("fill", "#20A0D0"))));
            string layerId = Text(added, "layerId"), freshRevision = Text(added, "revision"); var before = window.doc.Snapshot();
            Failure(Call(window, "set_layer", Arguments(firstId, oldRevision, ("layerId", layerId), ("x", 15))), "stale_revision");
            Unchanged(window, before, true, false);
            var second = New(window, "둘째 문서"); var secondBefore = window.doc.Snapshot();
            Failure(Call(window, "set_layer", Arguments(firstId, freshRevision, ("layerId", layerId), ("name", "잘못된 편집"))), "inactive_document");
            Unchanged(window, secondBefore, false, false);
            Check(Text(State(window), "activeDocumentId") == Text(second, "documentId") && window.tabs.Count == 2, "Inactive-document rejection changed the active tab");
            Success(Call(window, "activate_document", new JsonObject { ["documentId"] = firstId }));
            Success(Call(window, "set_layer", Write(window, ("layerId", layerId), ("name", "정상 편집"))));
            Check(window.doc.Layers.Single(layer => layer.Id.ToString() == layerId).Name == "정상 편집", "Activating the document did not permit a fresh write");
        });

        Case("locked layers and locked ancestors refuse text editing and deletion", window =>
        {
            New(window, "잠금 보호");
            string layerId = Text(Success(Call(window, "add_text", Write(window, ("text", "보호할 내용"), ("fontSize", 10)))), "layerId");
            Success(Call(window, "set_layer", Write(window, ("layerId", layerId), ("locked", true))));
            var before = window.doc.Snapshot();
            Failure(Call(window, "update_text", Write(window, ("layerId", layerId), ("text", "변경 금지"))), "layer_locked");
            Failure(Call(window, "delete_layer", Write(window, ("layerId", layerId))), "layer_locked");
            Failure(Call(window, "set_layer", Write(window, ("layerId", layerId), ("x", 25))), "layer_locked");
            Unchanged(window, before, true, false);
            Success(Call(window, "set_layer", Write(window, ("layerId", layerId), ("locked", false))));
            var child = window.doc.Layers.Single(layer => layer.Id.ToString() == layerId);
            window.Edit("검사 그룹", () => { var group = DocumentFeatures.CreateGroup(window.doc); group.Locked = true; window.doc.Add(group); child.ParentId = group.Id; });
            before = window.doc.Snapshot();
            Failure(Call(window, "update_text", Write(window, ("layerId", layerId), ("text", "상위 잠금 무시"))), "layer_locked");
            Failure(Call(window, "delete_layer", Write(window, ("layerId", layerId))), "layer_locked");
            Unchanged(window, before, true, false);
        });

        Case("project and image output protect existing files and render actual edited pixels", window =>
        {
            New(window, "파일 출력", 32, 24);
            string layerId = Text(Success(Call(window, "add_shape", Write(window, ("shape", "rectangle"), ("width", 12), ("height", 10),
                ("x", 4), ("y", 5), ("fill", "#FF0000"), ("strokeWidth", 0)))), "layerId");
            string project = Path.Combine(files, "protected.moruproj"), png = Path.Combine(files, "protected.png");
            Success(Call(window, "save_project", Write(window, ("path", project))));
            Success(Call(window, "export_image", Write(window, ("path", png))));
            Check(!window.history.Dirty(window.doc), "Successful project save did not mark the saved state");
            var first = Raster.Load(png); int inside = (8 * 32 + 5) * 4;
            Check(first.Width == 32 && first.Height == 24 && first.Data[3] == 0 && first.Data[inside + 2] == 255 && first.Data[inside + 3] == 255,
                "Exported PNG does not contain the editable red shape and transparent canvas");
            var oldProject = File.ReadAllBytes(project); var oldPng = File.ReadAllBytes(png);
            Success(Call(window, "set_layer", Write(window, ("layerId", layerId), ("x", 8))));
            var before = window.doc.Snapshot();
            Failure(Call(window, "save_project", Write(window, ("path", project))), "file_exists");
            Failure(Call(window, "export_image", Write(window, ("path", png))), "file_exists");
            Check(File.ReadAllBytes(project).SequenceEqual(oldProject) && File.ReadAllBytes(png).SequenceEqual(oldPng), "Default output overwrote an existing file");
            Unchanged(window, before, true, false);
            Check(window.history.Dirty(window.doc), "Rejected save incorrectly marked pending changes as saved");
            Success(Call(window, "save_project", Write(window, ("path", project), ("overwrite", true))));
            Success(Call(window, "export_image", Write(window, ("path", png), ("overwrite", true))));
            var saved = ProjectStore.Load(project); var updated = Raster.Load(png);
            Check(saved.Layers.Single(layer => layer.Id.ToString() == layerId) is { Kind: LayerKind.Shape, X: 8, Shape: not null }, "Project output lost editable shape data");
            Check(updated.Data[inside + 3] == 0 && updated.Data[(8 * 32 + 12) * 4 + 2] == 255, "Explicit overwrite did not render the latest layer position");
        });

        Case("opening and adding images preserve existing document history", window =>
        {
            string path = Path.Combine(files, "import-source.png");
            using (var stream = File.Create(path)) Raster.Solid(7, 5, Color.FromArgb(127, 20, 70, 160)).WritePng(stream);
            string firstId = Text(New(window, "계속 편집할 문서"), "documentId");
            Success(Call(window, "add_text", Write(window, ("text", "저장하지 않은 글자"), ("fontSize", 10))));
            var first = window.doc.Snapshot();
            var added = Success(Call(window, "add_image", Write(window, ("path", path), ("x", 6), ("y", 9), ("name", "추가 이미지"))));
            var image = window.doc.Layers.Single(layer => layer.Id == LayerId(added));
            Check(image.Kind == LayerKind.Raster && image.Pixels.Width == 7 && image.Pixels.Height == 5 && image.X == 6 && image.Y == 9 && image.Pixels.Data[3] == 127,
                "Image insertion lost size, position or alpha");
            Success(Call(window, "undo", Write(window)));
            Check(window.doc.Revision == first.Revision && window.doc.Layers.Any(layer => layer.Text?.Content == "저장하지 않은 글자"), "Image insertion damaged the prior undo history");
            Success(Call(window, "open_document", new JsonObject { ["path"] = path }));
            Check(window.tabs.Count == 2 && window.doc.Width == 7 && window.doc.Height == 5, "Opening an image replaced the unsaved tab");
            Success(Call(window, "activate_document", new JsonObject { ["documentId"] = firstId }));
            Check(window.doc.Revision == first.Revision && window.history.CanRedo, "Open/activate discarded an existing tab's redo history");
        });

        Case("unknown arguments and invalid colors reject complete edits atomically", window =>
        {
            New(window, "입력 검증");
            string layerId = Text(Success(Call(window, "add_shape", Write(window, ("shape", "rectangle"), ("width", 8), ("height", 8), ("fill", "#123456")))), "layerId");
            var before = window.doc.Snapshot();
            Failure(Call(window, "set_layer", Write(window, ("layerId", layerId), ("x", 27), ("unexpected", true))));
            Failure(Call(window, "set_layer", Write(window, ("layerId", layerId), ("name", "부분 적용 금지"), ("blend", "invalid"))));
            foreach (string color in new[] { "red", "#12", "#GG0000", "#123456789" })
                Failure(Call(window, "add_shape", Write(window, ("shape", "rectangle"), ("width", 5), ("height", 5), ("fill", color))));
            Failure(Call(window, "add_text", Write(window, ("text", "추가 금지"), ("fontSize", 0))));
            Failure(Call(window, "add_adjustment", Write(window, ("kind", "levels"), ("black", 250), ("white", 10))));
            Failure(Call(window, "add_adjustment", Write(window, ("kind", "exposure"), ("hue", 20))));
            Failure(Call(window, "not_a_command"));
            Unchanged(window, before, true, false);
            int tabs = window.tabs.Count;
            Failure(Call(window, "new_document", new JsonObject { ["name"] = "생성 금지", ["width"] = 8, ["height"] = 8, ["dpi"] = 96, ["background"] = "invalid" }));
            Check(window.tabs.Count == tabs, "Invalid new-document input created a partial tab");
        });

        Case("foundation capabilities describe the running editor without creating a document", window =>
        {
            var capabilities = Success(Call(window, "get_capabilities"));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 10 && capabilities["commands"]!.AsArray().Count == 43,
                "Running editor did not advertise its command contract");
            Check(capabilities["unsupportedViaMcp"]!.AsArray().Any(n => n!.GetValue<string>() == "3d_uv_mapping") && capabilities["materials"]!["embeddedOriginals"]!.GetValue<bool>(),
                "Supported 2D mapping must remain distinct from unsupported 3D operations");
            Check(!window.HasDocument && window.tabs.Count == 0 && !window.IsVisible, "Discovery changed the workspace");
        });

        Case("compact state and paged queries stay bounded on a large drawing and reject stale pages", window =>
        {
            New(window, "Synthetic drawing");
            var pixels = Raster.Solid(1, 1, Colors.Black);
            for (int i = 0; i < 2000; i++) window.doc.Add(new Layer { Kind = LayerKind.Shape,
                Name = (i % 2 == 0 ? "벽체 " : "가구 ") + i, Pixels = pixels,
                Shape = new ShapeSpec { Width = 1, Height = 1 }, X = i % 64, Y = i % 48 });
            window.doc.Validate(); window.history.Reset(window.doc);
            window.selectedLayers.UnionWith(window.doc.Layers.Select(l => l.Id));
            var compact = Success(Call(window, "get_state", new JsonObject { ["includeLayers"] = false }));
            var document = DocumentState(compact);
            Check(document["layers"] == null && document["layerCount"]!.GetValue<int>() == 2001 && compact.ToJsonString().Length < 20000,
                "Compact state expanded the drawing graph");
            Check(document["selectedLayerCount"]!.GetValue<int>() == 2001 && document["selectedLayerIds"]!.AsArray().Count == 200 &&
                document["selectionTruncated"]!.GetValue<bool>(), "Large selections must be bounded and explicitly marked");
            var query = new JsonObject { ["documentId"] = Text(document, "documentId"), ["nameContains"] = "벽체", ["limit"] = 50 };
            var page = Success(Call(window, "query_layers", query));
            Check(page["totalMatches"]!.GetValue<int>() == 1000 && page["layers"]!.AsArray().Count == 50 && page["nextOffset"]!.GetValue<int>() == 50,
                "Query omitted or expanded matching CAD objects");
            query["offset"] = 50; Failure(Call(window, "query_layers", query), "invalid_arguments");
            query["expectedRevision"] = Text(page, "revision");
            var next = Success(Call(window, "query_layers", query));
            Check(!page["layers"]!.AsArray().Select(l => l!["layerId"]!.GetValue<string>()).Intersect(
                next["layers"]!.AsArray().Select(l => l!["layerId"]!.GetValue<string>())).Any(), "Pagination duplicated objects");
            Success(Call(window, "set_layer", Write(window, ("layerId", window.doc.Layers[1].Id.ToString()), ("x", 12))));
            Failure(Call(window, "query_layers", query), "stale_revision");
        });

        Case("layer inspection reports parent coordinates inherited locks and transformed surface bounds", window =>
        {
            New(window, "Synthetic hierarchy");
            var group = DocumentFeatures.CreateGroup(window.doc, "Parent"); group.X = 10; group.Y = 20; group.Scale = 2;
            group.Locked = true; group.Visible = false; window.doc.Add(group);
            var child = VectorShapes.Create(new ShapeSpec { Width = 4, Height = 5 }, 2, 3);
            child.ParentId = group.Id; child.Name = "Child"; window.doc.Add(child); window.doc.Validate();
            var before = window.doc.Revision;
            var state = DocumentState(State(window));
            var result = Success(Call(window, "get_layer", new JsonObject { ["documentId"] = Text(state, "documentId"), ["layerId"] = child.Id.ToString() }));
            var layer = result["layer"]!;
            Check(layer["frameBounds"]!["x"]!.GetValue<double>() == 14 && layer["frameBounds"]!["y"]!.GetValue<double>() == 26 &&
                layer["frameBounds"]!["width"]!.GetValue<double>() == 8 && layer["frameBounds"]!["height"]!.GetValue<double>() == 10,
                "Parent transform was omitted from document-space bounds");
            Check(layer["x"]!.GetValue<double>() == 2 && layer["positionSpace"]!.GetValue<string>() == "parent" &&
                layer["lockedInHierarchy"]!.GetValue<bool>() && !layer["visibleInHierarchy"]!.GetValue<bool>() &&
                !layer["editing"]!["properties"]!.GetValue<bool>(), "Inherited constraints were lost");
            var query = new JsonObject { ["documentId"] = Text(state, "documentId"), ["parentId"] = group.Id.ToString() };
            Check(Success(Call(window, "query_layers", query))["totalMatches"]!.GetValue<int>() == 1, "Parent query did not find the child");
            Check(window.doc.Revision == before && !window.history.CanUndo, "Inspection changed document history");
        });

        static JsonObject Step(string command, params (string Key, JsonNode? Value)[] values)
            => new() { ["command"] = command, ["arguments"] = new JsonObject(values.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))) };
        static JsonObject Batch(MainWindow window, params JsonObject[] steps) => Write(window,
            ("operationId", Guid.NewGuid().ToString()), ("label", "Synthetic atomic edit"), ("steps", new JsonArray(steps.Cast<JsonNode?>().ToArray())));

        Case("AI inspection follows drawing categories and artboards while batches preserve their structure", window =>
        {
            New(window, "Drawing workspace");
            var initial = DocumentState(Success(Call(window, "get_state", new JsonObject { ["includeLayers"] = false })));
            Check(initial["artboards"]![0]!["implicit"]!.GetValue<bool>() && initial["artboards"]![0]!["artboardId"] == null,
                "The default canvas must not invent a persisted artboard ID");
            DrawingLayers.Wrap(window.doc);
            var drawing = window.doc.Layers.Single(l => l.Kind == LayerKind.Group);
            var child = window.doc.Layers.Single(l => l.ParentId == drawing.Id);
            child.SourceLayerName = "Synthetic walls";
            var photo = new Layer { Name = "Photo", Pixels = Raster.Solid(2, 2, Colors.Gray) }; window.doc.Add(photo);
            _ = ArtboardEditing.Set(window.doc, new Artboard(Guid.Empty, "Second sheet", 64, 0, 32, 24), true);
            window.doc.Validate(); window.history.Reset(window.doc);
            var before = window.doc.Snapshot();
            var state = DocumentState(Success(Call(window, "get_state", new JsonObject { ["includeLayers"] = false })));
            Check(state["artboardCount"]!.GetValue<int>() == 2 && state["artboards"]![1]!["x"]!.GetValue<double>() == 64 &&
                state["layerCategories"]!["Drawing"]!.GetValue<int>() == 2 && state["layerCategories"]!["Photo"]!.GetValue<int>() == 1,
                "State omitted the drawing/photo workspace or artboards");
            var query = new JsonObject { ["documentId"] = Text(state, "documentId"), ["category"] = "Drawing" };
            var objects = Success(Call(window, "query_layers", query))["layers"]!.AsArray();
            Check(objects.Count == 2 && objects.All(l => l!["layerId"]!.GetValue<string>() != photo.Id.ToString()) &&
                objects.Any(l => l!["sourceLayerName"]?.GetValue<string>() == "Synthetic walls"), "Drawing query lost inherited categories or source identity");
            Success(Call(window, "apply_batch", Batch(window, Step("set_layer", ("layerId", child.Id.ToString()), ("x", 3)))));
            Check(window.doc.Artboards.SequenceEqual(before.Artboards) && window.doc.Layers.Single(l => l.Id == child.Id).ParentId == drawing.Id,
                "An ordinary AI edit changed artboards or drawing hierarchy");
            Success(Call(window, "undo", Write(window))); Unchanged(window, before, false, true);
        });

        Case("batch dry run preserves state and commit adds exactly one undoable edit", window =>
        {
            New(window, "Atomic plan"); var before = window.doc.Snapshot();
            var batch = Batch(window, Step("set_layer", ("layerId", window.doc.ActiveId.ToString()), ("name", "Base")),
                Step("add_shape", ("shape", "rectangle"), ("width", 8), ("height", 5), ("x", 3), ("fill", "#339966")),
                Step("add_text", ("text", "Plan A"), ("fontSize", 12)));
            batch["dryRun"] = true;
            var dry = Success(Call(window, "apply_batch", batch));
            Check(dry["wouldChange"]!.GetValue<bool>() && !dry["committed"]!.GetValue<bool>() && dry["undoSteps"]!.GetValue<int>() == 0,
                "Dry run did not explain whether a commit would change anything");
            Check(dry["steps"]!.AsArray().All(s => s!["layerId"] == null), "Dry run leaked non-live generated IDs");
            Unchanged(window, before, false, false);
            batch["dryRun"] = false;
            var applied = Success(Call(window, "apply_batch", batch));
            Check(applied["undoSteps"]!.GetValue<int>() == 1 && window.doc.Layers.Count == 3 && window.doc.Layers[0].Name == "Base",
                "Batch did not apply every step");
            Check(applied["steps"]!.AsArray().All(s => window.doc.Layers.Any(l => l.Id.ToString() == s!["layerId"]!.GetValue<string>())),
                "Committed steps did not return live identifiers");
            Success(Call(window, "undo", Write(window))); Unchanged(window, before, false, true);
            Success(Call(window, "redo", Write(window)));
            Check(window.doc.Layers.Count == 3 && window.doc.Revision.ToString() == Text(applied, "revision"), "One redo did not restore the entire batch");
        });

        Case("a failed batch rolls back earlier steps and provides the failing index", window =>
        {
            New(window, "Atomic rollback"); var before = window.doc.Snapshot();
            var batch = Batch(window, Step("set_layer", ("layerId", window.doc.ActiveId.ToString()), ("x", 20)),
                Step("delete_layer", ("layerId", Guid.NewGuid().ToString())));
            var failed = Call(window, "apply_batch", batch); Failure(failed, "layer_not_found");
            Check(failed["error"]!["details"]!["stepIndex"]!.GetValue<int>() == 1 &&
                !failed["error"]!["details"]!["committed"]!.GetValue<bool>() && failed["error"]!["suggestedAction"] != null,
                "Batch failure omitted recovery information");
            Unchanged(window, before, false, false);
            batch = Batch(window, Step("add_shape", ("shape", "ellipse"), ("width", 8), ("height", 8)),
                Step("update_text", ("layerId", window.doc.ActiveId.ToString()), ("text", "Wrong kind")));
            Failure(Call(window, "apply_batch", batch), "wrong_layer_kind"); Unchanged(window, before, false, false);
        });

        Case("batch replay survives lost responses and undo without applying a second edit", window =>
        {
            New(window, "Replay"); var before = window.doc.Snapshot();
            var batch = Batch(window, Step("add_shape", ("shape", "rectangle"), ("width", 5), ("height", 6)));
            var first = Success(Call(window, "apply_batch", batch));
            var reordered = new JsonObject(batch.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
            var replay = Success(Call(window, "apply_batch", reordered));
            Check(replay["replayed"]!.GetValue<bool>() && Text(replay, "revision") == Text(first, "revision") && window.doc.Layers.Count == 2,
                "An identical retry duplicated the edit or depended on JSON key order");
            var conflict = (JsonObject)batch.DeepClone(); conflict["steps"]![0]!["arguments"]!["width"] = 7;
            Failure(Call(window, "apply_batch", conflict), "operation_id_conflict");
            Success(Call(window, "undo", Write(window)));
            replay = Success(Call(window, "apply_batch", batch));
            Check(Text(replay, "currentRevision") == before.Revision.ToString() && Text(replay, "revision") == Text(first, "revision"),
                "Receipt confused the original commit with the current revision");
            Unchanged(window, before, false, true);
            var nextPlan = Batch(window, Step("set_layer", ("layerId", window.doc.ActiveId.ToString()), ("x", 7)));
            nextPlan["expectedRevision"] = Text(first, "revision");
            Failure(Call(window, "apply_batch", nextPlan), "stale_revision"); Unchanged(window, before, false, true);
        });

        Case("batch schema rejects external actions overrides nesting and oversized plans before editing", window =>
        {
            New(window, "Strict plan"); var before = window.doc.Snapshot();
            foreach (var step in new[] { Step("save_project", ("path", "C:\\Work\\not-written.moruproj")),
                Step("apply_batch"), Step("set_layer", ("layerId", window.doc.ActiveId.ToString()), ("documentId", Guid.NewGuid().ToString())),
                Step("set_layer", ("layerId", window.doc.ActiveId.ToString()), ("opacity", 2)), Step("delete_layer", ("layerId", "not-an-id")) })
                Failure(Call(window, "apply_batch", Batch(window, step)), "invalid_arguments");
            Failure(Call(window, "apply_batch", Batch(window)), "invalid_arguments");
            Failure(Call(window, "apply_batch", Batch(window, Enumerable.Range(0, 65).Select(_ => Step("set_layer", ("layerId", window.doc.ActiveId.ToString()))).ToArray())), "invalid_arguments");
            Unchanged(window, before, false, false);
        });

        Case("batch no-ops and pre-cancelled plans preserve redo", window =>
        {
            New(window, "No-op plan"); string id = window.doc.ActiveId.ToString();
            Success(Call(window, "set_layer", Write(window, ("layerId", id), ("x", 4))));
            Success(Call(window, "undo", Write(window))); var before = window.doc.Snapshot();
            var batch = Batch(window, Step("set_layer", ("layerId", id), ("x", 0)));
            var noOp = Success(Call(window, "apply_batch", batch));
            Check(!noOp["changed"]!.GetValue<bool>() && noOp["undoSteps"]!.GetValue<int>() == 0, "No-op added history");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Failure(Call(window, "apply_batch", Batch(window, Step("set_layer", ("layerId", id), ("x", 20))), cancellation.Token), "cancelled");
            Unchanged(window, before, false, true);
        });

        Case("material registration preserves user selection and maps an editable embedded texture", window =>
        {
            New(window, "Material workflow");
            string path = Path.Combine(files, "synthetic-material.png");
            using (var output = File.Create(path)) Raster.Solid(4, 4, Colors.Orange).WritePng(output);
            window.selection = new Selection(new Rect(4, 4, 20, 20), true); var selected = window.selection;
            var registered = Success(Call(window, "register_material", Write(window, ("name", "Synthetic stone"), ("path", path), ("source", "Synthetic fixture"))));
            Check(ReferenceEquals(selected, window.selection) && window.doc.Materials.Count == 1, "Registration cleared the user's target selection");
            var region = Success(Call(window, "define_region", Write(window, ("name", "Entry floor"), ("source", "selection"))));
            Check(ReferenceEquals(selected, window.selection), "Capturing a region changed the selection");
            var query = new JsonObject { ["documentId"] = Text(region, "documentId") };
            Check(Success(Call(window, "query_materials", query))["materials"]!.AsArray().Count == 1 &&
                Success(Call(window, "query_regions", query))["regions"]!.AsArray().Count == 1, "Registered IDs were not discoverable");
            var args = Write(window, ("materialId", Text(registered, "materialId")), ("regionId", Text(region, "regionId")), ("tileWidth", 8), ("tileHeight", 8));
            var result = Success(Call(window, "apply_material", args)); var layer = window.doc.Layers.Single(l => l.Id == LayerId(result));
            Check(layer.Material != null && layer.Category == LayerCategory.Photo && layer.Blend == BlendMode.Multiply &&
                layer.Material.Boundary.Geometry.FillContains(new Point(10, 10)) && !layer.Material.Boundary.Geometry.FillContains(new Point(1, 1)), "Material ignored the selected ellipse or default blend");
            var revision = window.doc.Revision; var pixels = layer.Pixels;
            Success(Call(window, "update_material", Write(window, ("layerId", layer.Id.ToString()), ("tileWidth", 8))));
            Check(window.doc.Revision == revision && ReferenceEquals(window.doc.Active!.Pixels, pixels), "No-op material update added an edit");
            Success(Call(window, "update_material", Write(window, ("layerId", layer.Id.ToString()), ("tileWidth", 12), ("angle", 25))));
            Check(window.doc.Active!.Material!.TileWidth == 12 && window.doc.Active.Material.Angle == 25, "Pattern update did not persist");
            Success(Call(window, "undo", Write(window))); Check(window.doc.Active!.Material!.TileWidth == 8, "Pattern undo failed");
            string project = Path.Combine(files, "mapped-material.moruproj");
            Success(Call(window, "save_project", Write(window, ("path", project))));
            Check(ProjectStore.Load(project).Active!.Material != null, "MCP save lost material editability");
        });

        Case("material polygons validate before mutation and batch dry run rollback replay and locks remain effective", window =>
        {
            New(window, "Material plan");
            window.doc.Materials.Add(new MaterialAsset(Guid.NewGuid(), "Brick", Raster.Solid(2, 2, Colors.Red)));
            JsonArray Points() => new(new JsonObject { ["x"] = 3, ["y"] = 4 }, new JsonObject { ["x"] = 24, ["y"] = 4 }, new JsonObject { ["x"] = 24, ["y"] = 20 });
            var before = window.doc.Snapshot();
            Failure(Call(window, "define_region", Write(window, ("source", "selection"), ("name", "Invalid"), ("points", Points()))), "invalid_arguments");
            Failure(Call(window, "define_region", Write(window, ("source", "polygon"), ("name", "Invalid"), ("points", new JsonArray()))), "invalid_arguments");
            Check(SameDocument(window.doc, before), "Invalid region modified the document");
            var region = Success(Call(window, "define_region", Write(window, ("source", "polygon"), ("name", "Triangle"), ("points", Points()))));
            string materialId = window.doc.Materials[0].Id.ToString();
            var batch = Batch(window, Step("apply_material", ("materialId", materialId), ("regionId", Text(region, "regionId")), ("tileWidth", 4), ("tileHeight", 4)));
            before = window.doc.Snapshot(); batch["dryRun"] = true;
            Success(Call(window, "apply_batch", batch)); Check(SameDocument(window.doc, before), "Material dry run changed the document");
            batch["dryRun"] = false; var applied = Success(Call(window, "apply_batch", batch));
            Check(Success(Call(window, "apply_batch", batch))["replayed"]!.GetValue<bool>() && window.doc.Layers.Count == before.Layers.Count + 1, "Material batch retry duplicated a mapping");
            string id = applied["steps"]![0]!["layerId"]!.GetValue<string>();
            before = window.doc.Snapshot();
            Failure(Call(window, "apply_batch", Batch(window, Step("update_material", ("layerId", id), ("tileWidth", 7)), Step("delete_layer", ("layerId", Guid.NewGuid().ToString())))), "layer_not_found");
            Check(SameDocument(window.doc, before), "Failed plan left a partial pattern change");
            Success(Call(window, "set_layer", Write(window, ("layerId", id), ("locked", true))));
            Failure(Call(window, "update_material", Write(window, ("layerId", id), ("tileWidth", 7))), "layer_locked");
        });

        Case("missing materials regions and source files report specific codes without editing", window =>
        {
            New(window, "Missing references");
            string texture = Path.Combine(files, "missing-reference-texture.png");
            using (var output = File.Create(texture)) Raster.Solid(4, 4, Colors.Olive).WritePng(output);
            string materialId = Text(Success(Call(window, "register_material", Write(window, ("name", "Olive"), ("path", texture)))), "materialId");
            JsonArray Points() => new(new JsonObject { ["x"] = 2, ["y"] = 2 }, new JsonObject { ["x"] = 30, ["y"] = 2 }, new JsonObject { ["x"] = 30, ["y"] = 30 });
            string regionId = Text(Success(Call(window, "define_region", Write(window, ("source", "polygon"), ("name", "Tri"), ("points", Points())))), "regionId");
            var before = window.doc.Snapshot();
            string unknown = Guid.NewGuid().ToString();
            var missingMaterial = FailureError(Call(window, "apply_material", Write(window, ("materialId", unknown), ("regionId", regionId), ("tileWidth", 4), ("tileHeight", 4))));
            Check(Text(missingMaterial, "code") == "material_not_found" && Text(missingMaterial, "suggestedAction").Contains("query_materials") && Text(missingMaterial, "suggestedAction").Contains("query_patterns"),
                "A missing material does not point to both material and pattern queries: " + missingMaterial.ToJsonString());
            Failure(Call(window, "apply_material", Write(window, ("materialId", materialId), ("regionId", unknown), ("tileWidth", 4), ("tileHeight", 4))), "region_not_found");
            var batch = FailureError(Call(window, "apply_batch", Batch(window, Step("apply_material", ("materialId", materialId), ("regionId", unknown), ("tileWidth", 4), ("tileHeight", 4)))));
            Check(Text(batch, "code") == "region_not_found" && batch["details"]!["stepIndex"]!.GetValue<int>() == 0, "Batch lost the specific missing-region code: " + batch.ToJsonString());
            Check(!string.IsNullOrWhiteSpace(Text(batch, "suggestedAction")) && Text(batch, "suggestedAction").Contains("query_regions"), "Missing region lacks a query hint");
            string absent = Path.Combine(files, "absent-source.png");
            foreach (var (command, args) in new[] { ("add_image", Write(window, ("path", absent))), ("register_material", Write(window, ("name", "Absent"), ("path", absent))) })
            {
                var error = FailureError(Call(window, command, args));
                Check(Text(error, "code") == "file_not_found" && Text(error, "message") == "파일을 찾을 수 없습니다.", command + " exposed a raw runtime message: " + error.ToJsonString());
            }
            Check(SameDocument(window.doc, before), "Rejected references changed the document");
        });

        Case("built-in hatch patterns are listed by surface, registered on use and tuned with ink and line weight", window =>
        {
            var all = Success(Call(window, "query_patterns"));
            var keys = all["patterns"]!.AsArray().Select(p => Text(p!.AsObject(), "patternId")).ToArray();
            Check(all["count"]!.GetValue<int>() == HatchPatterns.All.Count && keys.SequenceEqual(HatchPatterns.All.Select(HatchPatterns.Key)), "query_patterns did not list every pattern in catalog order");
            var ground = Success(Call(window, "query_patterns", new JsonObject { ["surface"] = "ground" }))["patterns"]!.AsArray().Select(p => Text(p!.AsObject(), "patternId"));
            Check(ground.SequenceEqual(SelectionMaterials.PatternOrder(SurfaceHint.Ground).Select(HatchPatterns.Key)), "Ground order differs from the palette");
            var wall = Success(Call(window, "query_patterns", new JsonObject { ["surface"] = "wall" }))["patterns"]![0]!.AsObject();
            Check(Text(wall, "patternId") == "concrete" && Text(wall, "materialId") == HatchPatterns.StableId(HatchPattern.Concrete).ToString() && Text(wall, "defaultInk") == "#FF262626"
                && wall["cadKeywords"]!.AsArray().Any(k => k!.GetValue<string>() == "CONC"), "Pattern entry lacks its ID, ink or CAD keywords: " + wall.ToJsonString());
            Check(Success(Call(window, "query_patterns", new JsonObject { ["nameContains"] = "모래" }))["count"]!.GetValue<int>() == 1, "Name filter failed");
            New(window, "Pattern plan", 96, 64);
            JsonArray Points() => new(new JsonObject { ["x"] = 4, ["y"] = 4 }, new JsonObject { ["x"] = 90, ["y"] = 4 }, new JsonObject { ["x"] = 90, ["y"] = 60 }, new JsonObject { ["x"] = 4, ["y"] = 60 });
            string regionId = Text(Success(Call(window, "define_region", Write(window, ("source", "polygon"), ("name", "Yard"), ("points", Points())))), "regionId");
            string sand = HatchPatterns.StableId(HatchPattern.Sand).ToString();
            var created = Success(Call(window, "apply_material", Write(window, ("materialId", sand), ("regionId", regionId), ("tileWidth", 32), ("tileHeight", 32))));
            var id = LayerId(created); Layer Mapped() => window.doc.Layers.Single(l => l.Id == id);
            Check(ReferenceEquals(Mapped().Material!.Asset, HatchPatternRenderer.Create(HatchPattern.Sand)) && window.doc.Materials.Any(m => m.Id.ToString() == sand),
                "An unregistered pattern was not added to the library on apply");
            var listed = Success(Call(window, "query_materials", new JsonObject { ["documentId"] = Text(created, "documentId") }))["materials"]![0]!.AsObject();
            Check(Text(listed, "kind") == "pattern" && Text(listed, "patternId") == "sand", "query_materials does not identify the pattern");
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("ink", "#FF3366AA"), ("lineWeight", 1.5))));
            var fill = AutomationMaterials.Fill(Mapped().Material!);
            Check(Mapped().Material!.Ink == 0xFF3366AA && Text(fill, "ink") == "#FF3366AA" && fill["lineWeight"]!.GetValue<double>() == 1.5 && Text(fill, "rendering") == "pattern_redrawn",
                "Ink or line weight did not round-trip: " + fill.ToJsonString());
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("ink", "default"))));
            Check(Mapped().Material!.Ink == 0 && Mapped().Material!.LineWeight == 1.5, "ink=default did not reset or an omitted field changed");
            Failure(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("lineWeight", 20))), "invalid_arguments");
            Failure(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("ink", "red"))), "invalid_arguments");
            // A fully transparent ink is refused: #00000000 would otherwise read as the default dark grey and #00RRGGBB draw nothing.
            foreach (var invisible in new[] { "#00000000", "#00FF0000" })
                Failure(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("ink", invisible))), "invalid_arguments");
            Check(Mapped().Material!.Ink == 0, "A refused ink changed the fill");
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("ink", "#80FF0000"))));
            Check(Mapped().Material!.Ink == 0x80FF0000, "A translucent ink was not kept");
            bool refused = false; try { AutomationMaterials.ParseInk("#00000000"); } catch (ArgumentException) { refused = true; }
            Check(refused, "ParseInk read a zero-alpha ink as the default");
            string brick = HatchPatterns.StableId(HatchPattern.Brick).ToString();
            Success(Call(window, "update_material", Write(window, ("layerId", id.ToString()), ("materialId", brick))));
            Check(Mapped().Material!.Asset.Id.ToString() == brick && window.doc.Materials.Any(m => m.Id.ToString() == brick), "update_material did not register a new pattern");
            AutomationCatalog.Validate("open_document", new JsonObject { ["path"] = @"C:\plan.dxf", ["cadCleanup"] = true, ["cadHatches"] = "pattern" });
        });

        Case("cancelled requests cannot create documents edit history or write output files", window =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Failure(Call(window, "new_document", new JsonObject { ["name"] = "취소된 문서", ["width"] = 16, ["height"] = 16, ["dpi"] = 96 }, cancellation.Token), "cancelled");
            Check(!window.HasDocument && window.tabs.Count == 0, "Cancelled creation committed a document");
            New(window, "취소 검사"); var before = window.doc.Snapshot();
            string project = Path.Combine(files, "cancelled.moruproj"), png = Path.Combine(files, "cancelled.png");
            Failure(Call(window, "add_text", Write(window, ("text", "취소할 글자")), cancellation.Token), "cancelled");
            Failure(Call(window, "add_shape", Write(window, ("shape", "ellipse"), ("width", 12), ("height", 12)), cancellation.Token), "cancelled");
            Failure(Call(window, "save_project", Write(window, ("path", project)), cancellation.Token), "cancelled");
            Failure(Call(window, "export_image", Write(window, ("path", png)), cancellation.Token), "cancelled");
            Unchanged(window, before, false, false);
            Check(!File.Exists(project) && !File.Exists(png), "Cancelled output created a destination file");
            using var queuedCancellation = new CancellationTokenSource();
            var arguments = Write(window, ("text", "대기 중 취소"));
            var pending = window.Dispatcher.InvokeAsync(() => window.ExecuteAutomationAsync(
                new JsonObject { ["command"] = "add_text", ["arguments"] = arguments }, queuedCancellation.Token), DispatcherPriority.Background).Task.Unwrap();
            queuedCancellation.Cancel(); Failure(Await(pending), "cancelled");
            Unchanged(window, before, false, false);
        });
        Case("compact edit responses, honest undo redo and reopened projects", window =>
        {
            New(window, "첫 문서"); New(window, "둘째 문서");
            var compact = Success(Call(window, "add_text", Write(window, ("text", "작게"), ("includeLayers", false))));
            var docs = compact["documents"]!.AsArray();
            Check(docs.Count == 1 && Text(docs[0]!.AsObject(), "documentId") == Text(compact, "documentId") && docs[0]!["layers"] == null && docs[0]!["layersIncluded"]!.GetValue<bool>() == false,
                "includeLayers=false must return only the target document without layers: " + compact.ToJsonString());
            var legacy = Success(Call(window, "add_text", Write(window, ("text", "기존"))));
            Check(legacy["documents"]!.AsArray().Count == 2 && legacy["documents"]!.AsArray().All(d => d!["layers"] is JsonArray), "Default edit responses must keep the legacy full inventory");
            Check(Success(Call(window, "undo", Write(window)))["changed"]!.GetValue<bool>(), "Real undo must report changed=true");
            Check(Success(Call(window, "undo", Write(window)))["changed"]!.GetValue<bool>(), "Second undo must report changed=true");
            var none = Success(Call(window, "undo", Write(window)));
            Check(none["changed"]!.GetValue<bool>() == false && none["message"] != null, "Undo without history must say changed=false");
            Check(Success(Call(window, "redo", Write(window)))["changed"]!.GetValue<bool>(), "Redo must report changed=true");
            string project = Path.Combine(files, "reopen.moruproj");
            Success(Call(window, "save_project", Write(window, ("path", project))));
            int count = window.tabs.Count;
            var reopened = Success(Call(window, "open_document", new JsonObject { ["path"] = project, ["includeLayers"] = false }));
            Check(reopened["alreadyOpen"]!.GetValue<bool>() && window.tabs.Count == count && reopened["documents"]!.AsArray().Count == 1, "Reopening an open project must activate it and say alreadyOpen=true");
        });
        Case("connection test round-trips through the local pipe and reports the app version", window =>
        {
            var (offOk, offText) = Await(window.TestAutomationConnectionAsync(CancellationToken.None));
            Check(!offOk && offText.Contains("꺼져"), "Connection test must explain that the connection is off");
            window.EnableAutomation();
            try
            {
                New(window, "연결 테스트");
                var (ok, text) = Await(window.TestAutomationConnectionAsync(CancellationToken.None));
                Check(ok && text.Contains("문서 1개") && text.Contains(AutomationMcpServer.ApplicationVersion), "Connection test did not report state: " + text);
                // The dialog button runs the same round trip and shows the result under the examples.
                var dialog = window.CreateAutomationSettingsDialog();
                static IEnumerable<DependencyObject> Tree(DependencyObject root) { foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { yield return c; foreach (var n in Tree(c)) yield return n; } }
                var button = Tree(dialog).OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Content, "연결 테스트"));
                var note = Tree(dialog).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Text.StartsWith("연결을 켠 다음"));
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!note.Text.StartsWith("연결 확인됨") && DateTime.UtcNow < deadline) Await(Task.Delay(50).ContinueWith(_ => true));
                Check(note.Text.StartsWith("연결 확인됨") && button.IsEnabled, "Connection test button did not report success: " + note.Text);
                dialog.Close();
            }
            finally { window.DisableAutomation(); }
            Check(!AutomationMcpServer.ApplicationVersion.Contains('+') && AutomationMcpServer.ApplicationVersion.StartsWith("0."), "serverInfo version must be the app version without build metadata");
            Check(AutomationExamplePrompts.Length >= 3, "Example prompts missing");
        });
        Case("artboards can be added, edited, exported alone and deleted through undoable AI commands", window =>
        {
            New(window, "대지 문서", 100, 80);
            Success(Call(window, "add_shape", Write(window, ("shape", "rectangle"), ("width", 100), ("height", 80), ("fill", "#FF0000"))));
            var added = Success(Call(window, "add_artboard", Write(window, ("name", "표지"), ("x", 0), ("y", 0), ("width", 50), ("height", 40), ("includeLayers", false))));
            var boardId = Text(added, "artboardId");
            var boards = DocumentState(added)["artboards"]!.AsArray();
            Check(boards.Count == 2 && boards.Any(b => b!["artboardId"]?.GetValue<string>() == boardId && b["name"]!.GetValue<string>() == "표지"), "add_artboard must turn the canvas into a board and add the new one");
            Success(Call(window, "update_artboard", Write(window, ("artboardId", boardId), ("name", "앞면"), ("width", 60))));
            var updated = DocumentState(State(window))["artboards"]!.AsArray().Single(b => b!["artboardId"]?.GetValue<string>() == boardId)!;
            Check(updated["name"]!.GetValue<string>() == "앞면" && updated["width"]!.GetValue<double>() == 60 && updated["height"]!.GetValue<double>() == 40, "update_artboard must change only the given fields");
            string png = Path.Combine(files, "board.png");
            var exported = Success(Call(window, "export_image", Write(window, ("path", png), ("artboardId", boardId), ("scale", 2), ("keepTransparency", false))));
            var image = Raster.Load(png);
            Check(exported["width"]!.GetValue<int>() == 120 && image.Width == 120 && image.Height == 80 && image.Data[3] == 255, "Artboard export must crop, scale and flatten");
            Failure(Call(window, "export_image", Write(window, ("path", Path.Combine(files, "both.png")), ("artboardId", boardId), ("layerId", window.doc.Layers[0].Id.ToString()))), "invalid_arguments");
            Failure(Call(window, "export_image", Write(window, ("path", Path.Combine(files, "alpha.jpg")), ("keepTransparency", true))), "invalid_arguments");
            Failure(Call(window, "update_artboard", Write(window, ("artboardId", Guid.NewGuid().ToString()), ("width", 10))), "artboard_not_found");
            Success(Call(window, "delete_artboard", Write(window, ("artboardId", boardId))));
            Check(DocumentState(State(window))["artboards"]!.AsArray().Count == 1, "delete_artboard must remove the board");
            var last = DocumentState(State(window))["artboards"]!.AsArray()[0]!["artboardId"]!.GetValue<string>();
            Failure(Call(window, "delete_artboard", Write(window, ("artboardId", last))), "artboard_invalid");
            Success(Call(window, "undo", Write(window)));
            Check(DocumentState(State(window))["artboards"]!.AsArray().Count == 2, "Undo must restore the deleted artboard");
        });
        Case("inspect_file lists PDF pages and CAD layouts and open_document honors page and structure", window =>
        {
            string pdf = Path.Combine(files, "two.pdf"); CompatibilityTests.WriteTwoPages(pdf);
            var info = Success(Call(window, "inspect_file", new JsonObject { ["path"] = pdf }));
            Check(Text(info, "format") == "pdf" && info["pageCount"]!.GetValue<uint>() == 2, "PDF inspection must report two pages");
            var second = Success(Call(window, "open_document", new JsonObject { ["path"] = pdf, ["page"] = 2, ["dpi"] = 72, ["includeLayers"] = false }));
            var pixels = Imaging.Render(window.doc);
            Check(pixels.Data[2] < 40 && pixels.Data[0] > 200, "page=2 must open the blue second page");
            Failure(Call(window, "open_document", new JsonObject { ["path"] = pdf, ["page"] = 5 }), "invalid_arguments");
            string dxf = Path.Combine(files, "plan.dxf"); File.WriteAllText(dxf, CompatibilityTests.DxfFixture());
            var cad = Success(Call(window, "inspect_file", new JsonObject { ["path"] = dxf }));
            Check(Text(cad, "format") == "cad" && cad["layouts"]!.AsArray().Any(l => l!["model"]!.GetValue<bool>()), "CAD inspection must list model space");
            int before = window.tabs.Count;
            Success(Call(window, "open_document", new JsonObject { ["path"] = dxf, ["cadStructure"] = "layers", ["cadLongEdge"] = 600, ["cadLayout"] = "*Model_Space" }));
            Check(window.tabs.Count == before + 1 && window.doc.Width == 600, "CAD options must reach the importer");
            Failure(Call(window, "open_document", new JsonObject { ["path"] = dxf, ["cadLayout"] = "없는 배치" }), "layout_not_found");
        });
        Case("apply_batch combines artboard and layer edits in one undo step and PSD layers can be separated", window =>
        {
            New(window, "묶음 대지", 100, 80);
            var dry = Success(Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("dryRun", true), ("steps", new JsonArray(
                new JsonObject { ["command"] = "add_artboard", ["arguments"] = new JsonObject { ["name"] = "A", ["x"] = 0, ["y"] = 0, ["width"] = 50, ["height"] = 40 } })))));
            Check(dry["steps"]!.AsArray()[0]!["artboardId"] == null && DocumentState(State(window))["artboards"]!.AsArray().Count == 1, "Dry run must not create artboards or leak ids");
            var batch = Success(Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("label", "대지와 제목"), ("steps", new JsonArray(
                new JsonObject { ["command"] = "add_artboard", ["arguments"] = new JsonObject { ["name"] = "A", ["x"] = 0, ["y"] = 0, ["width"] = 50, ["height"] = 40 } },
                new JsonObject { ["command"] = "add_artboard", ["arguments"] = new JsonObject { ["name"] = "B", ["x"] = 50, ["y"] = 0, ["width"] = 50, ["height"] = 40 } },
                new JsonObject { ["command"] = "add_text", ["arguments"] = new JsonObject { ["text"] = "제목", ["x"] = 4, ["y"] = 4 } })))));
            var ids = batch["steps"]!.AsArray().Take(2).Select(r => r!["artboardId"]!.GetValue<string>()).ToArray();
            var boards = DocumentState(State(window))["artboards"]!.AsArray();
            Check(boards.Count == 3 && ids.All(id => boards.Any(b => b!["artboardId"]?.GetValue<string>() == id)), "Batch must add both artboards and return their ids");
            Success(Call(window, "undo", Write(window)));
            Check(DocumentState(State(window))["artboards"]!.AsArray().Count == 1 && !window.doc.Layers.Any(l => l.Text != null), "One undo must revert the whole batch");
            var failing = Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("steps", new JsonArray(
                new JsonObject { ["command"] = "add_text", ["arguments"] = new JsonObject { ["text"] = "남으면 안 됨" } },
                new JsonObject { ["command"] = "update_artboard", ["arguments"] = new JsonObject { ["artboardId"] = Guid.NewGuid().ToString(), ["width"] = 10 } }))));
            Check(Text(FailureError(failing), "code") == "artboard_not_found" && !window.doc.Layers.Any(l => l.Text != null), "A failing artboard step must roll back the batch");

            var layered = new Document { Width = 16, Height = 12, Name = "psd" };
            layered.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(16, 12, Colors.White) });
            layered.Add(new Layer { Name = "위", Pixels = Raster.Solid(4, 4, Colors.Red), X = 2, Y = 2 });
            string psd = Path.Combine(files, "two-layers.psd"); ProjectStore.AtomicWrite(psd, s => PsdCompatibility.Write(layered, s, true));
            Success(Call(window, "open_document", new JsonObject { ["path"] = psd }));
            int composite = window.doc.Layers.Count;
            Success(Call(window, "open_document", new JsonObject { ["path"] = psd, ["separateLayers"] = true }));
            Check(composite == 1 && window.doc.Layers.Count == 2 && window.doc.Layers.Any(l => l.Name == "위"), "separateLayers must import each PSD layer");
        });
        Case("apply_batch steps refer to objects created earlier in the same batch with @ref", window =>
        {
            New(window, "참조 묶음", 100, 80);
            JsonObject Step(string command, JsonObject arguments, string? reference = null)
            { var step = new JsonObject { ["command"] = command, ["arguments"] = arguments }; if (reference != null) step["ref"] = reference; return step; }
            var steps = new JsonArray(
                Step("add_artboard", new JsonObject { ["name"] = "표지", ["x"] = 0, ["y"] = 0, ["width"] = 50, ["height"] = 40 }, "cover"),
                Step("update_artboard", new JsonObject { ["artboardId"] = "@cover", ["name"] = "앞표지" }),
                Step("add_text", new JsonObject { ["text"] = "제목", ["x"] = 4, ["y"] = 4 }, "title"),
                Step("set_layer", new JsonObject { ["layerId"] = "@title", ["opacity"] = .5 }),
                Step("add_shape", new JsonObject { ["shape"] = "rectangle", ["width"] = 10, ["height"] = 10 }, "box"),
                Step("reorder_layer", new JsonObject { ["layerId"] = "@box", ["direction"] = "down" }));
            var dry = Success(Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("dryRun", true), ("steps", steps.DeepClone()))));
            Check(dry["steps"]!.AsArray().All(r => r!["layerId"] == null && r["artboardId"] == null) && !window.doc.Layers.Any(l => l.Text != null), "Dry run with refs must not edit or leak ids");
            var applied = Success(Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("steps", steps))));
            var title = window.doc.Layers.Single(l => l.Text != null);
            Check(Math.Abs(title.Opacity - .5) < 1e-9 && applied["steps"]!.AsArray()[2]!["ref"]!.GetValue<string>() == "title", "@title must reach set_layer");
            Check(DocumentState(State(window))["artboards"]!.AsArray().Any(b => b!["name"]!.GetValue<string>() == "앞표지"), "@cover must reach update_artboard");
            Success(Call(window, "undo", Write(window)));
            Check(!window.doc.Layers.Any(l => l.Text != null), "Referenced batch must still be one undo step");
            foreach (var (bad, why) in new[]
            {
                (new JsonArray(Step("set_layer", new JsonObject { ["layerId"] = "@missing", ["opacity"] = .5 })), "unknown ref"),
                (new JsonArray(Step("add_text", new JsonObject { ["text"] = "a" }, "x"), Step("add_text", new JsonObject { ["text"] = "b" }, "x")), "duplicate ref"),
                (new JsonArray(Step("add_text", new JsonObject { ["text"] = "a" }, "1bad")), "invalid ref name"),
                (new JsonArray(Step("add_text", new JsonObject { ["text"] = "a" }, "t"), Step("update_text", new JsonObject { ["layerId"] = "@t", ["text"] = "@t" })), "ref in a non-ID field is only text")
            })
            {
                var response = Call(window, "apply_batch", Write(window, ("operationId", Guid.NewGuid().ToString()), ("steps", bad)));
                if (why.StartsWith("ref in a non-ID")) { Success(response); Check(window.doc.Layers.Any(l => l.Text?.Content == "@t"), "Plain text starting with @ must stay text"); Success(Call(window, "undo", Write(window))); continue; }
                Check(Text(FailureError(response), "code") == "invalid_arguments", "Expected invalid_arguments for " + why);
            }
            Check(!window.doc.Layers.Any(l => l.Text != null), "Rejected batches must not edit the document");
        });
    }
}
