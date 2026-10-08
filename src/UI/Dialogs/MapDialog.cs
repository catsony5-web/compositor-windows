using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace Compositor.Windows;

public enum MapSource { File, Online }

/// <summary>What the map dialog asks of its owner: files, consent and the (optional) network layer.</summary>
public sealed record MapDialogServices(Func<string?> PickFile, Func<MapRequest, Window?, MapConsentAnswer> AskConsent, MapDownload Download, Func<byte[], string, string?> SaveOsm);

/// <summary>
/// 지도 만들기: OpenStreetMap data from a file (or, as a separate explicit action, from the public
/// servers after the user's consent), a template (지도 포스터 · 대지 위치도), a theme, text and drawn
/// classes, with a live preview built by the same <see cref="MapComposer"/> at a smaller size.
/// Clicking the preview of a site map moves the site.
/// </summary>
public sealed class MapDialog : Window
{
    public const double PreviewLongSide = 860;
    internal static readonly (string Label, int Width, int Height, double Dpi)[] PosterSizes =
    [
        ("세로 30 × 40 cm", 1772, 2362, 150), ("A3 세로", 1754, 2480, 150), ("A2 세로", 2480, 3508, 150), ("정사각형 30 cm", 1772, 1772, 150), ("가로 40 × 30 cm", 2362, 1772, 150)
    ];
    internal static readonly (string Label, int Width, int Height, double Dpi)[] SiteSizes =
    [
        ("A3 가로", 2480, 1754, 150), ("A4 가로", 1754, 1240, 150), ("A3 세로", 1754, 2480, 150), ("A4 세로", 1240, 1754, 150)
    ];
    internal static readonly int[] Scales = [0, 1000, 2500, 5000, 10000, 25000];
    internal static readonly (string Label, double[] Meters)[] RingChoices = [("없음", []), ("250 · 500 m", [250, 500]), ("500 m · 1 km", [500, 1000]), ("1 · 2 km", [1000, 2000])];
    internal static readonly double[] Radii = [500, 1000, 1500, 2500];

    public MapOptions Options { get; private set; }
    public MapData? Data { get; private set; }
    public string SourceName { get; private set; } = "";
    public MapSource Source { get; }
    internal byte[]? DownloadedOsm { get; private set; }
    internal Raster? PreviewRaster { get; private set; }
    internal MapTag? PreviewTag { get; private set; }
    internal string Info => info.Text;
    internal IReadOnlyList<MapPlace> Places => places;
    internal SegmentedChoice<MapTemplate> TemplateChoice { get; }
    internal ComboBox SizeChoice { get; } = PropertyRows.Choice("크기");
    internal TextBox TitleBox { get; } = PropertyRows.Input("", "제목");
    internal TextBox SubtitleBox { get; } = PropertyRows.Input("", "부제");
    internal TextBox QueryBox { get; } = PropertyRows.Input("", "찾을 장소");
    internal TextBox LatitudeBox { get; } = PropertyRows.NumberBox("37.5665", "중심 위도");
    internal TextBox LongitudeBox { get; } = PropertyRows.NumberBox("126.978", "중심 경도");
    internal Button Create { get; }

