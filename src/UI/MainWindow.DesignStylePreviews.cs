using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Offscreen review of the design styles (--render-studio-previews): the gallery on a photo and on a
// plan, the editor and the style folder's properties after applying one, and every style applied
// to the sample plan and the sample photo, rendered through the same compositor as export.
public sealed partial class MainWindow
{
    internal static Document SamplePhotoDocument(string name = "Sea Window")
    {
        var sample = Demo.Create(); var photo = sample.Layers[0];
        var doc = new Document { Name = name, Width = sample.Width, Height = sample.Height };
        doc.Add(new Layer { Name = photo.Name, Pixels = photo.Pixels });
        return doc;
    }

    static void CaptureWindow(Window window, string path, int width, int height)
    {
        var content = OffscreenPreview.Host(window); var size = new Size(width, height);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        Loc.PrepareOffscreen(content); content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        SavePng(image, path);
    }

    internal void RenderDesignStylePreviews(string directory, List<string>? timings = null)
    {
        Directory.CreateDirectory(directory);
        void Gallery(string name)
        {
            var gallery = CreateDesignStyleDialog(null);
            gallery.WaitForPreviews(TimeSpan.FromSeconds(90));
            CaptureWindow(gallery, Path.Combine(directory, name + ".png"), 1040, 720);
            gallery.CancelPreviews(); gallery.Close();
        }
        AddTab(SamplePhotoDocument(), null); SetWorkspaceMode(false);
        Gallery("design-styles");
        var plan = SyntheticPlan.Create();
        AddTab(plan, null); SetWorkspaceMode(true);
        Gallery("design-styles-drawing");
        // The editor after applying a style: the folder selected, its properties in the right panel.
        WaitOnDispatcher(() => ApplyDesignStyleAsync(DesignStyles.ScreentonePlan, null));
        ShowStudioPage(1); RenderPreview(Path.Combine(directory, "design-style-applied.png"));
        RenderPane(studioPanes[1], Path.Combine(directory, "design-style-properties.png"), 360, 1080);
        // Re-opening the gallery on the selected folder: settings loaded, "다시 적용" and "스타일 제거".
        Gallery("design-styles-reapply");
        RenderDesignStyleSamples(directory, timings);
    }

    internal static void RenderDesignStyleSamples(string directory, List<string>? timings = null)
    {
        Directory.CreateDirectory(directory);
        var plan = SyntheticPlan.Create(); var photo = SamplePhotoDocument();
        void Save(Raster image, string name) => SavePng(image.Bitmap(), Path.Combine(directory, name + ".png"));
        foreach (var (style, source, suffix) in new[]
        {
            (DesignStyles.ScreentonePlan, plan, "plan"), (DesignStyles.DarkSection, plan, "plan"), (DesignStyles.Cyanotype, plan, "plan"),
            (DesignStyles.Cyanotype, photo, "photo"), (DesignStyles.NeoBrutalistPoster, photo, "photo"), (DesignStyles.TranslucentEditorial, photo, "photo")
        })
        {
            var doc = source.Snapshot();
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(style));
            var watch = System.Diagnostics.Stopwatch.StartNew(); var image = Imaging.Render(doc); watch.Stop();
            Save(image, $"style-{style}-{suffix}");
            timings?.Add($"{style} on {suffix}: apply {outcome.Elapsed.TotalMilliseconds:0} ms, render {watch.Elapsed.TotalMilliseconds:0} ms, {outcome.LayerCount} layers; {string.Join(" / ", outcome.Notes)}");
        }
    }
}
