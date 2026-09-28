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
        // Synthetic entries show the recent-documents list without reading the user's history.
        recentDocuments = [@"C:\작업\예시\여름 캠페인 포스터.moruproj", @"C:\작업\예시\제품 사진 보정.png", @"C:\작업\예시\카드뉴스 3장.moruproj"];
        RebuildRecentDocuments(); RenderPreview(Path.Combine(directory, "startup-recent.png"));
        recentDocuments = []; RebuildRecentDocuments();
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
        void CaptureFit(Window window, string name, int width)
        {
            var content = (FrameworkElement)window.Content; window.Content = null;
            var host = new System.Windows.Controls.Border { Background = window.Background, Child = content };
            try
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                int height = (int)Math.Ceiling(host.DesiredSize.Height);
                host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
            }
            finally { host.Child = null; window.Close(); }
        }
        CaptureFit(Dialogs.CreateFields(null, "캔버스 크기 · 좌측 상단 기준", [("너비 (px)", doc.Width.ToString()), ("높이 (px)", doc.Height.ToString())]).Dialog, "dialog-fields", 420);
        CaptureFit(new MessageDialog(null, "먼저 선택 도구로 자를 영역을 지정하세요.", "Morupixel", NoticeKind.Warning), "dialog-warning", 460);
        CaptureFit(new MessageDialog(null, "이 파일은 다른 문서 탭에서 편집 중입니다. 그 탭에서 저장하거나 새 파일 이름을 사용하세요.", "저장하지 못했습니다", NoticeKind.Error), "dialog-error", 460);
        CaptureFit(CreateAutomationSettingsDialog(), "ai-connection", 620);
        Capture(CompatibilityDialog.CleanupPreview(Path.Combine(directory, "평면 예시.dxf")), "import-cad-cleanup", 940, 700);
        RenderCleanupPreviews(directory);
        // Palette states: recent commands first, then a ranked search.
        recentCommands.Clear(); recentCommands.AddRange(["menu:레이어/레이어 복제", "tool:Brush", "menu:보정/레벨…"]);
        CaptureFit(new CommandPalette(null, BuildCommandRegistry(), recentCommands.ToArray()), "command-palette", 600);
        var paletteSearch = new CommandPalette(null, BuildCommandRegistry(), recentCommands.ToArray()); paletteSearch.SetQuery("브러시");
        CaptureFit(paletteSearch, "command-palette-search", 600);
        recentCommands.Clear();
        CaptureFit(new CompatibilityExportDialog(null!, doc), "compat-export", 510);
        Capture(ExportDialog.Create(null, doc), "export", 920, 630);
        Capture(new ColorPickerDialog(null!, foreground), "color-picker", 560, 470);
        Capture(new CmykExportDialog(null!, doc), "cmyk-export", 990, 710);
        Capture(new SelectedLayerExportDialog(null!, doc, [doc.Layers[0].Id]), "layer-export", 960, 720);
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
        // Small laptop and large desktop client areas for responsive layout review.
        foreach (var (width, height) in new[] { (1280, 720), (1366, 768), (1920, 1080) }) Capture(this, $"window-{width}x{height}", width, height);
        // Ribbon layout: the favorites tab and a dense menu tab, then back to the menu bar.
        SetRibbonMode(true); SelectRibbonTab(FavoritesTab); Capture(this, "ribbon", 1480, 920);
        SelectRibbonTab("레이어"); Capture(this, "ribbon-layer-1280x720", 1280, 720); SetRibbonMode(false);
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
        ShowStudioPage(0); CapturePane(studioPanes[0], "photo-actions", 360, 720);
        ShowStudioPage(1); CapturePane(studioPanes[1], "image-properties", 360, 840);
        // A folded section keeps its title; its rows return when it is opened again.
        SectionHeader.SetCollapsedKeys(["위치와 변형"]); CapturePane(studioPanes[1], "image-properties-folded", 360, 640); SectionHeader.SetCollapsedKeys([]);
        ShowStudioPage(2); CapturePane(studioPanes[2], "color-palette", 360, 1180);
        ShowStudioPage(3); SelectBrushTip(BrushTip.Star); CapturePane(studioPanes[3], "brush-settings", 360, 1120);
        SelectBrushTip(BrushTip.Round);
        var text = doc.Layers.First(l => l.Text != null); doc.ActiveId = text.Id; BuildProperties(); ShowStudioPage(1);
        CapturePane(studioPanes[1], "text-properties", 360, 1160);
        var shape = VectorShapes.Create(new ShapeSpec { Width = 360, Height = 150, CornerRadius = 28, FillArgb = 0xD92E4862, StrokeEnabled = true, StrokeArgb = 0xFFC0D9F2, StrokeWidth = 2 }, 100, 100);
        doc.Add(shape); SetWorkspaceMode(true); Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        Capture(this, "design", 1480, 920); Capture(this, "design-1280x720", 1280, 720); CapturePane(studioPanes[1], "shape-properties", 360, 880);
        ShowStudioPage(0); CapturePane(studioPanes[0], "design-actions", 360, 560);
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
