using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// Main window at small sizes: the tool rail, the layer list under the ribbon, the zoom readout,
// the ribbon's large buttons and icons, and drawing thumbnails.
public sealed partial class MainWindow
{
    internal static void RunWindowFitTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in Visuals(child)) yield return nested;
            }
        }
        // The same steps as an offscreen capture of the main window at a client size.
        static void Lay(MainWindow w, double width, double height)
        {
            var content = (FrameworkElement)w.Content; var size = new Size(width, height);
            w.studioScroll.Height = w.PreferredStudioHeight(height);
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        }
        MainWindow Open()
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.AddTab(NewDocumentDialog.CreateDocument("창 맞춤", "1586", "992", 1), null);
            return w;
        }
        string RailState(MainWindow w) => $"layout {w.toolRailLayout}, viewport {w.toolRailScroll!.ViewportHeight:0.#}, content {w.toolRailScroll.ExtentHeight:0.#}";
        void CheckRail(MainWindow w, string where)
        {
            var scroll = w.toolRailScroll!;
            Check(scroll.ScrollableHeight <= .5, $"{where}: the tool rail scrolls ({RailState(w)})");
            var inRail = Visuals(w.toolRailContent).OfType<Button>().Where(b => w.toolButtons.ContainsValue(b)).ToHashSet();
            var groups = w.CurrentToolGroups(w.designWorkspace).SelectMany(g => g.Tools).ToArray();
            Check(groups.All(t => inRail.Contains(w.toolButtons[t]) && w.toolButtons[t].Visibility == Visibility.Visible), $"{where}: a tool is missing from the rail");
            foreach (var tool in groups)
            {
                var b = w.toolButtons[tool]; var bottom = b.TranslatePoint(new Point(0, b.ActualHeight), scroll).Y;
                Check(b.ActualHeight >= 26 && bottom <= scroll.ViewportHeight + .5, $"{where}: {tool} is cut off ({RailState(w)})");
            }
            var swatches = w.colorSwatches; var swatchBottom = swatches.TranslatePoint(new Point(0, swatches.ActualHeight), scroll).Y;
            Check(swatchBottom <= scroll.ViewportHeight + .5 && swatches.ActualWidth <= scroll.ViewportWidth + .5, $"{where}: the color swatches are cut off ({RailState(w)})");
        }

        test("tool rail keeps every tool and the color swatches in view on short windows", () =>
        {
            var w = Open();
            try
            {
                Lay(w, 1480, 920); CheckRail(w, "photo 1480x920");
                Check(w.toolRailLayout == 0 && w.toolRailColumn.Width.Value == ToolRailWidth, "The default window no longer uses the regular two-column rail");
                w.SetWorkspaceMode(true); Lay(w, 1480, 920); CheckRail(w, "design 1480x920");
                Lay(w, 1280, 720); CheckRail(w, "design 1280x720");
                Check(w.ToolRailColumns == 2, $"design 1280x720 should only tighten the rail, not widen it ({RailState(w)})");
                w.SetWorkspaceMode(false); Lay(w, 1280, 720); CheckRail(w, "photo 1280x720");
                w.SetRibbonMode(true); w.SelectRibbonTab("레이어"); Lay(w, 1280, 720); CheckRail(w, "ribbon photo 1280x720");
                Check(w.ToolRailColumns <= 3, $"The ribbon at 1280x720 widened the rail more than needed ({RailState(w)})");
                w.SetWorkspaceMode(true); Lay(w, 1280, 720); CheckRail(w, "ribbon design 1280x720");
                Lay(w, 1000, 640); CheckRail(w, "ribbon design at the minimum window size");
                // Back at a roomy size the rail returns to the regular layout.
                w.SetRibbonMode(false); w.SetWorkspaceMode(false); Lay(w, 1480, 920);
                Check(w.toolRailLayout == 0 && w.toolRailColumn.Width.Value == ToolRailWidth && w.toolButtons.Values.All(b => b.Height == ToolButtonHeight), "The rail did not return to its regular layout");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("layer list keeps at least two rows under the ribbon at 1280x720", () =>
        {
            var w = Open();
            try
            {
                w.SetRibbonMode(true); w.SelectRibbonTab("레이어");
                foreach (var (width, height) in new[] { (1280, 720), (1366, 768), (1000, 640) })
                {
                    Lay(w, width, height);
                    double list = w.layerList.ActualHeight, rows = w.layerList.Items.Count;
                    Check(list >= 2 * LayerRowReserve - 4, $"{width}x{height}: the layer list is {list:0.#}px tall (studio {w.studioScroll.ActualHeight:0.#}px, card {w.studioTopCard!.ActualHeight:0.#}px, layers {w.layersPane!.ActualHeight:0.#}px, column {((FrameworkElement)w.studioTopCard.Parent).ActualHeight:0.#}px)");
                    Check(w.studioScroll.ActualHeight >= StudioMinimumHeight - .5, $"{width}x{height}: the studio panel collapsed to {w.studioScroll.ActualHeight:0.#}px");
                    var footer = w.layersPane!.TranslatePoint(new Point(0, w.layersPane.ActualHeight), (UIElement)w.Content).Y;
                    Check(footer <= height - 30 + .5, $"{width}x{height}: the layer toolbar runs under the status bar ({footer:0.#})");
                }
                // A roomy window keeps the preferred studio height.
                w.SetRibbonMode(false); Lay(w, 1480, 920);
                Check(Math.Abs(w.studioScroll.ActualHeight - w.studioScroll.Height) < .5, "The studio card was capped although the window has room");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("zoom readout follows every refit of the canvas", () =>
        {
            var w = Open();
            try
            {
                string Expected() => $"{w.canvas.Zoom * 100:0.#}%";
                foreach (var (width, height) in new[] { (1480, 920), (1280, 720), (1920, 1080) })
                {
                    Lay(w, width, height); w.canvas.Fit();
                    Check(w.zoomBox!.Text == Expected(), $"{width}x{height}: the readout says {w.zoomBox.Text} after a fit at {Expected()}");
                }
                double before = w.canvas.Zoom;
                w.SetWorkspaceMode(true); Lay(w, 1480, 920); w.canvas.Fit();
                Check(w.zoomBox!.Text == Expected(), $"Design mode: the readout says {w.zoomBox.Text} at {Expected()}");
                w.SetWorkspaceMode(false);
                // Real commands that refit after their edit: crop to a selection and a new tab.
                w.selection = new Selection(new Rect(0, 0, 400, 300));
                w.CropSelection();
                Check(w.doc.Width == 400 && w.zoomBox!.Text == Expected(), $"Crop: the readout says {w.zoomBox!.Text} at {Expected()}");
                w.AddTab(NewDocumentDialog.CreateDocument("두 번째", "300", "200", 1), null);
                Check(w.zoomBox!.Text == Expected(), $"New tab: the readout says {w.zoomBox.Text} at {Expected()}");
                Check(before > 0, "No zoom");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("large ribbon labels break only between words and share one icon line", () =>
        {
            static double Measure(string text) => MeasureRibbonText(text, Theme.CaptionSize);
            foreach (var label in new[] { "Import Compositor .comp", "Export Compositor .comp", "Compositor .comp 가져오기", "현재 문서 닫기", "Close current document", "내보내기 미리보기", "Save as" })
            {
                var lines = RibbonLabelLines(label, Measure) ?? throw new InvalidOperationException($"'{label}' fell back to character wrapping");
                Check(string.Join(' ', lines) == label, $"'{label}' was not split at spaces: {string.Join(" / ", lines)}");
                Check(lines.Length <= 2, $"'{label}' takes {lines.Length} lines");
                Check(!lines.Any(l => l.EndsWith(".c", StringComparison.Ordinal)), $"'{label}' splits the extension");
            }
            Check(RibbonLabelLines("既定の設定で書き出しを行うコマンド", Measure) == null, "Text without spaces should keep character wrapping");
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(NewDocumentDialog.CreateDocument("리본", "64", "64", 1), null);
                w.SetRibbonMode(true); w.SelectRibbonTab("파일"); Lay(w, 1480, 920);
                var large = Visuals(w.ribbonBody!).OfType<Button>().Where(b => b.Content is StackPanel { Orientation: Orientation.Vertical }).ToArray();
                Check(large.Length >= 6, "The File tab lost its large buttons");
                var tops = large.Select(b => ((StackPanel)b.Content).Children[0]).Select(icon => Math.Round(((UIElement)icon).TranslatePoint(new Point(), w.ribbonBody!).Y, 1)).Distinct().ToArray();
                Check(tops.Length == 1, "Large ribbon icons sit at different heights: " + string.Join(", ", tops));
                foreach (var button in large)
                {
                    var text = ((StackPanel)button.Content).Children.OfType<TextBlock>().Single();
                    Check(text.Text.Split('\n').All(line => line.Length > 0 && !line.StartsWith(' ')), $"A label broke badly: {text.Text}");
                    Check(text.ActualWidth <= button.ActualWidth, $"A label is wider than its button: {text.Text}");
                }
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("flip and mask commands have their own icons", () =>
        {
            string[] labels = ["가로 뒤집기", "세로 뒤집기", "마스크 추가", "마스크 반전", "마스크 제거"];
            var glyphs = labels.Select(RibbonGlyph).ToArray();
            Check(glyphs.Distinct().Count() == labels.Length, "Two of the flip / mask commands share an icon");
            Check(!glyphs.Contains(Theme.Glyphs.Swap) && !glyphs.Contains(Theme.Glyphs.More), "A flip or mask command still uses the color swap or the fallback icon");
            foreach (var glyph in new[] { Theme.Glyphs.FlipHorizontal, Theme.Glyphs.FlipVertical, Theme.Glyphs.MaskAdd, Theme.Glyphs.MaskInvert, Theme.Glyphs.MaskRemove })
            {
                Check(Theme.Glyph(glyph) is Image, "A new glyph did not draw");
                var bounds = System.Windows.Media.Geometry.Parse(string.Join(" ", glyph.Split('|').Select(l => l.Trim().TrimStart('~', '*')))).Bounds;
                Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, "A new glyph leaves the 24-unit grid");
            }
        });

        test("drawing layer thumbnails sit on a light backing", () =>
        {
            static double Luma(Brush brush) { var c = ((SolidColorBrush)brush).Color; return (.2126 * c.R + .7152 * c.G + .0722 * c.B) / 255; }
            var drawing = new Layer { Name = "A-WALL", Kind = LayerKind.Vector, Pixels = new Raster(8, 8) };
            var photo = new Layer { Name = "사진", Pixels = new Raster(8, 8) };
            Check(Luma(LayerRow.ThumbnailBacking(drawing)) > .85, "A drawing thumbnail is still dark");
            Check(ReferenceEquals(LayerRow.ThumbnailBacking(photo), Theme.Input), "Image thumbnails changed their backing");
            var row = new LayerRow(drawing, false, () => { }, _ => { }, () => { });
            var tile = LogicalDescendants(row).OfType<Border>().First(b => b.Width == 28 && b.Height == 28);
            Check(ReferenceEquals(tile.Background, Theme.Paper), "The layer row does not use the drawing backing");
        });
    }

    static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in LogicalDescendants(child)) yield return nested;
        }
    }

    int ToolRailColumns => ToolRailLayouts[toolRailLayout].Columns;
}
