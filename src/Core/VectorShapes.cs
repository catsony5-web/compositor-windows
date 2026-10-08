using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Stored by number in projects: append new kinds at the end. Line: an open or closed polyline or
// smooth curve through Points. Callout: a leader from Points[0] (anchor) over Points[1] (elbow) to
// Points[2] (label end) with Label beside it. Their Width/Height is the fitted layer surface.
public enum ShapeKind { Rectangle, Ellipse, Line, Callout }
public sealed record ShapeSpec
{
    public const double MaxMarkSize = 2048, MinDashScale = .1, MaxDashScale = 20;
    public ShapeKind Kind { get; init; }
    public int Width { get; init; } = 160;
    public int Height { get; init; } = 120;
    public uint FillArgb { get; init; } = 0xFFBCD9FA;
    public uint StrokeArgb { get; init; } = 0xFF20344F;
    public bool FillEnabled { get; init; } = true;
    public bool StrokeEnabled { get; init; }
    public double StrokeWidth { get; init; } = 2;
    public double CornerRadius { get; init; }
    // Line and callout geometry in layer pixels. Projects without these values read as before.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ShapePoints? Points { get; init; }
    // Line: a smooth curve through every point instead of straight segments.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Smooth { get; init; }
    // Line: the last point joins the first; the fill paints inside.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Closed { get; init; }
    // Stroke style for every kind: dash pattern (lengths follow the stroke width × DashScale) and line ends.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StrokeDash Dash { get; init; }
    public double DashScale { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StrokeCap Cap { get; init; }
    // Line: marks at the first and last point. Callout: StartMark at the anchor, EndMark at the label end.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public LineMark StartMark { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public LineMark EndMark { get; init; }
    public double MarkSize { get; init; } = 12;
    // Callout: the label text (the text engine with outline and paragraph box) and the leader's bend.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TextSpec? Label { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public CalloutLeader Leader { get; init; }
    [JsonIgnore] public bool HasPoints => Kind is ShapeKind.Line or ShapeKind.Callout;
    public void Validate()
    {
        Raster.ValidateSize(Width, Height);
        if (!Enum.IsDefined(Kind) || !double.IsFinite(StrokeWidth) || StrokeWidth < 0 || StrokeWidth > 512 || !double.IsFinite(CornerRadius) || CornerRadius < 0 || CornerRadius > 4096)
            throw new InvalidDataException("도형의 크기·선·모서리 값을 확인하세요.");
        if (!Enum.IsDefined(Dash) || !Enum.IsDefined(Cap) || !Enum.IsDefined(StartMark) || !Enum.IsDefined(EndMark) || !Enum.IsDefined(Leader) ||
            !double.IsFinite(DashScale) || DashScale < MinDashScale || DashScale > MaxDashScale || !double.IsFinite(MarkSize) || MarkSize < 1 || MarkSize > MaxMarkSize)
            throw new InvalidDataException("선 모양과 끝 모양 값을 확인하세요.");
        if (HasPoints) (Points ?? throw new InvalidDataException("선의 점 정보가 없습니다.")).Validate(Kind == ShapeKind.Callout ? 3 : Closed ? 3 : 2, Kind == ShapeKind.Callout ? 3 : ShapePoints.MaxCount);
        else if (Points != null) throw new InvalidDataException("사각형과 타원에는 점 정보가 없습니다.");
        if (Kind == ShapeKind.Callout) (Label ?? throw new InvalidDataException("지시선 라벨 정보가 없습니다.")).Validate();
        else if (Label != null) throw new InvalidDataException("라벨은 지시선에만 있습니다.");
    }
}

public static class VectorShapes
{
    public static uint Argb(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
    public static Color Color(uint value) => System.Windows.Media.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    // Lines and callouts: points are given relative to (x, y); the layer is fitted around what they draw.
    public static Layer Create(ShapeSpec spec, double x = 0, double y = 0)
    {
        if (spec.HasPoints) { (spec, var shift) = ShapeGeometry.Fit(spec); x -= shift.X; y -= shift.Y; }
        spec.Validate(); return new Layer { Kind = LayerKind.Shape, Shape = spec, Pixels = Render(spec), X = x, Y = y, Name = DefaultName(spec) };
    }
    /// <summary>Korean name of a new shape layer (translated by the UI).</summary>
    public static string DefaultName(ShapeSpec spec) => spec.Kind switch
    {
        ShapeKind.Ellipse => "타원", ShapeKind.Line when spec.Closed => "닫힌 선", ShapeKind.Line => spec.Smooth ? "매끄러운 곡선" : spec.Points?.Count > 2 ? "꺾은선" : "선",
        ShapeKind.Callout => "지시선", _ => "사각형"
    };
    public static Raster Render(ShapeSpec spec)
    {
        spec.Validate();
        // Lines, callouts and dashed outlines are drawn from their retained drawing (WPF, on the calling STA thread).
        if (ShapeGeometry.UsesDrawing(spec)) { var drawing = ShapeGeometry.Drawing(spec); return Imaging.Draw(spec.Width, spec.Height, dc => dc.DrawDrawing(drawing)); }
        var output = new Raster(spec.Width, spec.Height);
        var layer = new Layer { Kind = LayerKind.Shape, Shape = spec, Pixels = output };
        Composite(output, layer, default); return output;
    }
    public static void Update(Layer layer, ShapeSpec spec)
    {
        // Lines and callouts are fitted again: the surface follows the points, marks and label while
        // every point keeps its place on the canvas (the layer moves by the fitting shift).
        Vector shift = default;
        if (spec.HasPoints) (spec, shift) = ShapeGeometry.Fit(spec);
        spec.Validate(); if (layer.Shape == spec) return;
        var pixels = Render(spec); int oldWidth = layer.Pixels.Width, oldHeight = layer.Pixels.Height;
        byte[]? mask = layer.Mask;
        if (spec.HasPoints && layer.Shape?.HasPoints == true)
        {
            var before = layer.Matrix; var probe = new Point();
            if (mask != null)
            {
                // The mask stays on the same canvas pixels; uncovered new area shows.
                var moved = new byte[pixels.Width * pixels.Height];
                int dx = (int)Math.Round(shift.X), dy = (int)Math.Round(shift.Y);
                for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    moved[y * pixels.Width + x] = sx >= 0 && sy >= 0 && sx < oldWidth && sy < oldHeight ? mask[sy * oldWidth + sx] : (byte)255;
                }
                mask = moved;
            }
            layer.Shape = spec; layer.Pixels = pixels; layer.Mask = mask;
            var after = layer.Matrix.Transform(probe + shift); var target = before.Transform(probe);
            layer.X += target.X - after.X; layer.Y += target.Y - after.Y;
            return;
        }
        if (mask != null && (oldWidth != pixels.Width || oldHeight != pixels.Height))
        {
            var resized = new byte[pixels.Width * pixels.Height];
            for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
                resized[y * pixels.Width + x] = mask[Math.Min(oldHeight - 1, y * oldHeight / pixels.Height) * oldWidth + Math.Min(oldWidth - 1, x * oldWidth / pixels.Width)];
            mask = resized;
        }
        // Warp corners live in local coordinates. Keep their document-space positions.
        if (layer.Warp is { } warp && (oldWidth != pixels.Width || oldHeight != pixels.Height))
        {
            var old = layer.Matrix; var changed = layer.Snapshot(); changed.Pixels = pixels; var nextInverse = changed.Matrix; nextInverse.Invert();
            Point Map(Point p) => nextInverse.Transform(old.Transform(p));
            layer.Warp = new WarpQuad(Map(warp.TopLeft), Map(warp.TopRight), Map(warp.BottomRight), Map(warp.BottomLeft));
        }
        layer.Shape = spec; layer.Pixels = pixels; layer.Mask = mask;
    }
    static bool Contains(ShapeSpec shape, Point p, double inset)
    {
        double width = shape.Width - inset * 2, height = shape.Height - inset * 2, x = p.X - inset, y = p.Y - inset;
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height) return false;
        if (shape.Kind == ShapeKind.Ellipse) { double dx = (x - width / 2) / (width / 2), dy = (y - height / 2) / (height / 2); return dx * dx + dy * dy <= 1; }
        double radius = Math.Clamp(shape.CornerRadius - inset, 0, Math.Min(width, height) / 2);
        double cx = Math.Clamp(x, radius, width - radius), cy = Math.Clamp(y, radius, height - radius);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
    }
    // Sample the retained geometry directly at the destination scale. The pixel cache is
    // only for thumbnails, raster tools and backward-compatible image payloads.
    public static void Composite(Raster output, Layer layer, CancellationToken token, Matrix? transform = null)
    {
        var shape = layer.Shape!; var forward = transform ?? layer.Matrix;
        var corners = new[] { new Point(0, 0), new Point(shape.Width, 0), new Point(shape.Width, shape.Height), new Point(0, shape.Height) }.Select(p => forward.Transform(layer.Warp?.Forward(p, shape.Width, shape.Height) ?? p)).ToArray();
        int left = (int)Math.Clamp(Math.Floor(corners.Min(p => p.X)) - 1, 0, output.Width), right = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.X)) + 1, 0, output.Width);
        int top = (int)Math.Clamp(Math.Floor(corners.Min(p => p.Y)) - 1, 0, output.Height), bottom = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.Y)) + 1, 0, output.Height);
        var inverse = forward; inverse.Invert(); var warp = layer.Warp?.Map().Inverse();
        var fill = Color(shape.FillArgb); var stroke = Color(shape.StrokeArgb);
        Parallel.For(top, bottom, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (int x = left; x < right; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
                {
                    var p = inverse.Transform(new Point(x + (sx + .5) / 2, y + (sy + .5) / 2));
                    if (warp is { } projection) { p = projection.Transform(p); p = new Point(p.X * shape.Width, p.Y * shape.Height); }
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !Contains(shape, p, 0)) continue;
                    bool edge = shape.StrokeEnabled && shape.StrokeWidth > 0 && !Contains(shape, p, shape.StrokeWidth);
                    double sa = edge ? stroke.A / 255d : 0, fa = shape.FillEnabled ? fill.A / 255d * (1 - sa) : 0;
                    double weight = .25;
                    if (layer.Mask != null)
                    { int mx = Math.Clamp((int)p.X, 0, layer.Pixels.Width - 1), my = Math.Clamp((int)p.Y, 0, layer.Pixels.Height - 1); weight *= layer.Mask[my * layer.Pixels.Width + mx] / 255d; }
                    a += (fa + sa) * weight; r += (fill.R * fa + stroke.R * sa) / 255d * weight; g += (fill.G * fa + stroke.G * sa) / 255d * weight; b += (fill.B * fa + stroke.B * sa) / 255d * weight;
                }
                if (a > 0) Imaging.Over(output.Data, (y * output.Width + x) * 4, b / a, g / a, r / a, a * layer.Opacity, layer.Blend);
            }
        });
    }
}
