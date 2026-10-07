using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Offscreen review of the design-style adjustment layers: each effect on a sample (the photo, the
// sample floor plan, a night scene) in its dialog with the actual-size view, plus the full-size result.
public sealed partial class MainWindow
{
    void RenderStyleEffectPreviews(string directory, Action<Window, string, int, int> capture)
    {
        var photo = Demo.Create();
        void Show(Document document, AdjustmentSpec spec, string name, Point focus)
        {
            var after = Imaging.Render(AdjustmentDialog.PreviewDocument(document, null, spec, true, null));
            using (var file = File.Create(Path.Combine(directory, name + "-result.png"))) after.WritePng(file);
            var dialog = new AdjustmentDialog(null, document, spec);
            try { dialog.SetDesignPreview(after); dialog.MoveDetail(focus); dialog.RenderDetailNow(); capture(dialog, name, 1040, 760); }
            finally { dialog.Close(); }
        }
        Show(photo, new() { Kind = AdjustmentKind.Threshold, Threshold = new() { Level = 150, Smoothness = 6 } }, "style-threshold", new Point(560, 560));
        Show(photo, new() { Kind = AdjustmentKind.Halftone, Halftone = new() { CellSize = 9 } }, "style-halftone", new Point(640, 520));
        Show(photo, new() { Kind = AdjustmentKind.Halftone, Halftone = new() { CellSize = 7, Angle = 15, Shape = HalftoneShape.Line, InkArgb = 0xFF1C2B78, PaperArgb = 0xFFF4EFE4 } }, "style-halftone-lines", new Point(900, 430));
        Show(StyleEffectPlan(directory), new() { Kind = AdjustmentKind.PaperTexture, Paper = new() { Toner = .55, Streaks = .45, Edges = .7, Grain = .5 } }, "style-paper", new Point(150, 120));
        Show(StyleEffectNight(), new() { Kind = AdjustmentKind.Glow, Glow = new() { Threshold = .65, Radius = 60, Intensity = 2 } }, "style-glow", new Point(520, 470));
        // The editor with an effect layer selected: layer row, inspector and the 스타일 효과 actions.
        var edited = Demo.Create();
        var halftone = DocumentFeatures.CreateAdjustment(edited, new AdjustmentSpec { Kind = AdjustmentKind.Halftone, Halftone = new() { CellSize = 9 } });
        halftone.Name = Loc.T(halftone.Name); edited.Add(halftone);
        AddTab(edited, null); SetWorkspaceMode(false); ShowStudioPage(0); Refresh(false);
        composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap(); capture(this, "style-editor", 1480, 920);
        CloseTab();
    }

    // The sample floor plan from the cleanup previews on white paper, or a few lines when it is unavailable.
    static Document StyleEffectPlan(string directory)
    {
        string path = Path.Combine(directory, "평면 예시.dxf");
        Document plan;
        if (File.Exists(path))
            plan = CompatibilityImport.ReadAsync(path, new(CadLongEdge: 1200, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Layers, SeparateLayers: true, Cleanup: new CadCleanup())).GetAwaiter().GetResult().Document;
        else
        {
            plan = new Document { Width = 1200, Height = 860 };
            plan.Add(new Layer { Name = Loc.T("선"), Pixels = Imaging.Draw(1200, 860, dc => { var pen = new Pen(Brushes.Black, 3); dc.DrawRectangle(null, pen, new Rect(100, 100, 1000, 660)); dc.DrawLine(pen, new Point(600, 100), new Point(600, 560)); }) });
        }
        var paper = new Layer { Name = Loc.T("종이"), Pixels = Raster.Solid(plan.Width, plan.Height, Colors.White) };
        plan.Layers.Insert(0, paper);
        return plan;
    }

    // A night elevation: dark sky, a long building with lit windows, street lamps and a red signal light.
    internal static Document StyleEffectNight()
    {
        const int width = 1600, height = 900;
        var scene = Imaging.Draw(width, height, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(6, 9, 22), Color.FromRgb(46, 32, 54), 90), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 22, 28)), null, new Rect(180, 280, 1240, 470));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 13, 17)), null, new Rect(0, 750, width, 150));
            var warm = new SolidColorBrush(Color.FromRgb(255, 222, 160)); var cool = new SolidColorBrush(Color.FromRgb(214, 232, 255));
            for (int row = 0; row < 5; row++) for (int column = 0; column < 15; column++)
                if ((row * 7 + column * 3) % 5 != 0) dc.DrawRectangle((row + column) % 3 == 0 ? cool : warm, null, new Rect(220 + column * 80, 320 + row * 82, 44, 38));
            foreach (int x in new[] { 90, 1500 })
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 46)), null, new Rect(x - 3, 640, 6, 110));
                dc.DrawEllipse(Brushes.White, null, new Point(x, 636), 7, 7);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 60, 40)), null, new Point(1380, 260), 5, 5);
        });
        var document = new Document { Width = width, Height = height, Name = Loc.T("야경 예시") };
        document.Add(new Layer { Name = Loc.T("야경"), Pixels = scene });
        return document;
    }
}
