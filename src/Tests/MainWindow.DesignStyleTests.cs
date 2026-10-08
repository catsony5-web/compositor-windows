using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Compositor.Windows;

// 디자인 스타일 in the editor: one undo step, re-apply and removal from the inspector, the gallery's
// live previews and cancellation, the entry points, and the query_styles / apply_style commands.
public sealed partial class MainWindow
{
    internal static void RunDesignStyleTests(Action<string, Action> test, string directory)
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
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        void Window(string name, Action<MainWindow> action) => test("design styles UI: " + name, () =>
        {
            var w = new MainWindow(null) { headlessTesting = true, designStyleServices = DesignStyleTests.Services };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                w.jobCts?.Cancel(); foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document); w.history.MarkSaved(w.doc);
                w.StopRenderingForShutdown(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        Window("a style applies as one undo step and re-applies and removes from the inspector", w =>
        {
            w.AddTab(SyntheticPlan.Create(1200, 800), null);
            var original = w.doc.Snapshot();
            var outcome = WaitOnDispatcher(() => w.ApplyDesignStyleAsync(DesignStyles.ScreentonePlan, null))!;
            var folder = w.doc.Layers.Single(l => l.Id == outcome.GroupId);
            Check(w.history.UndoLabel == "디자인 스타일 적용" && w.doc.ActiveId == folder.Id && w.selectedLayers.SetEquals([folder.Id]) && w.jobCts == null, "Applying was not one selected undo step");
            w.BuildProperties();
            Check(w.properties.Children.OfType<SectionHeader>().Any(h => h.Key == "디자인 스타일"), "The inspector has no style section");
            var strength = Descendants(w.properties).OfType<ParameterSlider>().Single(s => AutomationProperties.GetName(Descendants(s).OfType<Slider>().Single()) == "강도");
            strength.SetValue(80, true);
            Click(Descendants(w.properties).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "디자인 스타일 다시 적용"));
            WaitOnDispatcher(async () => { while (w.jobCts != null) await Task.Delay(20); return true; });
            var replaced = w.doc.Layers.Single(DesignStyles.IsStyleGroup);
            Check(replaced.Id == folder.Id && replaced.Style!.Get("strength", 0) == 80 && replaced.Style.Get("texture", 0) == 45 && w.history.UndoLabel == "디자인 스타일 다시 적용", "The inspector did not re-apply the folder with its settings");
            w.Undo(); Check(w.doc.Layers.Single(DesignStyles.IsStyleGroup).Style!.Get("strength", 0) == 55, "Undo did not return to the first application");
            w.Undo(); Check(SameDocument(original, w.doc) && !w.doc.Layers.Any(DesignStyles.IsStyleGroup), "Undo did not restore the document exactly");
            w.Redo(); w.Redo(); Check(w.doc.Layers.Single(DesignStyles.IsStyleGroup).Style!.Get("strength", 0) == 80, "Redo did not bring the styles back");
            w.doc.ActiveId = folder.Id; w.BuildProperties();
            Click(Descendants(w.properties).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "디자인 스타일 제거"));
            Check(!w.doc.Layers.Any(DesignStyles.IsStyleGroup) && w.history.UndoLabel == "디자인 스타일 제거", "Removing did not delete the folder in one step");
            Check(w.doc.Layers.Where(l => l.ParentId == null).Select(l => l.Id).SequenceEqual(original.Layers.Where(l => l.ParentId == null).Select(l => l.Id)), "Removing changed the original layers");
            w.Undo(); Check(w.doc.Layers.Any(DesignStyles.IsStyleGroup), "Undo did not restore the removed style");
            w.doc.Layers.Single(DesignStyles.IsStyleGroup).Locked = true; w.doc.ActiveId = folder.Id;
            Check(WaitOnDispatcher(() => w.ApplyDesignStyleAsync(DesignStyles.DarkSection, null, folder.Id)) == null && w.doc.Layers.Single(DesignStyles.IsStyleGroup).Style!.StyleId == DesignStyles.ScreentonePlan, "A locked folder was re-applied");
        });