    readonly MapDialogServices services;
    readonly Image preview = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14), Cursor = Cursors.Arrow };
    readonly TextBlock placeholder = DialogShell.Subtitle("");
    readonly TextBlock info = DialogShell.Note(""), dataNote = DialogShell.Note(""), siteNote = DialogShell.Note("");
    readonly List<MapPlace> places = [];
    readonly ComboBox placeChoice = PropertyRows.Choice("찾은 장소");
    readonly SegmentedChoice<double> radiusChoice;
    readonly SegmentedChoice<MapMarker> markerChoice;
    readonly SegmentedChoice<int> ringChoice;
    readonly ComboBox scaleChoice = PropertyRows.Choice("축척");
    readonly TextBox siteLabelBox = PropertyRows.Input("", "대지 표시 글자");
    readonly CheckBox onlineBuildings;
    readonly Button saveOsm, downloadButton, searchButton, cancelWork;
    readonly StackPanel posterOptions = new(), siteOptions = new(), onlinePanel = new(), filePanel = new();
    readonly Dictionary<string, Button> themeButtons = [];
    readonly List<CheckBox> optionBoxes = [];
    CancellationTokenSource? previewCts, workCts;
    readonly SemaphoreSlim renderGate = new(1, 1);
    long version; bool closed, synchronizing;

    public MapDialog(Window? owner, MapSource source, MapDialogServices services, MapOptions? initial = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        Source = source; Options = initial ?? MapOptions.For(MapTemplate.Poster);
        string caption = source == MapSource.Online ? "인터넷 지도 데이터로 지도 만들기" : "지도 만들기";
        DialogShell.Prepare(this, owner, caption);
        Width = 1180; Height = 860; MinWidth = 900; MinHeight = 600;

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(392) });
        var stage = new Grid();
        stage.Children.Add(preview);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextAlignment = TextAlignment.Center; placeholder.TextWrapping = TextWrapping.Wrap; placeholder.MaxWidth = 420; placeholder.Margin = new Thickness(24);
        stage.Children.Add(placeholder);
        var card = DialogShell.Card(stage); card.Margin = new Thickness(0, 0, 18, 0); root.Children.Add(card);
        preview.MouseLeftButtonDown += (_, e) => { if (preview.Source != null && PreviewRaster != null) ClickPreview(Scale(e.GetPosition(preview))); };

        var side = new Grid(); side.RowDefinitions.Add(new RowDefinition()); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(side, 1); root.Children.Add(side);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        side.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(DialogShell.Title(caption));
        var subtitle = DialogShell.Subtitle(source == MapSource.Online
            ? "장소나 좌표를 정한 뒤 직접 받기를 누르면, 보내는 내용을 확인한 다음 OpenStreetMap 공개 서버에서 작은 영역의 데이터를 받습니다."
            : "openstreetmap.org의 ‘내보내기’로 받은 .osm 파일(또는 GeoJSON)로 길·물·녹지·건물을 편집할 수 있는 벡터 레이어로 그립니다.");
        subtitle.TextWrapping = TextWrapping.Wrap; panel.Children.Add(subtitle);

        // ---- data
        panel.Children.Add(Theme.Section("지도 데이터"));
        dataNote.TextWrapping = TextWrapping.Wrap; dataNote.Margin = new Thickness(1, 2, 1, 6); Loc.Keep(dataNote);
        var openFile = Theme.ActionRow("다른 지도 파일 열기", () => _ = OpenFileAsync(null), "openstreetmap.org에서 내보낸 .osm 또는 GeoJSON 파일을 고릅니다.", Theme.Glyphs.Open);
        filePanel.Children.Add(openFile);
        panel.Children.Add(dataNote);
        panel.Children.Add(filePanel);

        onlinePanel.Children.Add(DialogShell.FieldLabel("장소 찾기 (선택)"));
        var searchRow = new Grid(); searchRow.ColumnDefinitions.Add(new ColumnDefinition()); searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(QueryBox);
        searchButton = Theme.Button("찾기…", () => _ = SearchAsync(), "검색어를 OpenStreetMap 장소 검색으로 보냅니다. 보내기 전에 확인합니다.");
        searchButton.MinHeight = Theme.ControlHeight; searchButton.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(searchButton, 1); searchRow.Children.Add(searchButton);
        onlinePanel.Children.Add(searchRow);
        placeChoice.Visibility = Visibility.Collapsed; placeChoice.Margin = new Thickness(0, 6, 0, 0);
        placeChoice.SelectionChanged += (_, _) => { if (placeChoice.SelectedIndex >= 0 && placeChoice.SelectedIndex < places.Count) UsePlace(places[placeChoice.SelectedIndex]); };
        onlinePanel.Children.Add(placeChoice);
        onlinePanel.Children.Add(PropertyRows.Pair("중심 위도", LatitudeBox, "중심 경도", LongitudeBox, new Thickness(0, 8, 0, 0)));
        onlinePanel.Children.Add(DialogShell.FieldLabel("범위 (중심에서 반경)"));
        radiusChoice = new SegmentedChoice<double>(Radii.Select(r => (r, r >= 1000 ? (r / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km" : r.ToString(CultureInfo.InvariantCulture) + " m")), 1000);
        radiusChoice.Changed += _ => UpdateOnlineOptions();
        onlinePanel.Children.Add(radiusChoice);
        onlineBuildings = new CheckBox { Content = new TextBlock { Text = "건물도 받기 (반경 1.5 km 이하)", TextWrapping = TextWrapping.Wrap }, IsChecked = true, Foreground = Theme.Text, Margin = new Thickness(1, 8, 1, 4) };
        onlinePanel.Children.Add(onlineBuildings);
        downloadButton = Theme.Button("인터넷에서 지도 데이터 받기…", () => _ = DownloadAsync(), "보내는 영역과 서버를 확인한 뒤 데이터를 받습니다.");
        downloadButton.MinHeight = 34; downloadButton.Margin = new Thickness(0, 6, 0, 2); downloadButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        onlinePanel.Children.Add(downloadButton);
        var onlineNote = DialogShell.Note("받기 전에 보내는 내용과 받는 곳을 보여 드립니다. 앱을 열 때나 미리보기 중에는 인터넷을 쓰지 않습니다.");
        onlineNote.TextWrapping = TextWrapping.Wrap; onlinePanel.Children.Add(onlineNote);
        saveOsm = Theme.Button("받은 데이터를 .osm 파일로 저장…", SaveDownloaded, "다음에는 인터넷 없이 이 파일로 지도를 만들 수 있습니다.");
        saveOsm.MinHeight = Theme.ControlHeight; saveOsm.Margin = new Thickness(0, 8, 0, 0); saveOsm.Visibility = Visibility.Collapsed; saveOsm.HorizontalAlignment = HorizontalAlignment.Left;
        onlinePanel.Children.Add(saveOsm);
        panel.Children.Add(onlinePanel);
        filePanel.Visibility = source == MapSource.File ? Visibility.Visible : Visibility.Collapsed;
        onlinePanel.Visibility = source == MapSource.Online ? Visibility.Visible : Visibility.Collapsed;
        cancelWork = Theme.Button("받기 취소", () => workCts?.Cancel(), "읽거나 받는 중인 지도 데이터를 멈춥니다.");
        cancelWork.MinHeight = Theme.ControlHeight; cancelWork.Visibility = Visibility.Collapsed; cancelWork.HorizontalAlignment = HorizontalAlignment.Left; cancelWork.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(cancelWork);

        // ---- template
        panel.Children.Add(Theme.Section("지도 양식"));
        TemplateChoice = new SegmentedChoice<MapTemplate>([(MapTemplate.Poster, "지도 포스터"), (MapTemplate.SiteLocation, "대지 위치도")], Options.Template);
        TemplateChoice.Changed += SetTemplate;
        panel.Children.Add(TemplateChoice);
        var templateNote = DialogShell.Note("포스터는 넓은 자간의 제목·부제·좌표를, 위치도는 대지 표시·방위표·실제 축척 막대를 넣습니다. 모두 만든 뒤에도 편집할 수 있습니다.");
        templateNote.TextWrapping = TextWrapping.Wrap; panel.Children.Add(templateNote);

        // ---- theme
        panel.Children.Add(Theme.Section("지도 색"));
        var themes = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 2, -3, 4) };
        foreach (var theme in MapThemes.All)
        {
            var button = ThemeButton(theme); themeButtons[theme.Key] = button; themes.Children.Add(button);
        }
        panel.Children.Add(themes);

        // ---- text
        panel.Children.Add(Theme.Section("지도 글자"));
        panel.Children.Add(PropertyRows.Field("제목", TitleBox));
        panel.Children.Add(PropertyRows.Field("부제", SubtitleBox));
        TitleBox.TextChanged += (_, _) => { if (!synchronizing) { Options = Options with { Title = TitleBox.Text }; Schedule(); } };
        SubtitleBox.TextChanged += (_, _) => { if (!synchronizing) { Options = Options with { Subtitle = SubtitleBox.Text }; Schedule(); } };
        Loc.Keep(TitleBox); Loc.Keep(SubtitleBox);
        panel.Children.Add(Option("좌표 표시", o => o.Coordinates, (o, v) => o with { Coordinates = v }, "지도 가운데(위치도는 대지)의 위도·경도를 적습니다."));

        // ---- classes
        panel.Children.Add(Theme.Section("그릴 것"));
        var classes = new WrapPanel();
        classes.Children.Add(Option("건물", o => o.Buildings, (o, v) => o with { Buildings = v }, null));
        classes.Children.Add(Option("보행로", o => o.Paths, (o, v) => o with { Paths = v }, null));
        classes.Children.Add(Option("철도", o => o.Rail, (o, v) => o with { Rail = v }, null));
        classes.Children.Add(Option("녹지", o => o.Green, (o, v) => o with { Green = v }, null));
        classes.Children.Add(Option("물", o => o.Water, (o, v) => o with { Water = v }, null));
        foreach (var box in classes.Children.OfType<CheckBox>()) box.Margin = new Thickness(1, 4, 14, 4);
        panel.Children.Add(classes);

        // ---- size and lines
        panel.Children.Add(Theme.Section("크기와 선"));
        panel.Children.Add(PropertyRows.Field("크기", SizeChoice));
        SizeChoice.SelectionChanged += (_, _) =>
        {
            if (synchronizing || SizeChoice.SelectedItem is not ComboBoxItem { Tag: ValueTuple<int, int, double> size }) return;
            Options = Options with { Width = size.Item1, Height = size.Item2, Dpi = size.Item3 }; Schedule();
        };
        var weight = new ParameterSlider("선 굵기 %", 50, 250, Options.LineWeight * 100, 100) { ToolTip = "모든 길·철도·물줄기 선의 굵기" };
        weight.Changed += value => { Options = Options with { LineWeight = Math.Clamp(value / 100, .25, 4) }; Schedule(); };
        panel.Children.Add(weight);

        // ---- template options
        posterOptions.Children.Add(Option("가장자리 흐리게", o => o.Fade, (o, v) => o with { Fade = v }, "지도 가장자리와 제목 뒤를 바탕색으로 부드럽게 덮습니다."));
        posterOptions.Children.Add(Option("테두리와 여백", o => o.Frame, (o, v) => o with { Frame = v }, "지도 둘레에 여백과 가는 테두리를 두고 제목을 지도 아래에 둡니다."));
        panel.Children.Add(posterOptions);
        siteOptions.Children.Add(Theme.Section("대지 표시"));
        siteOptions.Children.Add(PropertyRows.Caption("표시 모양"));
        markerChoice = new SegmentedChoice<MapMarker>([(MapMarker.Circle, "원"), (MapMarker.Pin, "핀")], Options.Marker) { Margin = new Thickness(2, 0, 2, 8) };
        markerChoice.Changed += m => { if (synchronizing) return; Options = Options with { Marker = m }; Schedule(); };
        siteOptions.Children.Add(markerChoice);
        siteLabelBox.TextChanged += (_, _) => { if (synchronizing) return; Options = Options with { SiteLabel = siteLabelBox.Text }; Schedule(); };
        Loc.Keep(siteLabelBox);
        siteOptions.Children.Add(PropertyRows.Field("대지 표시 글자", siteLabelBox));
        siteOptions.Children.Add(PropertyRows.Caption("반경 원"));
        ringChoice = new SegmentedChoice<int>(RingChoices.Select((r, i) => (i, r.Label)), 0) { Margin = new Thickness(2, 0, 2, 8) };
        ringChoice.Changed += i => { if (synchronizing) return; Options = Options with { Rings = RingChoices[i].Meters }; Schedule(); };
        siteOptions.Children.Add(ringChoice);
        foreach (int denominator in Scales)
            scaleChoice.Items.Add(new ComboBoxItem { Content = denominator == 0 ? "자동 (데이터에 맞춤)" : "1:" + denominator.ToString("N0", CultureInfo.InvariantCulture), Tag = denominator });
        scaleChoice.SelectedIndex = 0;
        scaleChoice.SelectionChanged += (_, _) =>
        {
            if (synchronizing) return;
            int denominator = scaleChoice.SelectedItem is ComboBoxItem { Tag: int d } ? d : 0;
            Options = Options with { MetersPerPixel = denominator == 0 ? null : denominator * .0254 / Options.Dpi }; ScaleDenominator = denominator; Schedule();
        };
        siteOptions.Children.Add(PropertyRows.Field("축척", scaleChoice));
        var toggles = new WrapPanel();
        toggles.Children.Add(Option("방위표", o => o.NorthArrow, (o, v) => o with { NorthArrow = v }, null));
        toggles.Children.Add(Option("축척 막대", o => o.ScaleBar, (o, v) => o with { ScaleBar = v }, null));
        foreach (var box in toggles.Children.OfType<CheckBox>()) box.Margin = new Thickness(1, 4, 14, 4);
        siteOptions.Children.Add(toggles);
        siteNote.TextWrapping = TextWrapping.Wrap; siteOptions.Children.Add(siteNote);
        panel.Children.Add(siteOptions);

        info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(1, 12, 1, 4);
        System.Windows.Automation.AutomationProperties.SetName(info, "미리보기 상태"); panel.Children.Add(info);
        var license = DialogShell.Note("지도 데이터 © OpenStreetMap contributors (ODbL). 만든 지도에는 이 출처 표기가 항상 들어갑니다.");
        license.TextWrapping = TextWrapping.Wrap; panel.Children.Add(license);

        var cancel = DialogShell.Secondary("취소", Close); cancel.IsCancel = true;
        Create = DialogShell.Primary("지도 만들기", () => { if (TryCommit() && IsVisible) DialogResult = true; }); Create.IsDefault = true;
        var footer = DialogShell.Footer(cancel, Create); footer.Margin = new Thickness(0, 14, 10, 0); Grid.SetRow(footer, 1); side.Children.Add(footer);

        ScaleDenominator = 0;
        SyncFields(); UpdateTemplateOptions(); UpdateOnlineOptions(); UpdateDataNote();
        Loaded += (_, _) => { if (InitialPath != null && Data == null) _ = OpenFileAsync(InitialPath); else Schedule(); };
        Closed += (_, _) => { closed = true; version++; previewCts?.Cancel(); workCts?.Cancel(); };
    }

    internal int ScaleDenominator { get; private set; }
    /// <summary>A file the menu picked; read when the dialog opens.</summary>
    internal string? InitialPath { get; set; }

    CheckBox Option(string label, Func<MapOptions, bool> get, Func<MapOptions, bool, MapOptions> set, string? tip)
    {
        var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = get(Options), Foreground = Theme.Text, Margin = new Thickness(1, 6, 1, 4), ToolTip = tip, Tag = get };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        void Changed() { if (synchronizing) return; Options = set(Options, box.IsChecked == true); Schedule(); }
        box.Checked += (_, _) => Changed(); box.Unchecked += (_, _) => Changed();
        optionBoxes.Add(box);
        return box;
    }

    // A swatch of the theme: page colour, water, a main road and a local road, with its name below.
    Button ThemeButton(MapTheme theme)
    {
        Brush B(uint c) { var b = new SolidColorBrush(VectorShapes.Color(c)); b.Freeze(); return b; }
        var swatch = new Grid { Height = 40, ClipToBounds = true };
        swatch.Children.Add(new Rectangle { Fill = B(theme.Water), Width = 26, HorizontalAlignment = HorizontalAlignment.Right });
        swatch.Children.Add(new Rectangle { Fill = B(theme.Park), Width = 18, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 4) });
        swatch.Children.Add(new Rectangle { Fill = B(theme.Motorway), Height = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11, 0, 0) });
        swatch.Children.Add(new Rectangle { Fill = B(theme.Local), Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(36, 0, 0, 0) });
        var frame = new Border { Background = B(theme.Background), CornerRadius = new CornerRadius(5), Child = swatch, BorderBrush = Theme.Line, BorderThickness = new Thickness(1) };
        var name = Theme.Label(theme.Name, Theme.CaptionSize); name.Margin = new Thickness(0, 5, 0, 0); name.TextAlignment = TextAlignment.Center; name.HorizontalAlignment = HorizontalAlignment.Center;
        var content = new StackPanel(); content.Children.Add(frame); content.Children.Add(name);
        var button = Theme.Styled(new Button { Content = content, Padding = new Thickness(5), Margin = new Thickness(3), BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = theme.Description }, "InspectorAction");
        System.Windows.Automation.AutomationProperties.SetName(button, theme.Name);
        button.Click += (_, _) => SelectTheme(theme.Key);
        return button;
    }

    internal void SelectTheme(string key)
    {
        if (!MapThemes.Exists(key)) return;
        Options = Options with { Theme = key }; PaintThemes(); Schedule();
    }
    void PaintThemes()
    {
        foreach (var (key, button) in themeButtons)
        {
            bool on = key == Options.Theme;
            button.BorderBrush = on ? Theme.Accent : Brushes.Transparent; button.Background = on ? Theme.Selected : Brushes.Transparent;
        }
    }

    internal void SetTemplate(MapTemplate template)
    {
        if (synchronizing) return;
        var defaults = MapOptions.For(template); var previous = Options;
        string title = previous.Title, subtitle = previous.Subtitle;
        if (template == MapTemplate.SiteLocation) { if (string.IsNullOrWhiteSpace(subtitle)) subtitle = title; title = Loc.T("대지 위치도"); }
        else { if (title == Loc.T("대지 위치도")) title = string.IsNullOrWhiteSpace(subtitle) ? SourceName : subtitle; if (subtitle == title) subtitle = ""; }
        Options = previous with
        {
            Template = template, Width = defaults.Width, Height = defaults.Height, Dpi = defaults.Dpi, Fade = defaults.Fade, Frame = false,
            NorthArrow = defaults.NorthArrow, ScaleBar = defaults.ScaleBar, Rings = template == MapTemplate.SiteLocation ? defaults.Rings : [],
            Title = title, Subtitle = subtitle, MetersPerPixel = null, CenterLatitude = null, CenterLongitude = null,
            SiteLatitude = template == MapTemplate.SiteLocation ? previous.SiteLatitude ?? Data?.Bounds.CenterLatitude : previous.SiteLatitude,
            SiteLongitude = template == MapTemplate.SiteLocation ? previous.SiteLongitude ?? Data?.Bounds.CenterLongitude : previous.SiteLongitude
        };
        if (template == MapTemplate.Poster) Options = Options with { SiteLatitude = null, SiteLongitude = null };
        ScaleDenominator = 0;
        if (TemplateChoice.Selected != template) TemplateChoice.Select(template);
        SyncFields(); UpdateTemplateOptions(); Schedule();
    }

    void SyncFields()
    {
        synchronizing = true;
        try
        {
            TitleBox.Text = Options.Title; SubtitleBox.Text = Options.Subtitle;
            foreach (var box in optionBoxes) if (box.Tag is Func<MapOptions, bool> get) box.IsChecked = get(Options);
            SizeChoice.Items.Clear();
            var sizes = Options.Template == MapTemplate.SiteLocation ? SiteSizes : PosterSizes;
            foreach (var (label, w, h, dpi) in sizes)
                SizeChoice.Items.Add(new ComboBoxItem { Content = Loc.T(label) + " · " + w.ToString("N0", CultureInfo.InvariantCulture) + " × " + h.ToString("N0", CultureInfo.InvariantCulture) + " px", Tag = (w, h, dpi) });
            int index = Array.FindIndex(sizes, s => s.Width == Options.Width && s.Height == Options.Height);
            if (index < 0) { SizeChoice.Items.Add(new ComboBoxItem { Content = $"{Options.Width:N0} × {Options.Height:N0} px", Tag = (Options.Width, Options.Height, Options.Dpi) }); index = SizeChoice.Items.Count - 1; }
            SizeChoice.SelectedIndex = index;
            markerChoice.Select(Options.Marker); siteLabelBox.Text = Options.SiteLabel;
            ringChoice.Select(Math.Max(0, Array.FindIndex(RingChoices, r => r.Meters.SequenceEqual(Options.Rings))));
            scaleChoice.SelectedIndex = Math.Max(0, Array.IndexOf(Scales, ScaleDenominator));
            PaintThemes();
        }
        finally { synchronizing = false; }
    }

    void UpdateTemplateOptions()
    {
        bool site = Options.Template == MapTemplate.SiteLocation;
        posterOptions.Visibility = site ? Visibility.Collapsed : Visibility.Visible;
        siteOptions.Visibility = site ? Visibility.Visible : Visibility.Collapsed;
        UpdateSiteNote();
    }

    void UpdateSiteNote()
    {
        siteNote.Text = Options.SiteLatitude is { } lat && Options.SiteLongitude is { } lon
            ? Loc.Format("대지 {0} · 미리보기를 클릭하면 대지 위치를 옮깁니다.", string.Format(CultureInfo.InvariantCulture, "{0:0.00000}, {1:0.00000}", lat, lon))
            : Loc.T("미리보기를 클릭하면 대지 위치를 옮깁니다. 정하지 않으면 지도 가운데입니다.");
    }

    void UpdateOnlineOptions()
    {
        bool small = radiusChoice.Selected <= MapDownload.MaxBuildingRadius;
        onlineBuildings.IsEnabled = small;
        if (!small) onlineBuildings.IsChecked = false;
    }

    void UpdateDataNote()
    {
        if (Data == null)
        {
            dataNote.Text = Source == MapSource.Online ? Loc.T("아직 받은 데이터가 없습니다.") : Loc.T("지도 파일을 열어 주세요.");
            placeholder.Text = Source == MapSource.Online
                ? Loc.T("장소를 찾거나 중심 좌표를 넣고 ‘인터넷에서 지도 데이터 받기…’를 누르면 미리보기가 나타납니다.")
                : Loc.T("지도 파일을 열면 미리보기가 나타납니다.");
            placeholder.Visibility = Visibility.Visible; Create.IsEnabled = false;
            return;
        }
        var counts = Data.Counts;
        int Count(params string[] keys) => keys.Sum(k => counts.GetValueOrDefault(k));
        string summary = Loc.Format("길 {0} · 건물 {1} · 물 {2} · 녹지 {3}",
            Count(MapClasses.Roads).ToString("N0", CultureInfo.InvariantCulture), Count(MapClasses.Building).ToString("N0", CultureInfo.InvariantCulture),
            Count(MapClasses.Sea, MapClasses.WaterArea, MapClasses.River, MapClasses.Stream).ToString("N0", CultureInfo.InvariantCulture),
            Count(MapClasses.Park, MapClasses.Forest, MapClasses.Grass).ToString("N0", CultureInfo.InvariantCulture));
        var (w, h) = Data.Bounds.Meters;
        string extent = Loc.Format("범위 약 {0} × {1} km", (w / 1000).ToString("0.0", CultureInfo.InvariantCulture), (h / 1000).ToString("0.0", CultureInfo.InvariantCulture));
        dataNote.Text = SourceName + "\n" + summary + " · " + extent + (Data.Warnings.Count > 0 ? "\n" + string.Join("\n", Data.Warnings.Select(Loc.T)) : "");
        placeholder.Visibility = Visibility.Collapsed; Create.IsEnabled = true;
    }

    void SetBusy(string? message)
    {
        bool busy = message != null;
        cancelWork.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        downloadButton.IsEnabled = searchButton.IsEnabled = !busy;
        if (busy) info.Text = message!;
    }

    /// <summary>Reads a map file off the UI thread with progress; the preview follows. False when it failed.</summary>
    internal async Task<bool> OpenFileAsync(string? path)
    {
        path ??= services.PickFile();
        if (string.IsNullOrEmpty(path)) return false;
        workCts?.Cancel(); var cts = workCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => { if (ReferenceEquals(workCts, cts)) info.Text = Loc.Format("지도 데이터 읽는 중… {0}%", Math.Round(p * 100)); });
        SetBusy(Loc.T("지도 데이터 읽는 중…"));
        try
        {
            var data = await Task.Run(() => MapImport.Read(path, progress, cts.Token), cts.Token);
            if (closed) return false;
            UseData(data, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path));
            return true;
        }
        catch (OperationCanceledException) { info.Text = Loc.T("지도 데이터 읽기를 취소했습니다."); return false; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or System.Text.Json.JsonException)
        { info.Text = e.Message; return false; }
        finally { if (ReferenceEquals(workCts, cts)) { workCts = null; SetBusy(null); } cts.Dispose(); }
    }

    void UseData(MapData data, string title, string sourceName)
    {
        Data = data; SourceName = sourceName;
        bool site = Options.Template == MapTemplate.SiteLocation;
        Options = Options with
        {
            Title = site ? Options.Title : string.IsNullOrWhiteSpace(Options.Title) || Options.Title == Loc.T("지도") ? title : Options.Title,
            Subtitle = site && string.IsNullOrWhiteSpace(Options.Subtitle) ? title : Options.Subtitle,
            SiteLatitude = site ? data.Bounds.CenterLatitude : null, SiteLongitude = site ? data.Bounds.CenterLongitude : null, CenterLatitude = null, CenterLongitude = null
        };
        SyncFields(); UpdateDataNote(); UpdateSiteNote(); Schedule();
    }

    /// <summary>Uses data already read (the menu picked the file first, or a self-test).</summary>
    internal void SetData(MapData data, string title, string sourceName) => UseData(data, title, sourceName);

    bool TryCenter(out double latitude, out double longitude)
    {
        bool ok = double.TryParse(LatitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude) & double.TryParse(LongitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out longitude);
        ok = ok && double.IsFinite(latitude) && double.IsFinite(longitude) && Math.Abs(latitude) <= 85 && Math.Abs(longitude) <= 180;
        LatitudeBox.BorderBrush = ok ? Theme.Stroke : Theme.Danger; LongitudeBox.BorderBrush = ok ? Theme.Stroke : Theme.Danger;
        return ok;
    }

    /// <summary>Place search: consent for this exact query first, then one request.</summary>
    internal async Task SearchAsync()
    {
        MapRequest request;
        try { request = MapDownload.Search(QueryBox.Text); }
        catch (ArgumentException e) { info.Text = e.Message; return; }
        var answer = services.AskConsent(request, IsVisible ? this : null);
        if (!answer.Agreed) { info.Text = Loc.T("장소를 찾지 않았습니다. 인터넷으로 보낸 것은 없습니다."); return; }
        workCts?.Cancel(); var cts = workCts = new CancellationTokenSource();
        SetBusy(Loc.T("장소 찾는 중…"));
        try
        {
            var found = await services.Download.SearchAsync(MapConsent.Grant(request), cts.Token);
            if (closed) return;
            places.Clear(); places.AddRange(found); placeChoice.Items.Clear();
            foreach (var place in found) placeChoice.Items.Add(Loc.Keep(new ComboBoxItem { Content = place.Name }));
            placeChoice.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            info.Text = found.Count > 0 ? Loc.Format("장소 {0}곳을 찾았습니다. 하나를 고르면 중심 좌표가 바뀝니다.", found.Count) : Loc.T("찾은 장소가 없습니다. 다른 이름이나 주소로 찾아 보세요.");
            if (found.Count > 0) placeChoice.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { info.Text = Loc.T("장소 찾기를 취소했습니다."); }
        catch (MapDownloadException e) { info.Text = e.Message; }
        finally { if (ReferenceEquals(workCts, cts)) { workCts = null; SetBusy(null); } cts.Dispose(); }
    }

    void UsePlace(MapPlace place)
    {
        LatitudeBox.Text = place.Latitude.ToString("0.#####", CultureInfo.InvariantCulture);
        LongitudeBox.Text = place.Longitude.ToString("0.#####", CultureInfo.InvariantCulture);
        string name = place.Name.Split(',')[0].Trim();
        if (name.Length > 0 && Data == null)
        {
            bool site = Options.Template == MapTemplate.SiteLocation;
            Options = site ? Options with { Subtitle = name } : Options with { Title = name };
            SyncFields();
        }
    }

    /// <summary>Area download: consent for this exact box first, then one request; the data replaces the preview's.</summary>
    internal async Task DownloadAsync()
    {
        if (!TryCenter(out double latitude, out double longitude)) { info.Text = Loc.T("중심 위도는 -85~85, 경도는 -180~180으로 입력해 주세요."); return; }
        MapRequest request;
        try { request = MapDownload.Area(latitude, longitude, radiusChoice.Selected, onlineBuildings.IsChecked == true); }
        catch (ArgumentException e) { info.Text = e.Message; return; }
        var answer = services.AskConsent(request, IsVisible ? this : null);
        if (!answer.Agreed) { info.Text = Loc.T("지도 데이터를 받지 않았습니다. 인터넷으로 보낸 것은 없습니다."); return; }
        workCts?.Cancel(); var cts = workCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => { if (ReferenceEquals(workCts, cts)) info.Text = Loc.Format("받은 지도 데이터 읽는 중… {0}%", Math.Round(p * 100)); });
        SetBusy(Loc.T("지도 데이터 받는 중… 영역에 따라 1분 넘게 걸릴 수 있습니다."));
        try
        {
            var (data, osm) = await services.Download.DownloadAsync(MapConsent.Grant(request), progress, cts.Token);
            if (closed) return;
            DownloadedOsm = osm; saveOsm.Visibility = Visibility.Visible;
            string name = Options.Template == MapTemplate.SiteLocation ? (string.IsNullOrWhiteSpace(Options.Subtitle) ? "" : Options.Subtitle) : Options.Title;
            if (string.IsNullOrWhiteSpace(name) || name == Loc.T("지도")) name = string.Format(CultureInfo.InvariantCulture, "{0:0.0000}, {1:0.0000}", latitude, longitude);
            UseData(data, name, Loc.T("인터넷에서 받은 OpenStreetMap 데이터"));
            if (Options.Template == MapTemplate.SiteLocation) { Options = Options with { SiteLatitude = latitude, SiteLongitude = longitude }; UpdateSiteNote(); Schedule(); }
        }
        catch (OperationCanceledException) { info.Text = Loc.T("지도 데이터 받기를 취소했습니다."); }
        catch (Exception e) when (e is MapDownloadException or InvalidDataException) { info.Text = e.Message; }
        finally { if (ReferenceEquals(workCts, cts)) { workCts = null; SetBusy(null); } cts.Dispose(); }
    }

    void SaveDownloaded()
    {
        if (DownloadedOsm is not { } bytes) return;
        string name = string.IsNullOrWhiteSpace(Options.Title) ? "map" : Options.Title.Trim();
        if (services.SaveOsm(bytes, name) is { } path) info.Text = Loc.Format("저장했습니다: {0}", Path.GetFileName(path));
    }

    // Preview pixels from a point on the shown image.
    Point Scale(Point shown) => PreviewRaster == null || preview.ActualWidth <= 0 ? shown
        : new Point(shown.X * PreviewRaster.Width / preview.ActualWidth, shown.Y * PreviewRaster.Height / preview.ActualHeight);

    /// <summary>A click on the preview of a site map moves the site there.</summary>
    internal void ClickPreview(Point previewPixel)
    {
        if (Options.Template != MapTemplate.SiteLocation || PreviewTag is not { } tag || !tag.Frame.Contains(previewPixel)) return;
        var (lat, lon) = tag.View.ToGeo(previewPixel);
        Options = Options with { SiteLatitude = lat, SiteLongitude = lon, CenterLatitude = tag.CenterLatitude, CenterLongitude = tag.CenterLongitude, MetersPerPixel = Options.MetersPerPixel ?? tag.MetersPerPixel * PreviewFactor };
        UpdateSiteNote(); Schedule();
    }

    double PreviewFactor => Math.Min(1, PreviewLongSide / Math.Max(Options.Width, Options.Height));

    internal bool TryCommit()
    {
        if (Data == null) { info.Text = Loc.T("먼저 지도 데이터를 열거나 받아 주세요."); return false; }
        try { Options.Validate(); return true; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { info.Text = e.Message; return false; }
    }

    async void Schedule()
    {
        if (closed || !IsLoaded || Data == null) return;
        long generation = ++version; var options = Options; var data = Data;
        if (ScaleDenominator > 0) options = Options = options with { MetersPerPixel = ScaleDenominator * .0254 / options.Dpi };
        previewCts?.Cancel(); var cts = previewCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token);
            if (generation != version || closed) return;
            info.Text = Loc.T("미리보기 그리는 중…");
            await renderGate.WaitAsync(cts.Token);
            (Raster Image, MapTag Tag) result;
            try { result = await CompatibilityImport.OnSta(() => RenderPreview(data, options, cts.Token), cts.Token); }
            finally { renderGate.Release(); }
            if (generation != version || closed) return;
            Show(result, options);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == version && !closed) info.Text = e.Message; }
        finally { if (ReferenceEquals(previewCts, cts)) previewCts = null; cts.Dispose(); }
    }

    /// <summary>Renders the current settings synchronously (offscreen captures and self-tests).</summary>
    internal void RefreshPreviewNow()
    {
        if (Data == null) return;
        if (ScaleDenominator > 0) Options = Options with { MetersPerPixel = ScaleDenominator * .0254 / Options.Dpi };
        Show(RenderPreview(Data, Options, CancellationToken.None), Options);
    }

    (Raster Image, MapTag Tag) RenderPreview(MapData data, MapOptions options, CancellationToken token)
    {
        double k = Math.Min(1, PreviewLongSide / Math.Max(options.Width, options.Height));
        var document = MapComposer.Build(data, options, token, k);
        var tag = document.Layers.First(l => l.Map is { IsRoot: true }).Map!;
        return (Imaging.Render(document, token), tag);
    }

    void Show((Raster Image, MapTag Tag) result, MapOptions options)
    {
        PreviewRaster = result.Image; PreviewTag = result.Tag; preview.Source = result.Image.Bitmap();
        preview.Cursor = options.Template == MapTemplate.SiteLocation ? Cursors.Cross : Cursors.Arrow;
        double mpp = result.Tag.MetersPerPixel * Math.Min(1, PreviewLongSide / Math.Max(options.Width, options.Height));
        info.Text = Loc.Format("미리보기 · 1 px ≈ {0} m · 축척 1:{1} ({2} DPI 출력 기준)",
            mpp.ToString(mpp < 10 ? "0.##" : "0", CultureInfo.InvariantCulture),
            (Math.Round(mpp * options.Dpi / .0254 / 10) * 10).ToString("N0", CultureInfo.InvariantCulture), options.Dpi.ToString("0", CultureInfo.InvariantCulture));
    }
}
