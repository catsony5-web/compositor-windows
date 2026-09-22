using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed partial class CanvasView : FrameworkElement
{
    public Document? Document { get; set; }
    public Selection? Selection { get; set; }
    public Rect? GestureBounds { get; set; }
    public bool EllipseGesture { get; set; }
    public bool ShowLayerBounds { get; set; }
    public Guid? HoveredLayerId { get; set; }
    public IReadOnlyList<MagneticGuide> SnapGuides { get; set; } = [];
    public bool PixelGrid { get; set; }
    public List<(bool Vertical, double Position)> Guides { get; } = [];
    public IReadOnlyList<Point>? GesturePoints { get; set; }
    public Point? BrushPoint { get; set; }
    public double BrushRadius { get; set; } = 21;
    public BitmapSource? BrushTipPreview { get; set; }
    public double BrushTipAspectRatio { get; set; } = 1;
    public double BrushTipAngle { get; set; }
    public string? BrushHud { get; set; }
    Selection? contourSelection;
    Geometry? contour;
    string? cachedBrushHud;
    double cachedBrushHudDpi;
    FormattedText? cachedBrushHudText;
    public BitmapSource? Composite { get; set; }
    // A text drag keeps the expensive document background fixed and moves only
    // the small text bitmap. The normal composite replaces this on mouse-up.
    public BitmapSource? MovePreviewBackground { get; set; }
    public BitmapSource? MovePreviewLayer { get; set; }
    public BitmapSource? MovePreviewForeground { get; set; }
    public Matrix MovePreviewMatrix { get; set; } = Matrix.Identity;
    public double MovePreviewOpacity { get; set; } = 1;
    public double Zoom { get; set; } = .65;
    public Vector Pan { get; set; }
    public Point Origin => new((ActualWidth - (Document?.Width ?? 0) * Zoom) / 2 + Pan.X, (ActualHeight - (Document?.Height ?? 0) * Zoom) / 2 + Pan.Y);
    public Point ToDocument(Point p) => new((p.X - Origin.X) / Zoom, (p.Y - Origin.Y) / Zoom);
    readonly DrawingBrush checker;
    public CanvasView()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross;
        Unloaded += (_, _) => CancelDesignPreview();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Theme.Brush("#FFFFFF"), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        group.Children.Add(new GeometryDrawing(Theme.Brush("#E2E4E8"), null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        group.Children.Add(new GeometryDrawing(Theme.Brush("#E2E4E8"), null, new RectangleGeometry(new Rect(10, 10, 10, 10))));
        checker = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 20, 20), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        checker.Freeze();
    }
    public void Fit()
    {
        if (Document == null) return;
        Zoom = Math.Clamp(Math.Min((ActualWidth - 96) / Document.Width, (ActualHeight - 96) / Document.Height), .001, 8); Pan = new Vector(); InvalidateVisual();
    }
    public void ZoomAt(double factor, Point screenPoint)
    {
        if (Document == null) return;
        var before = ToDocument(screenPoint); Zoom = Math.Clamp(Zoom * factor, .001, DesignMode ? 64 : 16);
        var after = new Point(Origin.X + before.X * Zoom, Origin.Y + before.Y * Zoom); Pan += screenPoint - after; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Brush("#14171D"), null, new Rect(RenderSize));
        if (Document == null) return;
        var origin = Origin; var rect = new Rect(origin.X, origin.Y, Document.Width * Zoom, Document.Height * Zoom);
        dc.DrawRectangle(Theme.Brush("#080A0D"), null, new Rect(rect.X + 6, rect.Y + 8, rect.Width, rect.Height));
        dc.DrawRectangle(checker, null, rect);
        if (!TryDrawDesign(dc) && (MovePreviewBackground ?? Composite) is { } image) dc.DrawImage(image, rect);
        dc.DrawRectangle(null, new Pen(Theme.Brush("#464E5B"), 1), rect);
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y)); dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Document.Width, Document.Height)));
        if (MovePreviewLayer is { } moving)
        {
            dc.PushOpacity(MovePreviewOpacity);
            dc.PushTransform(new MatrixTransform(MovePreviewMatrix));
            dc.DrawImage(moving, new Rect(0, 0, moving.PixelWidth, moving.PixelHeight));
            dc.Pop(); dc.Pop();
        }
        if (MovePreviewLayer != null && MovePreviewForeground is { } above)
            dc.DrawImage(above, new Rect(0, 0, Document.Width, Document.Height));
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
                if (!ReferenceEquals(contourSelection, Selection)) { contourSelection = Selection; contour = SelectionContours.Create(Selection); }
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
        foreach (var guide in SnapGuides)
        {
            var pen = new Pen(Theme.Brush("#E4A8FF"), 1 / Zoom);
            var a = guide.Vertical ? new Point(guide.Position, guide.Start) : new Point(guide.Start, guide.Position);
            var b = guide.Vertical ? new Point(guide.Position, guide.End) : new Point(guide.End, guide.Position);
            dc.DrawLine(pen, a, b);
            double r = 3 / Zoom;
            foreach (var p in new[] { a, b })
            { dc.DrawLine(pen, p + new Vector(-r, -r), p + new Vector(r, r)); dc.DrawLine(pen, p + new Vector(-r, r), p + new Vector(r, -r)); }
        }
        if (BrushPoint is { } brush)
        {
            if (BrushTipPreview is { } stamp)
            {
                double w = BrushRadius * 2 * Math.Min(1, BrushTipAspectRatio);
                double h = BrushRadius * 2 / Math.Max(1, BrushTipAspectRatio);
                var stampBounds = new Rect(brush.X - w / 2, brush.Y - h / 2, w, h);
                dc.PushTransform(new RotateTransform(BrushTipAngle, brush.X, brush.Y));
                dc.PushOpacity(.35); dc.DrawImage(stamp, new Rect(brush.X - BrushRadius, brush.Y - BrushRadius, BrushRadius * 2, BrushRadius * 2)); dc.Pop();
                dc.DrawRectangle(null, new Pen(Brushes.White, 1.5 / Zoom), stampBounds);
                dc.DrawRectangle(null, new Pen(Brushes.Black, .6 / Zoom), stampBounds);
                dc.Pop();
            }
            else
            {
                if (BrushHud != null) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(65, 255, 100, 90)), null, brush, BrushRadius, BrushRadius);
                dc.DrawEllipse(null, new Pen(Brushes.White, 1.5 / Zoom), brush, BrushRadius, BrushRadius);
                dc.DrawEllipse(null, new Pen(Brushes.Black, .6 / Zoom), brush, BrushRadius, BrushRadius);
            }
        }
        dc.Pop();
        if (ShowLayerBounds && HoveredLayerId is { } hoverId && hoverId != Document.ActiveId && Document.Layers.FirstOrDefault(l => l.Id == hoverId) is { } hovered)
        {
            var points = TransformHandles.Points(Document, hovered, Zoom);
            var shadow = new Pen(Theme.Brush("#303E55"), 2 / Zoom);
            var accent = new Pen(Theme.Brush("#90C5FF"), 1 / Zoom);
            for (int i = 0; i < 4; i++)
            { dc.DrawLine(shadow, points[i], points[(i + 1) % 4]); dc.DrawLine(accent, points[i], points[(i + 1) % 4]); }
        }
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
        if (BrushHud != null && BrushPoint is { } anchor)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (cachedBrushHudText == null || cachedBrushHud != BrushHud || cachedBrushHudDpi != dpi)
            {
                cachedBrushHud = BrushHud; cachedBrushHudDpi = dpi;
                cachedBrushHudText = new FormattedText(BrushHud, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, dpi);
            }
            var label = cachedBrushHudText;
            double x = Math.Clamp(origin.X + anchor.X * Zoom + 24, 4, Math.Max(4, ActualWidth - label.Width - 28));
            double y = Math.Clamp(origin.Y + anchor.Y * Zoom + 24, 26, Math.Max(26, ActualHeight - 40));
            dc.DrawRoundedRectangle(Theme.Surface, new Pen(Theme.Accent, 1), new Rect(x, y, label.Width + 22, 32), 5, 5);
            dc.DrawText(label, new Point(x + 11, y + 7));
        }
        dc.DrawRectangle(Theme.Header, null, new Rect(0, 0, ActualWidth, 22));
        // Use readable 1/2/5 intervals with at least 60 screen pixels between labels.
        double requestedStep = Math.Max(1, 60 / Zoom);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(requestedStep)));
        double normalizedStep = requestedStep / magnitude;
        double step = (normalizedStep <= 1 ? 1 : normalizedStep <= 2 ? 2 : normalizedStep <= 5 ? 5 : 10) * magnitude;
        double firstTick = Math.Ceiling(Math.Max(0, -origin.X / Zoom) / step) * step;
        double lastTick = Math.Min(Document.Width, (ActualWidth - origin.X) / Zoom);
        for (double x = firstTick; x <= lastTick; x += step)
        {
            double sx = origin.X + x * Zoom;
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
}
