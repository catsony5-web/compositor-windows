using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class MaterialRenderer
{
    static readonly ConditionalWeakTable<MaterialFill, DrawingGroup> drawings = new();
    static readonly ConditionalWeakTable<MaterialFill, BitmapSource> thumbnails = new();

    // The fill as a tiled brush inside its boundary. An image material repeats its stored pixels; a
    // built-in hatch pattern is redrawn from its geometry at deviceScale (device px per layer px) as a
    // tile of whole device pixels, so lines stay crisp when zoomed and exported. A repeat too large for
    // a tile bitmap is drawn as vector marks over `visible` (layer px; the whole fill when omitted).
    // Pattern tiles are cached; the drawing itself is not.
    public static DrawingGroup Drawing(MaterialFill fill, double deviceScale = 1, CancellationToken token = default, Rect? visible = null)
    {
        if (!HatchPatterns.TryGet(fill.Asset, out var pattern)) return drawings.GetValue(fill, f => Group(f, f.Asset.Pixels.Bitmap()));
        if (HatchPatternRenderer.NeedsVector(fill.TileWidth, fill.TileHeight, deviceScale) && Vector(fill, pattern, visible, token) is { } vector) return vector;
        var (width, height) = HatchPatternRenderer.TileSize(fill.TileWidth, fill.TileHeight, deviceScale);
        double penScale = width / fill.TileWidth * fill.LineWeight;
        return Group(fill, HatchPatternRenderer.Tile(pattern, width, height, penScale, fill.Ink, token));
    }

    static DrawingGroup? Vector(MaterialFill fill, HatchPattern pattern, Rect? visible, CancellationToken token)
    {
        var boundary = fill.Boundary.Geometry; var region = boundary.Bounds;
        if (visible is { } shown) region.Intersect(shown);
        var group = new DrawingGroup { ClipGeometry = boundary };
        if (!region.IsEmpty && region.Width > 0 && region.Height > 0)
        {
            // The brush rotates about the layer origin; the marks are laid out in the unrotated space.
            var rotate = new RotateTransform(fill.Angle); var back = new RotateTransform(-fill.Angle);
            var marks = HatchPatternRenderer.VectorMarks(pattern, new Rect(fill.OffsetX, fill.OffsetY, fill.TileWidth, fill.TileHeight), fill.LineWeight, fill.Ink, back.TransformBounds(region), token);
            if (marks == null) return null;
            var turned = new DrawingGroup { Transform = rotate }; turned.Children.Add(marks); group.Children.Add(turned);
        }
        group.Freeze(); return group;
    }

    static DrawingGroup Group(MaterialFill f, BitmapSource bitmap)
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

    // Layer-list thumbnail of a hatch pattern fill: its boundary on paper, filled with the pattern at a
    // density that stays readable at icon size (one 40 px repeat), with a faint outline. Null for images.
    public static BitmapSource? PatternThumbnail(MaterialFill fill, int size = 56)
    {
        if (!HatchPatterns.TryGet(fill.Asset, out var pattern)) return null;
        return thumbnails.GetValue(fill, f =>
        {
            double scale = size / (double)Math.Max(1, Math.Max(f.Width, f.Height));
            double dx = (size - f.Width * scale) / 2, dy = (size - f.Height * scale) / 2;
            var shape = f.Boundary.Geometry.Clone(); shape.Transform = new MatrixTransform(scale, 0, 0, scale, dx, dy); shape.Freeze();
            var tile = HatchPatternRenderer.Tile(pattern, 40, 40, 2.6 * f.LineWeight, f.Ink);
            var brush = new ImageBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 40, 40), Stretch = Stretch.Fill }; brush.Freeze();
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x60, 0x60, 0x60)), 1); outline.Freeze();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) { dc.DrawGeometry(Brushes.White, null, shape); dc.DrawGeometry(brush, outline, shape); }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            return bitmap;
        });
    }
}
