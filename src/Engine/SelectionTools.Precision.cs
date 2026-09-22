using System.Windows;

namespace Compositor.Windows;

public static partial class SelectionTools
{
    // Add only the immediately adjacent mixed-color edge. These soft samples are
    // not flood seeds, so antialiasing cannot open a path through a thin line.
    static void RefineWandEdges(Raster image, byte[] mask, int seed, double tolerance, CancellationToken token)
    {
        int w = image.Width, h = image.Height;
        double Distance(int i) => ColorDistance(image.Data, seed * 4, i * 4);
        for (int y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; if (mask[i] != 0) continue;
                bool neighbor = x > 0 && mask[i - 1] == 255 || x + 1 < w && mask[i + 1] == 255 || y > 0 && mask[i - w] == 255 || y + 1 < h && mask[i + w] == 255;
                if (!neighbor) continue;
                double distance = Distance(i), contrast = distance;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                        if (mask[yy * w + xx] != 255) contrast = Math.Max(contrast, Distance(yy * w + xx));
                if (contrast <= tolerance + 8 || distance >= contrast) continue;
                mask[i] = (byte)Math.Clamp((int)Math.Round(255 * (1 - distance / contrast)), 0, 254);
            }
        }
    }

    internal static double Density(Selection? selection) => selection?.CoverageBounds is { } area
        ? Math.Max(selection.CanvasWidth / area.Width, selection.CanvasHeight / area.Height) : 1;

    static Selection CombinePrecise(Selection? current, Selection incoming, int width, int height, SelectionCombine mode, CancellationToken token)
    {
        if (current == null || current.Bounds.Width <= 0 || current.Bounds.Height <= 0)
            return mode == SelectionCombine.Add ? incoming : new Selection(new Rect(0, 0, 0, 0));
        var area = current.Bounds;
        if (mode == SelectionCombine.Add) area.Union(incoming.Bounds);
        else if (mode == SelectionCombine.Intersect) area.Intersect(incoming.Bounds);
        area.Intersect(new Rect(0, 0, width, height));
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return new Selection(new Rect(0, 0, 0, 0));
        double density = PrecisionWand.FitDensity(area, Math.Max(Density(current), Density(incoming)));
        var grid = PrecisionWand.Grid(area, density, width, height);
        var mask = new byte[checked(grid.Width * grid.Height)];
        for (int y = 0; y < grid.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < grid.Width; x++)
            {
                double xx = grid.Area.X + (x + .5) / density, yy = grid.Area.Y + (y + .5) / density;
                double a = current.Weight(xx, yy), b = incoming.Weight(xx, yy);
                mask[y * grid.Width + x] = Imaging.Byte(255 * (mode switch
                {
                    SelectionCombine.Add => Math.Max(a, b), SelectionCombine.Subtract => a * (1 - b),
                    SelectionCombine.Intersect => Math.Min(a, b), _ => throw new ArgumentOutOfRangeException(nameof(mode))
                }));
            }
        }
        return FromMask(mask, grid.Width, grid.Height, grid.Area);
    }
}
