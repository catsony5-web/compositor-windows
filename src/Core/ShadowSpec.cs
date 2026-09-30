using System.IO;

namespace Compositor.Windows;

// Realistic shadows soften with distance; a shape shadow is the crisp cast form.
public enum ShadowStyle { Realistic, Shape }
// Drop: offset copy (cut-outs lifted off a board). Plan: a top-down footprint
// swept by its height (site plans). Ground: a standing silhouette laid on the floor.
public enum ShadowProjection { Drop, Plan, Ground }

/// <summary>
/// Editable parameters of a generated shadow layer. The layer itself stays an ordinary
/// raster (older readers and layered exports see its rendered pixels); this record lets
/// the shadow be edited again or regenerated after its source layers change.
/// Lengths are in the pixels of the layer's parent space (the document for top-level layers).
/// </summary>
public sealed record ShadowSpec
{
    public const int MaxSources = 512;
    public const double MaxLength = 10_000, MaxSoftness = 500, MinElevation = 5, MaxElevation = 85;
    public ShadowStyle Style { get; init; }
    public ShadowProjection Projection { get; init; }
    // Direction the shadow falls on screen: 0° right, 90° down, clockwise.
    public double Angle { get; init; } = 45;
    // Drop: offset of the shadow from its object.
    public double Distance { get; init; } = 16;
    // Plan: height of the drawn objects, swept to Height / tan(Elevation).
    public double Height { get; init; } = 60;
    // Sun height above the horizon (Plan, Ground). Lower suns cast longer shadows.
    public double Elevation { get; init; } = 40;
    // Blur at the far end of the shadow; contact points stay sharp (Realistic only).
    public double Softness { get; init; } = 20;
    public double Opacity { get; init; } = .55;
    public uint ColorArgb { get; init; } = 0xFF1E232B;
    // Shape style: draw only the edge of the cast form.
    public bool OutlineOnly { get; init; }
    public double OutlineWidth { get; init; } = 2;
    // Treat closed outlines (line-only drawings) as filled footprints.
    public bool FillClosed { get; init; }
    public Guid[] Sources { get; init; } = [];

    // Plan length in parent-space pixels, or the Ground length as a multiple of the object's height.
    public double PlanLength() => Math.Min(MaxLength * 2, Height / Math.Tan(Elevation * Math.PI / 180));
    public double GroundRatio() => 1 / Math.Tan(Elevation * Math.PI / 180);

    public static ShadowSpec Default(ShadowProjection projection, ShadowStyle style = ShadowStyle.Realistic)
    {
        var spec = projection switch
        {
            ShadowProjection.Plan => new ShadowSpec { Projection = projection, Angle = 315, Elevation = 35, Height = 60, Softness = 24, Opacity = .45 },
            ShadowProjection.Ground => new ShadowSpec { Projection = projection, Angle = 330, Elevation = 40, Softness = 22, Opacity = .5 },
            _ => new ShadowSpec { Projection = projection }
        };
        return style == ShadowStyle.Shape ? spec with { Style = style, ColorArgb = 0xFF3B4A63, Opacity = .5 } : spec;
    }

    public void Validate()
    {
        static bool In(double n, double low, double high) => double.IsFinite(n) && n >= low && n <= high;
        if (!Enum.IsDefined(Style) || !Enum.IsDefined(Projection) || !In(Angle, -360, 360) || !In(Distance, 0, MaxLength) || !In(Height, 0, MaxLength) ||
            !In(Elevation, MinElevation, MaxElevation) || !In(Softness, 0, MaxSoftness) || !In(Opacity, 0, 1) || !In(OutlineWidth, .5, 50))
            throw new InvalidDataException("그림자 속성이 올바르지 않습니다.");
        if (Sources == null || Sources.Length is < 1 or > MaxSources || Sources.Contains(Guid.Empty) || Sources.Distinct().Count() != Sources.Length)
            throw new InvalidDataException("그림자의 원본 레이어 정보가 올바르지 않습니다.");
    }

    // A shadow is a generated raster layer that never lists itself as its own source.
    internal static void ValidateLayer(Layer layer)
    {
        if (layer.Shadow is not { } shadow) return;
        if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("그림자 정보는 이미지 레이어에만 저장할 수 있습니다.");
        shadow.Validate();
        if (shadow.Sources.Contains(layer.Id)) throw new InvalidDataException("그림자 레이어가 자기 자신을 원본으로 가리킵니다.");
    }

    public bool Equals(ShadowSpec? other) => other is not null && Style == other.Style && Projection == other.Projection && Angle.Equals(other.Angle) &&
        Distance.Equals(other.Distance) && Height.Equals(other.Height) && Elevation.Equals(other.Elevation) && Softness.Equals(other.Softness) &&
        Opacity.Equals(other.Opacity) && ColorArgb == other.ColorArgb && OutlineOnly == other.OutlineOnly && OutlineWidth.Equals(other.OutlineWidth) &&
        FillClosed == other.FillClosed && (ReferenceEquals(Sources, other.Sources) || Sources != null && other.Sources != null && Sources.SequenceEqual(other.Sources));

    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Style, Projection, Angle, Distance, Height, Elevation, Softness, Opacity),
        HashCode.Combine(ColorArgb, OutlineOnly, OutlineWidth, FillClosed, Sources?.Length ?? -1));
}
