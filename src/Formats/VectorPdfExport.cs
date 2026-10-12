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
        (l.Mask != null || l.Clipped || l.Warp != null || l.Kind == LayerKind.Adjustment || l.Blend != BlendMode.Normal || l.Opacity != 1 ||
            LayerEffects.Any(l) && !DrawsEffectsAsVectors(l)))
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
                if (layer.Kind != LayerKind.Group && LayerEffects.Any(layer))
                {
                    var reach = LayerEffectRenderer.LocalBounds(layer); graphics.IntersectClip(new XRect(reach.X, reach.Y, reach.Width, reach.Height));
                    DrawStrokeEffects(graphics, layer, true, token);
                    var inner = graphics.Save(); graphics.IntersectClip(new XRect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
                    DrawContent(graphics, layer, token); graphics.Restore(inner);
                    DrawStrokeEffects(graphics, layer, false, token);
                    graphics.Restore(saved); return;
                }
                graphics.IntersectClip(new XRect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
                if (layer.Kind == LayerKind.Group) foreach (var child in children.GetValueOrDefault(layer.Id) ?? []) Layer(child);
                else DrawContent(graphics, layer, token);
                graphics.Restore(saved);
            }
            foreach (var layer in doc.Layers.Where(l => l.ParentId == null)) Layer(layer);
        }
        pdf.Save(output, false);
    }
    // One layer's own content in its local coordinates: PDF vectors, paths, glyph outlines or its pixels.
    internal static void DrawContent(XGraphics graphics, Layer layer, CancellationToken token)
    {
        if (layer.Vector is { Format: VectorFormat.Pdf } vector)
        {
            using var input = vector.Open(); using var form = XPdfForm.FromStream(input); form.PageNumber = vector.Page;
            graphics.DrawImage(form, 0, 0, vector.Width, vector.Height);
        }
        else if (layer.Vector != null) Drawing(graphics, layer.Vector.Drawing, token);
        else if (layer.Kind == LayerKind.Text) Drawing(graphics, DesignRenderer.TextDrawing(layer.Text!), token);
        else if (layer.Kind == LayerKind.Shape) Drawing(graphics, ShapeDrawing(layer.Shape!), token);
        else { using var input = new MemoryStream(); layer.Pixels.WritePng(input); input.Position = 0; using var image = XImage.FromStream(input); graphics.DrawImage(image, 0, 0, layer.Pixels.Width, layer.Pixels.Height); }
    }
    internal static void Transform(XGraphics graphics, Matrix matrix) => graphics.MultiplyTransform(new XMatrix(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY), XMatrixOrder.Prepend);
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
                XPen? pen = geometry.Pen?.Brush is SolidColorBrush stroke ? PdfPen(geometry.Pen, Color(stroke.Color)) : null;
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
    // ---- Layer effects as vectors ------------------------------------------------------------

    /// <summary>
    /// Text and shapes whose effects are only strokes in opaque colors keep those strokes as vectors:
    /// the outline of the layer's drawing widened by the stroke (round or square corners).
    /// Anything else with effects is placed as an image of its visible result.
    /// </summary>
    internal static bool DrawsEffectsAsVectors(Layer layer)
    {
        var effects = LayerEffects.Active(layer);
        if (effects.Length == 0 || layer.Kind is not (LayerKind.Text or LayerKind.Shape) || layer.Mask != null || layer.Warp != null) return false;
        if (!effects.All(e => e.Kind == LayerEffectKind.Stroke && e.Blend == BlendMode.Normal && e.ColorArgb >> 24 == 255)) return false;
        // An outside stroke runs under the layer; translucent content would show it through.
        return layer.Kind == LayerKind.Text ? layer.Text!.ColorArgb >> 24 == 255 && (!layer.Text.Outline || layer.Text.OutlineArgb >> 24 == 255)
            : (!layer.Shape!.FillEnabled || layer.Shape.FillArgb >> 24 is 0 or 255) && (!layer.Shape.StrokeEnabled || layer.Shape.StrokeArgb >> 24 is 0 or 255);
    }

    /// <summary>
    /// Draws the stroke effects of <paramref name="layer"/> in its local coordinates, in list order: the outside
    /// strokes (which go under the layer) when <paramref name="under"/>, otherwise the centre and inside strokes.
    /// </summary>
    internal static void DrawStrokeEffects(XGraphics graphics, Layer layer, bool under, CancellationToken token)
    {
        var strokes = LayerEffects.Active(layer).Where(e => (e.Position == StrokePosition.Outside) == under).ToArray();
        if (strokes.Length == 0) return;
        var outline = Coverage(layer.Kind == LayerKind.Text ? DesignRenderer.TextDrawing(layer.Text!) : ShapeDrawing(layer.Shape!));
        if (outline == null) return;
        foreach (var stroke in strokes)
        {
            token.ThrowIfCancellationRequested();
            var color = DocumentFeatures.Color(stroke.ColorArgb);
            var brush = new XSolidBrush(XColor.FromArgb(Imaging.Byte(255 * stroke.Opacity), color.R, color.G, color.B));
            var pen = new Pen(Brushes.Black, stroke.Position == StrokePosition.Center ? stroke.Size : stroke.Size * 2)
            { LineJoin = stroke.Rounded ? PenLineJoin.Round : PenLineJoin.Miter, MiterLimit = 1.5 };
            var band = outline.GetWidenedPathGeometry(pen);
            if (stroke.Position == StrokePosition.Inside)
            {
                var state = graphics.Save(); graphics.IntersectClip(Path(outline));
                graphics.DrawPath(brush, Path(band)); graphics.Restore(state);
            }
            else graphics.DrawPath(brush, Path(band));
        }
    }

    // The area a drawing covers (fills, pens and glyphs) as one outline.
    static Geometry? Coverage(Drawing drawing)
    {
        Geometry? union = null;
        void Add(Geometry geometry, Matrix transform)
        {
            var copy = geometry.CloneCurrentValue();
            var own = copy.Transform?.Value ?? Matrix.Identity; own.Append(transform); copy.Transform = new MatrixTransform(own);
            union = union == null ? copy : Geometry.Combine(union, copy, GeometryCombineMode.Union, null);
        }
        void Walk(Drawing node, Matrix transform)
        {
            switch (node)
            {
                case DrawingGroup group:
                    var inner = group.Transform?.Value ?? Matrix.Identity; inner.Append(transform);
                    foreach (var child in group.Children) Walk(child, inner);
                    break;
                case GeometryDrawing shape:
                    if (shape.Brush != null) Add(shape.Geometry, transform);
                    if (shape.Pen is { Thickness: > 0 } pen) Add(shape.Geometry.GetWidenedPathGeometry(pen), transform);
                    break;
                case GlyphRunDrawing glyphs: Add(glyphs.GlyphRun.BuildGeometry(), transform); break;
            }
        }
        Walk(drawing, Matrix.Identity);
        return union;
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
    // Line ends, joins and dash patterns stay stroke properties in the PDF (both count dashes in pen widths).
    static XPen PdfPen(Pen source, XColor color)
    {
        var pen = new XPen(color, source.Thickness);
        if (source.StartLineCap == source.EndLineCap && source.StartLineCap != PenLineCap.Flat)
            pen.LineCap = source.StartLineCap == PenLineCap.Round ? XLineCap.Round : XLineCap.Square;
        // Default pens (drawings, solid shape outlines) keep the writer's defaults: flat ends, miter joins.
        if (source.LineJoin != PenLineJoin.Miter) pen.LineJoin = source.LineJoin == PenLineJoin.Round ? XLineJoin.Round : XLineJoin.Bevel;
        else if (source.MiterLimit != 10) pen.MiterLimit = source.MiterLimit;
        if (source.DashStyle is { Dashes.Count: > 0 } dash && !ReferenceEquals(source.DashStyle, DashStyles.Solid))
        {
            // A zero-length dash with round or square ends draws a dot, as on the canvas.
            pen.DashStyle = XDashStyle.Custom; pen.DashPattern = dash.Dashes.ToArray(); pen.DashOffset = dash.Offset;
        }
        return pen;
    }
    internal static DrawingGroup ShapeDrawing(ShapeSpec shape)
    {
        if (ShapeGeometry.UsesDrawing(shape)) return ShapeGeometry.Drawing(shape);
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
