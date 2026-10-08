using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// 지도 포스터 · 대지 위치도 in the editor: the menu and design-panel entries, the dialog (file data,
// preview, themes, site clicks), the consent window in front of every online request (a refusal sends
// nothing), building the document, the inspector's theme and group colour as undo steps, and the
// create_map AI command with its session-only online permission. The network is always a fake.
public sealed partial class MainWindow
{
    internal static void RunMapTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(directory, "maps-ui"); Directory.CreateDirectory(root);
        string Fixture()
        {
            string file = Path.Combine(root, "town.osm");
            if (!File.Exists(file)) File.WriteAllText(file, MapFixtures.Osm());
            return file;
        }
        static bool Wait(Func<Task> start) => WaitOnDispatcher(async () => { await start(); return true; });
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        void Window(string name, Action<MainWindow> action) => test("map editor: " + name, () =>
        {
            MapDownload.ClearCache();
            var w = new MainWindow(null) { headlessTesting = true };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(w.Dispatcher));
            try { action(w); }
            finally
            {
                w.renderCts?.Cancel(); w.jobCts?.Cancel();
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                if (w.HasDocument) w.history.MarkSaved(w.doc);
                w.StopRenderingForShutdown(); SynchronizationContext.SetSynchronizationContext(previous); MapDownload.ClearCache();
            }
        });

        Window("the File menu, the command palette and the design panel offer both map entries", w =>
        {
            var file = w.mainMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "파일"));
            var labels = file.Items.OfType<MenuItem>().Select(i => i.Header as string).ToArray();
            int import = Array.IndexOf(labels, "레이어로 가져오기…"), map = Array.IndexOf(labels, "지도 만들기 (OSM 파일)…"), online = Array.IndexOf(labels, "인터넷에서 지도 데이터 받기…");
            Check(map > import && online == map + 1, "The map items are not after 레이어로 가져오기…: " + string.Join(" | ", labels));
            Check(file.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "지도 만들기 (OSM 파일)…")).IsEnabled, "The map item needs an open document");
            var commands = w.BuildCommandRegistry();
            Check(commands.Any(c => c.Id == "menu:파일/지도 만들기 (OSM 파일)…") && commands.Any(c => c.Id == "menu:파일/인터넷에서 지도 데이터 받기…"), "The command palette does not list the map commands");
            var panel = new StackPanel(); w.BuildDesignActions(panel);
            var names = Descendants(panel).OfType<Button>()
                .Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToArray();
            Check(names.Contains("지도 만들기 (OSM 파일)") && names.Contains("인터넷에서 지도 데이터 받기"), "The design panel has no map commands");
        });

        Window("a map file becomes a poster document through the dialog, with a preview and the chosen theme", w =>
        {
            w.mapFilePicker = Fixture; MapDialog? shown = null;
            w.mapDialogHandler = dialog =>
            {
                shown = dialog;
                Check(dialog.InitialPath == Fixture() && dialog.Source == MapSource.File && !dialog.Create.IsEnabled, "The dialog did not receive the picked file");
                Check(WaitOnDispatcher(() => dialog.OpenFileAsync(dialog.InitialPath)) && dialog.Data != null && dialog.Create.IsEnabled, "The dialog did not read the file");
                Check(dialog.TitleBox.Text == "town" && dialog.SourceName == "town.osm", "The title does not default to the file name");
                dialog.TitleBox.Text = "Seoul"; dialog.SubtitleBox.Text = "South Korea"; dialog.SelectTheme("dark");
                dialog.RefreshPreviewNow();
                Check(dialog.PreviewRaster is { } image && Math.Max(image.Width, image.Height) <= MapDialog.PreviewLongSide + 1 && dialog.PreviewTag?.Theme == "dark" && dialog.Info.Contains("1 px"), "The preview was not drawn");
                return true;
            };
            Check(WaitOnDispatcher(() => w.CreateMapAsync(MapSource.File)), "The map was not made");
            var rootLayer = w.doc.Layers.Single(l => l.Map is { IsRoot: true });
            Check(w.tabs.Count == 1 && rootLayer.Map!.Theme == "dark" && w.doc.Width == shown!.Options.Width && MapTests.Class(w.doc, MapClasses.Title).Text!.Content == "SEOUL", "The document does not follow the dialog");
            Check(w.history.Dirty(w.doc) && !w.history.CanUndo && w.designWorkspace, "A new map must ask to be saved and open in the design workspace");
            Check(w.status.Text.Contains("OpenStreetMap"), "The status does not credit the data");
            w.mapDialogHandler = _ => false;
            Check(!WaitOnDispatcher(() => w.CreateMapAsync(MapSource.File)) && w.tabs.Count == 1, "A cancelled dialog made a map");
            w.mapFilePicker = () => null; bool asked = false; w.mapDialogHandler = _ => asked = true;
            Check(!WaitOnDispatcher(() => w.CreateMapAsync(MapSource.File)) && !asked, "Cancelling the file picker opened the dialog");
        });

        Window("the site template's preview click moves the site, and rings, marker and scale follow the dialog", w =>
        {
            var dialog = w.CreateMapDialog(MapSource.File);
            dialog.SetData(MapTests.Town(), "town", "town.osm");
            dialog.SetTemplate(MapTemplate.SiteLocation);
            Check(dialog.Options.Template == MapTemplate.SiteLocation && dialog.Options.Title == "대지 위치도" && dialog.Options.Subtitle == "town" && dialog.Options.NorthArrow && dialog.Options.ScaleBar, "Site defaults were not applied");
            Check(dialog.Options.Width == MapDialog.SiteSizes[0].Width && (dialog.SizeChoice.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Contains("A3") == true, "The site sizes were not offered");
            dialog.RefreshPreviewNow(); var tag = dialog.PreviewTag!;
            var target = new Point(tag.FrameX + tag.FrameWidth * .3, tag.FrameY + tag.FrameHeight * .6);
            dialog.ClickPreview(target); var (lat, lon) = tag.View.ToGeo(target);
            Check(Math.Abs(dialog.Options.SiteLatitude!.Value - lat) < 1e-9 && Math.Abs(dialog.Options.SiteLongitude!.Value - lon) < 1e-9, "The click did not move the site");
            dialog.RefreshPreviewNow(); var moved = dialog.PreviewTag!; var at = moved.View.ToPixel(lat, lon);
            Check(Math.Abs(at.X - target.X) < 1.5 && Math.Abs(at.Y - target.Y) < 1.5, "The map moved under the clicked site");
            dialog.SetTemplate(MapTemplate.Poster);
            Check(dialog.Options.Title == "town" && dialog.Options.SiteLatitude == null && !dialog.Options.NorthArrow, "Switching back to the poster kept site settings");
            dialog.Close();
        });

        Window("online data: the consent window comes first, a refusal sends nothing, consent sends exactly the shown area", w =>
        {
            var http = new FakeMapHttp(); w.mapDownload = new MapDownload(http, TimeSpan.Zero);
            MapConsentDialog? asked = null; bool agree = false, allowAi = false;
            w.mapConsentHandler = consent => { asked = consent; if (agree) { consent.AutomationOption.IsChecked = allowAi; consent.Agree(); } return agree; };
            var dialog = w.CreateMapDialog(MapSource.Online);
            Check(!dialog.Create.IsEnabled && http.Calls.Count == 0, "The online dialog sent something or allowed an empty map");
            var center = MapFixtures.At(MapFixtures.Bounds, .5, .5);
            dialog.LatitudeBox.Text = center.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            dialog.LongitudeBox.Text = center.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Wait(dialog.DownloadAsync);
            Check(asked != null && http.Calls.Count == 0 && dialog.Data == null && !w.automationMapDownloads, "A refused download was sent");
            Check(asked!.Request.Kind == MapRequestKind.AreaData && asked.Rows.Any(r => r.Caption == "보내는 곳" && r.Value.Contains("overpass-api.de")) && asked.Rows.Any(r => r.Caption == "보내는 내용" && r.Value.Contains("남서"))
                && asked.Rows.Any(r => r.Caption == "보내지 않는 것") && asked.AutomationOption.IsChecked == false, "The consent window does not state the server, the area and what is not sent");
            agree = true; allowAi = true;
            Wait(dialog.DownloadAsync);
            Check(http.Calls.Count == 1 && dialog.Data != null && dialog.DownloadedOsm is { Length: > 0 } && w.automationMapDownloads, "The agreed download did not happen once");
            var area = asked.Request.Area!.Value; string body = System.Net.WebUtility.UrlDecode(http.Calls[0].Body);
            Check(body.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[bbox:{0:0.#####},{1:0.#####},{2:0.#####},{3:0.#####}]", area.South, area.West, area.North, area.East)), "The sent area differs from the one shown");
            dialog.QueryBox.Text = "서울시청"; agree = false;
            Wait(dialog.SearchAsync);
            Check(http.Calls.Count == 1 && asked.Request.Kind == MapRequestKind.PlaceSearch && asked.Rows.Any(r => r.Value.Contains("서울시청")), "A refused search was sent");
            agree = true; allowAi = false;
            Wait(dialog.SearchAsync);
            Check(http.Calls.Count == 2 && dialog.Places.Count == 1 && !w.automationMapDownloads, "The search did not run or the AI permission stayed on");
            string? saved = null; w.mapSaveHandler = (bytes, name) => { saved = Path.Combine(root, name + ".osm"); File.WriteAllBytes(saved, bytes); return saved; };
            dialog = w.CreateMapDialog(MapSource.Online); dialog.LatitudeBox.Text = "37.56"; dialog.LongitudeBox.Text = "126.97";
            Wait(dialog.DownloadAsync);
            Check(dialog.Data != null && http.Calls.Count == 3, "A second dialog did not download");
            var save = Descendants((DependencyObject)dialog.Content).OfType<Button>().FirstOrDefault(b => b.Content as string == "받은 데이터를 .osm 파일로 저장…");
            save?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(saved != null && MapImport.Read(saved).Counts.GetValueOrDefault(MapClasses.Building) == MapFixtures.Buildings, "The downloaded data could not be saved as an .osm file");
            dialog.LatitudeBox.Text = "north"; int before = http.Calls.Count; Wait(dialog.DownloadAsync);
            Check(http.Calls.Count == before && dialog.Info.Contains("위도"), "An invalid center was sent");
        });

        Window("the inspector changes the theme and a group's colour, each as one undo step", w =>
        {
            w.AddTab(MapComposer.Build(MapTests.Town(), new MapOptions { Width = 800, Height = 1000, Title = "Seoul" }), null);
            var rootLayer = w.doc.Layers.Single(l => l.Map is { IsRoot: true }); var roads = MapTests.Class(w.doc, MapClasses.RoadGroup);
            w.SelectLayer(roads.Id); w.BuildProperties();
            var themeBox = w.properties.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<ComboBox>()).FirstOrDefault(c => System.Windows.Automation.AutomationProperties.GetName(c) == "지도 테마");
            Check(themeBox != null && themeBox.Items.Count == MapThemes.All.Length && (themeBox.SelectedItem as ComboBoxItem)?.Tag as string == "light", "The inspector has no map theme choice");
            Check(WaitOnDispatcher(() => w.ApplyMapThemeAsync(rootLayer.Id, "green")) && w.history.UndoLabel == "지도 테마", "The theme change is not one undo step");
            Check(w.doc.Layers.Single(l => l.Map is { IsRoot: true }).Map!.Theme == "green" && MapTests.Scene(MapTests.Class(w.doc, MapClasses.Major)).Items.All(i => i.Color == MapThemes.Get("green").Major), "The theme was not applied");
            w.mapColorPicker = _ => Colors.OrangeRed; w.SelectLayer(MapTests.Class(w.doc, MapClasses.RoadGroup).Id); w.RecolorMapLayer(w.doc.ActiveId);
            Check(w.history.UndoLabel == "지도 색 바꾸기" && MapTests.Scene(MapTests.Class(w.doc, MapClasses.Motorway)).Items.All(i => i.Color == VectorShapes.Argb(Colors.OrangeRed)), "The group colour was not one undo step");
            w.Undo(); w.Undo();
            Check(w.doc.Layers.Single(l => l.Map is { IsRoot: true }).Map!.Theme == "light" && MapTests.Scene(MapTests.Class(w.doc, MapClasses.Motorway)).Items.All(i => i.Color == MapThemes.Get("light").Motorway), "Undo did not restore the colours");
            w.SelectLayer(MapTests.Class(w.doc, MapClasses.Attribution).Id); w.BuildProperties();
            Check(w.properties.Children.OfType<FrameworkElement>().Any(e => e is TextBlock t && t.Text.Contains("OpenStreetMap")), "The inspector does not remind about the attribution");
        });

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

        Window("create_map AI command: a file map, a refused online map, then an allowed one", w =>
        {
            var file = Ok(Call(w, "create_map", new JsonObject { ["path"] = Fixture(), ["title"] = "Seoul", ["theme"] = "navy", ["width"] = 900, ["height"] = 1200, ["includeLayers"] = false }));
            var map = file["map"]!.AsObject();
            Check(map["template"]!.GetValue<string>() == "poster" && map["theme"]!.GetValue<string>() == "navy" && map["source"]!.GetValue<string>() == "file" && map["metersPerPixel"]!.GetValue<double>() > 0
                && map["attribution"]!.GetValue<string>() == "© OpenStreetMap contributors" && map["features"]![MapClasses.Building]!.GetValue<int>() == MapFixtures.Buildings, "The file map result is incomplete: " + map.ToJsonString());
            Check(w.tabs.Count == 1 && file["layerId"]!.GetValue<string>() == map["layerId"]!.GetValue<string>() && w.doc.Width == 900, "The map document was not opened");
            var http = new FakeMapHttp(); w.mapDownload = new MapDownload(http, TimeSpan.Zero);
            var center = MapFixtures.At(MapFixtures.Bounds, .5, .5);
            var online = new JsonObject { ["source"] = "online", ["centerLatitude"] = center.Latitude, ["centerLongitude"] = center.Longitude, ["radius"] = 600, ["template"] = "site", ["width"] = 1200, ["height"] = 900, ["scale"] = 5000, ["rings"] = "200,400", ["marker"] = "pin" };
            Check(Code(Call(w, "create_map", online)) == "online_map_not_allowed" && http.Calls.Count == 0 && w.tabs.Count == 1, "The AI connection downloaded without the user's permission");
            w.automationMapDownloads = true;
            var site = Ok(Call(w, "create_map", online))["map"]!.AsObject();
            Check(http.Calls.Count == 1 && site["template"]!.GetValue<string>() == "site" && site["source"]!.GetValue<string>() == "online" && Math.Abs(site["scaleDenominator"]!.GetValue<double>() - 5000) < 1
                && Math.Abs(site["siteLatitude"]!.GetValue<double>() - center.Latitude) < 1e-9 && w.tabs.Count == 2, "The allowed online site map is wrong: " + site.ToJsonString());
            Check(MapTests.Class(w.doc, MapClasses.Marker) != null && MapTests.Scene(MapTests.Class(w.doc, MapClasses.Rings)).Items.Length == 2 && w.status.Text.Contains("overpass-api.de"), "Site parts or the status note are missing");
            Check(Code(Call(w, "create_map", new JsonObject { ["path"] = Fixture(), ["place"] = "서울" })) == "invalid_arguments", "place with a file was accepted");
            Check(Code(Call(w, "create_map", new JsonObject { ["source"] = "online", ["place"] = "서울", ["centerLatitude"] = 1, ["centerLongitude"] = 1 })) == "invalid_arguments", "place and a center together were accepted");
            Check(Code(Call(w, "create_map", new JsonObject { ["path"] = Fixture(), ["width"] = 8000, ["height"] = 8000 })) == "invalid_arguments", "A huge map was accepted");
            Check(Code(Call(w, "create_map", new JsonObject { ["path"] = Fixture(), ["rings"] = "a,b" })) == "invalid_arguments", "Bad rings were accepted");
            Check(Code(Call(w, "create_map", new JsonObject { ["source"] = "online", ["centerLatitude"] = 1, ["centerLongitude"] = 1, ["radius"] = 9000 })) == "invalid_arguments", "A large online area was accepted");
            string bad = Path.Combine(root, "bad.osm"); File.WriteAllText(bad, "<svg/>");
            Check(Code(Call(w, "create_map", new JsonObject { ["path"] = bad })) == "map_data_invalid", "A broken map file was not reported");
            http.Failure = new MapDownloadException("인터넷에 연결할 수 없습니다.");
            Check(Code(Call(w, "create_map", new JsonObject { ["source"] = "online", ["centerLatitude"] = 10, ["centerLongitude"] = 10 })) == "map_download_failed", "An offline download was not reported");
            var capabilities = Ok(Call(w, "get_capabilities", new JsonObject()));
            Check(capabilities["contractVersion"]!.GetValue<int>() == 9 && capabilities["commands"]!.AsArray().Any(c => c!.GetValue<string>() == "create_map") && capabilities["maps"]!["attributionRequired"]!.GetValue<bool>()
                && capabilities["maps"]!["online"]!["endpoints"]!.AsArray().Count == 2, "Capabilities do not describe create_map");
        });
    }
}
