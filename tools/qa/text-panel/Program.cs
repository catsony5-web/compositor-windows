using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Windows;

static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] static int Main(string[] args)
    {
        string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/qa/text-panel");
        Directory.CreateDirectory(outputDirectory);
        var app = new Application(); Theme.Apply(app);
        var window = new MainWindow(null);
        typeof(MainWindow).GetField("headlessTesting", Private)!.SetValue(window, true);
        var doc = (Document)typeof(MainWindow).GetField("doc", Private)!.GetValue(window)!;
        var layer = doc.Layers.First(item => item.Kind == LayerKind.Text);
        foreach (var item in doc.Layers) item.Locked = false;
        doc.ActiveId = layer.Id;
        typeof(MainWindow).GetMethod("OpenTextProperties", Private)!.Invoke(window, new object?[] { layer, null });
        var panes = (Array)typeof(MainWindow).GetField("studioPanes", Private)!.GetValue(window)!;
        var pane = (FrameworkElement)panes.GetValue(1)!;
        var studioScroll = (ScrollViewer)typeof(MainWindow).GetField("studioScroll", Private)!.GetValue(window)!;
        studioScroll.Content = null;
        foreach (var (width, height, scale) in new[] { (350, 1120, 1d), (350, 410, 1d), (310, 1000, 1d), (350, 900, 1.5) })
        {
            var size = new Size(width, height); pane.Measure(size); pane.Arrange(new Rect(size)); pane.UpdateLayout();
            var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(pane);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string path = Path.Combine(outputDirectory, $"text-properties-{width}x{height}-{scale:0.0}x.png");
            using (var output = File.Create(path)) encoder.Save(output); Console.WriteLine(path);
            foreach (var label in Descendants(pane).OfType<TextBlock>())
                if (label.IsVisible && (label.DesiredSize.Height > label.ActualHeight + 1 || label.DesiredSize.Width > label.ActualWidth + 1))
                    Console.WriteLine($"CLIPPED? {label.Text} desired={label.DesiredSize} actual={label.ActualWidth}x{label.ActualHeight}");
        }
        app.Shutdown(); return 0;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
