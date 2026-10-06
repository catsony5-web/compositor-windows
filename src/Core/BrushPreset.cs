using System.Text.RegularExpressions;

namespace Compositor.Windows;

/// <summary>
/// A saved brush (내 프리셋): shape, size, hardness, opacity, spacing and tip rotation. The brush,
/// eraser, mask painting and retouch brushes share these settings, so a preset applies to all of
/// them and does not record a tool. Opacity, spacing and hardness are fractions (1 = 100%).
/// </summary>
public sealed record BrushPreset(Guid Id, string Name, string TipId, double Size, double Hardness, double Opacity, double Spacing, double Angle)
{
    public const double MinSize = 1, MaxSize = 1000, MinOpacity = .01, MinSpacing = .01, MaxSpacing = 1.5, MaxAngle = 180;
    public const int MaxNameLength = 40;
    static readonly Regex tipId = new("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Trims, removes control characters and limits the length; an empty name becomes <paramref name="fallback"/>.</summary>
    public static string CleanName(string? name, string fallback = "브러시 프리셋")
    {
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? fallback : clean;
    }

    public static bool IsValidTipId(string? id) => id != null && tipId.IsMatch(id);

    /// <summary>
    /// The preset with a clean name and every value inside the panel's ranges, or null when it
    /// cannot be used (no ID, an invalid shape ID or a value that is not a finite number).
    /// </summary>
    public static BrushPreset? Normalize(BrushPreset? preset)
    {
        if (preset == null || preset.Id == Guid.Empty || !IsValidTipId(preset.TipId)) return null;
        double[] values = [preset.Size, preset.Hardness, preset.Opacity, preset.Spacing, preset.Angle];
        if (values.Any(v => !double.IsFinite(v))) return null;
        return preset with
        {
            Name = CleanName(preset.Name),
            Size = Math.Clamp(preset.Size, MinSize, MaxSize),
            Hardness = Math.Clamp(preset.Hardness, 0, 1),
            Opacity = Math.Clamp(preset.Opacity, MinOpacity, 1),
            Spacing = Math.Clamp(preset.Spacing, MinSpacing, MaxSpacing),
            Angle = Math.Clamp(preset.Angle, -MaxAngle, MaxAngle)
        };
    }

    /// <summary>
    /// Same settings as the panel shows them: whole pixels, whole percent and whole degrees.
    /// The name and ID are ignored.
    /// </summary>
    public bool SameSettings(BrushPreset other) =>
        TipId == other.TipId && Math.Abs(Size - other.Size) < .5 && Math.Abs(Hardness - other.Hardness) < .005
        && Math.Abs(Opacity - other.Opacity) < .005 && Math.Abs(Spacing - other.Spacing) < .005 && Math.Abs(Angle - other.Angle) < .5;
}
