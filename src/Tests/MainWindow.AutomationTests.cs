using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
            => Await(window.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments ?? new JsonObject() }, token));
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
    }
}
