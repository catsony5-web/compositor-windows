using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Offscreen review of 간결한 화면 (--render-studio-previews, or --render-compact-previews alone):
// the whole window at 1480×920 and 1280×720 with a photo and with a drawing, the start screen,
// and the dock with every group's tabs. The screen returns to 친절한 화면 afterwards.
public sealed partial class MainWindow
{
    public void RenderCompactPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        headlessTesting = true;
        try
        {
            SetScreenStyle(true);
            RenderCompactCapture(directory, "compact-startup", 1480, 920);
            OpenLearningSample();
            // A few steps for the 기록 panel, the last one undone so a redo step shows.
            Edit("레이어 이름", () => doc.Layers[0].Name = Loc.T("바다 창문"));
            EditLayer("불투명도", l => l.Opacity = .92);
            Duplicate();
            Undo();
            PrepareCompactCapture();
            RenderCompactCapture(directory, "compact-photo-1480x920", 1480, 920);
            RenderCompactCapture(directory, "compact-photo-1280x720", 1280, 720);
            foreach (var group in dockGroups)
            {
                bool folded = group.Collapsed;
                for (int i = 0; i < group.Tabs.Count; i++)
                {
                    ShowDockTab(group.Tabs[i].Key);
                    PrepareCompactCapture();
                    RenderCompactCapture(directory, $"compact-dock-{group.Tabs[i].Key}", 1480, 920, dockOnly: true);
                }
                group.Select(0);
                if (folded) SetDockGroupCollapsed(group, true);
            }
            // A folded group: the icon strip appears beside the stack.
            SetDockGroupCollapsed(dockGroups.First(g => g.Key == "navigator"), true);
            PrepareCompactCapture();
            RenderCompactCapture(directory, "compact-dock-folded", 1480, 920, dockOnly: true);
            ResetDockLayout();
            TrimPreviewTabs();
            var plan = File.Exists(Path.Combine(directory, "평면 예시.dxf"))
                ? CompatibilityImport.ReadAsync(Path.Combine(directory, "평면 예시.dxf"), new ImportSettings { CadLongEdge = 1400 }.Options(Path.Combine(directory, "평면 예시.dxf"))).GetAwaiter().GetResult().Document
                : SyntheticPlan.Create(1800, 1200);
            AddTab(plan, null); SetWorkspaceMode(true);
            AddArtboard();
            ShowDockTab("artboards");
            PrepareCompactCapture();
            RenderCompactCapture(directory, "compact-drawing-1480x920", 1480, 920);
            RenderCompactCapture(directory, "compact-drawing-1280x720", 1280, 720);
            ShowDockTab("layers");
            SetWorkspaceMode(false);
            TrimPreviewTabs();
        }
        finally { SetScreenStyle(false); }
    }

    // Renders the document and lets the dock panels follow it, as a finished render would.
    void PrepareCompactCapture()
    {
        BuildProperties(); BuildLayers(); RebuildTabs(); UpdateStatus();
        if (HasDocument) { composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); histogram.Update(composite); }
        UpdateDockPanels(); UpdateDockAfterRender();
        if (HasDocument) UpdateDockInfo(new Point(doc.Width * .42, doc.Height * .38));
    }

    internal void RenderCompactCapture(string directory, string name, int width, int height, bool dockOnly = false)
    {
        var content = (FrameworkElement)Content; var size = new Size(width, height);
        if (content is System.Windows.Controls.Panel panel && panel.Background == null) panel.Background = Background;
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        if (HasDocument) { canvas.Fit(); canvas.InvalidateVisual(); content.UpdateLayout(); UpdateDockAfterRender(); content.UpdateLayout(); }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(content); content.UpdateLayout(); image.Render(content);
        BitmapSource output = image;
        if (dockOnly && compactDock is { ActualWidth: > 0 } dock)
        {
            var bounds = dock.TransformToAncestor(content).TransformBounds(new Rect(dock.RenderSize));
            output = new CroppedBitmap(image, new Int32Rect((int)bounds.X, (int)bounds.Y, (int)Math.Min(bounds.Width, width - bounds.X), (int)Math.Min(bounds.Height, height - bounds.Y)));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
