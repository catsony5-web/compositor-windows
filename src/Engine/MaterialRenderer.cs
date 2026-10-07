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
    // The user's line pattern is recolored and resampled to whole device pixels the same way. A line
    // pattern's background color, when set, fills the region under the lines.
    // Screentones are tiles too, except black poché (the boundary filled with the ink) and the gradient
    // screentones, which are evaluated over the region per device pixel (ToneGradientRenderer); `device`
    // (layer px → device px, defaults to deviceScale) lets their pixels land on device pixels exactly.
    // Pattern tiles are cached; the drawing itself is not.
    public static DrawingGroup Drawing(MaterialFill fill, double deviceScale = 1, CancellationToken token = default, Rect? visible = null, Matrix? device = null)
    {
        if (LinePatterns.IsCustom(fill.Asset))
        {
            var (w, h) = HatchPatternRenderer.TileSize(fill.TileWidth, fill.TileHeight, deviceScale);
            return Group(fill, LinePatternRenderer.Tile(fill.Asset, w, h, fill.LineWeight, fill.Ink, token), true);
        }
        if (!HatchPatterns.TryGet(fill.Asset, out var pattern)) return drawings.GetValue(fill, f => Group(f, f.Asset.Pixels.Bitmap(), false));
        if (pattern == HatchPattern.SolidBlack) return Solid(fill);
        if (HatchPatterns.IsGradient(pattern))
        {
            if (!double.IsFinite(deviceScale) || deviceScale <= 0) deviceScale = 1;
            return ToneGradientRenderer.Drawing(fill, pattern, device ?? new Matrix(deviceScale, 0, 0, deviceScale, 0, 0), visible, token);
        }
        if (HatchPatternRenderer.NeedsVector(fill.TileWidth, fill.TileHeight, deviceScale) && Vector(fill, pattern, visible, token) is { } vector) return vector;
        var (width, height) = HatchPatternRenderer.TileSize(fill.TileWidth, fill.TileHeight, deviceScale);
        double penScale = HatchPatternRenderer.PenScale(pattern, width, fill.TileWidth, fill.LineWeight);
        return Group(fill, HatchPatternRenderer.Tile(pattern, width, height, penScale, fill.Ink, token), true);
    }

    // Black poché: the region filled with the ink (over the background color, which shows through a translucent ink).
    static DrawingGroup Solid(MaterialFill fill)
    {
        var group = new DrawingGroup();
        if (Background(fill) is { } paper) group.Children.Add(new GeometryDrawing(paper, null, fill.Boundary.Geometry));
        group.Children.Add(new GeometryDrawing(InkBrush(fill.Ink), null, fill.Boundary.Geometry));
        group.Freeze(); return group;
    }

    static SolidColorBrush InkBrush(uint ink)
    {
        var brush = new SolidColorBrush(VectorShapes.Color(ink == 0 ? HatchPatterns.DefaultInk : ink)); brush.Freeze();
        return brush;
    }

    // The background brush of a line pattern fill, or null when it has none (transparent, as before).
    internal static Brush? Background(MaterialFill fill)
    {
        if (fill.Background >> 24 == 0 || !LinePatterns.IsPattern(fill.Asset)) return null;
        var brush = new SolidColorBrush(VectorShapes.Color(fill.Background)); brush.Freeze();
        return brush;
    }

    static DrawingGroup? Vector(MaterialFill fill, HatchPattern pattern, Rect? visible, CancellationToken token)
    {
        var boundary = fill.Boundary.Geometry; var region = boundary.Bounds;
        if (visible is { } shown) region.Intersect(shown);
        var group = new DrawingGroup { ClipGeometry = boundary };
        if (Background(fill) is { } paper) group.Children.Add(new GeometryDrawing(paper, null, boundary));
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

    static DrawingGroup Group(MaterialFill f, BitmapSource bitmap, bool pattern)
    {
        if (!bitmap.IsFrozen) bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(f.OffsetX, f.OffsetY, f.TileWidth, f.TileHeight),
            TileMode = TileMode.Tile, Stretch = Stretch.Fill, Transform = new RotateTransform(f.Angle)
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        var drawing = new DrawingGroup();
        if (pattern && Background(f) is { } paper) drawing.Children.Add(new GeometryDrawing(paper, null, f.Boundary.Geometry));
        drawing.Children.Add(new GeometryDrawing(brush, null, f.Boundary.Geometry));
        drawing.Freeze(); return drawing;
    }

    // Document resolution: layer pixels and exports at scale 1 draw patterns from vectors at that resolution.
    public static Raster Render(MaterialFill fill) => Imaging.Draw(fill.Width, fill.Height, dc => dc.DrawDrawing(Drawing(fill)));

    // Layer-list thumbnail of a line pattern fill: its boundary on paper (or its background color),
    // filled with the pattern at a density that stays readable at icon size (one 40 px repeat), with a
    // faint outline. A gradient screentone shows its density ramp across the boundary, black poché its
    // ink. Null for images.
    public static BitmapSource? PatternThumbnail(MaterialFill fill, int size = 56)
    {
        bool custom = LinePatterns.IsCustom(fill.Asset);
        if (!custom && !HatchPatterns.TryGet(fill.Asset, out _)) return null;
        return thumbnails.GetValue(fill, f =>
        {
            double scale = size / (double)Math.Max(1, Math.Max(f.Width, f.Height));
            double dx = (size - f.Width * scale) / 2, dy = (size - f.Height * scale) / 2;
            // The boundary keeps its own placement (a region stored in document pixels is moved into the layer by it).
            var placement = f.Boundary.Geometry.Transform?.Value ?? Matrix.Identity; placement.Append(new Matrix(scale, 0, 0, scale, dx, dy));
            var shape = f.Boundary.Geometry.Clone(); shape.Transform = new MatrixTransform(placement); shape.Freeze();
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x60, 0x60, 0x60)), 1); outline.Freeze();
            HatchPatterns.TryGet(f.Asset, out var p);
            // Black poché and gradients are drawn as themselves at icon scale (a gradient's marks average into its ramp).
            bool itself = !custom && (p == HatchPattern.SolidBlack || HatchPatterns.IsGradient(p));
            Brush? brush = null;
            if (!itself)
            {
                // A user's tile repeats a little larger when its lines are thin for its size (RepeatScale); a screentone
                // repeats over 80 px, so its fine dots and lines still read when the row shows the icon at half size.
                int tileWidth = custom ? Math.Clamp((int)Math.Round(40 * LinePatternRenderer.RepeatScale(f.Asset)), 40, 120) : HatchPatterns.IsScreentone(p) ? 80 : 40;
                int tileHeight = custom ? Math.Clamp((int)Math.Round(tileWidth * MaterialEditing.Aspect(f.Asset)), 4, 240) : tileWidth;
                var tile = custom ? LinePatternRenderer.Tile(f.Asset, tileWidth, tileHeight, f.LineWeight, f.Ink)
                    : HatchPatternRenderer.Tile(p, tileWidth, tileHeight, HatchPatternRenderer.SwatchPen(p, 2.6, f.LineWeight), f.Ink);
                brush = new ImageBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, tileWidth, tileHeight), Stretch = Stretch.Fill };
                brush.Freeze();
            }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawGeometry(Brushes.White, null, shape);
                if (Background(f) is { } paper) dc.DrawGeometry(paper, null, shape);
                if (itself)
                {
                    var device = new Matrix(scale, 0, 0, scale, dx, dy); var plain = f with { Background = 0 };
                    dc.PushTransform(new MatrixTransform(device));
                    dc.DrawDrawing(p == HatchPattern.SolidBlack ? Solid(plain) : ToneGradientRenderer.Drawing(plain, p, device, null));
                    dc.Pop();
                }
                dc.DrawGeometry(brush, outline, shape);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            return bitmap;
        });
    }
}
