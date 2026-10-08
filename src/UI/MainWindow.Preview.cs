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
            // Dialogs are captured on their window background so the root margin (inner padding) shows.
            if (!ReferenceEquals(window, this)) content = OffscreenPreview.Host(window);
            else if (content is System.Windows.Controls.Panel panel && panel.Background == null) panel.Background = window.Background;
            if (ReferenceEquals(window, this)) studioScroll.Height = PreferredStudioHeight(height);
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            // Fit raises canvas.ZoomChanged, which refreshes the zoom readout for this size. (UpdateStatus
            // is not called: it would replace the status line some captures show.)
            if (ReferenceEquals(window, this)) { canvas.Fit(); canvas.InvalidateVisual(); content.UpdateLayout(); }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(content); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        Capture(new NewDocumentDialog(null), "new-document", 1020, 760);
        void CaptureFit(Window window, string name, int width)
        {
            var content = (FrameworkElement)window.Content; window.Content = null;
            var host = new System.Windows.Controls.Border { Background = window.Background, Child = content };
            try
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                // Translate before sizing so a longer language gets the height it needs.
                Loc.PrepareOffscreen(host); host.Measure(new Size(width, double.PositiveInfinity));
                int height = (int)Math.Ceiling(host.DesiredSize.Height);
                host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(host); image.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
            }
            finally { host.Child = null; window.Close(); }
        }
        CaptureFit(Dialogs.CreateFields(null, "캔버스 크기 · 좌측 상단 기준", [("너비 (px)", doc.Width.ToString()), ("높이 (px)", doc.Height.ToString())]).Dialog, "dialog-fields", 420);
        CaptureFit(new MessageDialog(null, "먼저 선택 도구로 자를 영역을 지정하세요.", "Morupixel", NoticeKind.Warning), "dialog-warning", 460);
        CaptureFit(new LinePatternDialog(null, LinePatternSource.From(PreviewLinePatternScan()), "손그림 물결"), "line-pattern-dialog", 460);
        CaptureFit(new MessageDialog(null, "이 파일은 다른 문서 탭에서 편집 중입니다. 그 탭에서 저장하거나 새 파일 이름을 사용하세요.", "저장하지 못했습니다", NoticeKind.Error), "dialog-error", 460);
        CaptureFit(CreateAutomationSettingsDialog(), "ai-connection", 620);
        Capture(CompatibilityDialog.CleanupPreview(Path.Combine(directory, "평면 예시.dxf")), "import-cad-cleanup", 940, 700);
        Capture(CompatibilityDialog.CleanupPreview(Path.Combine(directory, "평면 예시.dxf"), HatchTreatment.Pattern), "import-cad-cleanup-patterns", 940, 700);
        Capture(CompatibilityDialog.BatchPreview(Path.Combine(directory, "2층 평면도.dwg")), "import-cad-batch", 940, 700);
        Capture(CompatibilityDialog.BatchPreview(Path.Combine(directory, "2층 평면도.dwg"), quick: true), "import-cad-quick", 940, 700);
        Capture(CompatibilityDialog.BatchPreview(Path.Combine(directory, "2층 평면도.dwg"), failed: true), "import-cad-skip", 940, 700);
        RenderCleanupPreviews(directory);
        // Palette states: recent commands first, then a ranked search.
        recentCommands.Clear(); recentCommands.AddRange(["menu:레이어/레이어 복제", "tool:Brush", "menu:보정/레벨…"]);
        CaptureFit(new CommandPalette(null, BuildCommandRegistry(), recentCommands.ToArray()), "command-palette", 600);
        var paletteSearch = new CommandPalette(null, BuildCommandRegistry(), recentCommands.ToArray()); paletteSearch.SetQuery("브러시");
        CaptureFit(paletteSearch, "command-palette-search", 600);
        recentCommands.Clear();
        CaptureFit(new CompatibilityExportDialog(null, doc), "compat-export", 580);
        CaptureFit(new CompatibilityExportDialog(null, doc, CompatibilityExportFormat.PsdLayers), "compat-export-psd", 580);
        CaptureFit(new CompatibilityExportDialog(null, doc, CompatibilityExportFormat.AiLayers), "compat-export-ai", 580);
        var export = ExportDialog.Create(null, doc); ExportDialog.WaitForPreview(export, TimeSpan.FromSeconds(20)); Capture(export, "export", 1040, 680);
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
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(saveHost); image.Render(saveHost);
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
        var developAfter = Imaging.Render(AdjustmentDialog.PreviewDocument(doc, null, developSpec, true, null));
        develop.SetDesignPreview(developAfter);
        Capture(develop, "photo-develop", 1040, 760);
        // Before/after views: split bar, side by side, and one image switched to "before" (also at the minimum width).
        var developBefore = Imaging.Render(AdjustmentDialog.PreviewDocument(doc, null, developSpec, false, null));
        foreach (var (mode, name, width, height) in new[] { (CompareMode.Split, "photo-develop-split", 1040, 760), (CompareMode.SideBySide, "photo-develop-side-by-side", 1040, 760),
            (CompareMode.Toggle, "photo-develop-toggle", 1040, 760), (CompareMode.Toggle, "photo-develop-toggle-860", 860, 600) })
        {
            var compare = new AdjustmentDialog(null, doc, developSpec);
            compare.SetDesignPreview(developAfter); compare.SetDesignBefore(developBefore); compare.SelectCompareMode(mode);
            if (mode == CompareMode.Toggle) compare.ToggleCompareState();
            Capture(compare, name, width, height); compare.Close();
        }
        // A "before" render that failed: the empty cell and the info line say so and offer the retry.
        var failedBefore = new AdjustmentDialog(null, doc, developSpec);
        failedBefore.SetDesignPreview(developAfter); failedBefore.SelectCompareMode(CompareMode.SideBySide);
        failedBefore.SetDesignBeforeFailure(new OutOfMemoryException().Message);
        Capture(failedBefore, "photo-develop-before-failed", 1040, 760); failedBefore.Close();
        ShowStudioPage(2); Capture(this, "colors", 1480, 920);
        ShowStudioPage(3); Capture(this, "brush", 1480, 920);
        ShowStudioPage(0); Capture(this, "compact", 1200, 750);
        // Small laptop and large desktop client areas for responsive layout review.
        foreach (var (width, height) in new[] { (1280, 720), (1366, 768), (1920, 1080) }) Capture(this, $"window-{width}x{height}", width, height);
        // Ribbon layout: the favorites tab and a dense menu tab, then back to the menu bar.
        SetRibbonMode(true); SelectRibbonTab(FavoritesTab); Capture(this, "ribbon", 1480, 920);
        SelectRibbonTab("레이어"); Capture(this, "ribbon-layer-1280x720", 1280, 720);
        SelectRibbonTab("파일"); Capture(this, "ribbon-file-1480x920", 1480, 920); SetRibbonMode(false);
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
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(host); image.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
            }
            finally { host.Child = null; ShowStudioPage(studioPage, false); }
        }
        ShowStudioPage(0); CapturePane(studioPanes[0], "photo-actions", 360, 800);
        ShowStudioPage(1); CapturePane(studioPanes[1], "image-properties", 360, 840);
        // A folded section keeps its title; its rows return when it is opened again.
        SectionHeader.SetCollapsedKeys(["위치와 변형"]); CapturePane(studioPanes[1], "image-properties-folded", 360, 640); SectionHeader.SetCollapsedKeys([]);
        ShowStudioPage(2); CapturePane(studioPanes[2], "color-palette", 360, 1180);
        ShowStudioPage(3); SelectBrushTip(BrushTip.Star); CapturePane(studioPanes[3], "brush-settings", 360, 1120);
        SelectBrushTip(BrushTip.Round);
        // 내 프리셋 with saved brushes kept in memory (headless), the second one matching the current brush, and the save dialog.
        void PreviewPreset(BrushTip tip, double size, double edge, double flow, double spacing, double angle, string name)
        {
            brushTip = tip; brushSize = size; hardness = edge; brushOpacity = flow; brushSpacing = spacing; brushAngle = angle; SyncBrushControls(); AddBrushPreset(name);
        }
        string statusBeforePresets = status.Text;
        PreviewPreset(BrushTip.Round, 6, 1, 1, .05, 0, Loc.T("도면 잉크 펜"));
        PreviewPreset(BrushTip.Star, 64, .8, .7, 1.2, 15, Loc.T("별 스탬프"));
        PreviewPreset(BrushTip.Round, 220, .1, .35, .1, 0, Loc.T("투시도 하늘 그라데이션용 부드럽고 큰 에어브러시"));
        PreviewPreset(BrushTip.Diamond, 30, .6, 1, .3, 45, Loc.Format("내 브러시 {0}", 1));
        ApplyBrushPreset(brushPresets[1]); ShowStudioPage(3);
        CapturePane(studioPanes[3], "brush-presets", 360, 1560);
        CaptureFit(new BrushPresetDialog(null, brushTip, CurrentBrushSettings(), NextBrushPresetName()), "brush-preset-dialog", 440);
        brushPresets.Clear(); RebuildBrushPresetList();
        brushTip = BrushTip.Round; brushSize = 42; hardness = .8; brushOpacity = 1; brushSpacing = .1; brushAngle = 0; SyncBrushControls(); status.Text = statusBeforePresets;
        var text = doc.Layers.First(l => l.Text != null); doc.ActiveId = text.Id; BuildProperties(); ShowStudioPage(1);
        CapturePane(studioPanes[1], "text-properties", 360, 1160);
        var shape = VectorShapes.Create(new ShapeSpec { Width = 360, Height = 150, CornerRadius = 28, FillArgb = 0xD92E4862, StrokeEnabled = true, StrokeArgb = 0xFFC0D9F2, StrokeWidth = 2 }, 100, 100);
        shape.Name = Loc.T(shape.Name);
        doc.Add(shape); SetWorkspaceMode(true); Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        Capture(this, "design", 1480, 920); Capture(this, "design-1280x720", 1280, 720); CapturePane(studioPanes[1], "shape-properties", 360, 880);
        ShowStudioPage(0); CapturePane(studioPanes[0], "design-actions", 360, 560);
        TrimPreviewTabs();
        RenderSketchPreviews(Capture);
        TrimPreviewTabs();
        RenderSelectionMaterialPreviews(directory, CapturePane, (name, width, height) => Capture(this, name, width, height));
        TrimPreviewTabs();
        RenderHatchPreviews(directory, CapturePane, (name, width, height) => Capture(this, name, width, height));
        TrimPreviewTabs();
        RenderScreentonePreviews(directory, CapturePane, (name, width, height) => Capture(this, name, width, height));
        TrimPreviewTabs();
        RenderProfilePreviews(directory);
        TrimPreviewTabs();
        RenderTextPosterPreviews(directory);
        TrimPreviewTabs();
        RenderShadowPreviews(Capture, CapturePane);
        // A cleaned-up plan: its hatch materials are listed and edited on the photo layer tab.
        string plan = Path.Combine(directory, "평면 예시.dxf");
        var drawing = CompatibilityImport.ReadAsync(plan, new ImportSettings { CadLongEdge = 900 }.Options(plan)).GetAwaiter().GetResult().Document;
        TrimPreviewTabs();
        AddTab(drawing, null); var hatch = doc.Layers.First(l => l.Kind == LayerKind.Material);
        doc.ActiveId = hatch.Id; selectedLayers.Clear(); selectedLayers.Add(hatch.Id); ShowStudioPage(1);
        Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        Capture(this, "drawing-photo-layers", 1480, 920);
        TrimPreviewTabs();
        RenderStyleEffectPreviews(directory, Capture);
        TrimPreviewTabs();
        RenderDesignStylePreviews(directory);
        TrimPreviewTabs();
        RenderEntouragePreviews(directory);
        TrimPreviewTabs();
        RenderCompactPreviews(directory);
    }
    // Each review group opens its own sample documents; drop every tab but the active one between
    // groups so the whole review stays under the 8-document limit (no save prompts: review only).
    void TrimPreviewTabs()
    {
        if (tabs.Count <= 1) return;
        StoreTab(); var keep = tabs[activeTab];
        tabs.RemoveAll(tab => !ReferenceEquals(tab, keep)); activeTab = 0; RebuildTabs();
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
            UpdateStatus(); // the zoom readout must reflect the fit, not the unlaid-out canvas
        }
        else { composite = null; canvas.Composite = null; }
        canvas.InvalidateVisual();
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(content); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); using var output = File.Create(full); encoder.Save(output);
    }
}
