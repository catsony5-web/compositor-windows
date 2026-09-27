using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Border whose content is clipped to its rounded corners, so the canvas and other
// full-bleed children follow the card shape without a bitmap effect.
public sealed class ClipBorder : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateClip();
        return size;
    }

    void UpdateClip()
    {
        if (Child is not UIElement child || ActualWidth <= 0 || ActualHeight <= 0) return;
        var inset = BorderThickness;
        double width = Math.Max(0, ActualWidth - inset.Left - inset.Right), height = Math.Max(0, ActualHeight - inset.Top - inset.Bottom);
        double radius = Math.Max(0, CornerRadius.TopLeft - Math.Max(inset.Left, inset.Top) / 2);
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        clip.Freeze(); child.Clip = clip;
    }
}
