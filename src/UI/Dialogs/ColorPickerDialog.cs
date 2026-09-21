using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ColorValues
{
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return ((h + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
    public static Color FromHsv(double h, double s, double v, byte alpha = 255)
    {
        h = (h % 360 + 360) % 360; s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb(alpha, Imaging.Byte((r + m) * 255), Imaging.Byte((g + m) * 255), Imaging.Byte((b + m) * 255));
    }
}

public sealed class ColorPlane : FrameworkElement
{
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Value { get; set; }
    public event Action<double, double>? Changed;
    public ColorPlane()
    {
        Cursor = Cursors.Cross; MinWidth = 240; MinHeight = 240;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } };
    }
    void Pick(Point p) { Saturation = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1); Value = 1 - Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1); Changed?.Invoke(Saturation, Value); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, ColorValues.FromHsv(Hue, 1, 1), 0), null, rect);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), new Pen(Theme.Line, 1), rect);
        var p = new Point(Saturation * ActualWidth, (1 - Value) * ActualHeight);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), p, 5, 5); dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 5, 5);
    }
}

public sealed class HueStrip : FrameworkElement
{
    public double Hue { get; set; }
    public event Action<double>? Changed;
    public HueStrip()
    {
        Width = 22; MinHeight = 240; Cursor = Cursors.Hand;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } };
    }
    void Pick(Point p) { Hue = Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1) * 359.999; Changed?.Invoke(Hue); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i <= 6; i++) gradient.GradientStops.Add(new GradientStop(ColorValues.FromHsv(i * 60, 1, 1), i / 6.0));
        dc.DrawRectangle(gradient, new Pen(Theme.Line, 1), new Rect(RenderSize));
        dc.DrawRectangle(null, new Pen(Brushes.Black, 3), new Rect(-2, Hue / 360 * ActualHeight - 2, ActualWidth + 4, 4));
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(-2, Hue / 360 * ActualHeight - 2, ActualWidth + 4, 4));
    }
}

public sealed class ColorPickerDialog : Window
{
    public Color SelectedColor { get; private set; }
    readonly ColorPlane plane = new(); readonly HueStrip hue = new();
    readonly TextBox hex = new() { Width = 116 }, red = new(), green = new(), blue = new(), alpha = new();
    readonly Border newColor = new() { Height = 45, BorderBrush = Theme.Line, BorderThickness = new Thickness(1) };
    readonly TextBlock error = new() { Foreground = Theme.Brush("#FFBE9D"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    readonly Button apply;
    bool syncing;
    public ColorPickerDialog(Window owner, Color initial, string label = "전경색")
    {
        SelectedColor = initial; Owner = owner; Title = "Morupixel · " + label; Width = 560; Height = 455;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Panel; Foreground = Theme.Text; FontFamily = Theme.UiFont;
        var root = new Grid { Margin = new Thickness(22) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Theme.Label(label + " 선택", 20));
        var body = new Grid { Margin = new Thickness(0, 12, 0, 8) }; body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); Grid.SetRow(body, 1); root.Children.Add(body);
        body.Children.Add(plane); Grid.SetColumn(hue, 1); body.Children.Add(hue);
        var values = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(values, 2); body.Children.Add(values);
        values.Children.Add(Theme.Label("새 색상 / 이전 색상", 10, Theme.Muted)); values.Children.Add(newColor);
        var previous = Theme.Button("", () => SetColor(initial), "이전 색상으로 복원"); previous.Height = 26; previous.Background = new SolidColorBrush(initial); previous.Margin = new Thickness(0, 0, 0, 8); values.Children.Add(previous);
        void Field(string text, TextBox box) { var row = new DockPanel(); row.Children.Add(new TextBlock { Text = text, Width = 25, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Muted }); box.Width = 92; box.Padding = new Thickness(5, 2, 5, 2); row.Children.Add(box); values.Children.Add(row); }
        Field("R", red); Field("G", green); Field("B", blue); Field("A", alpha); Field("#", hex);
        values.Children.Add(error);
        var footer = new DockPanel(); Grid.SetRow(footer, 2); root.Children.Add(footer);
        apply = Theme.Button("색상 적용", () => { if (ValidateFields()) DialogResult = true; }); apply.IsDefault = true; apply.Background = Theme.Primary; DockPanel.SetDock(apply, Dock.Right); footer.Children.Add(apply);
        var cancel = Theme.Button("취소", () => DialogResult = false); cancel.IsCancel = true; DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        plane.ToolTip = "드래그하여 채도·명도 조절"; hue.ToolTip = "드래그하여 색조 조절";
        plane.Changed += (_, _) => SetColor(ColorValues.FromHsv(hue.Hue, plane.Saturation, plane.Value, SelectedColor.A), true);
        hue.Changed += h => { plane.Hue = h; SetColor(ColorValues.FromHsv(h, plane.Saturation, plane.Value, SelectedColor.A), true); };
        foreach (var box in new[] { red, green, blue, alpha }) box.TextChanged += (_, _) => { if (!syncing) ReadRgb(box); };
        hex.TextChanged += (_, _) => { if (!syncing) ReadHex(); };
        SetColor(initial);
    }
    void SetColor(Color color, bool preserveHue = false, TextBox? edited = null)
    {
        SelectedColor = color; syncing = true;
        if (!preserveHue) { var hsv = ColorValues.ToHsv(color); if (hsv.S > 0) hue.Hue = hsv.H; plane.Hue = hue.Hue; plane.Saturation = hsv.S; plane.Value = hsv.V; }
        // Do not replace the active input while typing; replacing Text resets its caret.
        if (edited != red) red.Text = color.R.ToString();
        if (edited != green) green.Text = color.G.ToString();
        if (edited != blue) blue.Text = color.B.ToString();
        if (edited != alpha) alpha.Text = color.A.ToString();
        if (edited != hex) hex.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        newColor.Background = new SolidColorBrush(color); plane.InvalidateVisual(); hue.InvalidateVisual(); syncing = false; error.Text = ""; apply.IsEnabled = true;
    }
    bool ValidateFields() => apply.IsEnabled;
    void ReadRgb(TextBox? edited = null)
    {
        if (!byte.TryParse(red.Text, out var r) || !byte.TryParse(green.Text, out var g) || !byte.TryParse(blue.Text, out var b) || !byte.TryParse(alpha.Text, out var a)) { error.Text = "RGBA: 0~255"; apply.IsEnabled = false; return; }
        SetColor(Color.FromArgb(a, r, g, b), edited: edited);
    }
    void ReadHex()
    {
        string text = hex.Text.Trim().TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) { error.Text = "HEX: 여섯 자리"; apply.IsEnabled = false; return; }
        SetColor(Color.FromArgb(SelectedColor.A, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), edited: hex);
    }
}
