using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Compositor.Windows;

public static class VectorPdfExport
{
    public static string? Limitation(Document doc) => doc.Layers.Any(l => l.Visible &&
        (l.Mask != null || l.Clipped || l.Warp != null || l.Kind == LayerKind.Adjustment || l.Blend != BlendMode.Normal || l.Opacity != 1))
        ? "마스크·클리핑 레이어·원근·조정·혼합·불투명도 효과가 있는 문서는 합성 PDF로 내보내거나 효과를 먼저 병합해 주세요."
        : null;
    public static void Write(Document doc, Stream output, CancellationToken token = default)
    {
        doc.Validate(); if (Limitation(doc) is { } limitation) throw new NotSupportedException(limitation);
        using var pdf = new PdfDocument(); var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(doc.Width * 72d / doc.Dpi); page.Height = XUnit.FromPoint(doc.Height * 72d / doc.Dpi);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            graphics.ScaleTransform(72d / doc.Dpi);
            graphics.IntersectClip(new XRect(0, 0, doc.Width, doc.Height));
            var children = doc.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
            void Layer(Layer layer)
            {
                token.ThrowIfCancellationRequested(); if (!layer.Visible) return;
                var saved = graphics.Save(); Transform(graphics, layer.Matrix);
                graphics.IntersectClip(new XRect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
                if (layer.Kind == LayerKind.Group) foreach (var child in children.GetValueOrDefault(layer.Id) ?? []) Layer(child);
                else if (layer.Vector is { Format: VectorFormat.Pdf } vector)
                {
                    using var input = vector.Open(); using var form = XPdfForm.FromStream(input); form.PageNumber = vector.Page;
                    graphics.DrawImage(form, 0, 0, vector.Width, vector.Height);
                }
                else if (layer.Vector != null) Drawing(graphics, layer.Vector.Drawing, token);
                else if (layer.Kind == LayerKind.Text) Drawing(graphics, DesignRenderer.TextDrawing(layer.Text!), token);
                else if (layer.Kind == LayerKind.Shape) Drawing(graphics, ShapeDrawing(layer.Shape!), token);
                else { using var input = new MemoryStream(); layer.Pixels.WritePng(input); input.Position = 0; using var image = XImage.FromStream(input); graphics.DrawImage(image, 0, 0, layer.Pixels.Width, layer.Pixels.Height); }
                graphics.Restore(saved);
            }
            foreach (var layer in doc.Layers.Where(l => l.ParentId == null)) Layer(layer);
        }
        pdf.Save(output, false);
    }
    static void Transform(XGraphics graphics, Matrix matrix) => graphics.MultiplyTransform(new XMatrix(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY), XMatrixOrder.Prepend);
    static XColor Color(System.Windows.Media.Color color) => XColor.FromArgb(color.A, color.R, color.G, color.B);
    static void Drawing(XGraphics graphics, System.Windows.Media.Drawing drawing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (drawing)
        {
            case DrawingGroup group:
                var state = graphics.Save(); Transform(graphics, group.Transform?.Value ?? Matrix.Identity);
                if (group.ClipGeometry != null) graphics.IntersectClip(Path(group.ClipGeometry));
                foreach (var child in group.Children) Drawing(graphics, child, token);
                graphics.Restore(state); break;
            case GeometryDrawing geometry:
                var path = Path(geometry.Geometry);
                XBrush? brush = geometry.Brush is SolidColorBrush fill ? new XSolidBrush(Color(fill.Color)) : null;
                XPen? pen = geometry.Pen?.Brush is SolidColorBrush stroke ? new XPen(Color(stroke.Color), geometry.Pen.Thickness) : null;
                if (brush != null && pen != null) graphics.DrawPath(pen, brush, path);
                else if (brush != null) graphics.DrawPath(brush, path);
                else if (pen != null) graphics.DrawPath(pen, path);
                break;
            case GlyphRunDrawing glyph:
                if (glyph.ForegroundBrush is SolidColorBrush ink) graphics.DrawPath(new XSolidBrush(Color(ink.Color)), Path(glyph.GlyphRun.BuildGeometry()));
                break;
            case ImageDrawing image when image.ImageSource is BitmapSource bitmap:
                using (var encoded = new MemoryStream())
                {
                    Raster.FromBitmap(bitmap).WritePng(encoded); encoded.Position = 0; using var raster = XImage.FromStream(encoded);
                    graphics.DrawImage(raster, image.Rect.X, image.Rect.Y, image.Rect.Width, image.Rect.Height);
                }
                break;
            default: throw new NotSupportedException("이 벡터 그리기 종류는 PDF 내보내기를 지원하지 않습니다.");
        }
    }
    static XGraphicsPath Path(Geometry geometry)
    {
        var source = PathGeometry.CreateFromGeometry(geometry);
        // Arc-only geometries are approximated as paths, never embedded as bitmaps.
        // Cubic glyph and ellipse curves keep their original Bézier control points.
        if (source.Figures.Any(f => f.Segments.Any(s => s is ArcSegment))) source = source.GetFlattenedPathGeometry(.00001, ToleranceType.Relative);
        var matrix = source.Transform?.Value ?? Matrix.Identity; XPoint P(Point p) { p = matrix.Transform(p); return new(p.X, p.Y); }
        var path = new XGraphicsPath { FillMode = source.FillRule == FillRule.Nonzero ? XFillMode.Winding : XFillMode.Alternate };
        foreach (var figure in source.Figures)
        {
            path.StartFigure(); var current = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line: path.AddLine(P(current), P(line.Point)); current = line.Point; break;
                    case PolyLineSegment lines: foreach (var p in lines.Points) { path.AddLine(P(current), P(p)); current = p; } break;
                    case BezierSegment bezier: path.AddBezier(P(current), P(bezier.Point1), P(bezier.Point2), P(bezier.Point3)); current = bezier.Point3; break;
                    case PolyBezierSegment beziers:
                        for (int i = 0; i < beziers.Points.Count; i += 3) { path.AddBezier(P(current), P(beziers.Points[i]), P(beziers.Points[i + 1]), P(beziers.Points[i + 2])); current = beziers.Points[i + 2]; } break;
                    case QuadraticBezierSegment quadratic:
                        path.AddBezier(P(current), P(current + (quadratic.Point1 - current) * (2d / 3)), P(quadratic.Point2 + (quadratic.Point1 - quadratic.Point2) * (2d / 3)), P(quadratic.Point2)); current = quadratic.Point2; break;
                    case PolyQuadraticBezierSegment quadratics:
                        for (int i = 0; i < quadratics.Points.Count; i += 2) { var q = quadratics.Points[i]; var end = quadratics.Points[i + 1]; path.AddBezier(P(current), P(current + (q - current) * (2d / 3)), P(end + (q - end) * (2d / 3)), P(end)); current = end; } break;
                    default: throw new NotSupportedException("지원하지 않는 벡터 경로입니다.");
                }
            }
            if (figure.IsClosed) path.CloseFigure();
        }
        return path;
    }
    internal static DrawingGroup ShapeDrawing(ShapeSpec shape)
    {
        Geometry Outline(double inset)
        {
            var rect = new Rect(inset, inset, Math.Max(.001, shape.Width - inset * 2), Math.Max(.001, shape.Height - inset * 2));
            return shape.Kind == ShapeKind.Ellipse ? new EllipseGeometry(rect) : new RectangleGeometry(rect, Math.Max(0, shape.CornerRadius - inset), Math.Max(0, shape.CornerRadius - inset));
        }
        var drawing = new DrawingGroup(); using (var dc = drawing.Open())
        {
            if (shape.FillEnabled) dc.DrawGeometry(new SolidColorBrush(VectorShapes.Color(shape.FillArgb)), null, Outline(0));
            if (shape.StrokeEnabled && shape.StrokeWidth > 0) dc.DrawGeometry(null, new Pen(new SolidColorBrush(VectorShapes.Color(shape.StrokeArgb)), shape.StrokeWidth), Outline(shape.StrokeWidth / 2));
        }
        drawing.Freeze(); return drawing;
    }
}
