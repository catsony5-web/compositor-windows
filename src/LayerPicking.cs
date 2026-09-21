using System.Windows;

namespace Compositor.Windows;

public static class LayerPicking
{
    const double MinimumEffectiveAlpha = 1.0 / 255;

    public static Layer? Pick(Document doc, Point documentPoint)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!double.IsFinite(documentPoint.X) || !double.IsFinite(documentPoint.Y) ||
            documentPoint.X < 0 || documentPoint.Y < 0 ||
            documentPoint.X >= doc.Width || documentPoint.Y >= doc.Height)
            return null;

        for (int layerIndex = doc.Layers.Count - 1; layerIndex >= 0; layerIndex--)
        {
            var layer = doc.Layers[layerIndex];
            if (!layer.Visible || layer.Opacity <= 0) continue;

            var inverse = layer.Matrix;
            inverse.Invert();
            var local = inverse.Transform(documentPoint);
            var pixels = layer.Pixels;
            if (local.X < 0 || local.Y < 0 || local.X >= pixels.Width || local.Y >= pixels.Height) continue;

            double sampleX = Math.Clamp(local.X - .5, 0, pixels.Width - 1);
            double sampleY = Math.Clamp(local.Y - .5, 0, pixels.Height - 1);
            int x0 = (int)sampleX, y0 = (int)sampleY;
            int x1 = Math.Min(x0 + 1, pixels.Width - 1), y1 = Math.Min(y0 + 1, pixels.Height - 1);
            double fractionX = sampleX - x0, fractionY = sampleY - y0;

            double alpha =
                Alpha(layer, x0, y0) * (1 - fractionX) * (1 - fractionY) +
                Alpha(layer, x1, y0) * fractionX * (1 - fractionY) +
                Alpha(layer, x0, y1) * (1 - fractionX) * fractionY +
                Alpha(layer, x1, y1) * fractionX * fractionY;

            if (alpha * layer.Opacity > MinimumEffectiveAlpha) return layer;
        }

        return null;
    }

    static double Alpha(Layer layer, int x, int y)
    {
        int pixelIndex = y * layer.Pixels.Width + x;
        double alpha = layer.Pixels.Data[pixelIndex * 4 + 3] / 255.0;
        return layer.Mask == null ? alpha : alpha * layer.Mask[pixelIndex] / 255.0;
    }
}
