using Compositor.Windows;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
public static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/qa/color-palette");
        Directory.CreateDirectory(outputDirectory);
        var app = new Application(); Theme.Apply(app);
        int failed = 0;
        ColorPaletteTests.Run((name, test) => { try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } });
        var palette = new ColorPalettePanel();
        var root = new Border { Background = Theme.Panel, Child = palette, Padding = new Thickness(12) };
        root.Measure(new Size(300, double.PositiveInfinity));
        int height = (int)Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, 300, height)); root.UpdateLayout();
        var render = new RenderTargetBitmap(300, height, 96, 96, PixelFormats.Pbgra32); render.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using (var file = File.Create(Path.Combine(outputDirectory, "palette.png"))) encoder.Save(file);
        app.Shutdown(); return failed;
    }
}
