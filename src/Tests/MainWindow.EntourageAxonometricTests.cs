using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 점경 in the axonometric view: the palette's third view and its projection choice, placing on the
// projected ground (at a point, in a selection, scattered), the document's projection, changing the
// projection of placed items, their ground shadows, 내 점경 in the axonometric view, and the AI
// connection's view and projection parameters.
public sealed partial class MainWindow
{
    internal static void RunEntourageAxonometricTests(Action<string, Action> test)
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
        static Button[] Tiles(FrameworkElement palette) => Descendants(palette).OfType<Button>().Where(b => b.Tag is EntourageChoice).ToArray();
        static EntourageChoice Choice(string id) => new(EntourageLibrary.Find(id)!, null);
        void Window(string name, Action<MainWindow> action) => test("entourage UI: " + name, () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document); w.history.MarkSaved(w.doc);
                w.StopRenderingForShutdown(); SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        static Document Board(int width = 1200, int height = 800)
        {
            var document = new Document { Width = width, Height = height, Name = "보드" };
            document.Add(new Layer { Name = "배경", Pixels = Raster.Solid(width, height, Colors.White) });
            return document;
        }

        Window("the axonometric view lists its items, follows the drawing's projection and places on the ground", w =>
        {
            w.AddTab(Board(), null);
            var palette = w.EntouragePalette("axo");
            var views = Descendants(palette).OfType<SegmentedChoice<EntourageView>>().Single();
            var projections = Descendants(palette).OfType<SegmentedChoice<EntourageProjection>>().Single();
            Check(views.Buttons.Count() == 3 && projections.Visibility == Visibility.Collapsed, "The palette lacks the third view or shows the projection outside it");
            views.Select(EntourageView.Axonometric);
            Check(w.entourageView == EntourageView.Axonometric && projections.Visibility == Visibility.Visible && AutomationProperties.GetName(projections) == "액소노메트릭 투상", "Choosing the axonometric view did not show its projection");
            Check(Tiles(palette).Select(b => ((EntourageChoice)b.Tag).Id).SequenceEqual(EntourageLibrary.All.Where(i => i.Category == EntourageCategory.People && i.View == EntourageView.Axonometric).Select(i => i.Id)), "The axonometric people are not listed");
            Check(w.EntourageAxoProjection == EntourageProjection.Isometric, "A new drawing does not start isometric");
            projections.Select(EntourageProjection.Military);
            Check(w.EntourageAxoProjection == EntourageProjection.Military, "The projection choice was not kept");
            var person = w.PlaceEntourage(Choice("person.axo-walking"), new Point(400, 500)).Single();
            Check(person.Entourage is { View: EntourageView.Axonometric, Projection: EntourageProjection.Military } && (EntourageRenderer.Anchor(person) - new Point(400, 500)).Length < .01 && w.history.UndoLabel == "점경 넣기",
                "An axonometric item was not placed on its ground point in the chosen projection");
            w.selection = new Selection(new Rect(600, 200, 300, 200));
            var car = w.PlaceEntourage(Choice("car.axo-sedan")).Single();
            Check((EntourageRenderer.Anchor(car) - new Point(750, 300)).Length < .01, "An axonometric item placed in a selection does not stand in its middle");
            w.selection = null;
            w.entourageScatter = true; w.entourageScatterCount = 6;
            var trees = w.PlaceEntourage(Choice("tree.axo-round"), new Point(600, 600));
            var p = AxoProjection.Of(EntourageProjection.Military);
            double radius = trees[0].Entourage!.PixelsPerMeter * 8 * EntourageLibrary.Find("tree.axo-round")!.Footprint * (.75 + .55 * Math.Sqrt(6)) * 1.2;
            Check(trees.Count == 6 && w.doc.Active!.Kind == LayerKind.Group && trees.All(t => t.Entourage!.Projection == EntourageProjection.Military && t.Rotation == 0 && p.Ground(EntourageRenderer.Anchor(t) - new Point(600, 600)).Length <= radius)
                && w.history.UndoLabel == "점경 흩어 놓기", "The axonometric scatter did not spread one group on the projected ground");
            w.entourageScatter = false;
            // Another tab with this drawing's entourage takes its projection.
            var copy = w.doc.Snapshot(); copy.Name = "복사";
            w.AddTab(copy, null);
            Check(w.EntourageAxoProjection == EntourageProjection.Military, "A drawing with axonometric entourage does not set the projection");
            w.AddTab(Board(), null);
            Check(w.EntourageAxoProjection == EntourageProjection.Isometric, "An empty drawing took another tab's projection");
        });

