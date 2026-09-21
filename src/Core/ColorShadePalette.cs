using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Related hue columns, with tints above and shades below an exact base color.</summary>
public static class ColorShadePalette
{
    public const int Columns = 9;
    public const int Rows = 7;
    public const int BaseIndex = (Rows / 2) * Columns + Columns / 2;

    public static Color[] Create(Color source)
    {
        var (h, s, v) = ColorValues.ToHsv(source);
        var colors = new Color[Columns * Rows];
        for (int column = 0; column < Columns; column++)
        {
            int offset = column - Columns / 2;
            // Keep neutral selections neutral instead of inventing an arbitrary hue.
            byte neutral = (byte)Math.Clamp(source.R + offset * 10, 0, 255);
            Color basis = offset == 0 ? source : s < .001
                ? Color.FromArgb(source.A, neutral, neutral, neutral)
                : ColorValues.FromHsv(h + offset * 10, s, v, source.A);
            for (int row = 0; row < Rows; row++)
            {
                double mix = row switch { 0 => .86, 1 => .62, 2 => .32, 4 => .23, 5 => .48, 6 => .74, _ => 0 };
                int target = row < Rows / 2 ? 255 : 0;
                byte Blend(byte channel) => (byte)Math.Round(channel + (target - channel) * mix);
                colors[row * Columns + column] = row == Rows / 2 ? basis
                    : Color.FromArgb(source.A, Blend(basis.R), Blend(basis.G), Blend(basis.B));
            }
        }
        return colors;
    }
}
