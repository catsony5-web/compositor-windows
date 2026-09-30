using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class NewDocumentDialog : Window
{
    public Document? Result { get; private set; }
    readonly TextBox name = new() { Text = Loc.T("제목 없음") }, width = new(), height = new(), dpi = new() { Text = "96" };
    readonly ComboBox units = new() { ItemsSource = new[] { "픽셀 (px)", "밀리미터 (mm)" }, SelectedIndex = 0 };
    readonly ComboBox background = new() { ItemsSource = new[] { "투명", "흰색", "검정" }, SelectedIndex = 0 };
    readonly TextBlock error = Theme.Label("", Theme.CaptionSize, Theme.Danger), summary = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly Border ratioFrame = new() { BorderBrush = Theme.Accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3), Background = Theme.Selected, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock ratioLabel = Theme.Label("", Theme.CaptionSize, Theme.Muted);
    readonly System.Windows.Controls.Primitives.UniformGrid cards = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
    readonly SegmentedChoice<string> groups;
    readonly string? store;
    Button? selected;

    public const string RecentGroup = "최근";
    public static readonly string[] Groups = [RecentGroup, "화면", "SNS", "인쇄", "사진"];

    /// <summary>Paper sizes are whole millimeters. Dpi/Background override the unit defaults (96 px, 150 mm).</summary>
    internal record Preset(string Name, int Width, int Height, bool Paper = false, string Group = "화면", double? Dpi = null, int? Background = null);

    internal static Preset[] Presets(int screenWidth, int screenHeight) =>
    [new("현재 Windows 화면", screenWidth, screenHeight), new("Full HD", 1920, 1080), new("QHD", 2560, 1440),
     new("4K UHD", 3840, 2160), new("HD", 1280, 720),
     new("정사각형 1:1", 1080, 1080, Group: "SNS"), new("세로 4:5", 1080, 1350, Group: "SNS"), new("세로 9:16", 1080, 1920, Group: "SNS"),
     new("가로 16:9", 1920, 1080, Group: "SNS"), new("가로 1.91:1", 1200, 628, Group: "SNS"), new("배너 3:1", 1500, 500, Group: "SNS"),
     new("A2", 420, 594, true, "인쇄"), new("A3", 297, 420, true, "인쇄"), new("A4", 210, 297, true, "인쇄"), new("A5", 148, 210, true, "인쇄"),
     new("엽서", 100, 148, true, "인쇄"), new("명함", 90, 50, true, "인쇄", 300),
     new("3:2 가로", 6000, 4000, Group: "사진", Background: 1), new("4:3 가로", 4032, 3024, Group: "사진", Background: 1), new("3:2 세로", 4000, 6000, Group: "사진", Background: 1),
     new("인화 10×15cm", 102, 152, true, "사진", 300), new("인화 13×18cm", 127, 178, true, "사진", 300), new("인화 20×25cm", 203, 254, true, "사진", 300)];

    public NewDocumentDialog(Window? owner, string? presetStore = null)
    {
        store = presetStore;
        Owner = owner; Title = "Morupixel · 새 문서"; Width = 1020; Height = 760; MinWidth = 900; MinHeight = 700;
        Background = Theme.Panel; Foreground = Theme.Text; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24), Background = Theme.Panel }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = Theme.Label("새 문서", Theme.TitleSize); title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        heading.Children.Add(Theme.Label("용도를 고르고 크기를 선택하거나 오른쪽에서 직접 입력하세요.", Theme.BodySize, Theme.Muted)); root.Children.Add(heading);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); Grid.SetRow(body, 1); root.Children.Add(body);

        var left = new Grid { Margin = new Thickness(0, 0, 18, 0) }; left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition()); body.Children.Add(left);
        groups = new SegmentedChoice<string>(Groups.Select(g => (g, g == "화면" ? "화면용" : g)), "화면") { Margin = new Thickness(4, 0, 4, 10) };
        left.Children.Add(groups);
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); left.Children.Add(scroll);

        foreach (var box in new[] { name, width, height, dpi }) box.Padding = new Thickness(9, 4, 9, 4);
        // Drop-downs share the text boxes' outer margin and text inset so the column lines up
        // (a text box draws its text 2 DIP inside its padding).
        foreach (var box in new[] { units, background }) { box.Margin = new Thickness(2); box.Padding = new Thickness(11, 4, 9, 4); }
        var fields = new StackPanel { Margin = new Thickness(16) };
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetColumn(right, 1); body.Children.Add(right);
        var card = new GlassPanel { Background = Theme.Header, CornerRadius = new CornerRadius(10), Child = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }; right.Children.Add(card);
        // Create/cancel stay visible below the scrolling settings.
        var actions = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(actions, 1); right.Children.Add(actions);
        var settingsTitle = Theme.Label("문서 설정", Theme.HeadingSize); settingsTitle.FontWeight = FontWeights.SemiBold; settingsTitle.Margin = new Thickness(2, 0, 2, 6); fields.Children.Add(settingsTitle);
        var ratioBox = new Grid { Height = RatioBoxHeight, Margin = new Thickness(2, 4, 2, 2) }; ratioBox.Children.Add(ratioFrame);
        System.Windows.Automation.AutomationProperties.SetName(ratioBox, "비율 미리보기");
        fields.Children.Add(ratioBox);
        ratioLabel.HorizontalAlignment = HorizontalAlignment.Center; ratioLabel.Margin = new Thickness(0, 4, 0, 2); fields.Children.Add(ratioLabel);
        static TextBlock Caption(string label) { var caption = Theme.Label(label, Theme.CaptionSize, Theme.Muted); caption.Margin = new Thickness(2, 6, 2, 2); return caption; }
        void Field(string label, FrameworkElement input) { fields.Children.Add(Caption(label)); fields.Children.Add(input); }
        Field("이름", name); Field("크기 단위", units);
        // Width and height share one row with the swap between them, so every setting fits the card at the default size.
        var sizeRow = new Grid();
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition()); sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
        sizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heightCaption = Caption("높이"); Grid.SetColumn(heightCaption, 2);
        var swap = Theme.IconButton(Theme.Glyphs.Swap, () => (width.Text, height.Text) = (height.Text, width.Text), "가로 / 세로 바꾸기");
        swap.Margin = new Thickness(2); swap.VerticalAlignment = VerticalAlignment.Center; System.Windows.Automation.AutomationProperties.SetName(swap, "가로 / 세로 바꾸기");
        Grid.SetRow(width, 1); Grid.SetRow(swap, 1); Grid.SetColumn(swap, 1); Grid.SetRow(height, 1); Grid.SetColumn(height, 2);
        foreach (var part in new UIElement[] { Caption("너비"), heightCaption, width, swap, height }) sizeRow.Children.Add(part);
        fields.Children.Add(sizeRow);
        Field("해상도 · DPI", dpi); Field("배경", background);
        var savePreset = Theme.Styled(Theme.Button("현재 크기를 내 프리셋으로 저장", SaveCustomPreset, "이름 칸의 이름으로 저장합니다. 최근 탭에 표시됩니다."), "GhostButton"); savePreset.Margin = new Thickness(2, 8, 2, 2); fields.Children.Add(savePreset);
        var create = Theme.Button("문서 만들기  →", () =>
        {
            try
            {
                Result = CreateDocument(name.Text, width.Text, height.Text, background.SelectedIndex, units.SelectedIndex == 1, dpi.Text);
                if (CurrentSize(Loc.T("최근 크기")) is { } recent) NewDocumentPresetStore.AddRecent(recent, store);
                DialogResult = true;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }); Theme.Styled(create, "PrimaryButton"); create.IsDefault = true; create.MinHeight = 34; create.Margin = new Thickness(0);
        // The size that "Create document" will make sits with that button, outside the scrolling settings.
        // An input error shows there too and takes no room while there is none.
        summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(2, 0, 2, 6); summary.ToolTip = "CMYK 미리보기는 상단에서 전환"; actions.Children.Add(summary);
        error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(2, 0, 2, 6); error.Visibility = Visibility.Collapsed; actions.Children.Add(error);
        actions.Children.Add(create);
        var cancel = Theme.Button("취소", () => DialogResult = false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 6, 0, 0); actions.Children.Add(cancel);
        foreach (var box in new[] { width, height, dpi }) box.TextChanged += (_, _) => UpdateSummary();
        units.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.Count > 0 && double.TryParse(width.Text, out double w) && double.TryParse(height.Text, out double h) && double.TryParse(dpi.Text, out double d) && d > 0)
            {
                double factor = units.SelectedIndex == 1 ? 25.4 / d : d / 25.4;
                width.Text = (units.SelectedIndex == 1 ? Math.Round(w * factor, 3) : Math.Round(w * factor)).ToString(CultureInfo.InvariantCulture);
                height.Text = (units.SelectedIndex == 1 ? Math.Round(h * factor, 3) : Math.Round(h * factor)).ToString(CultureInfo.InvariantCulture);
            }
            UpdateSummary();
        };
        screen = WindowAppearance.ScreenSize(owner);
        groups.Changed += _ => ShowGroup(false);
        ShowGroup(true);
        UpdateSummary();
    }

    readonly (int Width, int Height) screen;
    internal string Group => groups.Selected;
    internal IReadOnlyList<Button> Cards => cards.Children.OfType<Button>().ToArray();
    internal void ShowGroupForTest(string group) => groups.Select(group);
    internal TextBlock SummaryForTest => summary;
    internal IReadOnlyList<Control> SettingInputsForTest => [name, units, width, height, dpi, background];

    void ShowGroup(bool selectFirst)
    {
        cards.Children.Clear(); selected = null;
        if (groups.Selected == RecentGroup)
        {
            var saved = NewDocumentPresetStore.Load(store);
            foreach (var custom in saved.Custom) AddCard(Loc.Keep(Theme.Label(custom.Name, Theme.BodySize)), custom, true);
            foreach (var recent in saved.Recent) AddCard(Theme.Label("최근 크기", Theme.BodySize), recent, false);
            if (cards.Children.Count == 0)
            {
                var empty = Theme.Label("최근에 만든 크기와 저장한 프리셋이 여기에 표시됩니다.", Theme.BodySize, Theme.Muted);
                empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(6, 10, 6, 0); cards.Children.Add(empty);
            }
            return;
        }
        foreach (var preset in Presets(screen.Width, screen.Height).Where(p => p.Group == groups.Selected))
        {
            var size = new SavedDocumentSize(preset.Name, preset.Width, preset.Height, preset.Paper, preset.Dpi ?? (preset.Paper ? 150 : 96), preset.Background ?? (preset.Paper ? 1 : 0));
            var label = Theme.Label(preset.Name, Theme.BodySize);
            var button = AddCard(label, size, false);
            if (selectFirst && selected == null) Apply(size, button);
        }
    }

    Button AddCard(TextBlock label, SavedDocumentSize size, bool removable)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        double scale = 44d / Math.Max(size.Width, size.Height);
        // Icons of different ratios share one 44 px slot, so every card's title starts at the same height.
        var slot = new Grid { Height = 44, Margin = new Thickness(0, 0, 0, 10) };
        slot.Children.Add(new Border { Width = Math.Max(3, size.Width * scale), Height = Math.Max(3, size.Height * scale), BorderBrush = Theme.Accent, BorderThickness = new Thickness(1.3), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(slot);
        label.FontWeight = FontWeights.SemiBold; label.HorizontalAlignment = HorizontalAlignment.Center; label.TextAlignment = TextAlignment.Center; label.TextWrapping = TextWrapping.Wrap; content.Children.Add(label);
        var detail = Theme.Label(Describe(size), Theme.CaptionSize, Theme.Subtle); detail.HorizontalAlignment = HorizontalAlignment.Center; detail.TextAlignment = TextAlignment.Center; detail.TextWrapping = TextWrapping.Wrap; content.Children.Add(detail);
        var button = Theme.Button("", () => { }); button.Content = content; button.Margin = new Thickness(4); button.Padding = new Thickness(5, 12, 5, 10); button.BorderThickness = new Thickness(1.5); button.MinHeight = 128;
        button.Tag = size;
        System.Windows.Automation.AutomationProperties.SetName(button, $"{label.Text} · {Describe(size)}");
        button.Click += (_, _) => Apply(size, button);
        if (removable)
        {
            var remove = new MenuItem { Header = "프리셋 삭제" };
            remove.Click += (_, _) => { NewDocumentPresetStore.RemoveCustom(size.Name, store); ShowGroup(false); };
            button.ContextMenu = new ContextMenu { Items = { remove } }; button.ToolTip = "마우스 오른쪽 단추로 삭제";
        }
        cards.Children.Add(button); return button;
    }

    // Pixel sizes use the same digit grouping as the summary and the status bar ("4,480 × 1,440 px").
    internal static string Describe(SavedDocumentSize size) => size.Millimeters
        ? $"{size.Width.ToString("0.###", CultureInfo.InvariantCulture)} × {size.Height.ToString("0.###", CultureInfo.InvariantCulture)} mm · {size.Dpi:0.##} DPI"
        : PixelSize(size.Width, size.Height);
    internal static string PixelSize(double width, double height) => $"{width:N0} × {height:N0} px";
    const double RatioBoxHeight = 76;

    void Apply(SavedDocumentSize size, Button button)
    {
        units.SelectedIndex = size.Millimeters ? 1 : 0; dpi.Text = size.Dpi.ToString("0.##", CultureInfo.InvariantCulture);
        width.Text = size.Width.ToString("0.###", CultureInfo.InvariantCulture); height.Text = size.Height.ToString("0.###", CultureInfo.InvariantCulture);
        background.SelectedIndex = size.Background;
        if (selected != null) { selected.BorderBrush = Theme.Surface; selected.Background = Theme.Surface; }
        selected = button; button.BorderBrush = Theme.Accent; button.Background = Theme.Selected; UpdateSummary();
    }

    SavedDocumentSize? CurrentSize(string label)
    {
        try
        {
            _ = Dimensions(width.Text, height.Text, units.SelectedIndex == 1, dpi.Text);
            return new(label, double.Parse(width.Text, CultureInfo.InvariantCulture), double.Parse(height.Text, CultureInfo.InvariantCulture), units.SelectedIndex == 1, double.Parse(dpi.Text, CultureInfo.InvariantCulture), background.SelectedIndex);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or System.IO.InvalidDataException) { return null; }
    }

    internal void SaveCustomPreset()
    {
        string label = string.IsNullOrWhiteSpace(name.Text) ? Loc.T("내 프리셋") : name.Text.Trim();
        if (label.Length > 80) label = label[..80];
        if (CurrentSize(label) is not { } size) { ShowError(Loc.T("크기 또는 DPI를 확인하세요")); return; }
        NewDocumentPresetStore.AddCustom(size, store);
        groups.Select(RecentGroup); ShowGroup(false);
    }

    void UpdateSummary()
    {
        try
        {
            var size = Dimensions(width.Text, height.Text, units.SelectedIndex == 1, dpi.Text);
            summary.Text = $"{PixelSize(size.Width, size.Height)} · {size.Dpi:0.##} DPI · RGB · 8 bit · sRGB"; ShowError("");
            double scale = (RatioBoxHeight - 8) / Math.Max(size.Width, size.Height);
            ratioFrame.Width = Math.Max(4, size.Width * scale); ratioFrame.Height = Math.Max(4, size.Height * scale);
            ratioLabel.Text = Ratio(size.Width, size.Height);
        }
        catch (Exception ex) { summary.Text = "크기 또는 DPI를 확인하세요"; ShowError(ex.Message); ratioLabel.Text = ""; }
    }

    void ShowError(string message) { error.Text = message; error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    /// <summary>Reduced aspect ratio ("16:9"); falls back to a decimal for sizes that do not reduce to small numbers.</summary>
    internal static string Ratio(int w, int h)
    {
        int a = w, b = h; while (b != 0) (a, b) = (b, a % b);
        int rw = w / a, rh = h / a;
        if (rw <= 32 && rh <= 32) return $"{rw}:{rh}";
        double r = w >= h ? (double)w / h : (double)h / w;
        return w >= h ? $"{r.ToString("0.##", CultureInfo.InvariantCulture)}:1" : $"1:{r.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    internal static (int Width, int Height, double Dpi) Dimensions(string width, string height, bool millimeters, string dpi)
    {
        double resolution = Dialogs.Number(dpi, 1, 9600), w = Dialogs.Number(width, .01, 100000), h = Dialogs.Number(height, .01, 100000);
        if (millimeters) { w = Math.Round(w / 25.4 * resolution); h = Math.Round(h / 25.4 * resolution); }
        else if (w != Math.Truncate(w) || h != Math.Truncate(h)) throw new ArgumentException("픽셀 크기는 정수로 입력하세요.");
        int px = checked((int)w), py = checked((int)h); Raster.ValidateSize(px, py); return (px, py, resolution);
    }
    internal static Document CreateDocument(string name, string width, string height, int background, bool millimeters = false, string dpi = "96")
    {
        var size = Dimensions(width, height, millimeters, dpi);
        var document = new Document { Width = size.Width, Height = size.Height, Dpi = size.Dpi, Name = string.IsNullOrWhiteSpace(name) ? Loc.T("제목 없음") : name.Trim() };
        document.Add(new Layer { Name = Loc.T(background == 0 ? "레이어 1" : "배경"), Pixels = background == 0 ? new Raster(size.Width, size.Height) : Raster.Solid(size.Width, size.Height, background == 1 ? Colors.White : Colors.Black) });
        return document;
    }
}
