using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    public void RenderStudioPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        RenderPreview(Path.Combine(directory, "startup.png"));
        OpenLearningSample(); RenderPreview(Path.Combine(directory, "editor.png"));
        void Capture(Window window, string name, int width, int height)
        {
            var content = (FrameworkElement)window.Content; var size = new Size(width, height);
            if (content is System.Windows.Controls.Panel panel && panel.Background == null) panel.Background = window.Background;
            if (ReferenceEquals(window, this)) studioScroll.Height = PreferredStudioHeight(height);
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            if (ReferenceEquals(window, this)) { canvas.Fit(); canvas.InvalidateVisual(); content.UpdateLayout(); }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        Capture(new NewDocumentDialog(null), "new-document", 970, 720);
        var saveChanges = new SaveChangesDialog(null, doc.Name);
        var saveContent = (FrameworkElement)saveChanges.Content;
        saveChanges.Content = null;
        // Render the window background through the root's margin as well. The
        // native Windows caption is deliberately absent from offscreen previews.
        var saveHost = new System.Windows.Controls.Border { Background = saveChanges.Background, Child = saveContent };
        try
        {
            int width = (int)Math.Ceiling(saveChanges.Width);
            saveHost.Measure(new Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(Math.Max(saveChanges.MinHeight, saveHost.DesiredSize.Height));
            saveHost.Measure(new Size(width, height)); saveHost.Arrange(new Rect(0, 0, width, height)); saveHost.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(saveHost);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(directory, "save-changes.png")); encoder.Save(output);
        }
        finally { saveHost.Child = null; saveChanges.Close(); }
        foreach (string kind in new[] { "exposure", "levels", "saturation", "blur" })
        {
            var quick = CreateQuickAdjustmentDialog(null, kind);
            Capture(quick, "quick-" + kind, 480, (int)quick.Height);
            quick.Close();
        }
        var adjustment = new AdjustmentDialog(null, doc, new AdjustmentSpec { Kind = AdjustmentKind.HueSaturation });
        adjustment.SetDesignPreview(composite!); Capture(adjustment, "adjustment", 1040, 640);
        var developSpec = new AdjustmentSpec { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = new()
            { Exposure = .2, Highlights = -35, Shadows = 28, Temperature = 8, Vibrance = 20, Texture = 15 } };
        var develop = new AdjustmentDialog(null, doc, developSpec);
        develop.SetDesignPreview(Imaging.Render(AdjustmentDialog.PreviewDocument(doc, null, developSpec, true, null)));
        Capture(develop, "photo-develop", 1040, 760);
        ShowStudioPage(2); Capture(this, "colors", 1480, 920);
        ShowStudioPage(3); Capture(this, "brush", 1480, 920);
        ShowStudioPage(0); Capture(this, "compact", 1200, 750);
        void CapturePane(FrameworkElement pane, string name, int width, int height)
        {
            if (pane is StudioPane movable) RemovePane(movable);
            ((FrameworkElement)Content).UpdateLayout();
            // A previously docked element retains its last layout slot while it
            // is parentless. Give it a real standalone parent so deferred WPF
            // layout passes cannot restore the former sidebar viewport size.
            var host = new System.Windows.Controls.Border { Width = width, Height = height, Background = Theme.Header, Child = pane };
            try
            {
                host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
            }
            finally { host.Child = null; ShowStudioPage(studioPage, false); }
        }
        ShowStudioPage(1); CapturePane(studioPanes[1], "image-properties", 360, 840);
        ShowStudioPage(2); CapturePane(studioPanes[2], "color-palette", 360, 1180);
        ShowStudioPage(3); SelectBrushTip(BrushTip.Star); CapturePane(studioPanes[3], "brush-settings", 360, 1120);
        SelectBrushTip(BrushTip.Round);
        var text = doc.Layers.First(l => l.Text != null); doc.ActiveId = text.Id; BuildProperties(); ShowStudioPage(1);
        CapturePane(studioPanes[1], "text-properties", 360, 1160);
        var shape = VectorShapes.Create(new ShapeSpec { Width = 360, Height = 150, CornerRadius = 28, FillArgb = 0xD92E4862, StrokeEnabled = true, StrokeArgb = 0xFFC0D9F2, StrokeWidth = 2 }, 100, 100);
        doc.Add(shape); SetWorkspaceMode(true); Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        Capture(this, "design", 1480, 920); CapturePane(studioPanes[1], "shape-properties", 360, 880);
    }
    // Render the actual WPF controls without showing a window or taking input focus.
    public void RenderPreview(string path)
    {
        headlessTesting = true;
        UpdateColor(); UpdateBrushLabel(); canvas.Document = HasDocument ? doc : null; canvas.ShowLayerBounds = HasDocument;
        BuildProperties(); BuildLayers(); RebuildTabs(); UpdateStatus();
        var content = (FrameworkElement)Content;
        var size = new Size(1480, 920);
        studioScroll.Height = PreferredStudioHeight(size.Height);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        if (HasDocument)
        {
            canvas.Fit(); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); histogram.Update(composite);
        }
        else { composite = null; canvas.Composite = null; }
        canvas.InvalidateVisual();
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); using var output = File.Create(full); encoder.Save(output);
    }
}