        Window("the gallery renders the document in every style, follows settings and cancels", w =>
        {
            w.AddTab(SyntheticPhoto.Create(640, 400), null);
            var dialog = w.CreateDesignStyleDialog(null);
            try
            {
                Check(dialog.SelectedStyle.Target == StyleTarget.Photo && !dialog.Editing && (string)dialog.ApplyButton.Content == "적용", "A photo did not open on a photo style");
                Check(dialog.WaitForPreviews(TimeSpan.FromSeconds(90)) && dialog.ThumbnailsRendered == 5 && dialog.CardImages.All(i => i.Source != null) && dialog.LargePreview.Source != null, "The cards or the large preview were not rendered");
                int previews = dialog.PreviewsRendered;
                dialog.Select(DesignStyles.TranslucentEditorial); dialog.SetValue("panel", 0); dialog.SetValue("blur", 20);
                Check(dialog.WaitForPreviews(TimeSpan.FromSeconds(60)) && dialog.PreviewsRendered > previews && dialog.ThumbnailsRendered >= 6, "Changed settings did not re-render the preview and its card");
                Check(dialog.Values["panel"] == 0 && dialog.Values["blur"] == 20 && dialog.ParameterPanel.Children.OfType<SegmentedChoice<int>>().Count() == 1, "The settings do not follow the selected style");
            }
            finally { dialog.CancelPreviews(); dialog.Close(); }
            var cancelled = w.CreateDesignStyleDialog(null);
            cancelled.StartPreviews(); cancelled.CancelPreviews();
            Check(cancelled.Lifetime.IsCancellationRequested && cancelled.WaitForPreviews(TimeSpan.FromSeconds(30)), "Cancelling the gallery did not stop its renders");
            cancelled.Close();
            var outcome = WaitOnDispatcher(() => w.ApplyDesignStyleAsync(DesignStyles.NeoBrutalistPoster, new Dictionary<string, double> { ["color"] = 2 }))!;
            var editing = w.CreateDesignStyleDialog(null);
            try
            {
                Check(editing.Editing && editing.SelectedStyle.Id == DesignStyles.NeoBrutalistPoster && editing.Values["color"] == 2 && (string)editing.ApplyButton.Content == "다시 적용", "A selected style folder did not open the gallery for re-apply");
                Check(editing.WaitForPreviews(TimeSpan.FromSeconds(90)) && editing.ThumbnailsRendered == 5, "The re-apply gallery did not render previews");
            }
            finally { editing.CancelPreviews(); editing.Close(); }
            Check(w.doc.Layers.Any(l => l.Id == outcome.GroupId), "The gallery changed the document");
        });

