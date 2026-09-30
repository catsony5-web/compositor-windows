using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class MaterialRenderer
{
    static readonly ConditionalWeakTable<MaterialFill, DrawingGroup> drawings = new();

    // The fill as a tiled brush inside its boundary. An image material repeats its stored pixels; a
    // built-in hatch pattern is redrawn from its geometry at deviceScale (device px per layer px), so
    // lines stay crisp when zoomed and exported. Pattern tiles are cached; the drawing itself is not.
    public static DrawingGroup Drawing(MaterialFill fill, double deviceScale = 1, CancellationToken token = default)
    {
        if (!HatchPatterns.TryGet(fill.Asset, out var pattern)) return drawings.GetValue(fill, f => Group(f, f.Asset.Pixels.Bitmap()));
        var (width, height) = HatchPatternRenderer.TileSize(fill.TileWidth, fill.TileHeight, deviceScale);
        double penScale = width / fill.TileWidth * fill.LineWeight;
        return Group(fill, HatchPatternRenderer.Tile(pattern, width, height, penScale, fill.Ink, token));
    }

    static DrawingGroup Group(MaterialFill f, System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        if (!bitmap.IsFrozen) bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(f.OffsetX, f.OffsetY, f.TileWidth, f.TileHeight),
            TileMode = TileMode.Tile, Stretch = Stretch.Fill, Transform = new RotateTransform(f.Angle)
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        var drawing = new DrawingGroup(); drawing.Children.Add(new GeometryDrawing(brush, null, f.Boundary.Geometry));
        drawing.Freeze(); return drawing;
    }

    // Document resolution: layer pixels and exports at scale 1 draw patterns from vectors at that resolution.
    public static Raster Render(MaterialFill fill) => Imaging.Draw(fill.Width, fill.Height, dc => dc.DrawDrawing(Drawing(fill)));
}
