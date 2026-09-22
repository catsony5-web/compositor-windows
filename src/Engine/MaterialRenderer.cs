using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class MaterialRenderer
{
    static readonly ConditionalWeakTable<MaterialFill, DrawingGroup> drawings = new();
    public static DrawingGroup Drawing(MaterialFill fill) => drawings.GetValue(fill, f =>
    {
        var bitmap = f.Asset.Pixels.Bitmap(); bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(f.OffsetX, f.OffsetY, f.TileWidth, f.TileHeight),
            TileMode = TileMode.Tile, Stretch = Stretch.Fill, Transform = new RotateTransform(f.Angle)
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        var drawing = new DrawingGroup(); drawing.Children.Add(new GeometryDrawing(brush, null, f.Boundary.Geometry));
        drawing.Freeze(); return drawing;
    });
    public static Raster Render(MaterialFill fill) => Imaging.Draw(fill.Width, fill.Height, dc => dc.DrawDrawing(Drawing(fill)));
}
