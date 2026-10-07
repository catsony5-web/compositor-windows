using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Poster text in the editor: the outline and paragraph rows of the text panel, 피사체를 글자 앞으로
// from the menu, Ctrl+K, the layer right-click menu and the quick actions, and the automation arguments.
public sealed partial class MainWindow
{
    internal static void RunTextPosterUiTests(Action<string, Action> test, string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Click(UIElement element) => element.RaiseEvent(new RoutedEventArgs(element is MenuItem ? MenuItem.ClickEvent : ButtonBase.ClickEvent));
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        static TextSpec Title() => new() { Content = "TITLE", FontFamily = "Segoe UI", FontSize = 40, Bold = true, ColorArgb = 0xFFFFFFFF };

        test("text panel: outline switch, position, hollow letters and width are one undo step each", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 300, Height = 160, Name = "외곽선 패널" }; document.Add(DocumentFeatures.CreateText(Title(), 20, 20));
                window.AddTab(document, null); window.ShowStudioPage(1);
                TextPropertiesPanel Panel() => window.textPropertiesPanel ?? throw new InvalidOperationException("The text panel is missing");
                var start = window.doc.Active!.Text!;
                Check(!Panel().OutlineWidthForTesting.IsEnabled && !Panel().OutlineOnlyForTesting.IsEnabled && !Panel().OutlinePositionForTesting.IsEnabled && !Panel().OutlineColorForTesting.IsEnabled,
                    "Outline rows are editable while the outline is off");
                var outline = Panel().OutlineForTesting; outline.IsChecked = true; Click(outline);
                Check(window.doc.Active!.Text!.Outline && window.history.UndoLabel == "글자 외곽선" && Panel().OutlineWidthForTesting.IsEnabled, "Switching the outline on did not apply as its own step");
                Panel().OutlinePositionForTesting.Select(TextOutlinePosition.Center);
                Check(window.doc.Active!.Text!.OutlinePosition == TextOutlinePosition.Center, "The position choice did not apply");
                var hollow = Panel().OutlineOnlyForTesting; hollow.IsChecked = true; Click(hollow);
                Check(window.doc.Active!.Text!.OutlineOnly, "Hollow letters did not apply");
                Panel().OutlineWidthForTesting.Text = "9"; Check(Panel().TryApply() && window.doc.Active!.Text!.OutlineWidth == 9, "The outline width did not apply");
                Check(window.doc.Active!.Pixels.Width == DocumentFeatures.RenderText(window.doc.Active.Text!).Width, "The layer raster does not show the outline");
                var expected = new[] { start with { Outline = true, OutlinePosition = TextOutlinePosition.Center, OutlineOnly = true }, start with { Outline = true, OutlinePosition = TextOutlinePosition.Center }, start with { Outline = true }, start };
                foreach (var spec in expected) { window.Undo(); Check(window.doc.Active!.Text == spec, "Undo did not step back one outline change at a time"); }
                Check(!window.history.CanUndo && window.doc.Active!.X == 20 && window.doc.Active.Y == 20, "Undo left an extra step or moved the text");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("text panel: outline colour applies at once, paragraph box and justify apply together", () =>
        {
            var commits = new List<TextSpec>();
            var panel = new TextPropertiesPanel(Title() with { Outline = true }, spec => { commits.Add(spec); return null; }, _ => Color.FromRgb(200, 30, 40), _ => { });
            Click(panel.OutlineColorForTesting);
            Check(commits.Count == 1 && commits[0].OutlineArgb == 0xFFC81E28 && commits[0].Content == "TITLE", "Picking the outline colour did not apply once");
            var justify = panel.AlignmentButtonsForTesting.Single(b => AutomationProperties.GetName(b) == "단락 양쪽 정렬"); Click(justify);
            panel.BoxWidthForTesting.Text = "220"; Check(panel.TryApply() && commits[^1] is { BoxWidth: 220, Alignment: TextAlignment.Justify }, "Paragraph box and justify were not applied together");
            panel.BoxWidthForTesting.Text = "0.5"; Check(!panel.TryApply() && panel.ValidationForTesting.Contains("자동 줄바꿈 폭") && commits.Count == 2, "A box narrower than one pixel was accepted");
            panel.BoxWidthForTesting.Text = "0"; panel.OutlineWidthForTesting.Text = "0"; Check(!panel.TryApply() && panel.ValidationForTesting.Contains("외곽선 두께"), "A zero outline width was accepted");
            Check(TextPropertiesPanel.ShouldApplyKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None, false, false, false), "Enter must apply the new number fields");
        });

