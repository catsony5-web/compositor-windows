using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Studio UI tokens. Surfaces are layered from darkest to lightest:
// Canvas < Header (window base) < Panel (floating cards) < Surface (controls).
// Inputs deliberately sit below their panel so fields and buttons read differently.
// Theme.xaml mirrors these values as Ui* resources for templates.
public static class Theme
{
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly Brush Canvas = Brush("#0C0D10");
    // Pasteboard behind the document; the active document tab uses the same color.
    public static readonly Brush Stage = Brush("#15181D");
    public static readonly Brush Header = Brush("#111317");
    public static readonly Brush Panel = Brush("#1C1F24");
    public static readonly Brush Surface = Brush("#2D323A");
    public static readonly Brush Hover = Brush("#373D46");
    public static readonly Brush Pressed = Brush("#414853");
    public static readonly Brush Input = Brush("#101216");
    public static readonly Brush Line = Brush("#2A2F37");
    public static readonly Brush Stroke = Brush("#3B414B");
    public static readonly Brush Text = Brush("#E8EBF0");
    public static readonly Brush Muted = Brush("#A3ABB7");
    public static readonly Brush Subtle = Brush("#6F7784");
    public static readonly Brush Accent = Brush("#A8CAFF");
    public static readonly Brush Primary = Brush("#3A6FDB");
    public static readonly Brush PrimaryHover = Brush("#4A7FE9");
    public static readonly Brush Selected = Brush("#243857");
    public static readonly Brush RowHover = Brush("#252930");
    public static readonly Brush Danger = Brush("#F07178");
    public static readonly Brush Success = Brush("#6FD49A");
    public static readonly Brush Warning = Brush("#E9C46A");

    // Segoe UI for Latin/numbers with Windows-hinted Malgun Gothic for Korean. Small UI text stays on
    // hinted system faces at integer sizes: an offscreen A/B showed the bundled Pretendard CFF faces
    // render softer at 12-13 px (the reason Preview 7 moved away from them).
    public static readonly FontFamily UiFont = new("Segoe UI, Malgun Gothic");
    // Type scale: caption 12 · body 13 · strong/section 13 SemiBold · heading 14 · title 18.
    public const double CaptionSize = 12;
    public const double BodySize = 13;
    public const double HeadingSize = 14;
    public const double TitleSize = 18;
    // Control heights: compact option bars and dense rows use 28, standard fields and buttons 30.
    public const double CompactHeight = 28;
    public const double ControlHeight = 30;
    public static readonly DrawingImage BrandIcon = CreateBrandIcon();

    static DrawingImage CreateBrandIcon()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Accent, null, new RectangleGeometry(new Rect(8, 8, 240, 240), 54, 54)));
        drawing.Children.Add(new GeometryDrawing(Brush("#173960"), null, Geometry.Parse("M54 191V65h32l42 66 42-66h32v126h-32v-71l-42 61-42-61v71z")));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public static void Apply(Application app)
    {
        var resources = new ResourceDictionary { Source = new Uri("/Morupixel;component/UI/Theme.xaml", UriKind.Relative) };
        app.Resources.MergedDictionaries.Add(resources);
        app.Resources["UiFont"] = UiFont;
        WindowAppearance.Register();
    }

    public static TextBlock Label(string text, double size = BodySize, Brush? color = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = Math.Max(CaptionSize, size),
            Foreground = color ?? Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 3, 2, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(label, TextRenderingMode.Grayscale);
        return label;
    }

    // Section titles separate groups with a hairline and a compact semibold label.
    public static FrameworkElement Section(string title)
    {
        var label = Label(title, BodySize); label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 12, 0, 6);
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 2), Child = label };
    }

    // Action labels remain complete at narrow widths; only the affordance occupies a fixed column.
    public static Button ActionRow(string label, Action action, string? tooltip = null)
    {
        var button = Button("", action, tooltip ?? label);
        if (Application.Current?.TryFindResource("InspectorAction") is Style style) button.Style = style;
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.BorderBrush = Brushes.Transparent; button.Padding = new Thickness(8, 7, 6, 7); button.Margin = new Thickness(0, 1, 0, 1);
        button.MinHeight = 34; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.Children.Add(new TextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0 0 L 4 4 L 0 8"), Stroke = Subtle, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        Grid.SetColumn(arrow, 1); row.Children.Add(arrow); button.Content = row;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Content = text, ToolTip = tooltip ?? text };
        button.Click += (_, _) => action();
        return button;
    }

    // Applies one of the keyed button styles from Theme.xaml (GhostButton, PrimaryButton, IconButton).
    public static T Styled<T>(T element, string key) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, key);
        return element;
    }

    // Stroked UI glyphs drawn on a 24-unit grid, matching the tool icon family.
    public static FrameworkElement Glyph(string data, double size = 16, Brush? stroke = null, double thickness = 1.7)
    {
        var pen = new Pen(stroke ?? Text, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        // A transparent 24x24 frame keeps every glyph on the same scale and stroke weight.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(data)));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    }

    public static Button IconButton(string data, Action action, string tooltip, double size = CompactHeight, double glyph = 16)
    {
        var button = Styled(Button("", action, tooltip), "IconButton");
        button.Content = Glyph(data, glyph, Muted);
        button.Width = size; button.Height = size;
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    // Shared glyph paths (24-unit grid).
    public static class Glyphs
    {
        public const string Undo = "M9 14L4 9L9 4 M4 9H15A5 5 0 0 1 15 19H11";
        public const string Redo = "M15 14L20 9L15 4 M20 9H9A5 5 0 0 0 9 19H13";
        public const string Plus = "M12 5V19 M5 12H19";
        public const string Duplicate = "M8 8H19V19H8Z M5 16V5H16";
        public const string ArrowUp = "M12 19V5 M6 11L12 5L18 11";
        public const string ArrowDown = "M12 5V19 M6 13L12 19L18 13";
        public const string Delete = "M4 7H20 M9 7V4H15V7 M6 7L7 20H17L18 7 M10 11V16 M14 11V16";
        public const string More = "M5 12H5.01 M12 12H12.01 M19 12H19.01";
        public const string Pin = "M9 3H15 M10 3V9L6 13H18L14 9V3 M12 13V21";
        public const string Close = "M6 6L18 18 M18 6L6 18";
        public const string Fit = "M4 9V4H9 M15 4H20V9 M20 15V20H15 M9 20H4V15";
        public const string NewFile = "M13 3H6V21H18V8Z M13 3V8H18 M12 11V17 M9 14H15";
        public const string Open = "M3 7V19H21V9H12L10 7Z M3 7V5H9L11 7";
        public const string Learn = "M3 7L12 3L21 7L12 11Z M7 9V15C9 17 15 17 17 15V9 M21 7V13";
        public const string Import = "M12 4V15 M7 10L12 15L17 10 M4 19H20";
        public const string Export = "M12 15V4 M7 9L12 4L17 9 M4 19H20";
        public const string Swap = "M7 4L4 7L7 10 M4 7H16A4 4 0 0 1 20 11 M17 20L20 17L17 14 M20 17H8A4 4 0 0 1 4 13";
        public const string Reset = "M5 5H11V11H5Z M13 13H19V19H13Z";
        public const string Folder = "M3 7V19H21V9H12L10 7Z";
        public const string Adjustment = "M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z M12 3V21 M12 7H16 M12 11H19 M12 15H18";
    }
}
