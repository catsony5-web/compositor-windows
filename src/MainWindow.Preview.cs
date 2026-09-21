using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Render the actual WPF controls without showing a window or taking input focus.
    public void RenderPreview(string path)
    {
        history.Reset(doc); if (tabs.Count == 0) InitializeWorkspace();
        UpdateColor(); UpdateBrushLabel(); canvas.Document = doc; canvas.ShowLayerBounds = true;
        BuildProperties(); BuildLayers(); RebuildTabs(); UpdateStatus();
        var content = (FrameworkElement)Content;
        var size = new Size(1480, 920);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        canvas.Fit(); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); canvas.InvalidateVisual();
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); using var output = File.Create(full); encoder.Save(output);
    }
}
