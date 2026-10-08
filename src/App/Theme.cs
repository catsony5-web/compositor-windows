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

    // Two palettes share the token names: 친절한 화면 (the default, below) and 간결한 화면, a flat
    // neutral gray set whose accent marks only focus and selection. The tokens read the active
    // palette, so controls built later follow a switch; MainWindow.ScreenStyleRepaint swaps the
    // brushes that existing controls already hold.
    internal sealed record Palette(string Canvas, string Stage, string Header, string Panel, string Surface, string Hover, string Pressed, string Input,
        string Paper, string Line, string Stroke, string Text, string Muted, string Subtle, string Accent, string Primary, string PrimaryHover,
        string Selected, string RowHover, string Danger, string Success, string Warning, string Popup);
    internal static readonly Palette FriendlyPalette = new("#0C0D10", "#15181D", "#111317", "#1C1F24", "#2D323A", "#373D46", "#414853", "#101216",
        "#F4F5F7", "#2A2F37", "#3B414B", "#E8EBF0", "#A3ABB7", "#6F7784", "#A8CAFF", "#3A6FDB", "#4A7FE9", "#243857", "#252930", "#F07178", "#6FD49A", "#E9C46A", "#22262C");
    internal static readonly Palette CompactPalette = new("#161616", "#1F1F1F", "#323232", "#2B2B2B", "#3C3C3C", "#454545", "#4E4E4E", "#1E1E1E",
        "#F4F5F7", "#1B1B1B", "#4A4A4A", "#E3E3E3", "#ABABAB", "#7C7C7C", "#5E9EFF", "#3A6FDB", "#4A7FE9", "#2F4664", "#363636", "#F07178", "#6FD49A", "#E9C46A", "#383838");
    internal static readonly string[] TokenNames = ["Canvas", "Stage", "Header", "Panel", "Surface", "Hover", "Pressed", "Input", "Paper", "Line", "Stroke", "Text", "Muted", "Subtle", "Accent", "Primary", "PrimaryHover", "Selected", "RowHover", "Danger", "Success", "Warning", "Popup"];
    static Brush[] CreateBrushes(Palette p) => [Brush(p.Canvas), Brush(p.Stage), Brush(p.Header), Brush(p.Panel), Brush(p.Surface), Brush(p.Hover), Brush(p.Pressed), Brush(p.Input),
        Brush(p.Paper), Brush(p.Line), Brush(p.Stroke), Brush(p.Text), Brush(p.Muted), Brush(p.Subtle), Brush(p.Accent), Brush(p.Primary), Brush(p.PrimaryHover),
        Brush(p.Selected), Brush(p.RowHover), Brush(p.Danger), Brush(p.Success), Brush(p.Warning), Brush(p.Popup)];
    static readonly Brush[] friendlyBrushes = CreateBrushes(FriendlyPalette), compactBrushes = CreateBrushes(CompactPalette);
    static Brush[] tokens = friendlyBrushes;
    /// <summary>True while 간결한 화면 tokens are active (see <see cref="UseScreenStyle"/>).</summary>
    public static bool Compact => ReferenceEquals(tokens, compactBrushes);

    public static Brush Canvas => tokens[0];
    // Pasteboard behind the document; the active document tab uses the same color.
    public static Brush Stage => tokens[1];
    public static Brush Header => tokens[2];
    public static Brush Panel => tokens[3];
    public static Brush Surface => tokens[4];
    public static Brush Hover => tokens[5];
    public static Brush Pressed => tokens[6];
    public static Brush Input => tokens[7];
    // Light backing for drawing thumbnails, like the paper the canvas draws line work on.
    public static Brush Paper => tokens[8];
    public static Brush Line => tokens[9];
    public static Brush Stroke => tokens[10];
    public static Brush Text => tokens[11];
    public static Brush Muted => tokens[12];
    public static Brush Subtle => tokens[13];
    public static Brush Accent => tokens[14];
    public static Brush Primary => tokens[15];
    public static Brush PrimaryHover => tokens[16];
    public static Brush Selected => tokens[17];
    public static Brush RowHover => tokens[18];
    public static Brush Danger => tokens[19];
    public static Brush Success => tokens[20];
    public static Brush Warning => tokens[21];
    public static Brush Popup => tokens[22];

    // Switches the token palette and the Theme.xaml resources (colors, type sizes, control
    // heights and corner radii) for 친절한 화면 or 간결한 화면. Returns old → new brushes so the
    // caller can repaint controls that already hold a token (MainWindow.ScreenStyleRepaint).
    internal static IReadOnlyDictionary<Brush, Brush> UseScreenStyle(bool compact)
    {
        var next = compact ? compactBrushes : friendlyBrushes;
        var map = new Dictionary<Brush, Brush>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < next.Length; i++) map[tokens[i]] = next[i];
        tokens = next;
        if (Application.Current?.Resources is { } resources)
        {
            for (int i = 0; i < TokenNames.Length; i++)
            {
                string key = "Ui" + TokenNames[i];
                if (compact) resources[key] = next[i]; else resources.Remove(key);
            }
            foreach (var (key, value) in CompactResources)
                if (compact) resources[key] = value; else resources.Remove(key);
        }
        return map;
    }

    // Theme.xaml values replaced while 간결한 화면 is active (removed again for 친절한 화면).
    static readonly (string Key, object Value)[] CompactResources =
    [
        ("UiBodySize", 12d), ("UiCaptionSize", 11d), ("UiControlHeight", 24d), ("UiButtonPadding", new Thickness(8, 2, 8, 2)), ("UiInputPadding", new Thickness(6, 2, 6, 2)),
        ("UiControlRadius", new CornerRadius(3)), ("UiInnerRadius", new CornerRadius(2)), ("UiPopupRadius", new CornerRadius(3)), ("UiItemRadius", new CornerRadius(2)), ("UiFocusRadius", new CornerRadius(4))
    ];

    // Segoe UI for Latin/numbers with Windows-hinted Malgun Gothic for Korean. Small UI text stays on
    // hinted system faces at integer sizes: an offscreen A/B showed the bundled Pretendard CFF faces
    // render softer at 12-13 px (the reason Preview 7 moved away from them).
    // Follows the display language (Loc.Use runs before Theme is first touched).
    public static readonly FontFamily UiFont = new(Loc.FontFamilyName);
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
        // Wrapped Korean breaks between words (KeepWordsTextBlock); Text stays the source string.
        var label = new KeepWordsTextBlock
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
    public static FrameworkElement Section(string title, bool rule = true, bool foldedByDefault = false) => new SectionHeader(title, rule, foldedByDefault);

    // Action labels remain complete at narrow widths; only the affordance occupies a fixed column.
    public static Button ActionRow(string label, Action action, string? tooltip = null, string? glyph = null)
    {
        var button = Button("", action, tooltip ?? label);
        if (Application.Current?.TryFindResource("InspectorAction") is Style style) button.Style = style;
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.BorderBrush = Brushes.Transparent; button.Padding = new Thickness(8, 7, 6, 7); button.Margin = new Thickness(0, 1, 0, 1);
        button.MinHeight = 34; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        var text = new KeepWordsTextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
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

    // UI glyphs on a 24-unit grid, drawn in layers like hierarchical symbols:
    // "primary | ~secondary | *tint". Secondary strokes draw at 45% and tints fill the
    // shape at 20% of the same color, so one brush gives depth without extra colors.
    public const double SecondaryOpacity = .45, TintOpacity = .2;
    public static FrameworkElement Glyph(string data, double size = 16, Brush? stroke = null, double thickness = 1.7)
    {
        var brush = stroke ?? Text;
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        // A transparent 24x24 frame keeps every glyph on the same scale and stroke weight.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        var layers = data.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var layer in layers.OrderBy(layer => layer[0] == '*' ? 0 : 1))
        {
            bool tint = layer[0] == '*', secondary = layer[0] == '~';
            var geometry = Geometry.Parse(tint || secondary ? layer[1..] : layer);
            var group = new DrawingGroup { Opacity = tint ? TintOpacity : secondary ? SecondaryOpacity : 1 };
            group.Children.Add(tint ? new GeometryDrawing(brush, null, geometry) : new GeometryDrawing(null, pen, geometry));
            drawing.Children.Add(group);
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return Density.Mark(new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true }, DensityRole.Glyph);
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
        public const string Undo = "M9 14L4 9L9 4 M4 9H14.5A5.5 5.5 0 0 1 14.5 20H11";
        public const string Redo = "M15 14L20 9L15 4 M20 9H9.5A5.5 5.5 0 0 0 9.5 20H13";
        public const string Plus = "M12 5V19 M5 12H19";
        public const string Duplicate = "M11.2 9H17.8A2.2 2.2 0 0 1 20 11.2V17.8A2.2 2.2 0 0 1 17.8 20H11.2A2.2 2.2 0 0 1 9 17.8V11.2A2.2 2.2 0 0 1 11.2 9Z | ~M15 9V6.2A2.2 2.2 0 0 0 12.8 4H6.2A2.2 2.2 0 0 0 4 6.2V12.8A2.2 2.2 0 0 0 6.2 15H9";
        public const string ArrowUp = "M12 19V5 M6 11L12 5L18 11";
        public const string ArrowDown = "M12 5V19 M6 13L12 19L18 13";
        public const string Delete = "M4 7H20 M9.5 7V5.2A1.2 1.2 0 0 1 10.7 4H13.3A1.2 1.2 0 0 1 14.5 5.2V7 M6.5 7L7.3 18.2A2 2 0 0 0 9.3 20H14.7A2 2 0 0 0 16.7 18.2L17.5 7 | ~M10 11V16 M14 11V16";
        public const string More = "M5 12H5.01 M12 12H12.01 M19 12H19.01";
        public const string Pin = "M9 3.5H15 M10 3.5V9L7 12.5V14H17V12.5L14 9V3.5 M12 14V20.5 | *M10 3.5H14V9L17 12.5V14H7V12.5L10 9Z";
        public const string Close = "M6 6L18 18 M18 6L6 18";
        public const string Fit = "M4 9V6A2 2 0 0 1 6 4H9 M15 4H18A2 2 0 0 1 20 6V9 M20 15V18A2 2 0 0 1 18 20H15 M9 20H6A2 2 0 0 1 4 18V15";
        public const string NewFile = "M13.5 3H7A2 2 0 0 0 5 5V19A2 2 0 0 0 7 21H17A2 2 0 0 0 19 19V8.5Z M13.5 3V8.5H19 | ~M12 12V17.5 M9.25 14.75H14.75";
        public const string Open = "M3.5 8A2 2 0 0 1 5.5 6H9.3L11.1 7.8H18.5A2 2 0 0 1 20.5 9.8V17A2 2 0 0 1 18.5 19H5.5A2 2 0 0 1 3.5 17Z | ~M3.5 11H20.5";
        public const string Learn = "M2.5 9L12 4.5L21.5 9L12 13.5Z | ~M6.5 11V15.3C8 16.9 10 17.8 12 17.8C14 17.8 16 16.9 17.5 15.3V11 M21.5 9V14 | *M2.5 9L12 4.5L21.5 9L12 13.5Z";
        public const string Import = "M12 3.5V14 M8 10L12 14L16 10 M5 14V18A2 2 0 0 0 7 20H17A2 2 0 0 0 19 18V14";
        public const string Export = "M12 14.5V4 M8 8L12 4L16 8 M5 12V18A2 2 0 0 0 7 20H17A2 2 0 0 0 19 18V12";
        public const string Swap = "M7 4L4 7L7 10 M4 7H16A4 4 0 0 1 20 11 M17 20L20 17L17 14 M20 17H8A4 4 0 0 1 4 13";
        public const string Reset = "M6 4.5H10A1.5 1.5 0 0 1 11.5 6V10A1.5 1.5 0 0 1 10 11.5H6A1.5 1.5 0 0 1 4.5 10V6A1.5 1.5 0 0 1 6 4.5Z | M14 12.5H18A1.5 1.5 0 0 1 19.5 14V18A1.5 1.5 0 0 1 18 19.5H14A1.5 1.5 0 0 1 12.5 18V14A1.5 1.5 0 0 1 14 12.5Z | *M6 4.5H10A1.5 1.5 0 0 1 11.5 6V10A1.5 1.5 0 0 1 10 11.5H6A1.5 1.5 0 0 1 4.5 10V6A1.5 1.5 0 0 1 6 4.5Z";
        public const string Folder = "M3.5 8A2 2 0 0 1 5.5 6H9.3L11.1 7.8H18.5A2 2 0 0 1 20.5 9.8V17A2 2 0 0 1 18.5 19H5.5A2 2 0 0 1 3.5 17Z";
        public const string Adjustment = "M12 3.5A8.5 8.5 0 1 0 12 20.5A8.5 8.5 0 1 0 12 3.5Z | M12 3.5V20.5 | *M12 3.5A8.5 8.5 0 0 0 12 20.5Z";
        public const string ChevronRight = "M9 6L15 12L9 18";
        public const string Eye = "M2.5 12C4.8 7.6 8.2 5.5 12 5.5C15.8 5.5 19.2 7.6 21.5 12C19.2 16.4 15.8 18.5 12 18.5C8.2 18.5 4.8 16.4 2.5 12Z | M12 9A3 3 0 1 0 12 15A3 3 0 1 0 12 9Z | *M12 9A3 3 0 1 0 12 15A3 3 0 1 0 12 9Z";
        public const string EyeSlash = "~M2.5 12C4.8 7.6 8.2 5.5 12 5.5C15.8 5.5 19.2 7.6 21.5 12C19.2 16.4 15.8 18.5 12 18.5C8.2 18.5 4.8 16.4 2.5 12Z | M4.5 4.5L19.5 19.5";
        public const string Lock = "M7.7 10.5H16.3A2.2 2.2 0 0 1 18.5 12.7V18.3A2.2 2.2 0 0 1 16.3 20.5H7.7A2.2 2.2 0 0 1 5.5 18.3V12.7A2.2 2.2 0 0 1 7.7 10.5Z | M8.5 10.5V7.5A3.5 3.5 0 0 1 15.5 7.5V10.5 | *M7.7 10.5H16.3A2.2 2.2 0 0 1 18.5 12.7V18.3A2.2 2.2 0 0 1 16.3 20.5H7.7A2.2 2.2 0 0 1 5.5 18.3V12.7A2.2 2.2 0 0 1 7.7 10.5Z";
        public const string LockOpen = "M7.7 10.5H16.3A2.2 2.2 0 0 1 18.5 12.7V18.3A2.2 2.2 0 0 1 16.3 20.5H7.7A2.2 2.2 0 0 1 5.5 18.3V12.7A2.2 2.2 0 0 1 7.7 10.5Z | M8.5 10.5V7.5A3.5 3.5 0 0 1 15.3 6.3";
        public const string Revert = "M4.5 12A7.5 7.5 0 1 0 7 6.4 M4.5 3.5V8H9";
        // A hatched square: a fill area drawn with line hatching.
        // Favorites: an outline star to add, the tinted star once starred.
        public const string Star = "M12 3.8L14.4 8.9L20 9.6L15.9 13.4L17 19L12 16.2L7 19L8.1 13.4L4 9.6L9.6 8.9Z";
        public const string StarFilled = "M12 3.8L14.4 8.9L20 9.6L15.9 13.4L17 19L12 16.2L7 19L8.1 13.4L4 9.6L9.6 8.9Z | *M12 3.8L14.4 8.9L20 9.6L15.9 13.4L17 19L12 16.2L7 19L8.1 13.4L4 9.6L9.6 8.9Z";
        public const string Hatch = "M4 4H20V20H4Z | ~M4 12L12 4M4 20L20 4M12 20L20 12";
        // Tone screens (스크린톤): a frame with dots that grow from the light corner to the dark one.
        public const string Screentone = "M4 4H20V20H4Z | ~M8 7.6A.4 .4 0 1 0 8 8.4A.4 .4 0 1 0 8 7.6Z M12 7.4A.6 .6 0 1 0 12 8.6A.6 .6 0 1 0 12 7.4Z M8 11.4A.6 .6 0 1 0 8 12.6A.6 .6 0 1 0 8 11.4Z | M16 7.2A.8 .8 0 1 0 16 8.8A.8 .8 0 1 0 16 7.2Z M12 11.1A.9 .9 0 1 0 12 12.9A.9 .9 0 1 0 12 11.1Z M8 15.2A.8 .8 0 1 0 8 16.8A.8 .8 0 1 0 8 15.2Z M16 10.8A1.2 1.2 0 1 0 16 13.2A1.2 1.2 0 1 0 16 10.8Z M12 14.8A1.2 1.2 0 1 0 12 17.2A1.2 1.2 0 1 0 12 14.8Z M16 14.5A1.5 1.5 0 1 0 16 17.5A1.5 1.5 0 1 0 16 14.5Z | *M16 14.5A1.5 1.5 0 1 0 16 17.5A1.5 1.5 0 1 0 16 14.5Z M16 10.8A1.2 1.2 0 1 0 16 13.2A1.2 1.2 0 1 0 16 10.8Z M12 14.8A1.2 1.2 0 1 0 12 17.2A1.2 1.2 0 1 0 12 14.8Z";
        public const string Info = "M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z | M12 11V16.5 M12 7.8V7.9 | *M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z";
        public const string Warning = "M10.3 4.6A2 2 0 0 1 13.7 4.6L21.2 17.6A2 2 0 0 1 19.5 20.6H4.5A2 2 0 0 1 2.8 17.6Z | M12 9.5V13.5 M12 16.8V16.9 | *M10.3 4.6A2 2 0 0 1 13.7 4.6L21.2 17.6A2 2 0 0 1 19.5 20.6H4.5A2 2 0 0 1 2.8 17.6Z";
        public const string Error = "M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z | M9 9L15 15 M15 9L9 15 | *M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z";
        public const string Document = "M13.5 3H7A2 2 0 0 0 5 5V19A2 2 0 0 0 7 21H17A2 2 0 0 0 19 19V8.5Z M13.5 3V8.5H19 | ~M9 13H15 M9 16.5H15 | *M13.5 3H7A2 2 0 0 0 5 5V19A2 2 0 0 0 7 21H17A2 2 0 0 0 19 19V8.5Z";
        public const string Save = "M6.5 4H15.5L20 8.5V17.5A2.5 2.5 0 0 1 17.5 20H6.5A2.5 2.5 0 0 1 4 17.5V6.5A2.5 2.5 0 0 1 6.5 4Z M8 4V9H15V4 M8 20V14.5H16V20 | *M8 14.5H16V20H8Z";
        public const string Paste = "M9 3.5H15V6.5H9Z M9 5H7A2 2 0 0 0 5 7V18A2 2 0 0 0 7 20H17A2 2 0 0 0 19 18V7A2 2 0 0 0 17 5H15 | ~M9 11H15 M9 15H13";
        public const string Globe = "M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z M3.5 12H20.5 | ~M12 3C9.2 6.2 9.2 17.8 12 21 M12 3C14.8 6.2 14.8 17.8 12 21 M5 7.5H19 M5 16.5H19";
        public const string ChevronDown = "M6 9L12 15L18 9";
        // Adjustments and photo work.
        public const string Camera = "M4 8.5A2 2 0 0 1 6 6.5H7.8L9.3 4.5H14.7L16.2 6.5H18A2 2 0 0 1 20 8.5V17.5A2 2 0 0 1 18 19.5H6A2 2 0 0 1 4 17.5Z | M12 9.5A3.5 3.5 0 1 0 12 16.5A3.5 3.5 0 1 0 12 9.5Z | *M12 9.5A3.5 3.5 0 1 0 12 16.5A3.5 3.5 0 1 0 12 9.5Z";
        // Before/after comparison views: one frame split by a handle, two frames, one frame that switches.
        public const string CompareSplit = "M6 4.5H18A2 2 0 0 1 20 6.5V17.5A2 2 0 0 1 18 19.5H6A2 2 0 0 1 4 17.5V6.5A2 2 0 0 1 6 4.5Z | M12 2.5V9.5 M12 14.5V21.5 M12 9.5A2.5 2.5 0 1 0 12 14.5A2.5 2.5 0 1 0 12 9.5Z | *M6 4.5H12V19.5H6A2 2 0 0 1 4 17.5V6.5A2 2 0 0 1 6 4.5Z";
        public const string CompareSideBySide = "M4.8 6H8.7A1.8 1.8 0 0 1 10.5 7.8V16.2A1.8 1.8 0 0 1 8.7 18H4.8A1.8 1.8 0 0 1 3 16.2V7.8A1.8 1.8 0 0 1 4.8 6Z | M15.3 6H19.2A1.8 1.8 0 0 1 21 7.8V16.2A1.8 1.8 0 0 1 19.2 18H15.3A1.8 1.8 0 0 1 13.5 16.2V7.8A1.8 1.8 0 0 1 15.3 6Z | *M4.8 6H8.7A1.8 1.8 0 0 1 10.5 7.8V16.2A1.8 1.8 0 0 1 8.7 18H4.8A1.8 1.8 0 0 1 3 16.2V7.8A1.8 1.8 0 0 1 4.8 6Z";
        public const string CompareToggle = "M8 9.5H16 M13.5 7L16 9.5L13.5 12 M16 14.5H8 M10.5 12L8 14.5L10.5 17 | ~M6 4.5H18A2 2 0 0 1 20 6.5V17.5A2 2 0 0 1 18 19.5H6A2 2 0 0 1 4 17.5V6.5A2 2 0 0 1 6 4.5Z | *M6 4.5H18A2 2 0 0 1 20 6.5V17.5A2 2 0 0 1 18 19.5H6A2 2 0 0 1 4 17.5V6.5A2 2 0 0 1 6 4.5Z";
        public const string Exposure = "M12 8A4 4 0 1 0 12 16A4 4 0 1 0 12 8Z | M12 2.5V4.5 M12 19.5V21.5 M5.3 5.3L6.7 6.7 M17.3 17.3L18.7 18.7 M2.5 12H4.5 M19.5 12H21.5 M5.3 18.7L6.7 17.3 M17.3 6.7L18.7 5.3 | *M12 8A4 4 0 1 0 12 16A4 4 0 1 0 12 8Z";
        public const string Levels = "M5 13H7A1 1 0 0 1 8 14V19A1 1 0 0 1 7 20H5A1 1 0 0 1 4 19V14A1 1 0 0 1 5 13Z | M11 6.5H13A1 1 0 0 1 14 7.5V19A1 1 0 0 1 13 20H11A1 1 0 0 1 10 19V7.5A1 1 0 0 1 11 6.5Z | M17 10H19A1 1 0 0 1 20 11V19A1 1 0 0 1 19 20H17A1 1 0 0 1 16 19V11A1 1 0 0 1 17 10Z | *M11 6.5H13A1 1 0 0 1 14 7.5V19A1 1 0 0 1 13 20H11A1 1 0 0 1 10 19V7.5A1 1 0 0 1 11 6.5Z";
        public const string Curves = "M4 20C11 20 13 4 20 4 | ~M6 4H18A2 2 0 0 1 20 6V18A2 2 0 0 1 18 20H6A2 2 0 0 1 4 18V6A2 2 0 0 1 6 4Z M12 4V20 M4 12H20";
        public const string HueSaturation = "M12 3.5C12 3.5 5.5 10.4 5.5 14.6A6.5 6.5 0 0 0 18.5 14.6C18.5 10.4 12 3.5 12 3.5Z | *M12 3.5C12 3.5 18.5 10.4 18.5 14.6A6.5 6.5 0 0 1 12 21.1Z";
        public const string GradientMap = "M5.5 6H18.5A2.5 2.5 0 0 1 21 8.5V15.5A2.5 2.5 0 0 1 18.5 18H5.5A2.5 2.5 0 0 1 3 15.5V8.5A2.5 2.5 0 0 1 5.5 6Z | *M5.5 6H9V18H5.5A2.5 2.5 0 0 1 3 15.5V8.5A2.5 2.5 0 0 1 5.5 6Z | ~M13 6V18 M17 6V18";
        public const string Grain = "M7 5.95A1.05 1.05 0 1 0 7 8.05A1.05 1.05 0 1 0 7 5.95Z M17 5.95A1.05 1.05 0 1 0 17 8.05A1.05 1.05 0 1 0 17 5.95Z M12 10.95A1.05 1.05 0 1 0 12 13.05A1.05 1.05 0 1 0 12 10.95Z M7 15.95A1.05 1.05 0 1 0 7 18.05A1.05 1.05 0 1 0 7 15.95Z M17 15.95A1.05 1.05 0 1 0 17 18.05A1.05 1.05 0 1 0 17 15.95Z | ~M12 5.95A1.05 1.05 0 1 0 12 8.05A1.05 1.05 0 1 0 12 5.95Z M7 10.95A1.05 1.05 0 1 0 7 13.05A1.05 1.05 0 1 0 7 10.95Z M17 10.95A1.05 1.05 0 1 0 17 13.05A1.05 1.05 0 1 0 17 10.95Z M12 15.95A1.05 1.05 0 1 0 12 18.05A1.05 1.05 0 1 0 12 15.95Z";
        // Design-style effects: a black/white step (한계값), dots growing with darkness (망점),
        // a sheet with fibres and specks (종이·인쇄 질감) and a light with its halo (빛 번짐).
        public const string Threshold = "M6 4H18A2 2 0 0 1 20 6V18A2 2 0 0 1 18 20H6A2 2 0 0 1 4 18V6A2 2 0 0 1 6 4Z | M8 16H12V8H16 | *M6 4H12V20H6A2 2 0 0 1 4 18V6A2 2 0 0 1 6 4Z";
        public const string Halftone = "M18 4.3A1.7 1.7 0 1 0 18 7.7A1.7 1.7 0 1 0 18 4.3Z M12 10.3A1.7 1.7 0 1 0 12 13.7A1.7 1.7 0 1 0 12 10.3Z M18 9.8A2.2 2.2 0 1 0 18 14.2A2.2 2.2 0 1 0 18 9.8Z M6 16.3A1.7 1.7 0 1 0 6 19.7A1.7 1.7 0 1 0 6 16.3Z M12 15.8A2.2 2.2 0 1 0 12 20.2A2.2 2.2 0 1 0 12 15.8Z M18 15.3A2.7 2.7 0 1 0 18 20.7A2.7 2.7 0 1 0 18 15.3Z | ~M6 5.2A0.8 0.8 0 1 0 6 6.8A0.8 0.8 0 1 0 6 5.2Z M12 4.8A1.2 1.2 0 1 0 12 7.2A1.2 1.2 0 1 0 12 4.8Z M6 10.8A1.2 1.2 0 1 0 6 13.2A1.2 1.2 0 1 0 6 10.8Z | *M18 9.8A2.2 2.2 0 1 0 18 14.2A2.2 2.2 0 1 0 18 9.8Z M12 15.8A2.2 2.2 0 1 0 12 20.2A2.2 2.2 0 1 0 12 15.8Z M18 15.3A2.7 2.7 0 1 0 18 20.7A2.7 2.7 0 1 0 18 15.3Z";
        public const string PaperTexture = "M6.5 3.5H14L19 8.5V19A1.5 1.5 0 0 1 17.5 20.5H6.5A1.5 1.5 0 0 1 5 19V5A1.5 1.5 0 0 1 6.5 3.5Z M14 3.5V8.5H19 | ~M8 12C9.2 11.2 10.4 12.6 11.6 11.8 M9.5 15.5C10.8 14.6 12.2 16.2 14.5 15.2 M14.6 11.65A0.55 0.55 0 1 0 14.6 12.75A0.55 0.55 0 1 0 14.6 11.65Z M16 17.1A0.5 0.5 0 1 0 16 18.1A0.5 0.5 0 1 0 16 17.1Z | *M6.5 3.5H14L19 8.5V19A1.5 1.5 0 0 1 17.5 20.5H6.5A1.5 1.5 0 0 1 5 19V5A1.5 1.5 0 0 1 6.5 3.5Z";
        public const string Glow = "M12 9A3 3 0 1 0 12 15A3 3 0 1 0 12 9Z | ~M12 6A6 6 0 1 0 12 18A6 6 0 1 0 12 6Z M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z | *M12 6A6 6 0 1 0 12 18A6 6 0 1 0 12 6Z";
        public const string Sliders = "M4 7H12.5 M17.5 7H20 M15 4.8A2.2 2.2 0 1 0 15 9.2A2.2 2.2 0 1 0 15 4.8Z M4 12H6 M10.5 12H20 M8.2 9.8A2.2 2.2 0 1 0 8.2 14.2A2.2 2.2 0 1 0 8.2 9.8Z M4 17H11 M16 17H20 M13.5 14.8A2.2 2.2 0 1 0 13.5 19.2A2.2 2.2 0 1 0 13.5 14.8Z";
        public const string SelectAlpha = "M3.5 8.5V6.5A3 3 0 0 1 6.5 3.5H8.5 M10.5 3.5H13.5 M15.5 3.5H17.5A3 3 0 0 1 20.5 6.5V8.5 M20.5 10.5V13.5 M20.5 15.5V17.5A3 3 0 0 1 17.5 20.5H15.5 M13.5 20.5H10.5 M8.5 20.5H6.5A3 3 0 0 1 3.5 17.5V15.5 M3.5 13.5V10.5 | M10 8.5H14A1.5 1.5 0 0 1 15.5 10V14A1.5 1.5 0 0 1 14 15.5H10A1.5 1.5 0 0 1 8.5 14V10A1.5 1.5 0 0 1 10 8.5Z | *M10 8.5H14A1.5 1.5 0 0 1 15.5 10V14A1.5 1.5 0 0 1 14 15.5H10A1.5 1.5 0 0 1 8.5 14V10A1.5 1.5 0 0 1 10 8.5Z";
        public const string Mask = "M7 4H17A3 3 0 0 1 20 7V17A3 3 0 0 1 17 20H7A3 3 0 0 1 4 17V7A3 3 0 0 1 7 4Z | M12 7.5A4.5 4.5 0 1 0 12 16.5A4.5 4.5 0 1 0 12 7.5Z | *M12 7.5A4.5 4.5 0 1 0 12 16.5A4.5 4.5 0 1 0 12 7.5Z";
        // Mask commands: the mask frame with a badge (add / remove), or with its halves swapped (invert).
        public const string MaskAdd = "M6.5 3.5H14A2.5 2.5 0 0 1 16.5 6V14A2.5 2.5 0 0 1 14 16.5H6.5A2.5 2.5 0 0 1 4 14V6A2.5 2.5 0 0 1 6.5 3.5Z | M10.25 6.5A3.5 3.5 0 1 0 10.25 13.5A3.5 3.5 0 1 0 10.25 6.5Z M18.5 14V21 M15 17.5H22 | *M10.25 6.5A3.5 3.5 0 1 0 10.25 13.5A3.5 3.5 0 1 0 10.25 6.5Z";
        public const string MaskInvert = "M7 4H17A3 3 0 0 1 20 7V17A3 3 0 0 1 17 20H7A3 3 0 0 1 4 17V7A3 3 0 0 1 7 4Z | M12 7.5A4.5 4.5 0 1 0 12 16.5A4.5 4.5 0 1 0 12 7.5Z | ~M12 4V20 | *M7 4H12V7.5A4.5 4.5 0 0 0 12 16.5V20H7A3 3 0 0 1 4 17V7A3 3 0 0 1 7 4Z M12 7.5A4.5 4.5 0 0 1 12 16.5Z";
        public const string MaskRemove = "M6.5 3.5H14A2.5 2.5 0 0 1 16.5 6V14A2.5 2.5 0 0 1 14 16.5H6.5A2.5 2.5 0 0 1 4 14V6A2.5 2.5 0 0 1 6.5 3.5Z | M10.25 6.5A3.5 3.5 0 1 0 10.25 13.5A3.5 3.5 0 1 0 10.25 6.5Z M16.5 16.5L21.5 21.5 M21.5 16.5L16.5 21.5 | *M10.25 6.5A3.5 3.5 0 1 0 10.25 13.5A3.5 3.5 0 1 0 10.25 6.5Z";
        // Mirror across a dashed axis: left/right halves (horizontal) and top/bottom halves (vertical).
        public const string FlipHorizontal = "M9.5 6L3.5 18H9.5Z M14.5 6L20.5 18H14.5Z | ~M12 3V5 M12 8V10 M12 13V15 M12 18V21 | *M9.5 6L3.5 18H9.5Z";
        public const string FlipVertical = "M6 9.5L18 3.5V9.5Z M6 14.5L18 20.5V14.5Z | ~M3 12H5 M8 12H10 M13 12H15 M18 12H21 | *M6 9.5L18 3.5V9.5Z";
        public const string Sparkle = "M10 3.5C11.43 8.57 11.43 8.57 16.5 10C11.43 11.43 11.43 11.43 10 16.5C8.57 11.43 8.57 11.43 3.5 10C8.57 8.57 8.57 8.57 10 3.5Z | M18 14.5C18.66 16.84 18.66 16.84 21 17.5C18.66 18.16 18.66 18.16 18 20.5C17.34 18.16 17.34 18.16 15 17.5C17.34 16.84 17.34 16.84 18 14.5Z | *M10 3.5C11.43 8.57 11.43 8.57 16.5 10C11.43 11.43 11.43 11.43 10 16.5C8.57 11.43 8.57 11.43 3.5 10C8.57 8.57 8.57 8.57 10 3.5Z";
        public const string FillSelection = "M3.5 8.5V6.5A3 3 0 0 1 6.5 3.5H8.5 M10.5 3.5H13.5 M15.5 3.5H17.5A3 3 0 0 1 20.5 6.5V8.5 M20.5 10.5V13.5 M20.5 15.5V17.5A3 3 0 0 1 17.5 20.5H15.5 M13.5 20.5H10.5 M8.5 20.5H6.5A3 3 0 0 1 3.5 17.5V15.5 M3.5 13.5V10.5 | M12 7.8C12.92 11.08 12.92 11.08 16.2 12C12.92 12.92 12.92 12.92 12 16.2C11.08 12.92 11.08 12.92 7.8 12C11.08 11.08 11.08 11.08 12 7.8Z | *M12 7.8C12.92 11.08 12.92 11.08 16.2 12C12.92 12.92 12.92 12.92 12 16.2C11.08 12.92 11.08 12.92 7.8 12C11.08 11.08 11.08 11.08 12 7.8Z";
        // Layers, arrangement and design.
        public const string Rename = "M15.4 4.6A2.1 2.1 0 0 1 18.4 7.6L8.2 17.8L4.5 19L5.7 15.3Z | ~M13.4 6.6L16.4 9.6 M12.5 19.5H19.5";
        public const string Clip = "M7 4V11.5A2.5 2.5 0 0 0 9.5 14H17.5 M14.5 11L17.5 14L14.5 17 | ~M4 20H20";
        public const string Transform = "M4.5 3.5H6.5A1 1 0 0 1 7.5 4.5V6.5A1 1 0 0 1 6.5 7.5H4.5A1 1 0 0 1 3.5 6.5V4.5A1 1 0 0 1 4.5 3.5Z M17.5 3.5H19.5A1 1 0 0 1 20.5 4.5V6.5A1 1 0 0 1 19.5 7.5H17.5A1 1 0 0 1 16.5 6.5V4.5A1 1 0 0 1 17.5 3.5Z M4.5 16.5H6.5A1 1 0 0 1 7.5 17.5V19.5A1 1 0 0 1 6.5 20.5H4.5A1 1 0 0 1 3.5 19.5V17.5A1 1 0 0 1 4.5 16.5Z M17.5 16.5H19.5A1 1 0 0 1 20.5 17.5V19.5A1 1 0 0 1 19.5 20.5H17.5A1 1 0 0 1 16.5 19.5V17.5A1 1 0 0 1 17.5 16.5Z | ~M7.5 5.5H16.5 M7.5 18.5H16.5 M5.5 7.5V16.5 M18.5 7.5V16.5";
        public const string GroupAdd = "M3.5 8A2 2 0 0 1 5.5 6H9.3L11.1 7.8H18.5A2 2 0 0 1 20.5 9.8V17A2 2 0 0 1 18.5 19H5.5A2 2 0 0 1 3.5 17Z | M12 10.5V16.5 M9 13.5H15";
        public const string GroupRemove = "M3.5 8A2 2 0 0 1 5.5 6H9.3L11.1 7.8H18.5A2 2 0 0 1 20.5 9.8V17A2 2 0 0 1 18.5 19H5.5A2 2 0 0 1 3.5 17Z | M9 13.5H15";
        public const string Forward = "M12 13V3.5 M8 7.5L12 3.5L16 7.5 | ~M5 17H19 M5 20.5H19";
        public const string Backward = "M12 3.5V13 M8 9L12 13L16 9 | ~M5 17H19 M5 20.5H19";
        public const string FillStroke = "*M5.5 3.5H12.5A2 2 0 0 1 14.5 5.5V12.5A2 2 0 0 1 12.5 14.5H5.5A2 2 0 0 1 3.5 12.5V5.5A2 2 0 0 1 5.5 3.5Z | M5.5 3.5H12.5A2 2 0 0 1 14.5 5.5V12.5A2 2 0 0 1 12.5 14.5H5.5A2 2 0 0 1 3.5 12.5V5.5A2 2 0 0 1 5.5 3.5Z | M11.5 9.5H18.5A2 2 0 0 1 20.5 11.5V18.5A2 2 0 0 1 18.5 20.5H11.5A2 2 0 0 1 9.5 18.5V11.5A2 2 0 0 1 11.5 9.5Z";
        public const string Contiguous = "M5.5 4.5H11A1 1 0 0 1 12 5.5V12H5.5A1 1 0 0 1 4.5 11V5.5A1 1 0 0 1 5.5 4.5Z | ~M12 12H18.5A1 1 0 0 1 19.5 13V18.5A1 1 0 0 1 18.5 19.5H13A1 1 0 0 1 12 18.5Z | *M5.5 4.5H11A1 1 0 0 1 12 5.5V12H5.5A1 1 0 0 1 4.5 11V5.5A1 1 0 0 1 5.5 4.5Z";
        public const string LayerStack = "M12 4L20 8L12 12L4 8Z | ~M4 12L12 16L20 12 M4 16L12 20L20 16 | *M12 4L20 8L12 12L4 8Z";
        public const string SoftEdge = "M5 19L19 5 | ~M5 14.5L14.5 5 M9.5 19L19 9.5";
        public const string AutoSelect = "M6 5.5L17.5 13.2L12.2 14.5L9.6 19.5Z | ~M15 6.5L17 4.5 M17.8 9.5H20.3 M12 4.5V2 | *M6 5.5L17.5 13.2L12.2 14.5L9.6 19.5Z";
        public const string TipRound = "M12 4.5A7.5 7.5 0 1 1 12 19.5A7.5 7.5 0 1 1 12 4.5Z | *M12 4.5A7.5 7.5 0 1 1 12 19.5A7.5 7.5 0 1 1 12 4.5Z";
        public const string TipSquare = "M6.5 5H17.5A1.5 1.5 0 0 1 19 6.5V17.5A1.5 1.5 0 0 1 17.5 19H6.5A1.5 1.5 0 0 1 5 17.5V6.5A1.5 1.5 0 0 1 6.5 5Z | *M6.5 5H17.5A1.5 1.5 0 0 1 19 6.5V17.5A1.5 1.5 0 0 1 17.5 19H6.5A1.5 1.5 0 0 1 5 17.5V6.5A1.5 1.5 0 0 1 6.5 5Z";
        public const string TipDiamond = "M12 3.5L20.5 12L12 20.5L3.5 12Z | *M12 3.5L20.5 12L12 20.5L3.5 12Z";
        public const string TipStar = "M12 4.3L14.1 9.9L20.1 10.2L15.4 13.9L17 19.7L12 16.4L7 19.7L8.6 13.9L3.9 10.2L9.9 9.9Z | *M12 4.3L14.1 9.9L20.1 10.2L15.4 13.9L17 19.7L12 16.4L7 19.7L8.6 13.9L3.9 10.2L9.9 9.9Z";
        // A brush with a plus: save the current brush as a preset (내 프리셋).
        public const string BrushPreset = "M19.9 3.6A1.7 1.7 0 0 1 20.4 6L13.4 13L11 10.6L18 3.6A1.7 1.7 0 0 1 19.9 3.6Z | M11 10.6L13.4 13C13.4 16.6 10.8 19.8 4 20.5C4.6 13.8 7.4 11 11 10.6Z | ~M6.5 3.5V9.5 M3.5 6.5H9.5 | *M11 10.6L13.4 13C13.4 16.6 10.8 19.8 4 20.5C4.6 13.8 7.4 11 11 10.6Z";
        public const string Palette = "M12 3.5A8.5 8.5 0 1 0 12 20.5C13.1 20.5 13.6 19.6 13.1 18.7C12.5 17.7 13.1 16.5 14.5 16.5H16.5A4 4 0 0 0 20.5 12.5C20.5 7.5 16.7 3.5 12 3.5Z | ~M7.8 10.05A0.95 0.95 0 1 0 7.8 11.95A0.95 0.95 0 1 0 7.8 10.05Z M10.2 6.65A0.95 0.95 0 1 0 10.2 8.55A0.95 0.95 0 1 0 10.2 6.65Z M14.4 6.65A0.95 0.95 0 1 0 14.4 8.55A0.95 0.95 0 1 0 14.4 6.65Z M16.8 10.05A0.95 0.95 0 1 0 16.8 11.95A0.95 0.95 0 1 0 16.8 10.05Z";
        public const string Image = "M6 5H18A2.5 2.5 0 0 1 20.5 7.5V16.5A2.5 2.5 0 0 1 18 19H6A2.5 2.5 0 0 1 3.5 16.5V7.5A2.5 2.5 0 0 1 6 5Z | M4 16.5L8.8 11.8L13 16 M11.5 14.5L14.2 11.8L20 17.5 | ~M15.5 7.6A1.4 1.4 0 1 0 15.5 10.4A1.4 1.4 0 1 0 15.5 7.6Z";
        public const string Text = "M5.5 7V5H18.5V7 M12 5V19 M9.5 19H14.5";
        public const string Shape = "M5.3 3.5H11.2A1.8 1.8 0 0 1 13 5.3V11.2A1.8 1.8 0 0 1 11.2 13H5.3A1.8 1.8 0 0 1 3.5 11.2V5.3A1.8 1.8 0 0 1 5.3 3.5Z | M15.8 10.8A5 5 0 1 0 15.8 20.8A5 5 0 1 0 15.8 10.8Z | *M15.8 10.8A5 5 0 1 0 15.8 20.8A5 5 0 1 0 15.8 10.8Z";
        // Drawing work (사용 목적 · 건축학과): a floor plan with a door swing, and three line weights.
        public const string Plan = "M5 4H19A1 1 0 0 1 20 5V19A1 1 0 0 1 19 20H5A1 1 0 0 1 4 19V5A1 1 0 0 1 5 4Z M12 4V10.5 M4 13H8.5 | ~M12 13.5V20 M12 13.5A6.5 6.5 0 0 1 18.5 20 | *M12 13.5A6.5 6.5 0 0 1 18.5 20H12Z";
        // 지도 만들기: a folded map with a location pin on its middle panel.
        public const string Map = "M3.5 6.5L9 4.5L15 6.5L20.5 4.5V17.5L15 19.5L9 17.5L3.5 19.5Z M9 4.5V17.5 M15 6.5V19.5 | ~M12 8.2A2.3 2.3 0 0 1 14.3 10.5C14.3 12.4 12 14.8 12 14.8C12 14.8 9.7 12.4 9.7 10.5A2.3 2.3 0 0 1 12 8.2Z | *M3.5 6.5L9 4.5V17.5L3.5 19.5Z";
        public const string LineWeight = "M5 4.8H19A1 1 0 0 1 20 5.8V7.2A1 1 0 0 1 19 8.2H5A1 1 0 0 1 4 7.2V5.8A1 1 0 0 1 5 4.8Z M4 13H20 | ~M4 18.5H20 | *M5 4.8H19A1 1 0 0 1 20 5.8V7.2A1 1 0 0 1 19 8.2H5A1 1 0 0 1 4 7.2V5.8A1 1 0 0 1 5 4.8Z";
        // 스케치 사진 정리: a sheet seen in perspective with a hand-drawn stroke on it.
        public const string Sketch = "M6.5 4.5L18 3.5L20.5 20.5L3.5 19.5Z | M8 15.5C9.4 12.2 11 14.6 12.4 11.6C13.6 9.2 15.2 9.6 16.6 7.6 | *M6.5 4.5L18 3.5L20.5 20.5L3.5 19.5Z";
        // An object and the shadow it casts toward the lower right.
        public const string Shadow = "M6 4.5H12.5A1.5 1.5 0 0 1 14 6V12.5A1.5 1.5 0 0 1 12.5 14H6A1.5 1.5 0 0 1 4.5 12.5V6A1.5 1.5 0 0 1 6 4.5Z | ~M13.6 5L19.5 10.9V18A1.5 1.5 0 0 1 18 19.5H10.9L5 13.6 | *M13.6 5L19.5 10.9V18A1.5 1.5 0 0 1 18 19.5H10.9L5 13.6L6 14H12.5A1.5 1.5 0 0 0 14 12.5V6Z";
        // Canvas alignment: a guide line and two bars.
        public const string AlignLeft = "M4 3.5V20.5 | M7.5 6.5H18A1 1 0 0 1 19 7.5V9.5A1 1 0 0 1 18 10.5H7.5A1 1 0 0 1 6.5 9.5V7.5A1 1 0 0 1 7.5 6.5Z | M7.5 13.5H13.5A1 1 0 0 1 14.5 14.5V16.5A1 1 0 0 1 13.5 17.5H7.5A1 1 0 0 1 6.5 16.5V14.5A1 1 0 0 1 7.5 13.5Z | *M7.5 6.5H18A1 1 0 0 1 19 7.5V9.5A1 1 0 0 1 18 10.5H7.5A1 1 0 0 1 6.5 9.5V7.5A1 1 0 0 1 7.5 6.5Z M7.5 13.5H13.5A1 1 0 0 1 14.5 14.5V16.5A1 1 0 0 1 13.5 17.5H7.5A1 1 0 0 1 6.5 16.5V14.5A1 1 0 0 1 7.5 13.5Z";
        public const string AlignCenter = "M12 3.5V20.5 | M6 6.5H18A1 1 0 0 1 19 7.5V9.5A1 1 0 0 1 18 10.5H6A1 1 0 0 1 5 9.5V7.5A1 1 0 0 1 6 6.5Z | M8.5 13.5H15.5A1 1 0 0 1 16.5 14.5V16.5A1 1 0 0 1 15.5 17.5H8.5A1 1 0 0 1 7.5 16.5V14.5A1 1 0 0 1 8.5 13.5Z | *M6 6.5H18A1 1 0 0 1 19 7.5V9.5A1 1 0 0 1 18 10.5H6A1 1 0 0 1 5 9.5V7.5A1 1 0 0 1 6 6.5Z M8.5 13.5H15.5A1 1 0 0 1 16.5 14.5V16.5A1 1 0 0 1 15.5 17.5H8.5A1 1 0 0 1 7.5 16.5V14.5A1 1 0 0 1 8.5 13.5Z";
        public const string AlignRight = "M20 3.5V20.5 | M6 6.5H16.5A1 1 0 0 1 17.5 7.5V9.5A1 1 0 0 1 16.5 10.5H6A1 1 0 0 1 5 9.5V7.5A1 1 0 0 1 6 6.5Z | M10.5 13.5H16.5A1 1 0 0 1 17.5 14.5V16.5A1 1 0 0 1 16.5 17.5H10.5A1 1 0 0 1 9.5 16.5V14.5A1 1 0 0 1 10.5 13.5Z | *M6 6.5H16.5A1 1 0 0 1 17.5 7.5V9.5A1 1 0 0 1 16.5 10.5H6A1 1 0 0 1 5 9.5V7.5A1 1 0 0 1 6 6.5Z M10.5 13.5H16.5A1 1 0 0 1 17.5 14.5V16.5A1 1 0 0 1 16.5 17.5H10.5A1 1 0 0 1 9.5 16.5V14.5A1 1 0 0 1 10.5 13.5Z";
        public const string AlignTop = "M3.5 4H20.5 | M7.5 6.5H9.5A1 1 0 0 1 10.5 7.5V18A1 1 0 0 1 9.5 19H7.5A1 1 0 0 1 6.5 18V7.5A1 1 0 0 1 7.5 6.5Z | M14.5 6.5H16.5A1 1 0 0 1 17.5 7.5V13.5A1 1 0 0 1 16.5 14.5H14.5A1 1 0 0 1 13.5 13.5V7.5A1 1 0 0 1 14.5 6.5Z | *M7.5 6.5H9.5A1 1 0 0 1 10.5 7.5V18A1 1 0 0 1 9.5 19H7.5A1 1 0 0 1 6.5 18V7.5A1 1 0 0 1 7.5 6.5Z M14.5 6.5H16.5A1 1 0 0 1 17.5 7.5V13.5A1 1 0 0 1 16.5 14.5H14.5A1 1 0 0 1 13.5 13.5V7.5A1 1 0 0 1 14.5 6.5Z";
        public const string AlignMiddle = "M3.5 12H20.5 | M7.5 5H9.5A1 1 0 0 1 10.5 6V18A1 1 0 0 1 9.5 19H7.5A1 1 0 0 1 6.5 18V6A1 1 0 0 1 7.5 5Z | M14.5 7.5H16.5A1 1 0 0 1 17.5 8.5V15.5A1 1 0 0 1 16.5 16.5H14.5A1 1 0 0 1 13.5 15.5V8.5A1 1 0 0 1 14.5 7.5Z | *M7.5 5H9.5A1 1 0 0 1 10.5 6V18A1 1 0 0 1 9.5 19H7.5A1 1 0 0 1 6.5 18V6A1 1 0 0 1 7.5 5Z M14.5 7.5H16.5A1 1 0 0 1 17.5 8.5V15.5A1 1 0 0 1 16.5 16.5H14.5A1 1 0 0 1 13.5 15.5V8.5A1 1 0 0 1 14.5 7.5Z";
        public const string AlignBottom = "M3.5 20H20.5 | M7.5 5H9.5A1 1 0 0 1 10.5 6V16.5A1 1 0 0 1 9.5 17.5H7.5A1 1 0 0 1 6.5 16.5V6A1 1 0 0 1 7.5 5Z | M14.5 9.5H16.5A1 1 0 0 1 17.5 10.5V16.5A1 1 0 0 1 16.5 17.5H14.5A1 1 0 0 1 13.5 16.5V10.5A1 1 0 0 1 14.5 9.5Z | *M7.5 5H9.5A1 1 0 0 1 10.5 6V16.5A1 1 0 0 1 9.5 17.5H7.5A1 1 0 0 1 6.5 16.5V6A1 1 0 0 1 7.5 5Z M14.5 9.5H16.5A1 1 0 0 1 17.5 10.5V16.5A1 1 0 0 1 16.5 17.5H14.5A1 1 0 0 1 13.5 16.5V10.5A1 1 0 0 1 14.5 9.5Z";
        // Paragraph alignment.
        public const string TextLeft = "M4 6H20 M4 14H20 | ~M4 10H14 M4 18H14";
        public const string TextCenter = "M4 6H20 M4 14H20 | ~M7 10H17 M7 18H17";
        public const string TextRight = "M4 6H20 M4 14H20 | ~M10 10H20 M10 18H20";
        public const string TextJustify = "M4 6H20 M4 14H20 | ~M4 10H20 M4 18H13";
        // A cut-out figure standing in front of a large letter T (피사체를 글자 앞으로).
        public const string SubjectFront = "M15.5 6.6A2.7 2.7 0 1 0 15.5 12A2.7 2.7 0 1 0 15.5 6.6Z M10 20.5C10 16.7 12.4 14 15.5 14C18.6 14 21 16.7 21 20.5Z | ~M3 4.5H17 M8.5 4.5V20.5 | *M15.5 6.6A2.7 2.7 0 1 0 15.5 12A2.7 2.7 0 1 0 15.5 6.6Z M10 20.5C10 16.7 12.4 14 15.5 14C18.6 14 21 16.7 21 20.5Z";
        public const string Search = "M10.5 4A6.5 6.5 0 1 0 10.5 17A6.5 6.5 0 1 0 10.5 4Z M15.5 15.5L20 20";
        // 디자인 스타일: a picture split on its diagonal, one half restyled (tint) and the other screened (strokes).
        public const string Style = "M6 4.5H18A1.5 1.5 0 0 1 19.5 6V18A1.5 1.5 0 0 1 18 19.5H6A1.5 1.5 0 0 1 4.5 18V6A1.5 1.5 0 0 1 6 4.5Z | M5 19L19 5 | ~M11.5 19.5L19.5 11.5 M15.5 19.5L19.5 15.5 | *M6 4.5H18.5L4.5 18.5V6A1.5 1.5 0 0 1 6 4.5Z";
        // 화면 스타일: 친절한 화면 (a window with large cards) and 간결한 화면 (a thin tool column and stacked tab panels).
        public const string ScreenFriendly = "M5 4H19A2 2 0 0 1 21 6V18A2 2 0 0 1 19 20H5A2 2 0 0 1 3 18V6A2 2 0 0 1 5 4Z | ~M6.5 7.5V16.5 M10 7.5H13 | *M13.5 7.5H17.5V11H13.5Z M13.5 12.5H17.5V16.5H13.5Z";
        public const string ScreenCompact = "M5 4H19A2 2 0 0 1 21 6V18A2 2 0 0 1 19 20H5A2 2 0 0 1 3 18V6A2 2 0 0 1 5 4Z M6.5 4V20 M15.5 4V20 | ~M15.5 9.5H21 M15.5 14.5H21 | *M15.5 4H19A2 2 0 0 1 21 6V18A2 2 0 0 1 19 20H15.5Z";
        // 간결한 화면 dock: the group menu (☰), and the 내비게이터 · 히스토그램 · 기록 · 견본 panels.
        public const string Menu = "M5 7H19 M5 12H19 M5 17H19";
        public const string Navigator = "M5 5H19A1.5 1.5 0 0 1 20.5 6.5V17.5A1.5 1.5 0 0 1 19 19H5A1.5 1.5 0 0 1 3.5 17.5V6.5A1.5 1.5 0 0 1 5 5Z | M9.5 9H15.5V14.5H9.5Z | *M9.5 9H15.5V14.5H9.5Z";
        public const string Histogram = "M3.5 19.5H20.5 | M4 19.5L7.2 12.5L10.2 15L13.6 6L17 12.4L20 9.8V19.5 | *M4 19.5L7.2 12.5L10.2 15L13.6 6L17 12.4L20 9.8V19.5Z";
        public const string History = "M4.5 12A7.5 7.5 0 1 0 7 6.4 M4.5 3.5V8H9 | ~M12 8V12L14.8 13.8";
        public const string Swatches = "M5 4.5H10A.5 .5 0 0 1 10.5 5V10A.5 .5 0 0 1 10 10.5H5A.5 .5 0 0 1 4.5 10V5A.5 .5 0 0 1 5 4.5Z M14 4.5H19A.5 .5 0 0 1 19.5 5V10A.5 .5 0 0 1 19 10.5H14A.5 .5 0 0 1 13.5 10V5A.5 .5 0 0 1 14 4.5Z M5 13.5H10A.5 .5 0 0 1 10.5 14V19A.5 .5 0 0 1 10 19.5H5A.5 .5 0 0 1 4.5 19V14A.5 .5 0 0 1 5 13.5Z M14 13.5H19A.5 .5 0 0 1 19.5 14V19A.5 .5 0 0 1 19 19.5H14A.5 .5 0 0 1 13.5 19V14A.5 .5 0 0 1 14 13.5Z | *M5 4.5H10V10.5H4.5V5Z M13.5 13.5H19.5V19.5H13.5Z";
    }
}
