using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// Offscreen review of the 점경 library (--render-studio-previews, or --render-entourage-previews
// alone): contact sheets of every built-in item and variant, a section and a site plan dressed with
// entourage (also as a dark section), the library panel in 친절한 화면 and 간결한 화면, the properties
// of a placed item, 내 점경 and its import dialog.
public sealed partial class MainWindow
{
    public void RenderEntouragePreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        headlessTesting = true;
        SaveEntourageSheet(Path.Combine(directory, "entourage-sheet-elevation.png"), EntourageView.Elevation, EntourageFill.White, 0xFF262626, 0xFFFFFFFF);
        SaveEntourageSheet(Path.Combine(directory, "entourage-sheet-plan.png"), EntourageView.Plan, EntourageFill.None, 0xFF262626, 0xFFFFFFFF);
        SaveEntourageSheet(Path.Combine(directory, "entourage-sheet-styles.png"), EntourageView.Elevation, EntourageFill.Gray, 0xFF2B4A6F, 0xFFF4F1EA, styles: true);
        void Save(Raster image, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image.Bitmap()));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        Save(DesignRenderer.RenderOutput(EntourageSectionDocument(false, out _)), "entourage-section");
        Save(DesignRenderer.RenderOutput(EntourageSectionDocument(true, out _)), "entourage-section-dark");
        Save(DesignRenderer.RenderOutput(EntouragePlanDocument(out _)), "entourage-plan");

        // The editor: the section with a person selected (properties), and the 디자인 tab's palette.
        var section = EntourageSectionDocument(false, out var person);
        AddTab(section, null); SetWorkspaceMode(true);
        doc.ActiveId = person; selectedLayers.Clear(); selectedLayers.Add(person);
        ShowStudioPage(1); PrepareCompactCapture();
        RenderCompactCapture(directory, "entourage-editor-section", 1480, 920);
        RenderPane(studioPanes[1], Path.Combine(directory, "entourage-properties.png"), 360, 1180);
        entourageTab = 0; entourageView = EntourageView.Elevation; RefreshEntouragePalettes();
        ShowStudioPage(0); PrepareCompactCapture();
        RevealEntourageSection(); RenderCompactCapture(directory, "entourage-editor-palette", 1480, 920);
        RenderPane(studioPanes[0], Path.Combine(directory, "entourage-design-tab.png"), 360, 1500);
        TrimPreviewTabs();
        var plan = EntouragePlanDocument(out var tree);
        AddTab(plan, null); SetWorkspaceMode(true);
        entourageTab = 1; entourageView = EntourageView.Plan; RefreshEntouragePalettes();
        doc.ActiveId = tree; selectedLayers.Clear(); selectedLayers.Add(tree);
        ShowStudioPage(0); PrepareCompactCapture();
        RevealEntourageSection(); RenderCompactCapture(directory, "entourage-editor-plan", 1480, 920);
        ShowStudioPage(1); PrepareCompactCapture(); RenderPane(studioPanes[1], Path.Combine(directory, "entourage-properties-plan.png"), 360, 1100);
        // 내 점경: an item made from a line drawing (kept in memory) and the import dialog.
        var drawing = EntourageSampleDrawing();
        var dialog = new EntourageImportDialog(null, drawing, Loc.T("손그림 나무"), EntourageCategory.Plants, EntourageView.Elevation) { Synchronous = true };
        dialog.SetMode(EntourageImportMode.LineDrawing); dialog.SetMeters(7);
        CaptureEntourageDialog(dialog, Path.Combine(directory, "entourage-import-dialog.png"), 480);
        var dialog2 = new EntourageImportDialog(null, drawing, Loc.T("손그림 나무"), EntourageCategory.Plants, EntourageView.Elevation) { Synchronous = true };
        dialog2.SetMode(EntourageImportMode.LineDrawing); dialog2.SetMeters(7);
        if (dialog2.Accept() && dialog2.Result is { } custom) { AddCustomEntourage(custom); PlaceEntourage(new EntourageChoice(null, custom), new Point(doc.Width * .78, doc.Height * .3)); }
        dialog2.Close();
        entourageTab = EntourageCustomTab; entourageView = EntourageView.Elevation; RefreshEntouragePalettes();
        ShowStudioPage(0); PrepareCompactCapture(); RevealEntourageSection();
        RenderPane(studioPanes[0], Path.Combine(directory, "entourage-custom-tab.png"), 360, 1500);
        // 간결한 화면: the dock tab next to the patterns.
        try
        {
            SetScreenStyle(true);
            TrimPreviewTabs(); AddTab(EntourageSectionDocument(false, out _), null); SetWorkspaceMode(true);
            entourageTab = 0; entourageView = EntourageView.Elevation; RefreshEntouragePalettes();
            ShowDockTab("entourage"); PrepareCompactCapture();
            RenderCompactCapture(directory, "entourage-compact-1480x920", 1480, 920);
            RenderCompactCapture(directory, "entourage-compact-dock", 1480, 920, dockOnly: true);
            entourageTab = 1; RefreshEntouragePalettes(); PrepareCompactCapture();
            RenderCompactCapture(directory, "entourage-compact-1280x720", 1280, 720);
        }
        finally { SetScreenStyle(false); }
        TrimPreviewTabs();
    }

    // Scrolls the 디자인 tab so the 점경 section heads the visible part.
    void RevealEntourageSection()
    {
        ((FrameworkElement)Content).Measure(new Size(1480, 920)); ((FrameworkElement)Content).Arrange(new Rect(0, 0, 1480, 920)); ((FrameworkElement)Content).UpdateLayout();
        var header = studioContents[0].Children.OfType<SectionHeader>().FirstOrDefault(h => h.Key == "점경");
        if (header == null) return;
        if (header.Folded) header.IsChecked = false;
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(studioContents[0]); node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.ScrollViewer scroll)
            {
                var offset = header.TransformToAncestor(scroll).Transform(new Point());
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + offset.Y - 4); scroll.UpdateLayout(); break;
            }
    }

    static void CaptureEntourageDialog(Window dialog, string path, int width)
    {
        var content = (FrameworkElement)dialog.Content; dialog.Content = null;
        var host = new System.Windows.Controls.Border { Background = dialog.Background, Child = content };
        try
        {
            Loc.PrepareOffscreen(host); host.Measure(new Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(host.DesiredSize.Height);
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); Loc.PrepareOffscreen(host); image.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(path); encoder.Save(output);
        }
        finally { host.Child = null; dialog.Close(); }
    }

    // An original pen drawing of a small tree on paper, for the 내 점경 review.
    internal static Raster EntourageSampleDrawing()
    {
        var ink = new Pen(new SolidColorBrush(Color.FromRgb(30, 30, 30)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; ink.Freeze();
        return Imaging.Draw(360, 480, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 246, 240)), null, new Rect(0, 0, 360, 480));
            dc.DrawGeometry(null, ink, Geometry.Parse("M172 460 C176 400 170 330 180 260 M180 300 C150 270 120 250 100 210 M180 290 C210 260 240 240 260 200 M176 330 C160 300 140 300 130 280"));
            dc.DrawGeometry(null, ink, Geometry.Parse("M90 230 C40 220 30 160 70 140 C60 90 120 60 150 90 C170 40 240 50 240 100 C290 90 320 150 290 180 C320 220 280 260 250 245 C230 280 170 270 160 250 C130 270 90 260 90 230 Z"));
            dc.DrawGeometry(null, new Pen(ink.Brush, 2), Geometry.Parse("M110 180 C120 170 135 172 140 182 M200 130 C210 120 228 122 232 134 M240 200 C248 190 262 192 266 202 M150 150 C158 142 170 144 172 152"));
            dc.DrawGeometry(null, new Pen(ink.Brush, 2), Geometry.Parse("M120 462 L240 462"));
        });
    }

    // A two-storey building section on a street, dressed with people, trees, a car, a bicycle, street
    // furniture and ground shadows. Dark: white lines on black like a night section board.
    internal static Document EntourageSectionDocument(bool dark, out Guid selected)
    {
        const double ppm = 60; const int width = 2600, height = 1300; double ground = 1080;
        var line = dark ? Color.FromRgb(236, 236, 236) : Color.FromRgb(28, 28, 28);
        var document = new Document { Width = width, Height = height, Name = Loc.T("점경 예시 · 단면") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(width, height, dark ? Color.FromRgb(10, 10, 12) : Colors.White) });
        double M(double meters) => meters * ppm;
        double left = 720, right = left + M(21);
        var parts = new List<VectorPrimitive>();
        void Fill(Rect r) => parts.Add(new VectorPrimitive(new RectangleGeometry(r), line, true, 0));
        void Stroke(Geometry g, double w) => parts.Add(new VectorPrimitive(g, line, false, w));
        // Ground with earth below, slabs and walls cut (poché), glazing, roof parapet.
        Stroke(new LineGeometry(new Point(0, ground), new Point(width, ground)), 3);
        for (double x = 20; x < width; x += 46) Stroke(new LineGeometry(new Point(x, ground + 14), new Point(x + 26, ground + 40)), 1);
        double[] levels = [ground, ground - M(4.2), ground - M(8.4)];
        foreach (double y in levels) Fill(new Rect(left, y, right - left, M(.35)));
        Fill(new Rect(left - M(.3), levels[2] - M(1.1), M(.3), M(1.1)));
        Fill(new Rect(right, levels[2] - M(1.1), M(.3), M(1.1)));
        foreach (double x in new[] { left - M(.3), right })
            for (int floor = 0; floor < 2; floor++)
            {
                double top = levels[floor + 1] + M(.35), bottom = levels[floor];
                Fill(new Rect(x, top, M(.3), M(.9))); Fill(new Rect(x, bottom - M(.9), M(.3), M(.9)));
                Stroke(new LineGeometry(new Point(x + M(.15), top + M(.9)), new Point(x + M(.15), bottom - M(.9))), 1.2);
            }
        for (double x = left + M(7); x < right - M(1); x += M(7))
            for (int floor = 0; floor < 2; floor++) Stroke(new RectangleGeometry(new Rect(x - M(.2), levels[floor + 1] + M(.35), M(.4), levels[floor] - levels[floor + 1] - M(.35))), 1.4);
        // Beyond the cut: the back wall's windows in thin lines.
        for (double x = left + M(1.5); x < right - M(2); x += M(3.5))
            for (int floor = 0; floor < 2; floor++) Stroke(new RectangleGeometry(new Rect(x, levels[floor + 1] + M(1.3), M(2.2), M(1.9))), .7);
        var lines = VectorContent.FromPaths(width, height, parts);
        document.Add(new Layer { Name = Loc.T("단면선"), Kind = LayerKind.Vector, Vector = lines, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(lines.Drawing)) });
        uint ink = VectorShapes.Argb(line);
        Layer Put(string id, double x, double y, double? meters = null, EntourageFill? fill = null, bool flip = false, int variant = 0)
        {
            var item = EntourageLibrary.Find(id)!;
            var spec = EntourageRenderer.Spec(item, ppm, variant) with { LineArgb = ink, Meters = meters ?? item.DefaultMeters };
            spec = spec with { Fill = fill ?? (dark ? item.Category == EntourageCategory.People ? EntourageFill.Solid : EntourageFill.None : spec.Fill) };
            var layer = EntourageRenderer.Create(spec, new Point(x, y), Loc.T(item.Name), flip);
            document.Add(layer); return layer;
        }
        var shadowed = new List<Guid>();
        // Street side.
        Put("tree.round", 230, ground, variant: 1);
        Put("lamp.street", 80, ground);
        Put("car.sedan", 470, ground);
        Put("person.cyclist", 640, ground, variant: 1, flip: true);
        // Ground floor.
        var walking = Put("person.walking", 860, ground);
        Put("person.walking-bag", 1060, ground, flip: true, variant: 1);
        Put("person.child", 1140, ground, flip: true);
        Put("person.standing", 1330, ground, variant: 2);
        Put("bench.side", 1560, ground);
        Put("person.sitting", 1530, ground - M(.03));
        Put("plant.shrub", 1900, ground);
        // Upper floor and roof.
        Put("person.standing-side", 980, levels[1]);
        Put("person.walking", 1220, levels[1], variant: 1, flip: true);
        Put("person.standing", 1520, levels[1], variant: 1);
        Put("plant.grass", 1050, levels[2]); Put("plant.grass", 1110, levels[2], variant: 1); Put("plant.shrub", 1750, levels[2], .9);
        Put("person.standing-side", 1400, levels[2], variant: 2, flip: true);
        // Terrace side: the people and the table cast short shadows on the ground.
        Put("tree.airy", 2120, ground, 7.5, variant: 1);
        shadowed.Add(Put("table.parasol", 2290, ground).Id);
        shadowed.Add(Put("person.stroller", 2440, ground, flip: true).Id);
        Put("tree.conifer", 2555, ground, 9, variant: 2);
        if (!dark) ShadowRenderer.Insert(document, ShadowSpec.Default(ShadowProjection.Ground) with { Sources = [.. shadowed], Angle = 345, Elevation = 55, Softness = 14, Opacity = .3 }, Loc.T("그림자 · 점경"));        selected = walking.Id; document.ActiveId = walking.Id;
        return document;
    }

    // A site plan: a building, a road with parking, a plaza and a lawn, with plan entourage and tree shadows.
    internal static Document EntouragePlanDocument(out Guid selected)
    {
        const double ppm = 36; const int width = 2400, height = 1500;
        var document = new Document { Width = width, Height = height, Name = Loc.T("점경 예시 · 배치도") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(width, height, Colors.White) });
        var ink = Color.FromRgb(34, 34, 34); var parts = new List<VectorPrimitive>();
        void Stroke(Geometry g, double w) => parts.Add(new VectorPrimitive(g, ink, false, w));
        // Road along the bottom, a sidewalk, parking bays, the building, a paved plaza and a lawn edge.
        Stroke(new LineGeometry(new Point(0, 1260), new Point(width, 1260)), 2.5); Stroke(new LineGeometry(new Point(0, 1460), new Point(width, 1460)), 2.5);
        Stroke(new LineGeometry(new Point(0, 1180), new Point(width, 1180)), 1.2);
        for (double x = 0; x < width; x += 90) Stroke(new LineGeometry(new Point(x, 1360), new Point(x + 50, 1360)), 1);
        for (double x = 1240; x <= 2220; x += 92) Stroke(new LineGeometry(new Point(x, 980), new Point(x, 1180)), 1.2);
        Stroke(new LineGeometry(new Point(1240, 980), new Point(2220, 980)), 1.2);
        parts.Add(new VectorPrimitive(new RectangleGeometry(new Rect(300, 240, 760, 520)), Color.FromRgb(236, 234, 228), true, 0));
        Stroke(new RectangleGeometry(new Rect(300, 240, 760, 520)), 4);
        for (double x = 300; x <= 1060; x += 76) Stroke(new LineGeometry(new Point(x, 240), new Point(x, 760)), .5);
        Stroke(new RectangleGeometry(new Rect(1160, 200, 980, 640)), 1.5);
        for (double y = 240; y < 840; y += 40) Stroke(new LineGeometry(new Point(1160, y), new Point(2140, y)), .45);
        Stroke(Geometry.Parse("M40 840 C300 800 700 820 1100 860"), 1.2);        var lines = VectorContent.FromPaths(width, height, parts);
        document.Add(new Layer { Name = Loc.T("배치도"), Kind = LayerKind.Vector, Vector = lines, Pixels = Imaging.Draw(width, height, dc => dc.DrawDrawing(lines.Drawing)) });
        Layer Put(string id, double x, double y, double? meters = null, EntourageFill? fill = null, double rotation = 0, int variant = 0)
        {
            var item = EntourageLibrary.Find(id)!;
            var spec = EntourageRenderer.Spec(item, ppm, variant) with { Meters = meters ?? item.DefaultMeters, Fill = fill ?? item.DefaultFill };
            var layer = EntourageRenderer.Create(spec, new Point(x, y), Loc.T(item.Name), false, rotation);
            document.Add(layer); return layer;
        }
        var trees = new List<Guid>();
        // A grove on the lawn (scattered), conifers by the building, trees in the plaza.
        var grove = EntourageRenderer.Scatter(EntourageCategory.Plants, EntourageView.Plan, 5.5, ppm, 5.5 * ppm, 4, 6, new Point(500, 950), new Rect(90, 880, 1000, 160), null, 3);
        foreach (var p in grove) trees.Add(Put("tree.plan-lobed", p.Anchor.X, p.Anchor.Y, p.Meters, EntourageFill.Gray, p.Rotation, p.Variant).Id);
        trees.Add(Put("tree.plan-conifer", 1110, 150, 4.5).Id); trees.Add(Put("tree.plan-conifer", 200, 170, 4, variant: 1).Id);
        trees.Add(Put("tree.plan-scribble", 1700, 440, 6).Id); trees.Add(Put("tree.plan-scribble", 1990, 640, 5, variant: 1).Id);
        Put("plant.plan-shrubs", 1150, 960); Put("plant.plan-shrubs", 140, 620, variant: 2);
        // Parking, bicycles, plaza furniture and people, then street trees over the cars.
        for (int i = 0; i < 6; i++) if (i != 2) Put("car.plan", 1286 + i * 184, 1080, rotation: 180);
        for (int i = 0; i < 4; i++) Put("bike.plan", 1110 + i * 26, 1100);
        Put("bench.plan", 1450, 560); Put("bench.plan", 1450, 680, rotation: 180);
        Put("table.plan", 1850, 300); Put("table.plan", 2040, 400, rotation: 20);
        var people = EntourageRenderer.Scatter(EntourageCategory.People, EntourageView.Plan, .6, ppm, .6 * ppm, 3, 9, new Point(1600, 600), new Rect(1200, 260, 900, 540), null, 5);
        foreach (var p in people) Put(p.Variant == 1 ? "person.plan-walking" : "person.plan", p.Anchor.X, p.Anchor.Y, rotation: p.Rotation, variant: p.Variant);
        for (int i = 0; i < 7; i++) trees.Add(Put("tree.plan-branches", 120 + i * 360, 1220, 5.5, EntourageFill.White, variant: i).Id);        ShadowRenderer.Insert(document, ShadowSpec.Default(ShadowProjection.Plan) with { Sources = [.. trees], Height = 7 * ppm, Elevation = 50, Angle = 320, Opacity = .22 }, Loc.T("그림자 · 수목"));
        selected = trees[^1]; document.ActiveId = selected;
        return document;
    }

    // Every item (and its variants) of one view in cells of 220 × 250 px, each scaled to fit its cell.
    static void SaveEntourageSheet(string path, EntourageView view, EntourageFill fill, uint line, uint background, bool styles = false)
    {
        const int cell = 220, rowHeight = 250, columns = 6;
        var entries = new List<(EntourageItem Item, int Variant, EntourageFill Fill)>();
        foreach (var item in EntourageLibrary.All.Where(i => i.View == view))
        {
            if (styles) { foreach (var f in Enum.GetValues<EntourageFill>()) entries.Add((item, 0, f)); if (entries.Count >= 24) break; continue; }
            for (int v = 0; v < Math.Max(1, item.Variants); v++) entries.Add((item, v, fill));
        }
        int rows = (entries.Count + columns - 1) / columns, width = cell * columns, height = rowHeight * rows;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(VectorShapes.Color(background)), null, new Rect(0, 0, width, height));
            var typeface = new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (int i = 0; i < entries.Count; i++)
            {
                var (item, variant, style) = entries[i];
                double x = i % columns * cell, y = i / columns * rowHeight;
                var art = EntourageLibrary.Art(item, variant);
                double aspect = view == EntourageView.Plan ? 1 : art.Bounds.Width / Math.Max(1, -art.Bounds.Top);
                double target = Math.Min(rowHeight - 50, (cell - 30) / Math.Max(1, aspect));
                var spec = EntourageRenderer.Spec(item, Math.Max(EntourageSpec.MinPixelsPerMeter, target / item.DefaultMeters), variant) with { LineArgb = line, Fill = style };
                var (vector, _, anchor) = EntourageRenderer.Draw(spec);
                double ox = x + (cell - vector.Width) / 2, oy = view == EntourageView.Plan ? y + (rowHeight - 40 - vector.Height) / 2 : y + rowHeight - 34 - anchor.Y;
                dc.PushTransform(new TranslateTransform(Math.Round(ox), Math.Round(oy))); dc.DrawDrawing(vector.Drawing); dc.Pop();
                if (view == EntourageView.Elevation) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1), new Point(x + 10, y + rowHeight - 33.5), new Point(x + cell - 10, y + rowHeight - 33.5));
                string label = Loc.T(item.Name) + (styles ? " · " + style : item.Variants > 1 ? $" · {variant + 1}" : "");
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, new SolidColorBrush(Color.FromRgb(90, 90, 90)), 1);
                dc.DrawText(text, new Point(x + (cell - text.Width) / 2, y + rowHeight - 24));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
