using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Offscreen review of the diagram tools: a site board with dashed curved arrows, a dash-dot site
// boundary and callouts; the line and callout properties; the tool options bar while drawing.
public sealed partial class MainWindow
{
    void RenderDiagramPreviews(Action<Window, string, int, int> capture, Action<FrameworkElement, string, int, int> capturePane)
    {
        var board = DiagramSampleDocument(out var arrow, out var callout);
        AddTab(board, null); SetWorkspaceMode(true);
        composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        void Select(Guid id) { doc.ActiveId = id; selectedLayers.Clear(); selectedLayers.Add(id); ShowStudioPage(1); Refresh(false); }
        // The curve tool with a dashed curved arrow selected: options bar, point handles and line rows.
        SelectLineTool(true); Select(arrow);
        capture(this, "diagram-board", 1480, 920);
        capture(this, "diagram-board-1280x720", 1280, 720);
        capturePane(studioPanes[1], "diagram-line-properties", 360, 1080);
        // The callout tool with a callout selected: target, bend and label handles and the label rows.
        SetTool(Tool.Callout); Select(callout);
        capture(this, "diagram-callout", 1480, 920);
        capturePane(studioPanes[1], "diagram-callout-properties", 360, 1840);
        // 간결한 화면: the same tools, options bar and properties in the dock.
        SetScreenStyle(true);
        SelectLineTool(true); Select(arrow); capture(this, "diagram-compact", 1480, 920);
        SetTool(Tool.Callout); Select(callout); capture(this, "diagram-compact-callout", 1480, 920);
        SetScreenStyle(false);
        // A polyline being drawn: its styled preview follows the pointer.
        SelectLineTool(false); lineDash = StrokeDash.Dashed; lineEnd = LineMark.Arrow; lineWidth = 3; foreground = Color.FromRgb(0xC0, 0x39, 0x2B); SyncDiagramOptions();
        foreach (var p in new Point[] { new(240, 820), new(520, 760), new(700, 900) }) { LineDown(p, new Point(p.X, p.Y), 1); DiagramUp(p, new Point(p.X, p.Y)); }
        lineHover = new Point(980, 840); UpdateDiagramOverlay(); status.Text = Loc.T(DiagramHint()!);
        capture(this, "diagram-drawing", 1480, 920);
        ResetDiagramTransient(); foreground = Color.FromRgb(188, 217, 250); lineDash = StrokeDash.Solid; lineEnd = LineMark.None; lineWidth = 2; SyncDiagramOptions();
        SetTool(Tool.Move);
    }

    // A site board: ground, blocks, a dash-dot site boundary, dashed curved movement arrows and labels.
    internal static Document DiagramSampleDocument(out Guid arrow, out Guid callout)
    {
        const uint ink = 0xFF1F2933, red = 0xFFC0392B, blue = 0xFF2E5AAC;
        var document = new Document { Width = 1600, Height = 1000, Name = Loc.T("다이어그램 예시 · 배치도") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(1600, 1000, Colors.White) });
        var ground = VectorShapes.Create(new ShapeSpec { Width = 1400, Height = 760, CornerRadius = 18, FillArgb = 0xFFEFF1EC }, 100, 150);
        ground.Name = Loc.T("대지"); document.Add(ground);
        foreach (var (x, y, w, h, fill) in new[] { (260, 260, 320, 200, 0xFFFFFFFFu), (760, 230, 240, 300, 0xFFDCE7F5u), (1120, 300, 240, 180, 0xFFFFFFFFu), (420, 600, 360, 170, 0xFFFFFFFFu), (1000, 620, 300, 160, 0xFFFFFFFFu) })
        {
            var block = VectorShapes.Create(new ShapeSpec { Width = w, Height = h, FillArgb = fill, StrokeEnabled = true, StrokeArgb = 0xFF9AA3AD, StrokeWidth = 2 }, x, y);
            block.Name = Loc.T("건물"); document.Add(block);
        }
        var boundary = VectorShapes.Create(new ShapeSpec
        {
            Kind = ShapeKind.Line, Points = new ShapePoints([new(140, 190), new(1460, 190), new(1460, 870), new(860, 870), new(140, 800)]), Closed = true,
            StrokeEnabled = true, StrokeArgb = red, StrokeWidth = 3, Dash = StrokeDash.DashDot, FillEnabled = false
        });
        boundary.Name = Loc.T("대지 경계"); document.Add(boundary);
        Layer Arrow(Point[] points, uint color, StrokeDash dash, bool smooth, LineMark start, LineMark end, string name)
        {
            var layer = VectorShapes.Create(new ShapeSpec
            {
                Kind = ShapeKind.Line, Points = new ShapePoints(points), Smooth = smooth, StrokeEnabled = true, StrokeArgb = color, StrokeWidth = 3, Dash = dash,
                DashScale = 1.2, StartMark = start, EndMark = end, MarkSize = 18, FillEnabled = false
            });
            layer.Name = Loc.T(name); document.Add(layer); return layer;
        }
        var main = Arrow([new(300, 930), new(430, 560), new(700, 470), new(800, 400)], ink, StrokeDash.Dashed, true, LineMark.Dot, LineMark.Arrow, "보행 동선");
        Arrow([new(1010, 380), new(1180, 520), new(1140, 690)], blue, StrokeDash.Dashed, true, LineMark.Dot, LineMark.Arrow, "보행 동선");
        Arrow([new(1540, 760), new(1380, 560), new(1240, 560)], red, StrokeDash.Dotted, false, LineMark.None, LineMark.OpenArrow, "차량 동선");
        arrow = main.Id;
        Layer Callout(Point anchor, Point label, string text, CalloutLeader leader, LineMark mark, bool box)
        {
            var layer = VectorShapes.Create(new ShapeSpec
            {
                Kind = ShapeKind.Callout, Leader = leader, Points = new ShapePoints([anchor, leader == CalloutLeader.Elbow ? DiagramEditing.DefaultElbow(anchor, label) : anchor, label]),
                StrokeEnabled = true, StrokeArgb = ink, StrokeWidth = 2, Dash = StrokeDash.Dotted, StartMark = mark, MarkSize = 14,
                FillEnabled = box, FillArgb = 0xF2FFFFFF, CornerRadius = 6,
                Label = new TextSpec { Content = Loc.T(text), FontFamily = "Malgun Gothic", FontSize = 26, ColorArgb = ink }
            });
            layer.Name = Loc.T("지시선"); document.Add(layer); return layer;
        }
        var entrance = Callout(new(560, 445), new(640, 90), "주출입구", CalloutLeader.Elbow, LineMark.Dot, false);
        Callout(new(880, 520), new(1060, 120), "중정 · 공용 마당", CalloutLeader.Elbow, LineMark.Dot, true);
        Callout(new(1300, 700), new(1360, 960), "주차장 진입", CalloutLeader.Straight, LineMark.Ring, false);
        callout = entrance.Id;
        var title = DocumentFeatures.CreateText(new TextSpec { Content = Loc.T("배치 다이어그램"), FontFamily = "Malgun Gothic", FontSize = 44, Bold = true, ColorArgb = ink }, 100, 50);
        title.Name = Loc.T("제목"); document.Add(title);
        document.ActiveId = arrow;
        return document;
    }
}