        Window("placed axonometric items change projection together and cast shadows along the projected ground", w =>
        {
            w.AddTab(Board(), null);
            w.shadowDialogHandler = _ => true;
            var a = w.PlaceEntourage(Choice("person.axo-standing"), new Point(300, 600)).Single();
            var b = w.PlaceEntourage(Choice("tree.axo-conifer"), new Point(700, 600)).Single();
            w.selectedLayers.Clear(); w.selectedLayers.Add(a.Id); w.selectedLayers.Add(b.Id); w.doc.ActiveId = a.Id;
            w.BuildProperties();
            var choice = Descendants(w.properties).OfType<SegmentedChoice<EntourageProjection>>().Single(c => AutomationProperties.GetName(c) == "점경 투상");
            choice.Select(EntourageProjection.Dimetric);
            Check(w.doc.Layers.Where(l => l.Entourage != null).All(l => l.Entourage!.Projection == EntourageProjection.Dimetric) && w.history.UndoLabel == "점경 투상", "The projection did not change for the selected items in one step");
            Check((EntourageRenderer.Anchor(w.doc.Layers.Single(l => l.Id == a.Id)) - new Point(300, 600)).Length < .05, "Changing the projection moved the item");
            Check(w.EntourageAxoProjection == EntourageProjection.Dimetric, "New items do not follow the changed projection");
            w.selectedLayers.Clear(); w.selectedLayers.Add(a.Id); w.doc.ActiveId = a.Id;
            w.FlipEntourage();
            Check(w.doc.Layers.Single(l => l.Id == a.Id).FlipX && (EntourageRenderer.Anchor(w.doc.Layers.Single(l => l.Id == a.Id)) - new Point(300, 600)).Length < .05, "Flipping moved the item off its ground point");
            w.AddShadow();
            var shadow = w.doc.Active!.Shadow!;
            var expected = EntourageRenderer.AxonometricShadow(EntourageProjection.Dimetric, ShadowSpec.Default(ShadowProjection.Ground));
            Check(shadow.Projection == ShadowProjection.Ground && Math.Abs(shadow.Angle - expected.Angle) < .01 && Math.Abs(shadow.Elevation - expected.Elevation) < .01, $"The shadow does not follow the projected ground: {shadow.Angle}° {shadow.Elevation}°");
            w.Undo(); w.Undo();
            Check(w.doc.Layers.Where(l => l.Entourage != null).All(l => l.Entourage!.Projection == EntourageProjection.Dimetric) && !w.doc.Layers.Single(l => l.Id == a.Id).FlipX, "Undo did not step back through the edits");
        });

        Window("내 점경 stand in the axonometric view as elevation images", w =>
        {
            w.AddTab(Board(), null);
            var custom = EntourageTests.CustomItem();
            w.entourageLibrary = [custom];
            w.entourageTab = EntourageCustomTab; w.entourageView = EntourageView.Axonometric;
            var palette = w.EntouragePalette("custom-axo");
            Check(Tiles(palette).Any(t => ((EntourageChoice)t.Tag).Id == custom.ItemId), "The user's elevation images are not offered in the axonometric view");
            var placed = w.PlaceEntourage(new EntourageChoice(null, custom), new Point(400, 400)).Single();
            Check(placed.Entourage is { View: EntourageView.Elevation, IsCustom: true } && placed.Kind == LayerKind.Raster, "A user's image did not stay an elevation image");
            w.entourageLibrary = null;
        });

