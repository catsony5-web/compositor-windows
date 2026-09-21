using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Theme
{
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly Brush Text = Brush("#EAEDF2");
    public static readonly Brush Muted = Brush("#B0B7C2");
    public static readonly Brush Panel = Brush("#22252A");
    public static readonly Brush Line = Brush("#3C4149");
    public static readonly Brush Accent = Brush("#A8CAFF");
    public static readonly Brush Primary = Brush("#365F92");
    public static readonly Brush Selected = Brush("#304766");
    public static readonly Brush Surface = Brush("#2C3037");
    public static readonly Brush Header = Brush("#17191E");
    public static readonly Brush Input = Brush("#2B2F35");
    // Segoe UI for Latin/numbers, with Windows-hinted Korean fallback at readable sizes.
    public static readonly FontFamily UiFont = new("Segoe UI, Malgun Gothic");
    public const double BodySize = 13;
    public const double CaptionSize = 12;
    public const double HeadingSize = 14;
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

    public static FrameworkElement Section(string title)
    {
        var label = Label(title, HeadingSize); label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 12, 0, 8);
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(2, 8, 2, 2), Child = label };
    }

    // Action labels remain complete at narrow widths; only the affordance occupies a fixed column.
    public static Button ActionRow(string label, Action action, string? tooltip = null)
    {
        var button = Button("", action, tooltip ?? label);
        if (Application.Current?.TryFindResource("InspectorAction") is Style style) button.Style = style;
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0, 0, 0, 1);
        button.BorderBrush = Line; button.Padding = new Thickness(8, 9, 8, 9); button.Margin = new Thickness(0);
        button.MinHeight = 38; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.Children.Add(new TextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0 0 L 4 4 L 0 8"), Stroke = Muted, StrokeThickness = 1.4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
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
}
