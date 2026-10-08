using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

// 지도 포스터 · 대지 위치도 from OpenStreetMap data: the File menu and design-panel entries, the map
// dialog, building the document off the UI thread, the inspector's map section (theme and group
// colour) and the AI connection's create_map. Online data needs the user's consent for each request
// in the dialog; the AI connection may download only when the user allowed it in that consent window
// for this run (never saved, never on start-up).
public sealed partial class MainWindow
{
    // Self-tests answer the dialogs and replace the file pickers and the network layer.
    internal Func<MapDialog, bool>? mapDialogHandler;
    internal Func<MapConsentDialog, bool>? mapConsentHandler;
    internal Func<string?>? mapFilePicker;
    internal Func<byte[], string, string?>? mapSaveHandler;
    internal MapDownload? mapDownload;
    /// <summary>The AI connection may request online map data in this run. Set only from the consent window.</summary>
    internal bool automationMapDownloads;
    MapOptions? lastMapOptions;

    MapDownload MapNetwork => mapDownload ?? MapDownload.Online;
    Window? MapOwner => headlessTesting ? null : this;

    void AddMapMenu(MenuItem file)
    {
        var anchor = file.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Header, "레이어로 가져오기…"));
        int index = anchor == null ? file.Items.Count : file.Items.IndexOf(anchor) + 1;
        foreach (var (label, action, tip) in new (string, Action, string)[]
        {
            ("지도 만들기 (OSM 파일)…", CreateMapFromFile, "openstreetmap.org에서 내보낸 .osm 파일로 지도 포스터나 대지 위치도를 만듭니다. 인터넷을 쓰지 않습니다."),
            ("인터넷에서 지도 데이터 받기…", CreateMapOnline, "보내는 내용을 확인한 뒤 OpenStreetMap 공개 서버에서 작은 영역의 데이터를 받아 지도를 만듭니다.")
        })
        {
            var item = new MenuItem { Header = label, Foreground = Theme.Text, ToolTip = tip };
            item.Click += (_, _) => Guard(action);
            file.Items.Insert(index++, item);
        }
    }

    // Design panel: the map entries next to 만들기.
    void AddMapActions(StackPanel panel)
    {
        WorkspaceSection(panel, "지도", "OpenStreetMap 데이터로 지도 포스터나 대지 위치도를 편집할 수 있는 벡터 레이어로 만듭니다.");
        WorkspaceCommands(panel,
            (Theme.Glyphs.Map, "지도 파일로", CreateMapFromFile, "openstreetmap.org에서 내보낸 .osm 파일로 지도 포스터·대지 위치도 만들기", "지도 만들기 (OSM 파일)"),
            (Theme.Glyphs.Globe, "인터넷 데이터로", CreateMapOnline, "보내는 내용을 확인한 뒤 공개 서버에서 작은 영역의 지도 데이터를 받아 만들기", "인터넷에서 지도 데이터 받기"));
    }

    internal void CreateMapFromFile() => _ = CreateMapAsync(MapSource.File);
    internal void CreateMapOnline() => _ = CreateMapAsync(MapSource.Online);

    string? PickMapFile()
    {
        if (mapFilePicker != null) return mapFilePicker();
        var open = new OpenFileDialog { Title = "지도 데이터 열기", Filter = MapImport.Filter, CheckFileExists = true };
        return open.ShowDialog(this) == true ? open.FileName : null;
    }

    string? SaveOsmFile(byte[] data, string name)
    {
        if (mapSaveHandler != null) return mapSaveHandler(data, name);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var save = new SaveFileDialog { Title = "받은 지도 데이터 저장", Filter = "OpenStreetMap 데이터|*.osm", DefaultExt = ".osm", AddExtension = true, FileName = name + ".osm" };
        if (save.ShowDialog(this) != true) return null;
        ProjectStore.AtomicWrite(save.FileName, s => s.Write(data));
        return save.FileName;
    }

    MapConsentAnswer AskMapConsent(MapRequest request, Window? owner)
    {
        var dialog = new MapConsentDialog(headlessTesting ? null : owner ?? this, request, automationMapDownloads);
        bool agreed = mapConsentHandler?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!agreed || !dialog.Answer.Agreed) return new(false, automationMapDownloads);
        automationMapDownloads = dialog.Answer.AllowAutomation;
        return dialog.Answer;
    }

    internal MapDialog CreateMapDialog(MapSource source) =>
        new(MapOwner, source, new MapDialogServices(PickMapFile, AskMapConsent, MapNetwork, SaveOsmFile), lastMapOptions);

    /// <summary>The dialog (file first for a file map), then the map as a new document. True when one was made.</summary>
    internal async Task<bool> CreateMapAsync(MapSource source, string? path = null)
    {
        CommitFocusedInspectorField(); CancelGesture();
        if (tabs.Count >= 8) { status.Text = "열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요."; return false; }
        if (source == MapSource.File && (path ??= PickMapFile()) == null) return false;
        var dialog = CreateMapDialog(source);
        if (path != null) dialog.InitialPath = path;
        bool accepted = mapDialogHandler?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!accepted || dialog.Data == null) return false;
        // The next map starts from these choices, but not from this place, scale or text.
        lastMapOptions = dialog.Options with { Title = "", Subtitle = "", SiteLatitude = null, SiteLongitude = null, CenterLatitude = null, CenterLongitude = null, MetersPerPixel = null };
        return await BuildMapAsync(dialog.Data, dialog.Options);
    }

    /// <summary>Builds the map on an isolated STA (Esc cancels) and opens it as a new document.</summary>
    internal async Task<bool> BuildMapAsync(MapData data, MapOptions options)
    {
        if (tabs.Count >= 8) { status.Text = "열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요."; return false; }
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        status.Text = "지도 만드는 중…  Esc: 취소";
        try
        {
            var document = await CompatibilityImport.OnSta(() => MapComposer.Build(data, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { status.Text = "지도 만들기를 취소했습니다."; return false; }
            AddTab(document, null); doc.Revision = Guid.NewGuid(); SetWorkspaceMode(true); Refresh(false);
            var root = doc.Layers.First(l => l.Map is { IsRoot: true });
            status.Text = Loc.Format("지도를 만들었습니다 · 1 px ≈ {0} m · 데이터 © OpenStreetMap contributors", root.Map!.MetersPerPixel.ToString(root.Map.MetersPerPixel < 10 ? "0.##" : "0", CultureInfo.InvariantCulture));
            return true;
        }
        catch (OperationCanceledException) { status.Text = "지도 만들기를 취소했습니다."; return false; }
        catch (Exception error) { if (headlessTesting) throw; MessageDialog.Show(this, error.Message, "지도 만들기"); return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    // ---- inspector ---------------------------------------------------------------------------

    void AddMapProperties(Layer layer)
    {
        // Most documents have no map: skip the ancestor walk for them.
        if (layer.Map == null && !doc.Layers.Exists(l => l.Map is { IsRoot: true })) return;
        if (MapStyling.Root(doc, layer) is not { Map: { } tag } root) return;
        properties.Children.Add(Theme.Section("지도"));
        double scale = Math.Abs(root.Scale * root.ScaleX);
        double mpp = tag.MetersPerPixel / Math.Max(1e-9, scale);
        string where = string.Format(CultureInfo.InvariantCulture, "{0:0.0000}° {1}, {2:0.0000}° {3}",
            Math.Abs(tag.CenterLatitude), tag.CenterLatitude >= 0 ? "N" : "S", Math.Abs(tag.CenterLongitude), tag.CenterLongitude >= 0 ? "E" : "W");
        var about = Theme.Label(Loc.Format("{0} · 1 px ≈ {1} m · 중심 {2}", Loc.T(tag.Template == MapTemplate.SiteLocation ? "대지 위치도" : "지도 포스터"),
            mpp.ToString(mpp < 10 ? "0.##" : "0", CultureInfo.InvariantCulture), where), Theme.CaptionSize, Theme.Muted);
        about.TextWrapping = TextWrapping.Wrap; about.Margin = new Thickness(2, 0, 2, 6); Loc.Keep(about);
        properties.Children.Add(about);
        bool locked = IsLockedWithParents(root); var rootId = root.Id; var boundDocument = doc;
        var theme = PropertyRows.Choice("지도 테마");
        foreach (var item in MapThemes.All)
        {
            var option = new ComboBoxItem { Content = item.Name, Tag = item.Key, ToolTip = item.Description };
            theme.Items.Add(option); if (item.Key == tag.Theme) theme.SelectedItem = option;
        }
        theme.IsEnabled = !locked;
        theme.SelectionChanged += (_, _) =>
        {
            if (!ReferenceEquals(doc, boundDocument) || theme.SelectedItem is not ComboBoxItem { Tag: string key } || key == (doc.Layers.Find(l => l.Id == rootId)?.Map?.Theme)) return;
            _ = ApplyMapThemeAsync(rootId, key);
        };
        properties.Children.Add(PropertyRows.Field("지도 테마", theme));
        if (!ReferenceEquals(layer, root) && layer.Map is { } part)
        {
            var id = layer.Id;
            properties.Children.Add(InspectorAction(layer.Kind == LayerKind.Group ? "이 묶음 색 바꾸기" : "이 레이어 색 바꾸기", () => RecolorMapLayer(id),
                "선택한 지도 묶음(예: 도로, 물)이나 레이어를 한 가지 색으로 바꿉니다. 테마를 다시 고르면 테마 색으로 돌아갑니다.", layer, Theme.Glyphs.Palette));
        }
        var note = Theme.Label("지도를 공개하거나 인쇄할 때는 ‘© OpenStreetMap contributors’ 출처 표기를 지우지 마세요(ODbL).", Theme.CaptionSize, Theme.Subtle);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(2, 4, 2, 8);
        properties.Children.Add(note);
    }

    /// <summary>Recolours a whole map to a theme on an STA as one undo step. True when applied.</summary>
    internal async Task<bool> ApplyMapThemeAsync(Guid rootId, string themeKey)
    {
        if (!HasDocument) return false;
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var candidate = doc.Snapshot();
        status.Text = "지도 색 바꾸는 중…";
        try
        {
            await CompatibilityImport.OnSta(() => MapStyling.ApplyTheme(candidate, rootId, themeKey, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return false;
            if (!ReferenceEquals(document, doc) || revision != doc.Revision) { status.Text = "색을 바꾸는 동안 문서가 바뀌어 적용하지 않았습니다. 다시 고르세요."; return false; }
            Edit("지도 테마", () => { doc = candidate; maskEditing = false; });
            status.Text = Loc.Format("지도 테마를 ‘{0}’(으)로 바꿨습니다.", MapThemes.Get(themeKey).Name);
            return true;
        }
        catch (OperationCanceledException) { status.Text = "지도 색 바꾸기를 취소했습니다."; return false; }
        catch (Exception error) { if (headlessTesting) throw; MessageDialog.Show(this, error.Message, "지도 테마"); return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    // Self-tests pick the colour instead of the picker.
    internal Func<Color, Color?>? mapColorPicker;

    void RecolorMapLayer(Guid id)
    {
        if (doc.Layers.Find(l => l.Id == id) is not { } layer) return;
        if (IsLockedWithParents(layer)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        var current = MapThemes.Get(MapStyling.Root(doc, layer)?.Map?.Theme).Color(layer.Map?.Role ?? "");
        var picked = mapColorPicker != null ? mapColorPicker(VectorShapes.Color(current)) : Dialogs.ColorPicker(this, VectorShapes.Color(current), "지도 색");
        if (picked is not { } color) return;
        var candidate = doc.Snapshot();
        int changed = MapStyling.Recolor(candidate, id, color);
        if (changed == 0) { status.Text = "바꿀 색이 없습니다."; return; }
        Edit("지도 색 바꾸기", () => { doc = candidate; maskEditing = false; });
        status.Text = Loc.Format("지도 레이어 {0}개의 색을 바꿨습니다.", changed);
    }

    // ---- AI connection: create_map -----------------------------------------------------------

    async Task<JsonObject> AutomationCreateMapAsync(JsonObject args, CancellationToken token)
    {
        if (tabs.Count >= 8) throw new AutomationFault("document_limit", "문서는 최대 8개까지 열 수 있습니다.");
        var initialTab = activeTab >= 0 ? tabs[activeTab] : null; var initialRevision = doc.Revision;
        string source = AString(args, "source", args.ContainsKey("path") ? "file" : "online");
        var template = AString(args, "template", "poster") == "site" ? MapTemplate.SiteLocation : MapTemplate.Poster;
        MapData data; string name; string? place = null, note = null; (double Lat, double Lon)? onlineCenter = null;
        try
        {
            if (source == "file")
            {
                string path = AutomationPath(args);
                if (!File.Exists(path)) throw new FileNotFoundException("지도 데이터 파일을 찾을 수 없습니다.", path);
                data = await Task.Run(() => MapImport.Read(path, null, token), token);
                name = Path.GetFileNameWithoutExtension(path);
            }
            else
            {
                if (!automationMapDownloads)
                    throw new AutomationFault("online_map_not_allowed", "인터넷 지도 데이터는 사용자가 앱의 ‘인터넷에서 지도 데이터 받기’ 동의 창에서 AI 연결에 허용한 경우에만 받을 수 있습니다.");
                double lat, lon;
                if (args.ContainsKey("place"))
                {
                    var found = await MapNetwork.SearchAsync(MapConsent.Grant(MapDownload.Search(AString(args, "place"))), token);
                    if (found.Count == 0) throw new AutomationFault("place_not_found", "장소를 찾지 못했습니다. 다른 이름이나 좌표(centerLatitude, centerLongitude)로 요청하세요.");
                    lat = found[0].Latitude; lon = found[0].Longitude; place = found[0].Name;
                }
                else { lat = ANumber(args, "centerLatitude"); lon = ANumber(args, "centerLongitude"); }
                var request = MapDownload.Area(lat, lon, ANumber(args, "radius", 1000), ABool(args, "buildings", true));
                status.Text = Loc.Format("AI 연결이 인터넷에서 지도 데이터를 받는 중… {0}", request.Endpoint.Host);
                (data, _) = await MapNetwork.DownloadAsync(MapConsent.Grant(request), null, token);
                onlineCenter = (lat, lon);
                name = place?.Split(',')[0].Trim() is { Length: > 0 } brief ? brief : string.Format(CultureInfo.InvariantCulture, "{0:0.0000}, {1:0.0000}", lat, lon);
                note = Loc.Format("AI 연결이 인터넷에서 지도 데이터를 받았습니다 · {0}", request.Endpoint.Host);
            }
        }
        catch (MapDownloadException e) { throw new AutomationFault("map_download_failed", e.Message); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or System.Text.Json.JsonException) { throw new AutomationFault("map_data_invalid", e.Message); }
        catch (ArgumentException e) { throw new AutomationFault("invalid_arguments", e.Message); }

        var defaults = MapOptions.For(template);
        double dpi = ANumber(args, "dpi", defaults.Dpi);
        double[] rings = args.ContainsKey("rings")
            ? AString(args, "rings").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => double.Parse(r, CultureInfo.InvariantCulture)).ToArray()
            : defaults.Rings;
        (double Lat, double Lon)? site = args.ContainsKey("siteLatitude") ? (ANumber(args, "siteLatitude"), ANumber(args, "siteLongitude"))
            : template == MapTemplate.SiteLocation ? onlineCenter ?? (data.Bounds.CenterLatitude, data.Bounds.CenterLongitude) : null;
        var options = defaults with
        {
            Theme = AString(args, "theme", defaults.Theme),
            Width = (int)ANumber(args, "width", defaults.Width), Height = (int)ANumber(args, "height", defaults.Height), Dpi = dpi,
            Title = AString(args, "title", template == MapTemplate.SiteLocation ? defaults.Title : name),
            Subtitle = AString(args, "subtitle", template == MapTemplate.SiteLocation ? name : ""),
            Coordinates = ABool(args, "coordinates", true), Buildings = ABool(args, "buildings", true), Paths = ABool(args, "paths", true),
            Rail = ABool(args, "rail", true), Green = ABool(args, "green", true), Water = ABool(args, "water", true),
            LineWeight = ANumber(args, "lineWeight", 1), Fade = ABool(args, "fade", defaults.Fade), Frame = ABool(args, "frame", false),
            NorthArrow = ABool(args, "northArrow", defaults.NorthArrow), ScaleBar = ABool(args, "scaleBar", defaults.ScaleBar),
            Marker = AString(args, "marker", "circle") == "pin" ? MapMarker.Pin : MapMarker.Circle, SiteLabel = AString(args, "siteLabel", defaults.SiteLabel),
            Rings = rings, SiteLatitude = site?.Lat, SiteLongitude = site?.Lon,
            MetersPerPixel = args.ContainsKey("scale") ? ANumber(args, "scale") * .0254 / dpi : null
        };
        try { options.Validate(); }
        catch (InvalidDataException e) { throw new AutomationFault("invalid_arguments", e.Message); }
        Document document;
        try { document = await CompatibilityImport.OnSta(() => MapComposer.Build(data, options, token), token); }
        catch (InvalidDataException e) { throw new AutomationFault("map_data_invalid", e.Message); }
        RequireAutomationIdle(token);
        if ((activeTab >= 0 ? tabs[activeTab] : null) != initialTab || doc.Revision != initialRevision)
            throw new AutomationFault("workspace_changed", "작업 중인 문서가 변경되었습니다. 상태를 확인하고 다시 시도하세요.");
        AddTab(document, null); doc.Revision = Guid.NewGuid(); Refresh(false);
        if (note != null) status.Text = note;
        var root = doc.Layers.First(l => l.Map is { IsRoot: true }); var tag = root.Map!; var bounds = tag.View.Bounds;
        var result = AutomationResult(root.Id);
        result["map"] = new JsonObject
        {
            ["layerId"] = root.Id.ToString(), ["template"] = template == MapTemplate.SiteLocation ? "site" : "poster", ["theme"] = tag.Theme, ["source"] = source,
            ["metersPerPixel"] = tag.MetersPerPixel, ["scaleDenominator"] = Math.Round(tag.View.ScaleDenominator(doc.Dpi)), ["dpi"] = doc.Dpi,
            ["centerLatitude"] = tag.CenterLatitude, ["centerLongitude"] = tag.CenterLongitude,
            ["siteLatitude"] = tag.SiteLatitude, ["siteLongitude"] = tag.SiteLongitude, ["place"] = place,
            ["frame"] = new JsonObject { ["x"] = tag.FrameX, ["y"] = tag.FrameY, ["width"] = tag.FrameWidth, ["height"] = tag.FrameHeight },
            ["bounds"] = new JsonObject { ["south"] = bounds.South, ["west"] = bounds.West, ["north"] = bounds.North, ["east"] = bounds.East },
            ["features"] = new JsonObject(data.Counts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))),
            ["attribution"] = MapComposer.Attribution
        };
        result["warnings"] = new JsonArray(data.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
        return result;
    }
}
