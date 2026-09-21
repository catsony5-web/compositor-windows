using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : "test-results.txt");
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "Compositor · 오류", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        Theme.Apply(app);
        return app.Run(new MainWindow(args.FirstOrDefault()));
    }
}

public static class Theme
{
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static readonly Brush Text = Brush("#E8EBF1"), Muted = Brush("#929BAD"), Panel = Brush("#20232B"), Line = Brush("#373D49"), Accent = Brush("#A2E8CD");
    public static void Apply(Application app)
    {
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#313641")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(3)));
        button.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(content); template.VisualTree = border;
        button.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BorderBrushProperty, Accent)); button.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .35)); button.Triggers.Add(disabled);
        app.Resources.Add(typeof(Button), button);
        var text = new Style(typeof(TextBox));
        text.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#16191F"))); text.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        text.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
        text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(3))); text.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        app.Resources.Add(typeof(TextBox), text);
        var tip = new Style(typeof(ToolTip)); tip.Setters.Add(new Setter(Control.BackgroundProperty, Panel)); tip.Setters.Add(new Setter(Control.ForegroundProperty, Text)); app.Resources.Add(typeof(ToolTip), tip);
    }
    public static TextBlock Label(string text, double size = 12, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, Margin = new Thickness(3, 6, 3, 6), VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var b = new Button { Content = text, ToolTip = tooltip ?? text }; b.Click += (_, _) => action(); return b;
    }
}
