using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed class CanvasView : FrameworkElement
{
    public Document Document { get; set; } = null!;
    public Selection? Selection { get; set; }
    public Rect? GestureBounds { get; set; }
    public bool EllipseGesture { get; set; }
    public bool ShowLayerBounds { get; set; }
    public bool PixelGrid { get; set; }
    public List<(bool Vertical, double Position)> Guides { get; } = [];
    public IReadOnlyList<Point>? GesturePoints { get; set; }
    public Point? BrushPoint { get; set; }
    public double BrushRadius { get; set; } = 21;
    Selection? contourSelection;
    Geometry? contour;
    public BitmapSource? Composite { get; set; }
    public double Zoom { get; set; } = .65;
    public Vector Pan { get; set; }
    public Point Origin => new((ActualWidth - Document.Width * Zoom) / 2 + Pan.X, (ActualHeight - Document.Height * Zoom) / 2 + Pan.Y);
    public Point ToDocument(Point p) => new((p.X - Origin.X) / Zoom, (p.Y - Origin.Y) / Zoom);
    readonly DrawingBrush checker;
    public CanvasView()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Theme.Brush("#FFFFFF"), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        group.Children.Add(new GeometryDrawing(Theme.Brush("#E2E4E8"), null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        group.Children.Add(new GeometryDrawing(Theme.Brush("#E2E4E8"), null, new RectangleGeometry(new Rect(10, 10, 10, 10))));
        checker = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 20, 20), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        checker.Freeze();
    }
    public void Fit()
    {
        Zoom = Math.Clamp(Math.Min((ActualWidth - 96) / Document.Width, (ActualHeight - 96) / Document.Height), .03, 8); Pan = new Vector(); InvalidateVisual();
    }
    public void ZoomAt(double factor, Point screenPoint)
    {
        var before = ToDocument(screenPoint); Zoom = Math.Clamp(Zoom * factor, .03, 16);
        var after = new Point(Origin.X + before.X * Zoom, Origin.Y + before.Y * Zoom); Pan += screenPoint - after; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Brush("#14171D"), null, new Rect(RenderSize));
        if (Document == null) return;
        var origin = Origin; var rect = new Rect(origin.X, origin.Y, Document.Width * Zoom, Document.Height * Zoom);
        dc.DrawRectangle(Theme.Brush("#080A0D"), null, new Rect(rect.X + 6, rect.Y + 8, rect.Width, rect.Height));
        dc.DrawRectangle(checker, null, rect);
        if (Composite != null) dc.DrawImage(Composite, rect);
        dc.DrawRectangle(null, new Pen(Theme.Brush("#464E5B"), 1), rect);
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y)); dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Document.Width, Document.Height)));
        if (PixelGrid && Zoom >= 8)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(65, 180, 190, 200)), 1 / Zoom);
            int left = Math.Max(0, (int)(-origin.X / Zoom)), top = Math.Max(0, (int)(-origin.Y / Zoom));
            int right = Math.Min(Document.Width, (int)((ActualWidth - origin.X) / Zoom) + 1), bottom = Math.Min(Document.Height, (int)((ActualHeight - origin.Y) / Zoom) + 1);
            for (int x = left; x <= right; x++) dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            for (int y = top; y <= bottom; y++) dc.DrawLine(pen, new Point(left, y), new Point(right, y));
        }
        if (Selection != null)
        {
            if (Selection.Coverage == null) DrawSelection(dc, Selection.Bounds, Selection.Ellipse);
            else
            {
                if (!ReferenceEquals(contourSelection, Selection)) { contourSelection = Selection; contour = SelectionContour(Selection); }
                if (contour != null) DrawSelectionGeometry(dc, contour);
            }
        }
        if (GestureBounds != null) DrawSelection(dc, GestureBounds.Value, EllipseGesture);
        if (GesturePoints is { Count: > 1 } vertices)
        {
            var path = new StreamGeometry(); using (var context = path.Open()) { context.BeginFigure(vertices[0], false, false); context.PolyLineTo(vertices.Skip(1).ToArray(), true, false); }
            DrawSelectionGeometry(dc, path);
        }
        foreach (var guide in Guides)
        {
            var pen = new Pen(Theme.Brush("#68C9FF"), 1 / Zoom);
            dc.DrawLine(pen, guide.Vertical ? new Point(guide.Position, 0) : new Point(0, guide.Position), guide.Vertical ? new Point(guide.Position, Document.Height) : new Point(Document.Width, guide.Position));
        }
        if (BrushPoint is { } brush)
        {
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.5 / Zoom), brush, BrushRadius, BrushRadius);
            dc.DrawEllipse(null, new Pen(Brushes.Black, .6 / Zoom), brush, BrushRadius, BrushRadius);
        }
        dc.Pop();
        if (ShowLayerBounds && Document.Active is { } layer)
        {
            var points = TransformHandles.Points(Document, layer, Zoom);
            var pen = new Pen(Theme.Accent, 1 / Zoom);
            for (int i = 0; i < 4; i++) dc.DrawLine(pen, points[i], points[(i + 1) % 4]);
            if (!layer.Locked && layer.Kind != LayerKind.Adjustment)
            {
                for (int i = 0; i < 8; i++) dc.DrawRectangle(Theme.Panel, pen, new Rect(points[i].X - 3.5 / Zoom, points[i].Y - 3.5 / Zoom, 7 / Zoom, 7 / Zoom));
                dc.DrawLine(pen, points[4], points[8]); dc.DrawEllipse(Theme.Panel, pen, points[8], 4 / Zoom, 4 / Zoom);
            }
        }
        dc.Pop(); dc.Pop();
        dc.DrawRectangle(Theme.Brush("#191C23"), null, new Rect(0, 0, ActualWidth, 22));
        double step = Zoom > 1.5 ? 50 : Zoom > .5 ? 100 : 250;
        for (double x = 0; x <= Document.Width; x += step)
        {
            double sx = origin.X + x * Zoom;
            if (sx < 0 || sx > ActualWidth) continue;
            dc.DrawLine(new Pen(Theme.Line, 1), new Point(sx, 15), new Point(sx, 22));
            dc.DrawText(new FormattedText(x.ToString("0"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Theme.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(sx + 4, 3));
        }
    }
    void DrawSelection(DrawingContext dc, Rect bounds, bool ellipse)
    {
        Geometry shape = ellipse ? new EllipseGeometry(bounds) : new RectangleGeometry(bounds);
        DrawSelectionGeometry(dc, shape);
    }
    void DrawSelectionGeometry(DrawingContext dc, Geometry shape)
    {
        var white = new Pen(Brushes.White, 1.5 / Zoom); var black = new Pen(Brushes.Black, 1 / Zoom) { DashStyle = new DashStyle([4, 4], 0) };
        dc.DrawGeometry(null, white, shape); dc.DrawGeometry(null, black, shape);
    }
    static Geometry SelectionContour(Selection selection)
    {
        var geometry = new StreamGeometry(); var mask = selection.Coverage!;
        int w = selection.CanvasWidth, h = selection.CanvasHeight;
        // Bound the preview path complexity; pixel operations still use the full-resolution mask.
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)w * h / 2_000_000)));
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x] >= 128;
        using (var c = geometry.Open())
        {
            void Edge(int x1, int y1, int x2, int y2) { c.BeginFigure(new Point(x1, y1), false, false); c.LineTo(new Point(x2, y2), true, false); }
            for (int y = 0; y < h; y += step) for (int x = 0; x < w; x += step)
            {
                if (!Inside(x, y)) continue;
                int r = Math.Min(w, x + step), b = Math.Min(h, y + step);
                if (!Inside(x - step, y)) Edge(x, y, x, b);
                if (!Inside(x, y - step)) Edge(x, y, r, y);
                if (!Inside(x + step, y)) Edge(r, y, r, b);
                if (!Inside(x, y + step)) Edge(x, b, r, b);
            }
        }
        geometry.Freeze(); return geometry;
    }
}
