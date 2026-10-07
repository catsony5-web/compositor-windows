using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Letter outline (글자 외곽선) around a laid-out text drawing. The outline is a filled geometry
/// built from the glyph outlines, so the canvas, raster exports, .psd layers and the vector PDF
/// all paint the same shape. Outside: everything within the width of the letters' edges outside
/// their shapes. Center: half the width on each side of the edges, painted over the fill.
/// </summary>
internal static class TextOutline
{
    // Flattening error in layer pixels; small enough for exports drawn at 8× the layer size.
    const double Tolerance = .02;

    internal static (DrawingGroup Drawing, int Width, int Height) Apply(TextSpec spec, (DrawingGroup Drawing, int Width, int Height) layout)
    {
        double reach = spec.OutlineExtent;
        int width = (int)Math.Ceiling(layout.Width + 2 * reach), height = (int)Math.Ceiling(layout.Height + 2 * reach);
        Raster.ValidateSize(width, height);
        var letters = Letters(layout.Drawing);
        bool outside = spec.OutlinePosition == TextOutlinePosition.Outside, fill = spec.DrawsFill;
        var pen = new Pen(Brushes.Black, outside ? spec.OutlineWidth * 2 : spec.OutlineWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        Geometry band = letters.GetWidenedPathGeometry(pen, Tolerance, ToleranceType.Absolute);
        // An opaque fill covers the inner half of an outside outline, so the whole band goes under it
        // and the letter edges blend into the outline without a seam. Hollow or translucent letters
        // keep only the part outside their shapes.
        if (outside && !(fill && spec.ColorArgb >> 24 == 255))
            band = Geometry.Combine(band, letters, GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
        var brush = new SolidColorBrush(DocumentFeatures.Color(spec.OutlineArgb)); brush.Freeze();
        var outline = new GeometryDrawing(brush, null, band);
        var result = new DrawingGroup { Transform = new TranslateTransform(reach, reach) };
        if (outside) result.Children.Add(outline);
        if (fill) result.Children.Add(layout.Drawing);
        if (!outside) result.Children.Add(outline);
        result.Freeze();
        return (result, width, height);
    }

    // The glyph outlines of a drawing, in its own coordinates.
    internal static GeometryGroup Letters(Drawing drawing)
    {
        var letters = new GeometryGroup { FillRule = FillRule.Nonzero };
        void Collect(Drawing item, Matrix transform)
        {
            switch (item)
            {
                case GlyphRunDrawing glyph:
                    var outline = glyph.GlyphRun.BuildGeometry();
                    if (!transform.IsIdentity) outline = new GeometryGroup { Children = { outline }, Transform = new MatrixTransform(transform) };
                    letters.Children.Add(outline); break;
                case DrawingGroup group:
                    var inner = group.Transform?.Value ?? Matrix.Identity; inner.Append(transform);
                    foreach (var child in group.Children) Collect(child, inner);
                    break;
            }
        }
        Collect(drawing, Matrix.Identity);
        letters.Freeze();
        return letters;
    }
}
