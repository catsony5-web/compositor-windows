using System.Windows;

namespace Compositor.Windows;

/// <summary>Samples retained drawing sources, extending the search beyond the viewport until the region closes.</summary>
public static class PrecisionWand
{
    internal const int MaxSamples = 16_777_216;
    public static Selection Select(Document document, Point seed, double tolerance, bool contiguous, bool antialias, bool design, double displayScale, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(seed.X + seed.Y) || seed.X < 0 || seed.Y < 0 || seed.X >= document.Width || seed.Y >= document.Height)
            return new Selection(new Rect(0, 0, 0, 0));
        if (!design || !DesignRenderer.HasRetainedContent(document))
            return SelectionTools.MagicWand(Imaging.Render(document, token), seed, tolerance, contiguous, token, antialias);
        if (!double.IsFinite(displayScale) || displayScale <= 0) throw new ArgumentOutOfRangeException(nameof(displayScale));
        double requested = 4;
        while (requested < displayScale && requested < 64) requested *= 2;
        var canvas = new Rect(0, 0, document.Width, document.Height);
        double edge = 512 / requested;
        var area = contiguous ? new Rect(seed.X - edge / 2, seed.Y - edge / 2, edge, edge) : canvas;
        area.Intersect(canvas);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            token.ThrowIfCancellationRequested();
            double density = FitDensity(area, requested);
            var grid = Grid(area, density, document.Width, document.Height); area = grid.Area;
            // Large full-canvas selections retain at least native pixel resolution.
            if ((long)grid.Width * grid.Height > MaxSamples)
                return SelectionTools.MagicWand(DesignRenderer.RenderOutput(document, token), seed, tolerance, contiguous, token, antialias);
            var image = DesignRenderer.Render(document, area, grid.Width, grid.Height, token);
            var local = new Point((seed.X - area.X) * density, (seed.Y - area.Y) * density);
            var region = SelectionTools.MagicWand(image, local, tolerance, contiguous, token, antialias);
            var mask = region.Coverage!;
            bool left = false, right = false, top = false, bottom = false;
            if (contiguous)
            {
                for (int y = 0; y < grid.Height; y++) { left |= mask[y * grid.Width] > 0; right |= mask[y * grid.Width + grid.Width - 1] > 0; }
                for (int x = 0; x < grid.Width; x++) { top |= mask[x] > 0; bottom |= mask[(grid.Height - 1) * grid.Width + x] > 0; }
                left &= area.Left > 0; right &= area.Right < document.Width;
                top &= area.Top > 0; bottom &= area.Bottom < document.Height;
            }
            if (!left && !right && !top && !bottom) return SelectionTools.FromMask(mask, grid.Width, grid.Height, area);
            // Never mistake the edge of a rendered crop for a drawing boundary.
            double x0 = left ? Math.Max(0, area.Left - area.Width) : area.Left;
            double x1 = right ? Math.Min(document.Width, area.Right + area.Width) : area.Right;
            double y0 = top ? Math.Max(0, area.Top - area.Height) : area.Top;
            double y1 = bottom ? Math.Min(document.Height, area.Bottom + area.Height) : area.Bottom;
            area = new Rect(x0, y0, x1 - x0, y1 - y0);
        }
        throw new InvalidOperationException("선택 영역 계산이 너무 복잡합니다. 허용 오차를 줄여 다시 선택하세요.");
    }

    internal static double FitDensity(Rect area, double requested)
    {
        double density = Math.Clamp(requested, 1, 64);
        while (density > 1 && ((Math.Ceiling(area.Width * density) + 2) * (Math.Ceiling(area.Height * density) + 2) > MaxSamples ||
            Math.Ceiling(area.Width * density) + 2 > Raster.MaxDimension || Math.Ceiling(area.Height * density) + 2 > Raster.MaxDimension)) density = Math.Max(1, density / 2);
        return density;
    }

    internal static (Rect Area, int Width, int Height) Grid(Rect area, double density, int canvasWidth, int canvasHeight)
    {
        double left = Math.Max(0, Math.Floor(area.Left * density)), top = Math.Max(0, Math.Floor(area.Top * density));
        double right = Math.Min(canvasWidth * density, Math.Ceiling(area.Right * density)), bottom = Math.Min(canvasHeight * density, Math.Ceiling(area.Bottom * density));
        int width = Math.Max(1, checked((int)(right - left))), height = Math.Max(1, checked((int)(bottom - top)));
        return (new Rect(left / density, top / density, width / density, height / density), width, height);
    }
}
