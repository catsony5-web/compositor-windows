using System.Windows.Media;

namespace Compositor.Windows;

public enum ColorHarmonyKind { Complementary, Analogous, Triadic }

public static class ColorHarmony
{
    // Hue relationships are a starting point for a palette, independent of output profile.
    public static Color[] Create(Color color, ColorHarmonyKind kind, double achromaticHue = 210)
    {
        var (h, s, v) = ColorValues.ToHsv(color);
        if (s < .001) h = achromaticHue;
        // White/black still get useful colored companions while retaining the exact base chip.
        double companionS = s < .001 ? .32 : s;
        double companionV = Math.Max(.32, v);
        Color At(double offset, double saturation, double value) => ColorValues.FromHsv(h + offset, saturation, value, color.A);
        return kind switch
        {
            ColorHarmonyKind.Complementary => [color, At(180, companionS, companionV), At(180, companionS * .45, Math.Max(.86, companionV))],
            ColorHarmonyKind.Analogous => [At(-30, companionS, companionV), color, At(30, companionS, companionV)],
            ColorHarmonyKind.Triadic => [color, At(120, companionS, companionV), At(240, companionS, companionV)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
