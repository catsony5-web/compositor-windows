using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 점경 in the editor: the palette (categories, views, search, click to place), placing at a point,
// in a selection and as a scatter group, the properties of placed items, the scale, shadows, 내 점경
// import into an injected library, the entry points, and query_entourage / place_entourage.
public sealed partial class MainWindow
{
    internal static void RunEntourageTests(Action<string, Action> test, string directory)
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
        static EntourageChoice Choice(string id) => new(EntourageLibrary.Find(id)!, null);
        static Button[] Tiles(FrameworkElement palette) => Descendants(palette).OfType<Button>().Where(b => b.Tag is EntourageChoice).ToArray();

        Window("the palette filters by category, view and search and places by click", w =>
        {
            w.AddTab(Board(), null);
            var palette = w.EntouragePalette("test");
            var people = Tiles(palette);
            Check(people.Length == EntourageLibrary.All.Count(i => i.Category == EntourageCategory.People && i.View == EntourageView.Elevation), "The people tab lists the wrong items");
            Check(people.All(b => AutomationProperties.GetName(b).Length > 0 && b.ToolTip is string tip && tip.Contains("끌어 놓으면")), "Tiles lack names or the drag hint");
            w.entourageView = EntourageView.Plan; w.entourageTab = 1; w.RefreshEntouragePalettes();
            Check(Tiles(palette).Select(b => ((EntourageChoice)b.Tag).Id).SequenceEqual(EntourageLibrary.All.Where(i => i.Category == EntourageCategory.Plants && i.View == EntourageView.Plan).Select(i => i.Id)), "The plan trees are not listed");
            var search = Descendants(palette).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "점경 검색");
            search.Text = "자전거";
            var found = Tiles(palette).Select(b => ((EntourageChoice)b.Tag).Id).ToArray();
            Check(found.Contains("bike.side") && found.Contains("bike.plan") && found.Contains("person.cyclist") && !found.Contains("tree.round"), "Search did not look across categories: " + string.Join(", ", found));
            search.Text = ""; w.entourageTab = 0; w.entourageView = EntourageView.Elevation; w.RefreshEntouragePalettes();
            int layers = w.doc.Layers.Count;
            Click(Tiles(palette).Single(b => ((EntourageChoice)b.Tag).Id == "person.walking"));
            var placed = w.doc.Active!;
            Check(w.doc.Layers.Count == layers + 1 && placed.Entourage?.ItemId == "person.walking" && placed.Kind == LayerKind.Vector && w.history.UndoLabel == "점경 넣기"
                && w.selectedLayers.SetEquals([placed.Id]) && w.tool == Tool.Move, "Clicking a tile did not place one selected item in one step");
            var anchor = EntourageRenderer.Anchor(placed);
            Check(anchor.X > 0 && anchor.X < w.doc.Width && anchor.Y > 0 && anchor.Y <= w.doc.Height, "The item was not placed in the view");
            Check(placed.Name == "걷는 사람" && Math.Abs(placed.Entourage!.PixelsPerMeter - Math.Round(96 / 2.54, 2)) < .01, "The default name or scale is wrong");
            w.Undo(); Check(w.doc.Layers.Count == layers, "Undo did not remove the item");
        });

        Window("items stand on a point or in the selection, and scatter into one group", w =>
        {
            w.AddTab(Board(), null);
            var person = w.PlaceEntourage(Choice("person.standing"), new Point(300, 650)).Single();
            Check((EntourageRenderer.Anchor(person) - new Point(300, 650)).Length < .01, "A dropped item does not stand on the drop point");
            var tree = w.PlaceEntourage(Choice("tree.plan-lobed"), new Point(800, 300)).Single();
            Check((EntourageRenderer.Anchor(tree) - new Point(800, 300)).Length < .01, "A plan symbol is not centred on the drop point");
            w.selection = new Selection(new Rect(100, 200, 600, 300));
            var inSelection = w.PlaceEntourage(Choice("person.walking")).Single();
            Check((EntourageRenderer.Anchor(inSelection) - new Point(400, 500)).Length < .01, "An item placed with a selection does not stand on its bottom edge");
            w.selection = null;
            w.entourageScatter = true; w.entourageScatterCount = 6;
            var scattered = w.PlaceEntourage(Choice("tree.round"), new Point(600, 700));
            var group = w.doc.Active!;
            Check(scattered.Count == 6 && group.Kind == LayerKind.Group && scattered.All(l => l.ParentId == group.Id) && w.history.UndoLabel == "점경 흩어 놓기", "Scatter did not make one group in one step");
            Check(scattered.All(l => Math.Abs(EntourageRenderer.Anchor(l).Y - 700) < .01) && scattered.Select(l => l.Entourage!.Variant).Distinct().Count() > 1 && scattered.Any(l => l.FlipX) && scattered.Any(l => !l.FlipX),
                "Scattered trees do not stand on the line with varied shapes");
            var again = w.PlaceEntourage(Choice("tree.round"), new Point(600, 700));
            Check(!again.Select(l => EntourageRenderer.Anchor(l).X).SequenceEqual(scattered.Select(l => EntourageRenderer.Anchor(l).X)), "A second scatter repeated the first");
            w.Undo(); w.Undo(); Check(!w.doc.Layers.Any(l => l.Kind == LayerKind.Group), "Undo did not remove the scatter group");
            w.entourageScatter = false;
        });

        Window("the properties edit height, fill, colour, flip, variant and several items at once", w =>
        {
            w.AddTab(Board(), null);
            var a = w.PlaceEntourage(Choice("person.walking"), new Point(300, 600)).Single();
            w.BuildProperties();
            Check(Descendants(w.properties).OfType<SectionHeader>().Any(h => h.Key == "점경") && Descendants(w.properties).OfType<TextBox>().Any(t => AutomationProperties.GetName(t) == "점경 높이"), "The 점경 section is missing");
            var fill = Descendants(w.properties).OfType<SegmentedChoice<EntourageFill>>().Single();
            fill.Select(EntourageFill.Solid);
            Check(w.doc.Active!.Entourage!.Fill == EntourageFill.Solid && w.history.UndoLabel == "점경 채우기", "The fill choice did not restyle the item");
            var stand = EntourageRenderer.Anchor(w.doc.Active!); int tall = w.doc.Active!.Pixels.Height;
            w.EditEntourage("점경 높이", s => s with { Meters = 3.4 }, true);
            Check((EntourageRenderer.Anchor(w.doc.Active!) - stand).Length < .05 && w.doc.Active!.Pixels.Height > tall * 1.8, "Changing the height moved the item or did not resize it");
            w.SetEntourageLineColor(Color.FromRgb(200, 30, 30));
            Check(w.doc.Active!.Entourage!.LineArgb == 0xFFC81E1E, "The line colour was not applied");
            w.FlipEntourage();
            Check(w.doc.Active!.FlipX && (EntourageRenderer.Anchor(w.doc.Active!) - stand).Length < .05, "Flipping moved the item");
            int variant = w.doc.Active!.Entourage!.Variant;
            w.EditEntourage("점경 모양", s => s with { Variant = (s.Variant + 1) % 3 });
            Check(w.doc.Active!.Entourage!.Variant != variant, "The variant did not change");
            // A new item matches the colour chosen last; two selected items change together.
            var b = w.PlaceEntourage(Choice("tree.round"), new Point(700, 600)).Single();
            Check(b.Entourage!.LineArgb == 0xFFC81E1E, "New items do not take the last line colour");
            w.selectedLayers.Clear(); w.selectedLayers.Add(w.doc.Layers.First(l => l.Entourage?.ItemId == "person.walking").Id); w.selectedLayers.Add(b.Id);
            w.EditEntourage("점경 채우기", s => s with { Fill = EntourageFill.Gray });
            Check(w.doc.Layers.Where(l => l.Entourage != null).All(l => l.Entourage!.Fill == EntourageFill.Gray), "Several selected items did not change together");
            w.doc.Layers.First(l => l.Entourage != null).Locked = true;
            Check(w.EntourageTargets().Length == 1, "A locked item was a target");
        });

        Window("the scale follows the document, the user and a resized item", w =>
        {
            w.AddTab(Board(), null);
            Check(Math.Abs(w.EntourageScale - Math.Round(96 / 2.54, 2)) < .01, "The default scale is not 1:100 at the document DPI");
            w.SetEntourageScale(50);
            var person = w.PlaceEntourage(Choice("person.standing"), new Point(300, 600)).Single();
            Check(person.Entourage!.PixelsPerMeter == 50, "The scale set in the panel was not used");
            w.EditLayer("크기", l => l.Scale = 2);
            w.UseEntourageAsScale();
            Check(Math.Abs(w.EntourageScale - 100) < .01, "A resized item did not set the scale");
            var tree = w.PlaceEntourage(Choice("tree.round"), new Point(800, 600)).Single();
            Check(tree.Entourage!.PixelsPerMeter == 100, "New items do not use the item's scale");
            w.doc.ActiveId = tree.Id; w.selectedLayers.Clear(); w.selectedLayers.Add(tree.Id);
            w.EditLayer("크기", l => l.Scale = .5);
            w.MatchEntourageScale();
            Check(w.doc.Layers.Where(l => l.Entourage != null).All(l => l.Entourage!.PixelsPerMeter == 50 && l.Scale == 1), "Matching the scale did not redraw every item at it");
        });

        Window("shadows of placed items lie on the ground or follow the plan height", w =>
        {
            w.AddTab(Board(), null);
            w.shadowDialogHandler = _ => true;
            var person = w.PlaceEntourage(Choice("person.walking"), new Point(300, 600)).Single();
            w.AddShadow();
            var shadow = w.doc.Active!;
            Check(shadow.Shadow is { Projection: ShadowProjection.Ground } spec && spec.Sources.SequenceEqual([person.Id]), "An elevation item did not get a ground shadow");
            var tree = w.PlaceEntourage(Choice("tree.plan-lobed"), new Point(800, 300)).Single();
            w.AddShadow();
            var plan = w.doc.Active!.Shadow!;
            double expected = tree.Entourage!.SizePixels * EntourageLibrary.Find("tree.plan-lobed")!.ShadowHeight;
            Check(plan.Projection == ShadowProjection.Plan && Math.Abs(plan.Height - expected) < .2, $"A plan tree's shadow height is {plan.Height}, expected {expected}");
        });

        Window("내 점경 imports into the injected library, places and stays out of the user's folder", w =>
        {
            Check(w.EntourageDirectory == null && w.CustomEntourageLibrary().Count == 0, "Headless checks read the real 내 점경 folder");
            string folder = Path.Combine(directory, "entourage-library-" + Guid.NewGuid().ToString("N"));
            w.entourageStore = folder;
            w.entourageLibrary = null;
            w.AddTab(Board(), null);
            string image = Path.Combine(directory, "entourage-drawing.png");
            using (var file = File.Create(image)) EntourageSampleDrawing().WritePng(file);
            w.entourageDialogRunner = dialog => { dialog.Synchronous = true; dialog.SetMode(EntourageImportMode.LineDrawing); dialog.SetName("손그림 나무"); dialog.SetMeters(6); dialog.SetCategory(EntourageCategory.Plants); return dialog.Accept(); };
            var item = w.ImportCustomEntourage(image)!;
            Check(item.LineDrawing && item.Category == EntourageCategory.Plants && item.Meters == 6, "The dialog choices were not kept");
            Check(File.Exists(Path.Combine(folder, item.Id.ToString("N") + ".png")) && EntourageStore.Load(folder).Single().Name == "손그림 나무", "The item was not stored in the library folder");
            var placed = w.doc.Active!;
            Check(placed.Entourage is { IsCustom: true, LineDrawing: true } spec && spec.Source == item.Pixels && placed.Kind == LayerKind.Raster && w.entourageTab == EntourageCustomTab, "The imported item was not placed");
            var palette = w.EntouragePalette("custom");
            Check(Tiles(palette).Any(b => ((EntourageChoice)b.Tag).Id == item.ItemId) && Tiles(palette).Single().ContextMenu != null, "내 점경 does not list the item");
            w.RenameCustomEntourage(item.Id, "나무 스케치");
            Check(EntourageStore.Load(folder).Single().Name == "나무 스케치", "Renaming was not saved");
            w.RemoveCustomEntourage(item.Id);
            Check(EntourageStore.Load(folder).Count == 0 && w.CustomEntourageChoices().Any(c => c.ItemId == item.ItemId), "Removing from the library dropped the document's copy");
            w.entourageDialogRunner = _ => false;
            Check(w.ImportCustomEntourage(image) == null && EntourageStore.Load(folder).Count == 0, "A cancelled import stored an item");
        });

        Window("menu, dock tab, profile section and the library command", w =>
        {
            w.AddTab(Board(), null);
            var commands = w.BuildCommandRegistry();
            Check(commands.Any(c => c.Id == "menu:레이어/점경 라이브러리…") && commands.Any(c => c.Id == "menu:레이어/내 점경 추가…"), "The layer menu lacks the 점경 entries");
            w.SetWorkspaceMode(false); w.ShowEntourageLibrary();
            Check(w.designWorkspace && w.studioContents[0].Children.OfType<SectionHeader>().Any(h => h.Key == "점경"), "The library command did not open the design panel's 점경 section");
            w.SetUserProfile(UserProfiles.ArchitectureId);
            foreach (bool design in new[] { true, false })
            {
                w.SetWorkspaceMode(design);
                var keys = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
                Check(Array.IndexOf(keys, UserProfiles.Entourage) == Array.IndexOf(keys, UserProfiles.SketchPhoto) + 1, "건축학과 does not place 점경 after the drawing work: " + string.Join(", ", keys));
            }
            w.SetScreenStyle(true);
            try
            {
                Check(w.ShowDockTab("entourage") && w.DockTabVisible("entourage"), "The dock has no 점경 tab");
                Check(WorkspaceLayoutStore.DockGroupKeys.Single(k => k.Key == "color").Tabs.Contains("entourage"), "The dock layout does not know the 점경 tab");
                var saved = WorkspaceLayoutStore.Sanitize(new WorkspaceLayout { DockGroups = [new DockGroupLayout("color", 1, false, "entourage")] })!;
                Check(saved.DockGroups!.Single().Tab == "entourage", "The open 점경 tab is not saved with the layout");
            }
            finally { w.SetScreenStyle(false); }
        });

        Window("query_entourage and place_entourage place, scatter, restyle, batch and reject", w =>
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
            var library = Success(Call("query_entourage", new JsonObject()));
            Check(library["count"]!.GetValue<int>() == EntourageLibrary.All.Count && library["documentId"] == null, "The library query without a document failed");
            var trees = Success(Call("query_entourage", new JsonObject { ["category"] = "plants", ["view"] = "plan" }))["items"]!.AsArray();
            Check(trees.Count > 0 && trees.All(t => t!["category"]!.GetValue<string>() == "plants" && t["view"]!.GetValue<string>() == "plan"), "Filtering did not work");
            w.AddTab(Board(), null);
            var placed = Success(Call("place_entourage", Write(("itemId", "person.walking"), ("x", 300), ("y", 600), ("height", 1.8), ("fill", "gray"), ("lineColor", "#204060"), ("flip", true))));
            var layer = w.doc.Layers.Single(l => l.Id == Guid.Parse(placed["layerId"]!.GetValue<string>()));
            Check(layer.Entourage is { Meters: 1.8, Fill: EntourageFill.Gray, LineArgb: 0xFF204060 } && layer.FlipX && (EntourageRenderer.Anchor(layer) - new Point(300, 600)).Length < .01
                && w.history.UndoLabel == "AI · place_entourage" && placed["entourage"]!["anchor"]!["x"]!.GetValue<double>() == 300, "place_entourage did not place the styled item at its anchor");
            var scattered = Success(Call("place_entourage", Write(("itemId", "tree.plan-lobed"), ("x", 800), ("y", 300), ("count", 5), ("spread", 200), ("seed", 4))));
            var group = Guid.Parse(scattered["groupId"]!.GetValue<string>());
            Check(scattered["layerId"]!.GetValue<string>() == group.ToString() && scattered["layerIds"]!.AsArray().Count == 5 && w.doc.Layers.Count(l => l.ParentId == group) == 5
                && w.doc.Layers.Where(l => l.ParentId == group).All(l => (EntourageRenderer.Anchor(l) - new Point(800, 300)).Length <= 200.01), "The scatter was not grouped inside its radius");
            var restyled = Success(Call("place_entourage", Write(("layerId", layer.Id.ToString()), ("height", 2.2), ("fill", "solid"), ("flip", false))));
            layer = w.doc.Layers.Single(l => l.Id == layer.Id);
            Check(layer.Entourage is { Meters: 2.2, Fill: EntourageFill.Solid } && !layer.FlipX && (EntourageRenderer.Anchor(layer) - new Point(300, 600)).Length < .05 && restyled["entourage"]!["height"]!.GetValue<double>() == 2.2,
                "Restyling did not keep the anchor");
            var state = Success(Call("query_entourage", new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString() }));
            Check(state["placedCount"]!.GetValue<int>() == 6 && state["scale"]!["basis"]!.GetValue<string>() == "document_entourage", "The document's entourage and scale are not listed");
            var described = Success(Call("get_layer", new JsonObject { ["documentId"] = w.tabs[w.activeTab].Id.ToString(), ["layerId"] = layer.Id.ToString() }));
            Check(described["layer"]!["entourage"]!["itemId"]!.GetValue<string>() == "person.walking", "get_layer does not describe the entourage");
            var batch = Success(Call("apply_batch", Write(("operationId", Guid.NewGuid().ToString()), ("steps", new JsonArray(
                new JsonObject { ["command"] = "place_entourage", ["ref"] = "car", ["arguments"] = new JsonObject { ["itemId"] = "car.sedan", ["x"] = 600, ["y"] = 700 } },
                new JsonObject { ["command"] = "place_entourage", ["arguments"] = new JsonObject { ["layerId"] = "@car", ["fill"] = "none" } },
                new JsonObject { ["command"] = "set_layer", ["arguments"] = new JsonObject { ["layerId"] = "@car", ["opacity"] = .8 } })))));
            var car = w.doc.Layers.Single(l => l.Entourage?.ItemId == "car.sedan");
            Check(batch["undoSteps"]!.GetValue<int>() == 1 && car.Entourage!.Fill == EntourageFill.None && car.Opacity == .8, "The batch did not place and restyle in one step");
            var before = w.doc.Snapshot(); bool undo = w.history.CanUndo;
            void Rejected(string code, params (string Key, JsonNode? Value)[] values)
            {
                Failure(Call("place_entourage", Write(values)), code);
                Check(SameDocument(before, w.doc) && w.history.CanUndo == undo, "A rejected request changed the document");
            }
            Rejected("invalid_arguments", ("itemId", "person.walking"), ("x", 10));
            Rejected("invalid_arguments", ("itemId", "Person Walking"), ("x", 10), ("y", 10));
            Rejected("entourage_not_found", ("itemId", "person.flying"), ("x", 10), ("y", 10));
            Rejected("invalid_arguments", ("itemId", "tree.round"), ("x", 10), ("y", 10), ("height", 150), ("pixelsPerMeter", 100));
            Rejected("invalid_arguments", ("layerId", layer.Id.ToString()), ("itemId", "tree.round"));
            Rejected("invalid_arguments", ("itemId", "tree.round"), ("x", 10), ("y", 10), ("seed", 3));
            Rejected("wrong_layer_kind", ("layerId", w.doc.Layers[0].Id.ToString()), ("fill", "none"));
            Rejected("layer_not_found", ("layerId", Guid.NewGuid().ToString()), ("fill", "none"));
            var capabilities = Success(Call("get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 9 && capabilities["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "place_entourage")
                && capabilities["batch"]!["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "place_entourage") && capabilities["entourage"]!["count"]!.GetValue<int>() == EntourageLibrary.All.Count,
                "Capabilities do not advertise the entourage commands");
        });
    }
}
