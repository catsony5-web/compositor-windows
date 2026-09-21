using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    void ClearPixels() => PixelFill(true);
    void Fill() => PixelFill(false);
    void FillBackground() => PixelFill(false, backgroundColor);

    void PixelFill(bool erase, Color? fillColor = null) => EditLayer(erase ? "픽셀 지우기" : fillColor == null ? "전경색 채우기" : "배경색 채우기", layer =>
    {
        var color = fillColor ?? foreground;
        bool editingMask = maskEditing && layer.Mask != null;
        if (!editingMask && layer.Kind is LayerKind.Group or LayerKind.Adjustment)
            throw new InvalidOperationException("픽셀 레이어 또는 편집할 마스크를 선택하세요.");
        if (!editingMask && !erase && color.A == 0) return;

        var mask = editingMask ? (byte[])layer.Mask!.Clone() : null;
        var pixels = editingMask ? layer.Pixels : layer.Pixels.Clone();
        // An unrestricted fill needs no coordinate conversion. For a selection,
        // capture transforms once instead of rebuilding matrices per pixel.
        var chain = new List<(Matrix Matrix, ProjectiveMap? Warp, int Width, int Height)>();
        if (selection != null)
            foreach (var entry in new[] { layer }.Concat(Parents(doc, layer)))
                chain.Add((entry.Matrix, entry.Warp?.Map(), entry.Pixels.Width, entry.Pixels.Height));
        Point ToDocument(Point point)
        {
            foreach (var step in chain)
            {
                if (step.Warp is { } warp) point = warp.Transform(new Point(point.X / step.Width, point.Y / step.Height));
                point = step.Matrix.Transform(point);
            }
            return point;
        }
        bool changed = false;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
        {
            double weight = 1;
            if (selection != null) { var p = ToDocument(new Point(x + .5, y + .5)); weight = selection.Weight(p.X, p.Y); }
            if (weight <= 0) continue;
            int index = y * pixels.Width + x, i = index * 4;
            if (mask != null)
            {
                double target = erase ? 0 : .2126 * color.R + .7152 * color.G + .0722 * color.B;
                byte next = Imaging.Byte(mask[index] * (1 - weight) + target * weight);
                changed |= mask[index] != next; mask[index] = next;
            }
            else if (erase)
            {
                byte next = Imaging.Byte(pixels.Data[i + 3] * (1 - weight));
                changed |= pixels.Data[i + 3] != next; pixels.Data[i + 3] = next;
            }
            else
            {
                byte b = pixels.Data[i], g = pixels.Data[i + 1], r = pixels.Data[i + 2], a = pixels.Data[i + 3];
                if (weight == 1 && color.A == 255)
                { pixels.Data[i] = color.B; pixels.Data[i + 1] = color.G; pixels.Data[i + 2] = color.R; pixels.Data[i + 3] = 255; }
                else Imaging.Over(pixels.Data, i, color.B / 255.0, color.G / 255.0, color.R / 255.0, weight * color.A / 255.0);
                changed |= b != pixels.Data[i] || g != pixels.Data[i + 1] || r != pixels.Data[i + 2] || a != pixels.Data[i + 3];
            }
        }
        if (!changed) return;
        if (mask != null) layer.Mask = mask;
        else { DocumentFeatures.Rasterize(layer); layer.Pixels = pixels; }
    });
}
