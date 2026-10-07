using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 스케치 사진 정리 in the editor: the canvas rule and one undo step, placement on a board and inside a
// group, the dialog's detection/preview/settings, refusals, every entry point, and the clean_sketch
// AI command (single, batch with @ref, validation).
public sealed partial class MainWindow
{
    internal static void RunSketchCleanupTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        string root = Path.Combine(directory, "sketch-cleanup"); Directory.CreateDirectory(root);
        SyntheticSketch.Sketch? cached = null;
        SyntheticSketch.Sketch Sample() => cached ??= SyntheticSketch.Photo(1200, 900);
        static bool Accept(SketchCleanupDialog dialog) { dialog.DetectNow(); return dialog.Accept(); }
        void Window(Action<MainWindow> action)
        {
            var w = new MainWindow(null) { headlessTesting = true, sketchDialogRunner = Accept };
            // Commands started by a click continue on the dispatcher after their background work, as in the app.
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                w.history.MarkSaved(w.doc); foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document); w.StopRenderingForShutdown();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
        Document Opened(string name = "스케치")
        {
            var document = new Document { Width = Sample().Photo.Width, Height = Sample().Photo.Height, Name = name };
            document.Add(new Layer { Name = "원본", Pixels = Sample().Photo });
            return document;
        }
        static Layer Named(Document d, string name) => d.Layers.Single(l => l.Name == name);
        static Rect Bounds(Document d, Layer layer)
        {
            var corners = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }
                .Select(p => DocumentFeatures.ToDocumentSpace(d, layer, p)).ToArray();
            return new Rect(new Point(corners.Min(p => p.X), corners.Min(p => p.Y)), new Point(corners.Max(p => p.X), corners.Max(p => p.Y)));
        }

        test("스케치 사진 정리 on an opened photo makes the canvas the flattened sheet in one undo step", () => Window(w =>
        {
            w.AddTab(Opened(), null);
            var before = w.doc.Snapshot(); var beforePixels = Imaging.Render(w.doc);
            var expected = SketchCleanup.FlatSize(SketchCleanup.DetectSheet(Sample().Photo).Corners, 1200, 900);
            Check(WaitOnDispatcher(w.CleanSketchPhotoAsync), "The cleanup did not finish");
            Check(w.doc.Width == expected.Width && w.doc.Height == expected.Height, $"The canvas is {w.doc.Width}×{w.doc.Height}, not the sheet {expected}");
            var group = Named(w.doc, "스케치 정리"); var lines = Named(w.doc, "스케치 선"); var white = Named(w.doc, "흰 바탕"); var photo = Named(w.doc, "원본");
            Check(group.Kind == LayerKind.Group && lines.ParentId == group.Id && white.ParentId == group.Id && group.ParentId == null, "The result is not one group with the line and white layers");
            Check(w.doc.Layers.IndexOf(white) < w.doc.Layers.IndexOf(lines) && w.doc.Layers.IndexOf(photo) < w.doc.Layers.IndexOf(group), "The white layer is not under the lines or the group not above the photo");
            Check(!photo.Visible && ReferenceEquals(photo.Pixels, before.Layers[0].Pixels), "The photo was not kept hidden and unchanged");
            Check(lines.Pixels.Width == w.doc.Width && lines.X == 0 && lines.Y == 0 && lines.Scale == 1 && white.Pixels.Data.All(v => v == 255), "The result does not fill the canvas at 1:1");
            Check(lines.Pixels.Data[3] == 0 && lines.Pixels.Data.Where((_, i) => i % 4 == 3).Count(v => v == 255) > 2000, "The line layer is not transparent paper with solid lines");
            Check(w.doc.ActiveId == lines.Id && w.selectedLayers.SetEquals([lines.Id]) && !w.collapsedGroups.Contains(group.Id), "The line layer is not selected in an open group");
            Check(w.history.UndoLabel == "스케치 사진 정리", "The cleanup is not the last undo step");
            w.Undo();
            Check(w.doc.Width == 1200 && w.doc.Height == 900 && w.doc.Layers.Count == 1 && w.doc.Layers[0].Visible && !w.history.CanUndo, "Undo did not restore the photo document in one step");
            Check(Imaging.Render(w.doc).Data.SequenceEqual(beforePixels.Data), "Undo left different pixels");
            w.Redo(); Check(w.doc.Layers.Count == 4 && w.doc.Width == expected.Width, "Redo did not bring the result back");
        }));

        test("스케치 사진 정리 on a board fits the result into the photo's bounds above it, also inside a group", () => Window(w =>
        {
            var board = new Document { Width = 1600, Height = 1000, Name = "보드" };
            board.Add(new Layer { Name = "배경", Pixels = Raster.Solid(1600, 1000, Colors.White) });
            var photo = new Layer { Name = "사진", Pixels = Sample().Photo, X = 820, Y = 140, Scale = .42 }; board.Add(photo);
            board.Add(new Layer { Name = "제목", Pixels = Raster.Solid(40, 20, Colors.Black), X = 10, Y = 10 });
            board.ActiveId = photo.Id;
            w.AddTab(board, null); w.selectedLayers.Clear(); w.selectedLayers.Add(photo.Id);
            Check(WaitOnDispatcher(w.CleanSketchPhotoAsync), "The cleanup did not finish");
            Check(w.doc.Width == 1600 && w.doc.Height == 1000, "The board's canvas changed size");
            var roots = w.doc.Layers.Where(l => l.ParentId == null).Select(l => l.Name).ToArray();
            Check(roots.SequenceEqual(["배경", "사진", "스케치 정리", "제목"]), "The group is not directly above the photo: " + string.Join(", ", roots));
            var target = Bounds(w.doc, Named(w.doc, "사진")); var placed = Bounds(w.doc, Named(w.doc, "스케치 선"));
            Check(placed.Left >= target.Left - .5 && placed.Right <= target.Right + .5 && placed.Top >= target.Top - .5 && placed.Bottom <= target.Bottom + .5, $"The result {placed} leaves the photo's bounds {target}");
            Check(Math.Abs(placed.Height - target.Height) < 1 && Math.Abs((placed.Left + placed.Right) / 2 - (target.Left + target.Right) / 2) < 1, "The portrait sheet is not fitted to the height and centered");
            Check(Bounds(w.doc, Named(w.doc, "흰 바탕")) == placed, "The white layer is not under the lines");

            // A photo inside a moved group: the result joins that group on its surface.
            var framed = new Document { Width = 1600, Height = 1000, Name = "그룹 보드" };
            var folder = DocumentFeatures.CreateGroup(framed, "사진 묶음"); folder.X = 100; folder.Y = 50; framed.Add(folder);
            var inner = new Layer { Name = "사진", Pixels = Sample().Photo, Scale = .5, ParentId = folder.Id }; framed.Add(inner); framed.ActiveId = inner.Id;
            w.AddTab(framed, null); w.selectedLayers.Clear(); w.selectedLayers.Add(inner.Id);
            Check(WaitOnDispatcher(w.CleanSketchPhotoAsync), "The cleanup inside a group did not finish");
            var group = Named(w.doc, "스케치 정리");
            Check(group.ParentId == folder.Id && group.Pixels.Width == folder.Pixels.Width, "The result did not join the photo's group");
            var inside = Bounds(w.doc, Named(w.doc, "스케치 선")); var source = Bounds(w.doc, Named(w.doc, "사진"));
            Check(inside.Left >= source.Left - .5 && inside.Right <= source.Right + .5 && inside.Top >= source.Top - .5 && inside.Bottom <= source.Bottom + .5, "The result left the grouped photo's bounds");
        }));

        test("스케치 사진 정리 dialog finds the sheet, previews lines and returns its settings", () =>
        {
            var dialog = new SketchCleanupDialog(null, Sample().Photo, "스케치.jpg");
            try
            {
                dialog.DetectNow();
                Check(dialog.Detection is { Found: true } && Enumerable.Range(0, 4).All(k => (dialog.Corners[k] - Sample().Corners[k]).Length < 2), "The dialog did not find the sheet");
                Check(dialog.DetectionText.StartsWith("종이를 찾았습니다", StringComparison.Ordinal) && dialog.Handles.Count == 4 && dialog.Handles.All(h => h.Visibility == Visibility.Visible), "The found sheet has no handles or note");
                dialog.RefreshNow();
                Check(dialog.PreviewLines != null && dialog.Error == null, "No preview: " + dialog.Error);
                double automatic = Math.Round(dialog.PreparedSheet!.AutomaticThreshold * 100);
                Check(dialog.ThresholdPercent == automatic && automatic >= SketchCleanup.MinAutoThreshold * 100 && automatic <= SketchCleanup.MaxAutoThreshold * 100,
                    $"The threshold did not start at the automatic value ({dialog.ThresholdPercent} vs {automatic})");
                Check(dialog.PreviewLines!.Width == dialog.ResultSize.Width && dialog.PreviewLines.Height == dialog.ResultSize.Height, "A result smaller than the preview size was previewed at another size");
                Check(dialog.SpeckSize == SketchCleanup.AutomaticSpeckSize(dialog.ResultSize.Width, dialog.ResultSize.Height), "The speck size did not follow the result size");
                Check(dialog.Accept() && dialog.Result is { Corners: { Length: 4 }, WhiteBackground: true } chosen && chosen.Options.Flatten && chosen.Options.LineColor == SketchLineColor.Original
                    && Math.Abs(chosen.Options.Threshold!.Value * 100 - dialog.ThresholdPercent) < 1e-9, "The default settings were not returned");
                dialog.SetKeepWhole(true); dialog.RefreshNow();
                Check(dialog.ResultSize == SketchCleanup.WholeSize(1200, 900) && dialog.Handles.All(h => h.Visibility == Visibility.Collapsed), "Not flattening did not use the whole photo");
                Check(dialog.Accept() && dialog.Result!.Corners == null && !dialog.Result.Options.Flatten, "Not flattening still returned corners");
                dialog.SetKeepWhole(false); dialog.SetThreshold(60); dialog.SetSpeck(40); dialog.SetBoldness(30); dialog.SetLineColor(SketchLineColor.Custom, 0xFF336699); dialog.SetWhiteBackground(false); dialog.RefreshNow();
                Check(dialog.Accept() && dialog.Result is { WhiteBackground: false } custom && custom.Options.Threshold == .6 && custom.Options.SpeckSize == 40 && Math.Abs(custom.Options.Boldness - .3) < 1e-9
                    && custom.Options.LineColor == SketchLineColor.Custom && custom.Options.CustomColor == 0xFF336699, "Changed settings were not returned");
                dialog.SetCorners([new Point(100, 100), new Point(900, 800), new Point(900, 100), new Point(100, 800)]); dialog.RefreshNow();
                Check(dialog.Error != null && !dialog.Accept(), "A crossed quadrilateral was accepted");
            }
            finally { dialog.Close(); }
            var blank = new SketchCleanupDialog(null, Raster.Solid(600, 400, Color.FromRgb(240, 236, 226)), "빈 종이.jpg");
            try
            {
                blank.DetectNow(); blank.RefreshNow();
                Check(blank.Error != null && blank.Error.Contains("선을 찾지 못했습니다") && blank.Detection is { Found: false }, "A blank page did not report that no line was found: " + blank.Error);
            }
            finally { blank.Close(); }
        });

        test("스케치 사진 정리 refuses text, groups and locked photos and leaves the document alone", () => Window(w =>
        {
            var d = Opened();
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "메모", FontSize = 20 }); d.Add(text);
            w.AddTab(d, null);
            bool asked = false; w.sketchDialogRunner = dialog => { asked = true; return Accept(dialog); };
            w.doc.ActiveId = text.Id;
            Check(!WaitOnDispatcher(w.CleanSketchPhotoAsync) && !asked && w.status.Text.StartsWith("스케치를 찍은 사진", StringComparison.Ordinal), "A text layer was cleaned");
            var photo = Named(w.doc, "원본"); photo.Locked = true; w.doc.ActiveId = photo.Id;
            Check(!WaitOnDispatcher(w.CleanSketchPhotoAsync) && !asked && !w.history.CanUndo, "A locked photo was cleaned");
            photo.Locked = false; w.sketchDialogRunner = _ => false;
            Check(!WaitOnDispatcher(w.CleanSketchPhotoAsync) && !w.history.CanUndo && w.doc.Layers.Count == 2, "Cancelling the dialog changed the document");
        }));

        test("스케치 사진 정리 is in the 이미지 menu, Ctrl+K, the ribbon, the photo panel, 건축학과 and a photo layer's right-click menu", () => Window(w =>
        {
            w.AddTab(Opened(), null);
            var command = w.BuildCommandRegistry().Single(c => c.Id == "menu:이미지/스케치 사진 정리…");
            Check(command.IsAvailable(), "The command palette entry is unavailable with a photo open");
            Check(w.RibbonGroups("이미지").Any(g => g.Title == "스케치" && g.Items.Any(i => Equals(i.Header, "스케치 사진 정리…"))), "The ribbon has no 스케치 group");
            w.RunCommand(command); Check(WaitOnDispatcher(() => w.lastSketchCleanup!) && w.doc.Layers.Count == 4, "The palette command did not clean the photo");
            w.Undo();
            w.SetWorkspaceMode(false);
            var keys = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
            var action = Descendants(w.studioContents[0]).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "스케치 사진 정리");
            Check(keys[^1] == UserProfiles.SketchPhoto && action.ToolTip is string { Length: > 0 }, "The photo panel lacks the 스케치 사진 section");
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(WaitOnDispatcher(() => w.lastSketchCleanup!) && w.doc.Layers.Count == 4, "The panel action did not clean the photo");
            w.Undo();
            w.SetUserProfile(UserProfiles.ArchitectureId);
            foreach (bool design in new[] { true, false })
            {
                w.SetWorkspaceMode(design);
                var order = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToList();
                Check(order.IndexOf(UserProfiles.SketchPhoto) == 3 && Descendants(w.studioContents[0]).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "스케치 사진 정리"),
                    $"건축학과 does not show 스케치 사진 after the drawing sections ({(design ? "design" : "photo")}): " + string.Join(", ", order));
            }
            var photo = w.doc.Layers[0];
            var row = w.CreateLayerRow(new LayerListEntry(photo, 0, true, true));
            Check(row.ContextMenu is { } menu && menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["스케치 사진 정리…", "이름 변경…", "레이어 복제", "레이어 삭제"]), "The photo row has no right-click menu");
            var text = DocumentFeatures.CreateText(new TextSpec { Content = "글", FontSize = 12 });
            Check(w.CreateLayerRow(new LayerListEntry(text, 0, false, true)).ContextMenu == null, "A text row offers the photo menu");
            w.doc.ActiveId = Guid.Empty; w.selectedLayers.Clear();
            ((MenuItem)row.ContextMenu!.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(WaitOnDispatcher(() => w.lastSketchCleanup!) && w.doc.Layers.Count == 4 && !w.doc.Layers.Single(l => l.Id == photo.Id).Visible, "The right-click item did not clean the clicked photo");
        }));

        // ---- AI connection ---------------------------------------------------------------------

        static T Await<T>(Task<T> task)
        {
            if (!task.IsCompleted)
            {
                var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(60) };
                timeout.Tick += (_, _) => frame.Continue = false;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                timeout.Start();
                try { Dispatcher.PushFrame(frame); } finally { timeout.Stop(); }
                if (!task.IsCompleted) throw new TimeoutException("The automation command did not finish.");
            }
            return task.GetAwaiter().GetResult();
        }
        static JsonObject Call(MainWindow w, string command, JsonObject arguments) => Await(w.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments.DeepClone() }));
        static JsonObject Ok(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == true, "Command failed: " + response.ToJsonString()); return response["result"]!.AsObject(); }
        static string Code(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == false, "Command was accepted: " + response.ToJsonString()); return response["error"]!["code"]!.GetValue<string>(); }
        static JsonObject Edit(MainWindow w, params (string Key, JsonNode? Value)[] values)
        {
            var arguments = new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["expectedRevision"] = w.doc.Revision.ToString(), ["includeLayers"] = false };
            foreach (var (key, value) in values) arguments[key] = value;
            return arguments;
        }
        void Automation(string name, Action<MainWindow> action) => test("clean_sketch AI command: " + name, () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                w.renderCts?.Cancel(); w.jobCts?.Cancel(); w.history.MarkSaved(w.doc);
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                w.Close(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        string PhotoFile()
        {
            string file = Path.Combine(root, "sketch-photo.png");
            if (!File.Exists(file)) using (var stream = File.Create(file)) Sample().Photo.WritePng(stream);
            return file;
        }

        Automation("finds the sheet, fits the result on a board and undoes in one step", w =>
        {
            Ok(Call(w, "new_document", new JsonObject { ["name"] = "AI 보드", ["width"] = 1600, ["height"] = 1000, ["background"] = "#FFFFFF" }));
            var image = Ok(Call(w, "add_image", Edit(w, ("path", PhotoFile()), ("x", 200), ("y", 50))));
            string photoId = image["layerId"]!.GetValue<string>();
            var result = Ok(Call(w, "clean_sketch", Edit(w, ("layerId", photoId))));
            Check(result["flattened"]!.GetValue<bool>() && result["detected"]!.GetValue<bool>() && result["confidence"]!.GetValue<double>() >= .8 && !result["canvasResized"]!.GetValue<bool>(), "Detection or placement was not reported: " + result.ToJsonString());
            var corners = result["corners"]!.AsArray();
            Check(corners.Count == 4 && Enumerable.Range(0, 4).All(k => Math.Abs(corners[k]!["x"]!.GetValue<double>() - Sample().Corners[k].X) < 2 && Math.Abs(corners[k]!["y"]!.GetValue<double>() - Sample().Corners[k].Y) < 2), "Reported corners are wrong");
            var group = w.doc.Layers.Single(l => l.Id == Guid.Parse(result["layerId"]!.GetValue<string>()));
            var lines = w.doc.Layers.Single(l => l.Id == Guid.Parse(result["lineLayerId"]!.GetValue<string>()));
            Check(group.Kind == LayerKind.Group && lines.ParentId == group.Id && result["backgroundLayerId"]!.GetValue<string>() is { Length: 36 } && result["groupId"]!.GetValue<string>() == group.Id.ToString(), "The group and layers do not match the result");
            Check(!w.doc.Layers.Single(l => l.Id == Guid.Parse(photoId)).Visible && w.doc.Width == 1600 && lines.Pixels.Width == result["width"]!.GetValue<int>(), "The photo is visible or the canvas changed");
            Check(w.history.UndoLabel == "AI · clean_sketch", "The command is not one undo step");
            Ok(Call(w, "undo", Edit(w)));
            Check(w.doc.Layers.Count == 2 && w.doc.Layers.All(l => l.Visible), "Undo did not remove the result in one step");
        });

        Automation("given corners, colors and no background on an opened photo resize the canvas", w =>
        {
            Ok(Call(w, "open_document", new JsonObject { ["path"] = PhotoFile(), ["includeLayers"] = false }));
            var photo = w.doc.Layers.Single();
            var points = new JsonArray(Sample().Corners.Select(p => (JsonNode?)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray());
            var result = Ok(Call(w, "clean_sketch", Edit(w, ("layerId", photo.Id.ToString()), ("corners", points), ("lineColor", "#204080"), ("background", "none"), ("threshold", .3), ("speckSize", 0), ("boldness", .5), ("name", "손 스케치"))));
            Check(result["canvasResized"]!.GetValue<bool>() && w.doc.Width == result["width"]!.GetValue<int>() && w.doc.Height == result["height"]!.GetValue<int>(), "The opened photo's canvas did not become the sheet");
            Check(result["detected"] == null && result["confidence"] == null && result["backgroundLayerId"] == null && result["threshold"]!.GetValue<double>() == .3 && result["speckSize"]!.GetValue<int>() == 0, "Given settings were not reported");
            var lines = w.doc.Layers.Single(l => l.Id == Guid.Parse(result["lineLayerId"]!.GetValue<string>()));
            Check(w.doc.Layers.Single(l => l.Id == Guid.Parse(result["layerId"]!.GetValue<string>())).Name == "손 스케치" && w.doc.Layers.Count == 3, "The group name or layer count is wrong");
            bool colored = true; for (int i = 0; i < lines.Pixels.Data.Length; i += 4) if (lines.Pixels.Data[i + 3] > 0) colored &= lines.Pixels.Data[i] == 0x80 && lines.Pixels.Data[i + 1] == 0x40 && lines.Pixels.Data[i + 2] == 0x20;
            Check(colored, "lineColor #204080 was not painted");
            var whole = Ok(Call(w, "clean_sketch", Edit(w, ("layerId", photo.Id.ToString()), ("flatten", false))));
            Check(!whole["flattened"]!.GetValue<bool>() && whole["corners"] == null && whole["width"]!.GetValue<int>() == 1200 && !whole["canvasResized"]!.GetValue<bool>(), "flatten=false did not keep the whole photo");
        });

        Automation("rejects invalid arguments, wrong layers, locks and lineless photos without editing", w =>
        {
            Ok(Call(w, "new_document", new JsonObject { ["name"] = "검증", ["width"] = 800, ["height"] = 600 }));
            var image = Ok(Call(w, "add_image", Edit(w, ("path", PhotoFile()))));
            string photoId = image["layerId"]!.GetValue<string>();
            var text = Ok(Call(w, "add_text", Edit(w, ("text", "메모"))));
            var revision = w.doc.Revision; int count = w.doc.Layers.Count;
            JsonArray Corners(params (double X, double Y)[] p) => new(p.Select(c => (JsonNode?)new JsonObject { ["x"] = c.X, ["y"] = c.Y }).ToArray());
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId), ("lineColor", "blue")))) == "invalid_arguments", "A named color was accepted");
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId), ("threshold", 1.5)))) == "invalid_arguments", "A threshold above 1 was accepted");
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId), ("corners", Corners((0, 0), (10, 0), (10, 10)))))) == "invalid_arguments", "Three corners were accepted");
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId), ("flatten", false), ("corners", Corners((0, 0), (900, 0), (900, 800), (0, 800)))))) == "invalid_arguments", "corners with flatten=false were accepted");
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId), ("corners", Corners((100, 100), (900, 800), (900, 100), (100, 800)))))) == "invalid_arguments", "A crossed quadrilateral was accepted");
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", text["layerId"]!.GetValue<string>())))) == "wrong_layer_kind", "A text layer was accepted");
            w.doc.Layers.Single(l => l.Id == Guid.Parse(photoId)).Locked = true;
            Check(Code(Call(w, "clean_sketch", Edit(w, ("layerId", photoId)))) == "layer_locked", "A locked photo was accepted");
            Check(w.doc.Revision == revision && w.doc.Layers.Count == count, "A rejected command edited the document");
            string blankFile = Path.Combine(root, "blank-paper.png");
            using (var stream = File.Create(blankFile)) Raster.Solid(500, 400, Color.FromRgb(240, 236, 226)).WritePng(stream);
            var blank = Ok(Call(w, "add_image", Edit(w, ("path", blankFile))));
            var failure = Call(w, "clean_sketch", Edit(w, ("layerId", blank["layerId"]!.GetValue<string>())));
            Check(Code(failure) == "sketch_cleanup_failed" && failure["error"]!["suggestedAction"]!.GetValue<string>().Contains("threshold"), "A lineless photo was not reported as a cleanup failure");
        });

        Automation("apply_batch runs clean_sketch with a ref, reports its layers and undoes in one step", w =>
        {
            Ok(Call(w, "new_document", new JsonObject { ["name"] = "묶음", ["width"] = 1600, ["height"] = 1000, ["background"] = "#F3F1EC" }));
            var image = Ok(Call(w, "add_image", Edit(w, ("path", PhotoFile()))));
            int count = w.doc.Layers.Count;
            JsonObject Batch(bool dry) => Edit(w, ("operationId", Guid.NewGuid().ToString()), ("dryRun", dry), ("steps", new JsonArray(
                new JsonObject { ["command"] = "clean_sketch", ["ref"] = "sketch", ["arguments"] = new JsonObject { ["layerId"] = image["layerId"]!.GetValue<string>(), ["lineColor"] = "black", ["background"] = "none" } },
                new JsonObject { ["command"] = "set_layer", ["arguments"] = new JsonObject { ["layerId"] = "@sketch", ["opacity"] = .8, ["x"] = 12 } })));
            var dry = Ok(Call(w, "apply_batch", Batch(true)));
            var dryStep = dry["steps"]![0]!.AsObject();
            Check(!dry["committed"]!.GetValue<bool>() && w.doc.Layers.Count == count && dryStep["layerId"] == null && dryStep["lineLayerId"] == null && dryStep["corners"]!.AsArray().Count == 4, "The dry run edited the document or leaked IDs");
            var done = Ok(Call(w, "apply_batch", Batch(false)));
            var step = done["steps"]![0]!.AsObject();
            var group = w.doc.Layers.Single(l => l.Id == Guid.Parse(step["layerId"]!.GetValue<string>()));
            Check(group.Kind == LayerKind.Group && group.Opacity == .8 && group.X == 12 && step["backgroundLayerId"] == null && step["lineLayerId"] != null, "The ref did not target the new group");
            Check(done["undoSteps"]!.GetValue<int>() == 1 && done["createdLayerCount"]!.GetValue<int>() == 2, "The batch is not one step with two new layers");
            Ok(Call(w, "undo", Edit(w)));
            Check(w.doc.Layers.Count == count && w.doc.Layers.All(l => l.Visible), "Undo did not restore the batch in one step");
            var capabilities = Ok(Call(w, "get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 9 && capabilities["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "clean_sketch")
                && capabilities["batch"]!["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "clean_sketch") && capabilities["sketch"]!["command"]!.GetValue<string>() == "clean_sketch", "Capabilities do not advertise clean_sketch");
        });
    }
}
