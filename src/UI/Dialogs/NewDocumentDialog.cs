using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class NewDocumentDialog : Window
{
    public Document? Result { get; private set; }
    readonly TextBox name = new() { Text = "제목 없음" }, width = new(), height = new(), dpi = new() { Text = "96" };
    readonly ComboBox units = new() { ItemsSource = new[] { "픽셀 (px)", "밀리미터 (mm)" }, SelectedIndex = 0 };
    readonly ComboBox background = new() { ItemsSource = new[] { "투명", "흰색", "검정" }, SelectedIndex = 0 };
    readonly TextBlock error = Theme.Label("", 11, Theme.Brush("#F8ABAD")), summary = Theme.Label("", 11, Theme.Muted);
    internal record Preset(string Name, int Width, int Height, bool Paper = false);
    internal static Preset[] Presets(int screenWidth, int screenHeight) =>
    [new("현재 Windows 화면", screenWidth, screenHeight), new("Full HD", 1920, 1080), new("QHD", 2560, 1440),
     new("4K UHD", 3840, 2160), new("HD", 1280, 720), new("A2", 420, 594, true), new("A3", 297, 420, true), new("A4", 210, 297, true), new("A5", 148, 210, true)];
    public NewDocumentDialog(Window? owner)
    {
        Owner = owner; Title = "Morupixel · 새 문서"; Width = 990; Height = 760; MinWidth = 900; MinHeight = 700;
        Background = Theme.Header; Foreground = Theme.Text; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24), Background = Theme.Header }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(Theme.Label("새 문서", 25)); root.Children.Add(heading);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) }); Grid.SetRow(body, 1); root.Children.Add(body);
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 3, Margin = new Thickness(0, 0, 18, 0) }; body.Children.Add(cards);
        Button? selected = null;
        var screen = WindowAppearance.ScreenSize(owner);
        foreach (var preset in Presets(screen.Width, screen.Height))
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; double scale = 40d / Math.Max(preset.Width, preset.Height);
            content.Children.Add(new Border { Width = preset.Width * scale, Height = preset.Height * scale, BorderBrush = Theme.Accent, BorderThickness = new Thickness(1.3), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 10) });
            var label = Theme.Label(preset.Name, 13); label.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(label);
            var detail = Theme.Label($"{preset.Width} × {preset.Height} {(preset.Paper ? "mm" : "px")}", 11, Theme.Muted); detail.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(detail);
            var button = Theme.Button("", () => { }); button.Content = content; button.Margin = new Thickness(4); button.Padding = new Thickness(5);
            void Select()
            {
                units.SelectedIndex = preset.Paper ? 1 : 0; dpi.Text = preset.Paper ? "150" : "96";
                width.Text = preset.Width.ToString(); height.Text = preset.Height.ToString(); background.SelectedIndex = preset.Paper ? 1 : 0;
                if (selected != null) selected.BorderBrush = Theme.Line; selected = button; button.BorderBrush = Theme.Accent; UpdateSummary();
            }
            button.Click += (_, _) => Select(); cards.Children.Add(button); if (selected == null) Select();
        }
        foreach (var box in new[] { name, width, height, dpi }) { box.Height = 34; box.Padding = new Thickness(9, 4, 9, 4); }
        var fields = new StackPanel { Margin = new Thickness(17) };
        var card = new GlassPanel { Child = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }; Grid.SetColumn(card, 1); body.Children.Add(card);
        fields.Children.Add(Theme.Label("문서 설정", 16));
        void Field(string label, FrameworkElement input) { fields.Children.Add(Theme.Label(label, 11, Theme.Muted)); fields.Children.Add(input); }
        Field("이름", name); Field("크기 단위", units); Field("너비", width); Field("높이", height);
        fields.Children.Add(Theme.Button("가로 / 세로 바꾸기  ⇄", () => (width.Text, height.Text) = (height.Text, width.Text)));
        Field("해상도 · DPI", dpi); Field("배경", background);
        summary.TextWrapping = TextWrapping.Wrap; fields.Children.Add(summary);
        var colorMode = Theme.Label("RGB · 8 bit · sRGB", 11, Theme.Muted);
        colorMode.ToolTip = "CMYK 미리보기는 상단에서 전환"; fields.Children.Add(colorMode);
        error.TextWrapping = TextWrapping.Wrap; fields.Children.Add(error);
        var create = Theme.Button("문서 만들기  →", () =>
        {
            try { Result = CreateDocument(name.Text, width.Text, height.Text, background.SelectedIndex, units.SelectedIndex == 1, dpi.Text); DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; }
        }); create.Background = Theme.Primary; create.IsDefault = true; create.Margin = new Thickness(3, 10, 3, 3); fields.Children.Add(create);
        var cancel = Theme.Button("취소", () => DialogResult = false); cancel.IsCancel = true; fields.Children.Add(cancel);
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
        }; UpdateSummary();
    }
    void UpdateSummary()
    {
        try { var size = Dimensions(width.Text, height.Text, units.SelectedIndex == 1, dpi.Text); summary.Text = $"{size.Width:N0} × {size.Height:N0} px · {size.Dpi:0.##} DPI"; error.Text = ""; }
        catch (Exception ex) { summary.Text = "크기 또는 DPI를 확인하세요"; error.Text = ex.Message; }
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
        var document = new Document { Width = size.Width, Height = size.Height, Dpi = size.Dpi, Name = string.IsNullOrWhiteSpace(name) ? "제목 없음" : name.Trim() };
        document.Add(new Layer { Name = background == 0 ? "레이어 1" : "배경", Pixels = background == 0 ? new Raster(size.Width, size.Height) : Raster.Solid(size.Width, size.Height, background == 1 ? Colors.White : Colors.Black) });
        return document;
    }
}
