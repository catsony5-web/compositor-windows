using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Compositor.Windows;

// Offscreen review of hatch patterns (--render-studio-previews): the pattern tab after a wand pick,
// a room filled with lawn, the material properties of pattern and image layers, a zoomed view, and
// pattern sheets for tuning the line art.
public sealed partial class MainWindow
{
    void RenderHatchPreviews(string directory, Action<FrameworkElement, string, int, int> capturePane, Action<string, int, int> captureWindow)
    {
        string plan = Path.Combine(directory, "평면 예시.dxf");
        if (!File.Exists(plan)) return;
        Document Import() => CompatibilityImport.ReadAsync(plan, new(CadLongEdge: 900, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: new CadCleanup()))
            .GetAwaiter().GetResult().Document;
        void Pick()
        {
            var picked = PrecisionWand.Select(doc, new Point(doc.Width * .86, doc.Height * .22), wandTolerance, true, true, false, 1);
            selection = picked with { Contour = SelectionContours.Create(picked) };
        }
        void Show()
        {
            Refresh(false); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
            // Folded sections apply themselves on the dispatcher once their rows are added.
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        var previousTab = materialPaletteTab;
        try
        {
            AddTab(Import(), null); SetTool(Tool.MagicWand); Pick();
            materialPaletteTab = MaterialPaletteTab.Patterns; Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "selection-hatch-patterns", 360, 900);
            capturePane(studioPanes[1], "selection-hatch-patterns-narrow", 300, 900);
            ApplySelectionMaterial(HatchPatternRenderer.Create(HatchPattern.GrassSparse), HatchPatterns.Name(HatchPattern.GrassSparse));
            Show(); captureWindow("selection-hatch-applied", 1480, 920);
            // The pattern layer's own properties, with the swap palette opened.
            selection = null; SectionHeader.SetCollapsedKeys([], ["다른 재질로 바꾸기"]); Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "material-properties-pattern", 360, 1200);
            capturePane(studioPanes[1], "material-properties-narrow", 300, 1200);
            SectionHeader.SetCollapsedKeys([], []);
            var hatch = doc.Layers.First(l => l.Kind == LayerKind.Material && l.Material?.Asset.Source.StartsWith("morupixel:preset/", StringComparison.Ordinal) == true);
            materialPaletteTab = MaterialPaletteTab.Images; doc.ActiveId = hatch.Id; selectedLayers.Clear(); selectedLayers.Add(hatch.Id); Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "material-properties-image", 360, 1100);
            // 400%: the lawn is redrawn at screen resolution in the drawing workspace.
            var lawn = doc.Layers.First(l => l.Material is { } f && HatchPatterns.TryGet(f.Asset, out _));
            doc.ActiveId = lawn.Id; selectedLayers.Clear(); selectedLayers.Add(lawn.Id);
            SetWorkspaceMode(true); Show();
            CaptureZoomed("hatch-zoom-400", new Point(lawn.X + lawn.Pixels.Width / 2d, lawn.Y + lawn.Pixels.Height / 2d), 4, directory);
            SetWorkspaceMode(false);
            // The pattern library: two favorites (one built-in, one of the user's) and a pattern made from a scan.
            var mine = AddLinePattern(PreviewLinePattern());
            patternFavorites = [HatchPatterns.Key(HatchPattern.Brick), LinePatterns.FavoriteKey(mine)];
            AddLinePattern(LinePatterns.Create("손그림 점선", PreviewLinePattern(dashes: true).Pixels));
            Pick(); materialPaletteTab = MaterialPaletteTab.Patterns; Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "selection-hatch-library", 360, 1200);
            capturePane(studioPanes[1], "selection-hatch-library-narrow", 300, 1300);
            // The lawn swapped to the user's pattern, with a background color under its lines.
            selection = null; doc.ActiveId = lawn.Id; selectedLayers.Clear(); selectedLayers.Add(lawn.Id); Show();
            SwapLayerMaterial(mine, mine.Name);
            SetPatternBackground(Color.FromRgb(0xF3, 0xE6, 0xC4));
            Show(); ShowStudioPage(1);
            capturePane(studioPanes[1], "material-properties-background", 360, 1000);
            capturePane(studioPanes[1], "material-properties-background-narrow", 300, 1050);
            captureWindow("hatch-custom-background", 1480, 920);
        }
        finally { materialPaletteTab = previousTab; SectionHeader.SetCollapsedKeys([], []); patternFavorites = []; linePatternLibrary = null; }
        SavePng(PatternLibrarySheet(), Path.Combine(directory, "hatch-pattern-library.png"));
        SavePng(PatternRatioSheet(), Path.Combine(directory, "hatch-ratio-lineweight.png"));
        SavePng(PatternSitePlan(), Path.Combine(directory, "hatch-site-plan.png"));
    }

    // The main window at a fixed zoom around a document point, once the drawing view has rendered.
    void CaptureZoomed(string name, Point center, double zoom, string directory)
    {
        var content = (FrameworkElement)Content; var size = new Size(1480, 920);
        if (content is System.Windows.Controls.Panel panel && panel.Background == null) panel.Background = Background;
        studioScroll.Height = PreferredStudioHeight(size.Height);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        canvas.Zoom = zoom; canvas.Pan = new Vector((doc.Width / 2d - center.X) * zoom, (doc.Height / 2d - center.Y) * zoom);
        canvas.InvalidateVisual(); content.UpdateLayout();
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { canvas.InvalidateVisual(); content.UpdateLayout(); if (canvas.IsDesignPreviewReady || canvas.DesignPreviewError != null || clock.ElapsedMilliseconds > 30000) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        canvas.InvalidateVisual(); content.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(content); image.Render(content);
        SavePng(image, Path.Combine(directory, name + ".png"));
    }

    static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }

    // A scanned-looking hatch tile (warm paper with grain, hand-drawn waves or dashes) and the line
    // pattern made from it with the automatic threshold, for the offscreen review.
    internal static Raster PreviewLinePatternScan(bool dashes = false) => Imaging.Draw(180, 180, dc =>
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xDF)), null, new Rect(0, 0, 180, 180));
        var random = new Random(dashes ? 11 : 5);
        for (int i = 0; i < 900; i++)
        {
            byte tone = (byte)random.Next(0xD8, 0xEA);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(tone, tone, (byte)(tone - 10))), null, new Point(random.NextDouble() * 180, random.NextDouble() * 180), .6, .6);
        }
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)), dashes ? 2.4 : 2.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashes) pen.DashStyle = new DashStyle([3, 3.2], 0);
        for (int row = 0; row < 4; row++)
            foreach (double shift in new[] { -180d, 0, 180 })
            {
                var wave = new StreamGeometry();
                using (var g = wave.Open())
                {
                    double y = 22 + row * 45;
                    g.BeginFigure(new Point(shift, y), false, false);
                    for (int s = 1; s <= 36; s++) g.LineTo(new Point(shift + s * 5, y + Math.Sin(s * 5 / 180d * Math.PI * 4) * (dashes ? 4 : 9) + (random.NextDouble() - .5) * 1.2), true, true);
                }
                dc.DrawGeometry(null, pen, wave);
            }
    });

    internal static MaterialAsset PreviewLinePattern(bool dashes = false)
    {
        var source = LinePatternSource.From(PreviewLinePatternScan(dashes));
        return LinePatterns.Create("손그림 물결", source.Convert(source.AutoThreshold));
    }

    static MaterialFill PreviewFill(MaterialAsset asset, double width, double height, double tileWidth, double tileHeight) =>
        new(asset, Guid.NewGuid(), "Preview", new RegionPath($"M0,0 L{width},0 L{width},{height} L0,{height} Z"), (int)Math.Ceiling(width), (int)Math.Ceiling(height), tileWidth, tileHeight);

    static BitmapSource Sheet(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height)); draw(dc); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    static void SheetLabel(DrawingContext dc, string text, Point at, double size = 11) =>
        dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, Brushes.Black, 1), at);

    // Each pattern at the plan's default repeat (64 px) at 50%, 100% and 400%.
    static BitmapSource PatternLibrarySheet()
    {
        const int cell = 150, pad = 12, label = 18; const double tile = 64;
        var patterns = HatchPatterns.All;
        int columns = 2, rows = (patterns.Count + columns - 1) / columns, block = 3 * (cell + pad);
        return Sheet(pad + columns * (block + pad), pad + rows * (cell + label + pad), dc =>
        {
            for (int index = 0; index < patterns.Count; index++)
            {
                var p = patterns[index]; double x0 = pad + index % columns * (block + pad), y = pad + index / columns * (cell + label + pad) + label;
                SheetLabel(dc, $"{Loc.T(HatchPatterns.Name(p))} · {HatchPatterns.Key(p)}", new Point(x0, y - label + 2));
                double[] zooms = [.5, 1, 4];
                for (int c = 0; c < 3; c++)
                {
                    double z = zooms[c], area = cell / z, x = x0 + c * (cell + pad);
                    double repeat = tile * HatchPatterns.RepeatScale(p);
                    var drawing = MaterialRenderer.Drawing(PreviewFill(HatchPatternRenderer.Create(p), area, area, repeat, repeat), z);
                    dc.PushClip(new RectangleGeometry(new Rect(x, y, cell, cell))); dc.PushTransform(new MatrixTransform(z, 0, 0, z, x, y)); dc.DrawDrawing(drawing); dc.Pop(); dc.Pop();
                    dc.DrawRectangle(null, new Pen(Brushes.Silver, 1), new Rect(x + .5, y + .5, cell - 1, cell - 1));
                    dc.DrawRectangle(Brushes.White, null, new Rect(x + cell - 36, y + cell - 17, 35, 16)); SheetLabel(dc, $"{z * 100:0}%", new Point(x + cell - 33, y + cell - 16), 10);
                }
            }
        });
    }

    // Vertical ratio and line weight at 50/100/200% for dots, lines and flagstones.
    static BitmapSource PatternRatioSheet()
    {
        const int cell = 150, pad = 12, label = 18; const double tile = 48;
        HatchPattern[] patterns = [HatchPattern.Dots, HatchPattern.Lines, HatchPattern.Flagstone];
        double[] values = [.5, 1, 2];
        return Sheet(pad + 6 * (cell + pad) + pad, pad + patterns.Length * (cell + label + pad), dc =>
        {
            for (int r = 0; r < patterns.Length; r++)
                for (int c = 0; c < 6; c++)
                {
                    bool ratio = c < 3; double v = values[c % 3];
                    double repeat = tile * HatchPatterns.RepeatScale(patterns[r]);
                    var fill = PreviewFill(HatchPatternRenderer.Create(patterns[r]), cell, cell, repeat, ratio ? repeat * v : repeat) with { LineWeight = ratio ? 1 : v };
                    double x = pad + c * (cell + pad) + (ratio ? 0 : pad), y = pad + r * (cell + label + pad) + label;
                    SheetLabel(dc, $"{Loc.T(HatchPatterns.Name(patterns[r]))} · {Loc.T(ratio ? "세로 비율 %" : "선 굵기 %")} {v * 100:0}", new Point(x, y - label + 2), 10);
                    dc.PushTransform(new TranslateTransform(x, y)); dc.DrawDrawing(MaterialRenderer.Drawing(fill, 1)); dc.Pop();
                    dc.DrawRectangle(null, new Pen(Brushes.Silver, 1), new Rect(x + .5, y + .5, cell - 1, cell - 1));
                }
        });
    }

    // A small site plan in the style of a presentation board: curved beds of lawn, meadow, dashes,
    // pavers and stones under black linework, for comparing the patterns in context.
    static BitmapSource PatternSitePlan()
    {
        const int width = 1100, height = 760; const double tile = 72;
        Geometry Path(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
        var regions = new (HatchPattern Pattern, Geometry Area)[]
        {
            (HatchPattern.Meadow, Path("M0,0 L420,0 L300,330 L0,420 Z")),
            (HatchPattern.DashHorizontal, Path("M420,0 L760,0 C700,160 820,300 700,420 C620,520 520,560 400,640 L300,330 Z")),
            (HatchPattern.GrassSparse, Path("M0,420 L300,330 L400,640 L220,760 L0,760 Z")),
            (HatchPattern.Cobble, Path("M760,0 L860,0 C820,140 930,260 860,400 C800,520 700,600 620,760 L520,760 C600,600 700,520 760,400 C820,280 700,160 760,0 Z")),
            (HatchPattern.Flagstone, Path("M860,0 L1100,0 L1100,760 L620,760 C700,600 800,520 860,400 C930,260 820,140 860,0 Z")),
            (HatchPattern.Sand, Path("M220,760 L400,640 C520,560 620,520 520,760 Z")),
            (HatchPattern.PavingSmall, Path("M430,380 L600,300 L660,420 L490,500 Z")),
            (HatchPattern.Lines, Path("M300,180 L420,140 L450,230 L330,270 Z")),
        };
        return Sheet(width, height, dc =>
        {
            foreach (var (pattern, area) in regions)
            {
                var fill = new MaterialFill(HatchPatternRenderer.Create(pattern), Guid.NewGuid(), "Site", new RegionPath(PathGeometry.CreateFromGeometry(area).ToString(System.Globalization.CultureInfo.InvariantCulture)), width, height, tile * HatchPatterns.RepeatScale(pattern), tile * HatchPatterns.RepeatScale(pattern));
                dc.DrawDrawing(MaterialRenderer.Drawing(fill, 1));
            }
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 1.2); outline.Freeze();
            foreach (var (_, area) in regions) dc.DrawGeometry(null, outline, area);
            var wall = new Pen(Brushes.Black, 5); wall.Freeze();
            dc.DrawGeometry(null, wall, Path("M470,330 L600,270 L680,440 L550,500 Z"));
        });
    }
}
