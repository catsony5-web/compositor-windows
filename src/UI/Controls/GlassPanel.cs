using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Compositor.Windows;

// Reflections are confined to the UI surface; image pixels are never blurred or tinted.
public class GlassPanel : Border
{
    public bool ShowReflection { get; set; }
    public GlassPanel()
    {
        CornerRadius = new CornerRadius(8); BorderThickness = new Thickness(1);
        BorderBrush = Theme.Line;
        Background = Theme.Panel;
        // Do not rasterize text-bearing children through a bitmap shadow effect.
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!ShowReflection) return;
        var rect = new Rect(RenderSize); if (rect.Width < 2 || rect.Height < 2) return;
        dc.PushClip(new RectangleGeometry(rect, 14, 14));
        var reflection = new StreamGeometry();
        using (var g = reflection.Open()) { g.BeginFigure(new Point(0, 0), true, true); g.LineTo(new Point(rect.Width, 0), true, false); g.LineTo(new Point(rect.Width, rect.Height * .14), true, false); g.LineTo(new Point(0, rect.Height * .52), true, false); }
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(10, 255, 255, 255), Colors.Transparent, 90), null, reflection);
        dc.DrawLine(new Pen(new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Color.FromArgb(155, 246, 250, 255), .5), new(Colors.Transparent, 1) }, 0), 1), new Point(18, 1), new Point(rect.Width - 18, 1));
        dc.Pop();
    }
}
