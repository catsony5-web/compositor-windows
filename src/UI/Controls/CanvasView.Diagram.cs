using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public enum DiagramHandleKind { Point, Anchor, Elbow, Label, Draft }

/// <summary>A point handle of the selected line or callout, in document pixels.</summary>
public readonly record struct DiagramHandle(Point Position, DiagramHandleKind Kind, bool Selected, bool Hot);

// Overlay of the 선 · 곡선 and 지시선 tools: the styled preview of the line being drawn (or of a point
// being dragged) and the point handles of the selected line or callout, drawn at screen size.
public sealed partial class CanvasView
{
    /// <summary>Preview drawing in its own coordinates; DiagramPreviewTransform maps it to document pixels.</summary>
    public Drawing? DiagramPreview { get; set; }
    public Matrix DiagramPreviewTransform { get; set; } = Matrix.Identity;
    public IReadOnlyList<DiagramHandle>? DiagramHandles { get; set; }
    /// <summary>The first point of the line being drawn when a click there would close it.</summary>
    public Point? DiagramCloseTarget { get; set; }

    void DrawDiagram(DrawingContext dc)
    {
        if (DiagramPreview is { } preview)
        {
            dc.PushTransform(new MatrixTransform(DiagramPreviewTransform)); dc.DrawDrawing(preview); dc.Pop();
        }
        var handles = DiagramHandles ?? [];
        if (handles.Count == 0 && DiagramCloseTarget == null) return;
        double unit = 1 / Math.Max(.0001, Zoom);
        var outline = new Pen(Theme.Accent, 1.25 * unit); var shadow = new Pen(Theme.Brush("#303E55"), 3 * unit);
        if (DiagramCloseTarget is { } close)
        {
            dc.DrawEllipse(null, shadow, close, 9 * unit, 9 * unit); dc.DrawEllipse(null, outline, close, 9 * unit, 9 * unit);
        }
        foreach (var handle in handles)
        {
            double r = (handle.Hot ? 5.5 : 4.5) * unit; var p = handle.Position;
            Brush fill = handle.Selected ? Theme.Accent : Theme.Panel;
            Geometry shape = handle.Kind switch
            {
                DiagramHandleKind.Anchor => new EllipseGeometry(p, r, r),
                DiagramHandleKind.Elbow => Diamond(p, r * 1.25),
                DiagramHandleKind.Draft => new EllipseGeometry(p, r * .75, r * .75),
                _ => new RectangleGeometry(new Rect(p.X - r, p.Y - r, 2 * r, 2 * r))
            };
            dc.DrawGeometry(null, shadow, shape); dc.DrawGeometry(fill, outline, shape);
        }
        static Geometry Diamond(Point p, double r)
        {
            var figure = new PathFigure { StartPoint = new Point(p.X, p.Y - r), IsClosed = true };
            figure.Segments.Add(new PolyLineSegment([new Point(p.X + r, p.Y), new Point(p.X, p.Y + r), new Point(p.X - r, p.Y)], true));
            var path = new PathGeometry(); path.Figures.Add(figure); return path;
        }
    }
}
