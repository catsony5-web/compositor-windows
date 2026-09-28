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

    // Hairline + 13 SemiBold title; clicking folds the rows up to the next section.
    // The first section of a panel omits the hairline (rule: false).
    public static FrameworkElement Section(string title, bool rule = true) => new SectionHeader(title, rule);

    // Action labels remain complete at narrow widths; only the affordance occupies a fixed column.
    public static Button ActionRow(string label, Action action, string? tooltip = null, string? glyph = null)
    {
        var button = Button("", action, tooltip ?? label);
        if (Application.Current?.TryFindResource("InspectorAction") is Style style) button.Style = style;
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.BorderBrush = Brushes.Transparent; button.Padding = new Thickness(8, 7, 6, 7); button.Margin = new Thickness(0, 1, 0, 1);
        button.MinHeight = 34; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        var text = new TextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
        {
            // An optional leading icon names the command at a glance; the label stays complete.
            row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = new GridLength(28) });
            var icon = Glyph(glyph, 16, Muted); icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon); Grid.SetColumn(text, 1);
        }
        row.Children.Add(text);
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0 0 L 4 4 L 0 8"), Stroke = Subtle, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        Grid.SetColumn(arrow, row.ColumnDefinitions.Count - 1); row.Children.Add(arrow); button.Content = row;
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
        public const string ChevronDown = "M6 9L12 15L18 9";
        // Adjustments and photo work.
        public const string Camera = "M4 8H8L10 5H14L16 8H20V19H4Z M12 10A3.5 3.5 0 1 0 12 17A3.5 3.5 0 1 0 12 10Z";
        public const string Exposure = "M12 8A4 4 0 1 0 12 16A4 4 0 1 0 12 8Z M12 2.5V4.5 M12 19.5V21.5 M5.3 5.3L6.7 6.7 M17.3 17.3L18.7 18.7 M2.5 12H4.5 M19.5 12H21.5 M5.3 18.7L6.7 17.3 M17.3 6.7L18.7 5.3";
        public const string Levels = "M3 20H21 M5 20V14 M9 20V8 M13 20V4 M17 20V10 M21 20V16";
        public const string Curves = "M4 4V20H20 M4 19C11 19 12 6 20 5";
        public const string HueSaturation = "M12 3C12 3 6 10 6 14.5A6 6 0 0 0 18 14.5C18 10 12 3 12 3Z M12 20.5V9";
        public const string GradientMap = "M3 7H21V17H3Z M8 7V17 M13 7V17 M17.5 7V17";
        public const string Grain = "M6 6H6.01 M12 6H12.01 M18 6H18.01 M9 10H9.01 M15 10H15.01 M6 14H6.01 M12 14H12.01 M18 14H18.01 M9 18H9.01 M15 18H15.01";
        public const string Sliders = "M4 7H13 M17 7H20 M15 5V9 M4 17H7 M11 17H20 M9 15V19";
        public const string SelectAlpha = "M4 7V4H7 M10 4H14 M17 4H20V7 M20 10V14 M20 17V20H17 M14 20H10 M7 20H4V17 M4 14V10 M9 9H15V15H9Z";
        public const string Mask = "M4 4H20V20H4Z M12 8A4 4 0 1 0 12 16A4 4 0 1 0 12 8Z";
        public const string Sparkle = "M11 3L12.4 7.6L17 9L12.4 10.4L11 15L9.6 10.4L5 9L9.6 7.6Z M18 14L18.8 16.2L21 17L18.8 17.8L18 20L17.2 17.8L15 17L17.2 16.2Z";
        public const string FillSelection = "M4 4H20V20H4Z M12 8L13 11L16 12L13 13L12 16L11 13L8 12L11 11Z";
        // Layers, arrangement and design.
        public const string Rename = "M4 20H8L19 9L15 5L4 16Z M13 7L17 11";
        public const string Clip = "M6 4V11A3 3 0 0 0 9 14H18 M15 11L18 14L15 17 M4 20H20";
        public const string Transform = "M4 8V4H8 M16 4H20V8 M20 16V20H16 M8 20H4V16 M8.5 8.5H15.5V15.5H8.5Z";
        public const string GroupAdd = "M3 7V19H21V9H12L10 7Z M12 11.5V16.5 M9.5 14H14.5";
        public const string GroupRemove = "M3 7V19H21V9H12L10 7Z M9.5 14H14.5";
        public const string Forward = "M12 13V3 M8 7L12 3L16 7 M4 17H20 M4 21H20";
        public const string Backward = "M12 3V13 M8 9L12 13L16 9 M4 17H20 M4 21H20";
        public const string FillStroke = "M4 4H14V14H4Z M10 10H20V20H10Z";
        public const string Palette = "M12 3A9 9 0 1 0 12 21C13.2 21 13.6 20 13 19C12.4 18 13 17 14.5 17H17A4 4 0 0 0 21 13C21 7.5 17 3 12 3Z M7.5 11H7.51 M10 7.5H10.01 M14.5 7.5H14.51 M17 11H17.01";
        public const string Image = "M4 5H20V19H4Z M4 16L9 11L13 15L15.5 12.5L20 17 M15 8.5H15.01";
        public const string Text = "M6 6.5V4.5H18V6.5 M12 4.5V19.5 M9 19.5H15";
        public const string Shape = "M4 4H13V13H4Z M16 11A5 5 0 1 0 16 21A5 5 0 1 0 16 11Z";
        // Canvas alignment: a guide line and two bars.
        public const string AlignLeft = "M4 3V21 M8 6H19V10H8Z M8 14H14V18H8Z";
        public const string AlignCenter = "M12 3V21 M6 6H18V10H6Z M8.5 14H15.5V18H8.5Z";
        public const string AlignRight = "M20 3V21 M5 6H16V10H5Z M10 14H16V18H10Z";
        public const string AlignTop = "M3 4H21 M6 8H10V19H6Z M14 8H18V14H14Z";
        public const string AlignMiddle = "M3 12H21 M6 6H10V18H6Z M14 8.5H18V15.5H14Z";
        public const string AlignBottom = "M3 20H21 M6 5H10V16H6Z M14 10H18V16H14Z";
        // Paragraph alignment.
        public const string TextLeft = "M4 6H20 M4 10H14 M4 14H20 M4 18H14";
        public const string TextCenter = "M4 6H20 M7 10H17 M4 14H20 M7 18H17";
        public const string TextRight = "M4 6H20 M10 10H20 M4 14H20 M10 18H20";
        public const string Search = "M10.5 4A6.5 6.5 0 1 0 10.5 17A6.5 6.5 0 1 0 10.5 4Z M15.5 15.5L20 20";
    }
}