        // A photo (left half one colour, right half another), a white title over it, a layer clipped to the title and one more on top.
        static Document Poster(out Layer photo, out Layer title, out Layer clipped, out Layer top)
        {
            var document = new Document { Width = 120, Height = 80, Name = "포스터" };
            var pixels = new Raster(120, 80);
            for (int y = 0; y < 80; y++) for (int x = 0; x < 120; x++) { int i = (y * 120 + x) * 4; pixels.Data[i] = (byte)(x < 60 ? 30 : 200); pixels.Data[i + 1] = 90; pixels.Data[i + 2] = (byte)(x < 60 ? 200 : 30); pixels.Data[i + 3] = 255; }
            document.Add(photo = new Layer { Name = "사진", Pixels = pixels });
            document.Add(title = DocumentFeatures.CreateText(new TextSpec { Content = "BIG", FontFamily = "Segoe UI", FontSize = 48, Bold = true, ColorArgb = 0xFFFFFFFF }, 10, 6));
            document.Add(clipped = new Layer { Name = "글자 질감", Pixels = Raster.Solid(120, 80, Color.FromArgb(60, 0, 0, 0)), Clipped = true });
            document.Add(top = new Layer { Name = "맨 위", Pixels = Raster.Solid(4, 4, Colors.Yellow), X = 112, Y = 72 });
            return document;
        }
        // Left half of the photo is the subject.
        static byte[] LeftHalf(Raster pixels) { var mask = new byte[pixels.Width * pixels.Height]; for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width / 2; x++) mask[y * pixels.Width + x] = 255; return mask; }
        MainWindow Window(Document document, Guid[] selection)
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.AddTab(document, null); window.subjectCutOut = (pixels, _) => LeftHalf(pixels);
            window.selectedLayers.Clear(); foreach (var id in selection) window.selectedLayers.Add(id);
            window.doc.ActiveId = selection[^1];
            return window;
        }

        test("subject in front: a masked copy of the photo goes directly above the title in one undo step", () =>
        {
            var document = Poster(out var photo, out var title, out var clipped, out var top); var window = Window(document, [photo.Id]);
            try
            {
                var before = window.doc.Layers.Select(l => l.Id).ToArray();
                Check(WaitOnDispatcher(window.PlaceSubjectInFrontAsync), "The cut-out was not placed: " + window.status.Text);
                var layers = window.doc.Layers; var cut = window.doc.Active!;
                Check(layers.Select(l => l.Id).SequenceEqual([photo.Id, title.Id, clipped.Id, cut.Id, top.Id]), "The cut-out is not directly above the title and its clipped layer: " + string.Join(", ", layers.Select(l => l.Name)));
                Check(cut.Kind == LayerKind.Raster && ReferenceEquals(cut.Pixels.Data, photo.Pixels.Data) && cut.Mask!.SequenceEqual(LeftHalf(photo.Pixels)) && !cut.Clipped && cut.Name == "사진 피사체",
                    "The cut-out is not a masked copy of the photo");
                Check(window.selectedLayers.SetEquals([cut.Id]) && window.history.UndoLabel == "피사체를 글자 앞으로" && window.status.Text.Contains("마스크 브러시"), "The cut-out is not selected or the step is unnamed");
                Check(layers[0].Mask == null && layers[1].Text == title.Text, "The photo or the title changed");
                // On the left the subject covers the white title; on the right the title stays in front of the photo.
                var render = DesignRenderer.RenderOutput(window.doc);
                var letters = DocumentFeatures.RenderText(title.Text!);
                (int X, int Y)? Ink(int from, int to) { for (int y = 0; y < letters.Height; y++) for (int x = 0; x < letters.Width; x++) { int dx = (int)title.X + x; if (dx >= from && dx < to && letters.Data[(y * letters.Width + x) * 4 + 3] == 255) return (dx, (int)title.Y + y); } return null; }
                var left = Ink(4, 56)!.Value; var right = Ink(64, 116)!.Value;
                int l = (left.Y * render.Width + left.X) * 4, r = (right.Y * render.Width + right.X) * 4;
                Check(render.Data[l + 2] > 180 && render.Data[l] < 60, $"The subject is not in front of the title at {left}");
                Check(render.Data[r + 2] > 180 && render.Data[r] > 120, $"The title is not in front of the photo at {right}");
                window.Undo(); Check(window.doc.Layers.Select(x => x.Id).SequenceEqual(before) && !window.history.CanUndo, "Undo did not remove the cut-out in one step");
                window.Redo(); Check(window.doc.Layers.Count == before.Length + 1 && window.doc.Layers[3].Mask != null, "Redo did not restore the cut-out");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("subject in front: photo and title, title alone, and a masked photo", () =>
        {
            foreach (var pick in new[] { "both", "title" })
            {
                var document = Poster(out var photo, out var title, out _, out _);
                photo.Mask = Enumerable.Repeat((byte)128, photo.Pixels.Width * photo.Pixels.Height).ToArray();
                var window = Window(document, pick == "both" ? [photo.Id, title.Id] : [title.Id]);
                try
                {
                    Check(WaitOnDispatcher(window.PlaceSubjectInFrontAsync), $"{pick}: the cut-out was not placed: {window.status.Text}");
                    var cut = window.doc.Active!; var subject = LeftHalf(photo.Pixels);
                    Check(cut.Mask!.Select((v, i) => (v, i)).All(p => p.v == (subject[p.i] * 128 + 127) / 255), $"{pick}: the photo's own mask was not kept inside the subject mask");
                    Check(window.doc.Layers.IndexOf(cut) == window.doc.Layers.FindIndex(l => l.Id == title.Id) + 2, $"{pick}: wrong place");
                }
                finally { window.StopRenderingForShutdown(); }
            }
        });

        test("subject in front: selections that do not fit explain what to select and change nothing", () =>
        {
            void Refused(Document document, Guid[] selection, string expected, Action<MainWindow>? prepare = null)
            {
                var window = Window(document, selection);
                try
                {
                    prepare?.Invoke(window); var before = window.doc.Snapshot(); bool called = false; window.subjectCutOut = (pixels, _) => { called = true; return LeftHalf(pixels); };
                    Check(!WaitOnDispatcher(window.PlaceSubjectInFrontAsync), "A selection that does not fit was used");
                    Check(window.status.Text.Contains(expected) && !called && !window.history.CanUndo && SameDocument(before, window.doc), $"Expected '{expected}', saw '{window.status.Text}'");
                }
                finally { window.StopRenderingForShutdown(); }
            }
            var adjusted = Poster(out _, out _, out _, out _); var adjustment = DocumentFeatures.CreateAdjustment(adjusted, new AdjustmentSpec { Kind = AdjustmentKind.Exposure }); adjusted.Add(adjustment);
            Refused(adjusted, [adjustment.Id], "사진 레이어 하나와 텍스트 레이어 하나를");
            Refused(Poster(out _, out var title1, out var clipped1, out var top1), [title1.Id, clipped1.Id, top1.Id], "사진 레이어 하나와 텍스트 레이어 하나를");
            var noTitle = Poster(out _, out _, out _, out var topPhoto); Refused(noTitle, [topPhoto.Id], "선택한 사진 위에 텍스트 레이어가 없습니다");
            var below = Poster(out var photo2, out var title2, out _, out _); below.Layers.Remove(title2); below.Layers.Insert(0, title2);
            Refused(below, [photo2.Id, title2.Id], "텍스트 레이어가 사진 아래에 있습니다");
            var noPhoto = Poster(out var photo3, out var title3, out _, out _); noPhoto.Layers.Remove(photo3); noPhoto.Layers.Add(photo3);
            Refused(noPhoto, [title3.Id], "선택한 텍스트 아래에 사진 레이어가 없습니다");
            var apart = Poster(out var photo4, out var title4, out _, out _); var group = DocumentFeatures.CreateGroup(apart, "글자 그룹"); apart.Add(group); title4.ParentId = group.Id;
            Refused(apart, [photo4.Id, title4.Id], "같은 그룹 안에");
            var locked = Poster(out var photo5, out var title5, out _, out _); var folder = DocumentFeatures.CreateGroup(locked, "잠긴 그룹"); locked.Add(folder);
            foreach (var layer in locked.Layers.Where(l => l.Kind != LayerKind.Group)) layer.ParentId = folder.Id;
            folder.Locked = true; Refused(locked, [photo5.Id], "잠금을 먼저 해제");
        });

        test("subject in front: cancel and a document changed meanwhile leave the history as it was", () =>
        {
            var document = Poster(out var photo, out _, out _, out _); var window = Window(document, [photo.Id]);
            try
            {
                window.subjectCutOut = (pixels, token) => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); token.ThrowIfCancellationRequested(); return LeftHalf(pixels); };
                bool placed = WaitOnDispatcher(() => { var task = window.PlaceSubjectInFrontAsync(); window.jobCts?.Cancel(); return task; });
                Check(!placed && !window.history.CanUndo && window.doc.Layers.Count == 4 && window.status.Text.Contains("취소") && window.jobCts == null, "Cancelling still placed a cut-out");
                window.subjectCutOut = (pixels, _) => LeftHalf(pixels);
                placed = WaitOnDispatcher(() => { var task = window.PlaceSubjectInFrontAsync(); window.Edit("레이어 이름", () => window.doc.Layers[^1].Name = "바뀐 이름"); return task; });
                Check(!placed && window.history.UndoLabel == "레이어 이름" && window.doc.Layers.Count == 4 && window.status.Text.Contains("문서가 바뀌어"), "A cut-out made for an older document was placed");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("subject in front: runs from Ctrl+K, the layer right-click menu and both quick action panels", () =>
        {
            var document = Poster(out var photo, out var title, out _, out _); var window = Window(document, [photo.Id]);
            try
            {
                void Placed(string from, Action start)
                {
                    window.selectedLayers.Clear(); window.selectedLayers.Add(photo.Id); window.doc.ActiveId = photo.Id; window.subjectFrontTask = null;
                    Check(WaitOnDispatcher(() => { start(); return window.subjectFrontTask ?? Task.FromResult(false); }) && window.doc.Layers.Count == 5, $"{from} did not place the cut-out: {window.status.Text}");
                    window.Undo(); Check(window.doc.Layers.Count == 4, $"{from}: undo did not remove the cut-out");
                }
                var command = window.BuildCommandRegistry().Single(c => c.Id == "menu:레이어/피사체를 글자 앞으로");
                Check(command.IsAvailable() && command.Category == "레이어", "Ctrl+K lacks the command");
                Placed("Ctrl+K", () => window.RunCommand(command));
                var row = window.CreateLayerRow(new LayerListEntry(window.doc.Layers[0], 0, true, true));
                var item = row.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "피사체를 글자 앞으로"));
                Check(row.ContextMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header).Where(h => h != "스케치 사진 정리…").SequenceEqual(["레이어 복제", "이름 변경…", "피사체를 글자 앞으로", "레이어 삭제"]), "The layer right-click menu changed");
                Placed("The layer right-click menu", () => Click(item));
                foreach (bool design in new[] { false, true })
                {
                    window.SetWorkspaceMode(design); window.ShowStudioPage(0);
                    var button = Descendants(window.studioContents[0]).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "피사체를 글자 앞으로");
                    Check(button.Content is Grid && button.ToolTip is string { Length: > 0 }, "The quick action is not an icon command with a tooltip");
                    Placed(design ? "The design quick action" : "The photo quick action", () => Click(button));
                }
                Check(RibbonGlyph("피사체를 글자 앞으로") == Theme.Glyphs.SubjectFront, "The ribbon has no icon for the command");
                // Right-clicking another row selects it first; a row inside the selection keeps the whole selection.
                window.SetWorkspaceMode(false); window.selectedLayers.Clear(); window.selectedLayers.Add(photo.Id); window.selectedLayers.Add(title.Id); window.doc.ActiveId = title.Id;
                var inside = window.CreateLayerRow(new LayerListEntry(window.doc.Layers[0], 0, true, true));
                inside.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = PreviewMouseRightButtonDownEvent });
                Check(window.selectedLayers.SetEquals([photo.Id, title.Id]), "Right-clicking a selected row dropped the multi-selection");
                var outsideLayer = window.doc.Layers[^1];
                var outside = window.CreateLayerRow(new LayerListEntry(outsideLayer, 0, false, true));
                outside.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = PreviewMouseRightButtonDownEvent });
                Check(window.selectedLayers.SetEquals([outsideLayer.Id]) && window.doc.ActiveId == outsideLayer.Id, "Right-clicking an unselected row did not select it");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("subject in front: the bundled subject model cuts out a small photo end to end", () =>
        {
            var document = new Document { Width = 64, Height = 48, Name = "작은 사진" };
            var pixels = Raster.Solid(64, 48, Color.FromRgb(225, 235, 245));
            for (int y = 10; y < 46; y++) for (int x = 22; x < 42; x++) { int i = (y * 64 + x) * 4; pixels.Data[i] = 40; pixels.Data[i + 1] = 50; pixels.Data[i + 2] = 150; }
            document.Add(new Layer { Name = "사진", Pixels = pixels });
            document.Add(DocumentFeatures.CreateText(new TextSpec { Content = "A", FontSize = 30, ColorArgb = 0xFF000000 }, 4, 4));
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(document, null); window.selectedLayers.Clear(); window.selectedLayers.Add(window.doc.Layers[1].Id); window.doc.ActiveId = window.doc.Layers[1].Id;
                Check(WaitOnDispatcher(window.PlaceSubjectInFrontAsync), "The bundled model did not produce a cut-out: " + window.status.Text);
                var mask = window.doc.Active!.Mask!;
                Check(window.doc.Layers.Count == 3 && mask.Length == 64 * 48 && mask.Max() > mask.Min(), "The cut-out has no subject mask");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("automation: text outline, paragraph box and justify arguments", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                JsonObject Call(string command, JsonObject arguments) => WaitOnDispatcher(() => window.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments }));
                JsonObject Write(params (string Key, JsonNode? Value)[] values)
                {
                    var state = Call("get_state", new JsonObject())["result"]!;
                    string id = state["activeDocumentId"]!.GetValue<string>(); var current = state["documents"]!.AsArray().OfType<JsonObject>().Single(d => d["documentId"]!.GetValue<string>() == id);
                    var arguments = new JsonObject { ["documentId"] = id, ["expectedRevision"] = current["revision"]!.GetValue<string>() };
                    foreach (var (key, value) in values) arguments[key] = value;
                    return arguments;
                }
                JsonObject Ok(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == true, "Command failed: " + response.ToJsonString()); return response["result"]!.AsObject(); }
                Ok(Call("new_document", new JsonObject { ["name"] = "포스터 자동화", ["width"] = 400, ["height"] = 240, ["dpi"] = 96 }));
                string title = Ok(Call("add_text", Write(("text", "OUTLINE"), ("fontSize", 64), ("bold", true), ("outlineWidth", 6), ("outlineColor", "#FF102030"), ("outlinePosition", "center"))))["layerId"]!.GetValue<string>();
                Layer Layer(string id) => window.doc.Layers.Single(l => l.Id.ToString() == id);
                Check(Layer(title).Text is { Outline: true, OutlineWidth: 6, OutlineArgb: 0xFF102030, OutlinePosition: TextOutlinePosition.Center, OutlineOnly: false }, "add_text lost outline arguments");
                Ok(Call("update_text", Write(("layerId", title), ("outlineOnly", true))));
                Check(Layer(title).Text is { Outline: true, OutlineOnly: true }, "update_text outlineOnly did not make hollow letters");
                Ok(Call("update_text", Write(("layerId", title), ("outline", false))));
                Check(Layer(title).Text is { Outline: false, OutlineWidth: 6 } && window.history.UndoLabel.Length > 0, "outline=false did not switch it off and keep its settings");
                string paragraph = Ok(Call("add_text", Write(("text", "Small text blocks wrap between words inside a narrow column box."), ("fontSize", 12), ("boxWidth", 140), ("alignment", "Justify"))))["layerId"]!.GetValue<string>();
                Check(Layer(paragraph).Text is { BoxWidth: 140, Alignment: TextAlignment.Justify } && Layer(paragraph).Pixels.Width is >= 148 and <= 150, "add_text lost the paragraph box");
                var detail = Ok(Call("get_layer", new JsonObject { ["documentId"] = Write()["documentId"]!.DeepClone(), ["layerId"] = paragraph }));
                Check(detail.ToJsonString().Contains("\"BoxWidth\":140") && !detail.ToJsonString().Contains("DrawsFill"), "get_layer does not report the paragraph box");
                foreach (var (key, value) in new (string, JsonNode)[] { ("outlinePosition", "inside"), ("outlineWidth", 0), ("boxWidth", .5), ("alignment", "Justified") })
                {
                    var refused = Call("update_text", Write(("layerId", paragraph), (key, value)));
                    Check(refused["ok"]?.GetValue<bool>() == false && refused["error"]!["code"]!.GetValue<string>() == "invalid_arguments", $"{key}={value} was accepted: {refused.ToJsonString()}");
                }
                Ok(Call("undo", Write())); Check(window.doc.Layers.All(l => l.Id.ToString() != paragraph), "Undo did not remove the paragraph");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); window.StopRenderingForShutdown(); }
        });
    }
}