        Window("query_entourage and place_entourage take the axonometric view and projection", w =>
        {
            static JsonObject Await(Task<JsonObject> task) => WaitOnDispatcher(() => task);
            JsonObject Call(string command, JsonObject arguments) => Await(w.ExecuteAutomationAsync(new JsonObject { ["command"] = command, ["arguments"] = arguments }));
            static JsonObject Success(JsonObject response) { Check(response["ok"]?.GetValue<bool>() == true, "Automation failed: " + response.ToJsonString()); return response["result"]!.AsObject(); }
            JsonObject Write(params (string Key, JsonNode? Value)[] values)
            {
                var arguments = new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["expectedRevision"] = w.doc.Revision.ToString(), ["includeLayers"] = false };
                foreach (var (key, value) in values) arguments[key] = value;
                return arguments;
            }
            var items = Success(Call("query_entourage", new JsonObject { ["view"] = "axonometric" }))["items"]!.AsArray();
            Check(items.Count == EntourageLibrary.All.Count(i => i.View == EntourageView.Axonometric) && items.All(i => i!["view"]!.GetValue<string>() == "axonometric" && i["projections"]!.AsArray().Count == 3),
                "The axonometric items are not listed with their projections");
            w.AddTab(Board(), null);
            var placed = Success(Call("place_entourage", Write(("itemId", "person.axo-pair"), ("x", 300), ("y", 600), ("projection", "military"), ("variant", 1))));
            var layer = w.doc.Layers.Single(l => l.Id == Guid.Parse(placed["layerId"]!.GetValue<string>()));
            Check(layer.Entourage is { View: EntourageView.Axonometric, Projection: EntourageProjection.Military, Variant: 1 } && placed["entourage"]!["projection"]!.GetValue<string>() == "military"
                && placed["entourage"]!["view"]!.GetValue<string>() == "axonometric" && (EntourageRenderer.Anchor(layer) - new Point(300, 600)).Length < .01, "place_entourage did not place in the projection");
            var state = Success(Call("query_entourage", new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString() }));
            Check(state["axonometric"]!["projection"]!.GetValue<string>() == "military" && state["axonometric"]!["basis"]!.GetValue<string>() == "document_entourage", "The document's projection is not reported");
            // New items without a projection take the drawing's.
            var tree = Success(Call("place_entourage", Write(("itemId", "tree.axo-round"), ("x", 700), ("y", 600), ("count", 4), ("spread", 150), ("seed", 2))));
            var p = AxoProjection.Of(EntourageProjection.Military);
            var group = w.doc.Layers.Where(l => l.ParentId == Guid.Parse(tree["groupId"]!.GetValue<string>())).ToArray();
            Check(group.Length == 4 && group.All(l => l.Entourage!.Projection == EntourageProjection.Military && p.Ground(EntourageRenderer.Anchor(l) - new Point(700, 600)).Length <= 150.01), "The scatter did not stay in its ground disc");
            var restyled = Success(Call("place_entourage", Write(("layerId", layer.Id.ToString()), ("projection", "isometric"))));
            layer = w.doc.Layers.Single(l => l.Id == layer.Id);
            Check(layer.Entourage!.Projection == EntourageProjection.Isometric && restyled["entourage"]!["projection"]!.GetValue<string>() == "isometric" && (EntourageRenderer.Anchor(layer) - new Point(300, 600)).Length < .05, "Restyling the projection moved the item");
            var before = w.doc.Snapshot();
            var refused = Call("place_entourage", Write(("itemId", "tree.plan-lobed"), ("x", 10), ("y", 10), ("projection", "isometric")));
            Check(refused["ok"]?.GetValue<bool>() == false && refused["error"]?["code"]?.GetValue<string>() == "invalid_arguments" && SameDocument(before, w.doc), "A projection for a plan item was accepted");
            var wrong = Call("place_entourage", Write(("itemId", "tree.axo-round"), ("x", 10), ("y", 10), ("projection", "oblique")));
            Check(wrong["ok"]?.GetValue<bool>() == false && SameDocument(before, w.doc), "An unknown projection was accepted");
            var capabilities = Success(Call("get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 11 && capabilities["entourage"]!["views"]!.AsArray().Any(v => v!.GetValue<string>() == "axonometric")
                && capabilities["entourage"]!["projections"]!.AsArray().Count == 3, "Capabilities do not advertise the axonometric view");
        });
    }
}
