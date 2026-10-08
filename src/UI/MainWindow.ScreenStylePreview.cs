using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Offscreen review of 간결한 화면 (--render-studio-previews, or --render-compact-previews alone):
// the whole window at 1480×920 and 1280×720 with a photo and with a drawing, the start screen,
// the dock with every group's tabs, and rearranging it (a tab drag's markers, a flyout, a
// rearranged dock, a floating group). The screen returns to 친절한 화면 afterwards.
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
            RenderDockArrangeCaptures(directory);
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
            // The ribbon over the compact chrome.
            SetRibbonMode(true); SelectRibbonTab("레이어");
            PrepareCompactCapture();
            RenderCompactCapture(directory, "compact-ribbon-1480x920", 1480, 920);
            SetRibbonMode(false);
            SetWorkspaceMode(false);
            // Back on the friendly screen, which must look as it did before the switch.
            SetScreenStyle(false);
            PrepareCompactCapture();
            RenderCompactCapture(directory, "friendly-after-compact-1480x920", 1480, 920);
            TrimPreviewTabs();
        }
        finally { SetScreenStyle(false); }
    }

    // Rearranging the dock: a tab dragged onto another group's tab row (insertion marker) and onto
    // the edge between groups, the folded navigator group as a flyout, a rearranged dock and a
    // floating group's window content. Points come from the laid-out dock, as a pointer's would.
    void RenderDockArrangeCaptures(string directory)
    {
        const int width = 1480, height = 920;
        void Lay() { var content = (FrameworkElement)Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); }
        DockGroup Group(string tab) => DockGroupOf(tab)!;
        Point At(FrameworkElement element, double x, double y) => element.TranslatePoint(new Point(x, y), dockStack!);
        // The navigator group is folded here: open it beside the strip.
        PrepareCompactCapture(); Lay();
        var folded = Group("navigator");
        OpenDockFlyout(folded, 0, dockStrip?.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == $"{folded.Title(0)} 패널 열기"));
        PrepareCompactCapture();
        RenderCompactCapture(directory, "compact-dock-flyout", width, height, dockOnly: true);
        CloseDockFlyout(focusAnchor: false);
        ResetDockLayout();
        PrepareCompactCapture(); Lay();
        var color = Group("color"); var layers = Group("layers"); var properties = Group("properties");
        BeginDockDrag(color, color.Tabs.First(t => t.Key == "swatches"));
        var tab = layers.TabButtons[1];
        DockDragOver(At(tab, tab.ActualWidth * .3, tab.ActualHeight * .6));
        RenderCompactCapture(directory, "compact-dock-drag-tab", width, height, dockOnly: true);
        DockDragOver(At(properties, properties.ActualWidth * .55, properties.ActualHeight - 8));
        RenderCompactCapture(directory, "compact-dock-drag-split", width, height, dockOnly: true);
        CancelDockDrag();
        // 기록 beside the navigator, 히스토그램 on its own, 레이어 on top and the color group folded.
        MoveDockTab("history", Group("navigator"), 1);
        SplitDockTab("histogram", Group("navigator"));
        MoveDockGroup(Group("layers"), ShownDockGroups.First());
        SetDockGroupCollapsed(Group("color"), true);
        PrepareCompactCapture();
        RenderCompactCapture(directory, "compact-dock-rearranged", width, height, dockOnly: true);
        ResetDockLayout();
        FloatDockGroup(Group("color"));
        PrepareCompactCapture();
        RenderDockWindowCapture(directory, "compact-dock-floating", Group("color"));
        ResetDockLayout();
        PrepareCompactCapture();
    }

    // A floating group's window content at its size (the window itself is never shown offscreen).
    void RenderDockWindowCapture(string directory, string name, DockGroup group)
    {
        if (group.FloatingWindow is not { Content: FrameworkElement host } window) return;
        var size = new Size(Math.Round(window.Width), Math.Round(window.Height));
        host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
        Loc.PrepareOffscreen(host); host.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
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
            // An open flyout sits beside the dock; the crop takes it in.
            if (dockFlyout is { Visibility: Visibility.Visible, ActualWidth: > 0 } flyout) bounds.Union(flyout.TransformToAncestor(content).TransformBounds(new Rect(flyout.RenderSize)));
            bounds.Intersect(new Rect(0, 0, width, height));
            output = new CroppedBitmap(image, new Int32Rect((int)bounds.X, (int)bounds.Y, (int)Math.Min(bounds.Width, width - bounds.X), (int)Math.Min(bounds.Height, height - bounds.Y)));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