        Window("디자인 스타일 is reachable from the menu, Ctrl+K, both panels and the 건축학과 profile", w =>
        {
            w.AddTab(SyntheticPhoto.Create(320, 200), null);
            Check(w.BuildCommandRegistry().Any(c => c.Id == "menu:이미지/디자인 스타일…" && c.Title == "디자인 스타일…"), "The gallery is not a command");
            Check(RibbonGlyph("디자인 스타일…") == Theme.Glyphs.Style, "The ribbon has no icon for the gallery");
            foreach (bool design in new[] { false, true })
            {
                w.SetWorkspaceMode(design);
                var headers = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
                Check(headers.Contains(UserProfiles.DesignStyle) && Descendants(w.studioContents[0]).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "디자인 스타일"), "A panel lacks the 디자인 스타일 action");
                if (design) Check(headers[0] == UserProfiles.DesignStyle, "The design panel does not lead with the style gallery");
            }
            w.SetUserProfile(UserProfiles.ArchitectureId);
            foreach (bool design in new[] { true, false })
            {
                w.SetWorkspaceMode(design);
                var keys = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
                Check(Array.IndexOf(keys, UserProfiles.DesignStyle) == Array.IndexOf(keys, "배치와 그룹") + 1, "건축학과 does not place 디자인 스타일 after the drawing work: " + string.Join(", ", keys));
            }
        });

        Window("query_styles and apply_style apply, re-apply, switch and reject invalid requests", w =>
        {
            static JsonObject Await(Task<JsonObject> task) => WaitOnDispatcher(() => task);
            JsonObject Call(string command, JsonObject arguments) => Await(w.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments }));
            static JsonObject Success(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == true, "Automation failed: " + response.ToJsonString()); return response["result"]!.AsObject(); }
            void Failure(JsonObject response, string code) => Check(response["ok"]?.GetValue<bool>() == false && response["error"]?["code"]?.GetValue<string>() == code, $"Expected {code}: {response.ToJsonString()}");
            JsonObject Write(params (string Key, JsonNode? Value)[] values)
            {
                var arguments = new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["expectedRevision"] = w.doc.Revision.ToString(), ["includeLayers"] = false };
                foreach (var (key, value) in values) arguments[key] = value;
                return arguments;
            }
            var registry = Success(Call("query_styles", new JsonObject()));
            Check(registry["count"]!.GetValue<int>() == 5 && registry["styles"]!.AsArray().Count == 5 && registry["folders"] == null, "The registry query without a document failed");
            Check(registry["styles"]!.AsArray().Select(s => s!["styleId"]!.GetValue<string>()).SequenceEqual(DesignStyles.All.Select(s => s.Id)), "Style ids differ from the registry");
            var poster = registry["styles"]!.AsArray().Single(s => s!["styleId"]!.GetValue<string>() == DesignStyles.NeoBrutalistPoster)!;
            Check(poster["target"]!.GetValue<string>() == "photo" && poster["parameters"]!.AsArray().Any(p => p!["key"]!.GetValue<string>() == "color" && p["choices"]!.AsArray().Count == 4 && p["default"]!.GetValue<string>() == "mono"), "Parameters are not described");
            w.AddTab(SyntheticPhoto.Create(480, 300), null);
            var applied = Success(Call("apply_style", Write(("styleId", DesignStyles.Cyanotype), ("parameters", new JsonObject { ["strength"] = 40, ["paper"] = 80 }))));
            var group = Guid.Parse(applied["groupId"]!.GetValue<string>());
            Check(applied["layerId"]!.GetValue<string>() == group.ToString() && applied["parameters"]!["strength"]!.GetValue<double>() == 40 && w.history.UndoLabel == "AI · apply_style", "apply_style did not return the folder or commit one step");
            var listed = Success(Call("query_styles", new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString() }));
            Check(listed["documentKind"]!.GetValue<string>() == "photo" && listed["folders"]!.AsArray().Single()!["groupId"]!.GetValue<string>() == group.ToString(), "The document's style folder is not listed");
            var again = Success(Call("apply_style", Write(("styleId", DesignStyles.Cyanotype), ("groupId", group.ToString()), ("parameters", new JsonObject { ["strength"] = 90 }))));
            Check(again["groupId"]!.GetValue<string>() == group.ToString() && again["parameters"]!["strength"]!.GetValue<double>() == 90 && again["parameters"]!["paper"]!.GetValue<double>() == 80
                && w.doc.Layers.Count(DesignStyles.IsStyleGroup) == 1, "Re-apply did not keep the folder and its other settings");
            var switched = Success(Call("apply_style", Write(("styleId", DesignStyles.NeoBrutalistPoster), ("groupId", group.ToString()), ("parameters", new JsonObject { ["color"] = "blue", ["photo"] = "bitmap", ["title"] = "outline" }))));
            Check(switched["styleId"]!.GetValue<string>() == DesignStyles.NeoBrutalistPoster && switched["parameters"]!["color"]!.GetValue<string>() == "blue" && switched["parameters"]!["photo"]!.GetValue<string>() == "bitmap",
                "Switching the folder's style failed");
            var switchedMembers = w.doc.Layers.Where(l => l.ParentId == group).ToArray();
            Check(switchedMembers.Any(l => l.Adjustment?.Kind == AdjustmentKind.Threshold) && switchedMembers.Single(l => l.Name == "제목").Text is { Outline: true, OutlineOnly: true },
                "The bitmap print or the outlined title was not made");
            var before = w.doc.Snapshot(); bool undo = w.history.CanUndo;
            void Rejected(string code, params (string Key, JsonNode? Value)[] values)
            {
                Failure(Call("apply_style", Write(values)), code);
                Check(SameDocument(before, w.doc) && w.doc.Revision == before.Revision && w.history.CanUndo == undo, "A rejected request changed the document");
            }
            Rejected("invalid_arguments", ("styleId", DesignStyles.Cyanotype), ("parameters", new JsonObject { ["unknown"] = 1 }));
            Rejected("invalid_arguments", ("styleId", DesignStyles.Cyanotype), ("parameters", new JsonObject { ["strength"] = 150 }));
            Rejected("invalid_arguments", ("styleId", DesignStyles.NeoBrutalistPoster), ("parameters", new JsonObject { ["color"] = "green" }));
            Rejected("invalid_arguments", ("styleId", DesignStyles.DarkSection), ("parameters", new JsonObject { ["grid"] = "yes" }));
            Rejected("invalid_arguments", ("styleId", "watercolor"));
            Rejected("wrong_layer_kind", ("styleId", DesignStyles.Cyanotype), ("groupId", w.doc.Layers[0].Id.ToString()));
            Rejected("layer_not_found", ("styleId", DesignStyles.Cyanotype), ("groupId", Guid.NewGuid().ToString()));
            Rejected("layer_not_found", ("styleId", DesignStyles.Cyanotype), ("targetLayerIds", new JsonArray(Guid.NewGuid().ToString())));
            Rejected("invalid_arguments", ("styleId", DesignStyles.Cyanotype), ("groupId", group.ToString()), ("targetLayerIds", new JsonArray(w.doc.Layers[0].Id.ToString())));
            Rejected("invalid_arguments", ("styleId", DesignStyles.Cyanotype), ("targetLayerIds", new JsonArray(group.ToString())));
            var stale = Write(("styleId", DesignStyles.Cyanotype)); stale["expectedRevision"] = Guid.NewGuid().ToString();
            Failure(Call("apply_style", stale), "stale_revision");
            Success(Call("delete_layer", Write(("layerId", group.ToString()))));
            Check(!w.doc.Layers.Any(DesignStyles.IsStyleGroup), "delete_layer did not remove the style folder");
            var capabilities = Success(Call("get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 9 && capabilities["commands"]!.AsArray().Count == 41 && capabilities["styles"]!["count"]!.GetValue<int>() == 5, "Capabilities do not describe the style commands");
        });
    }
}
