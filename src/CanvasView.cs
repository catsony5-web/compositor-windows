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
        if (Selection != null) DrawSelection(dc, Selection.Bounds, Selection.Ellipse);
        if (GestureBounds != null) DrawSelection(dc, GestureBounds.Value, EllipseGesture);
        dc.Pop();
        if (ShowLayerBounds && Document.Active is { } layer)
        {
            var m = layer.Matrix;
            var points = new[] { new Point(0, 0), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }.Select(m.Transform).ToArray();
            var pen = new Pen(Theme.Accent, 1 / Zoom);
            for (int i = 0; i < 4; i++) { dc.DrawLine(pen, points[i], points[(i + 1) % 4]); dc.DrawRectangle(Theme.Accent, null, new Rect(points[i].X - 3 / Zoom, points[i].Y - 3 / Zoom, 6 / Zoom, 6 / Zoom)); }
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
        var white = new Pen(Brushes.White, 1.5 / Zoom); var black = new Pen(Brushes.Black, 1 / Zoom) { DashStyle = new DashStyle([4, 4], 0) };
        Geometry shape = ellipse ? new EllipseGeometry(bounds) : new RectangleGeometry(bounds);
        dc.DrawGeometry(null, white, shape); dc.DrawGeometry(null, black, shape);
    }
}
