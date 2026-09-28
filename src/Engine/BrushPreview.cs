using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Paints a sample S-stroke with the real brush engine so panel previews match what the canvas draws.</summary>
public static class BrushPreview
{
    /// <summary>Maps a document brush diameter to a readable preview diameter; larger brushes stay larger.</summary>
    public static double DisplayDiameter(double diameter, int height)
    {
        double d = double.IsFinite(diameter) ? Math.Max(1, diameter) : 1;
        return Math.Clamp(3 + Math.Sqrt(d) * 2.1, 3, height * .72);
    }

    public static Raster Stroke(BrushTip tip, double diameter, double hardness, double spacing, double angle, int width, int height, Color? color = null)
    {
        width = Math.Clamp(width, 16, 2048); height = Math.Clamp(height, 8, 512);
        var layer = new Layer { Pixels = new Raster(width, height) };
        double d = DisplayDiameter(diameter, height);
        var stroke = new BrushStroke(layer, null, color ?? Colors.White, d, hardness, 1, false, false, tip, spacing, angle);
        double margin = d / 2 + 3, span = width - margin * 2, amplitude = Math.Max(0, (height - d) / 2 - 3);
        int steps = Math.Max(24, (int)(span / 2));
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            stroke.Point(new Point(margin + span * t, height / 2d - Math.Sin(t * Math.PI * 2) * amplitude));
        }
        return layer.Pixels;
    }
}
