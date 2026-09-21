using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace Compositor.Windows;

public static class ToolIcons
{
    public static FrameworkElement Create(Tool tool)
    {
        string path = tool switch
        {
            Tool.Move => "M4 3L18 12L11 13L8 20Z M13 14L18 20",
            Tool.RectangleSelect => "M3 3H8 M11 3H16 M19 3H21V8 M21 11V16 M21 19V21H16 M13 21H8 M5 21H3V16 M3 13V8",
            Tool.EllipseSelect => "M12 3C24 3 24 21 12 21C0 21 0 3 12 3Z",
            Tool.Crop => "M6 2V18H22 M2 6H18V22 M19 5L5 19",
            Tool.Brush => "M9 15L18 3Q22 1 21 5L13 17Z M9 15C4 13 7 22 2 21C10 23 13 20 12 17",
            Tool.Eraser => "M3 14L13 3L22 11L12 21H10Z M7 10L16 18 M12 21H23",
            Tool.Rectangle => "M3 4H21V20H3Z", Tool.Ellipse => "M12 3C24 3 24 21 12 21C0 21 0 3 12 3Z",
            Tool.Bucket => "M9 2L19 12L10 21L1 12L10 3 M3 12H18 M21 15Q16 22 21 22Q26 22 21 15",
            Tool.Gradient => "M3 4H21V20H3Z M6 5V19 M9 5V19 M12 5V19 M15 5V19",
            Tool.Text => "M3 6V3H21V6 M12 3V21 M8 21H16",
            Tool.Eyedropper => "M15 3L21 9 M17 2L22 7L9 20L3 21L4 15Z",
            Tool.Hand => "M6 12V7Q8 4 10 7V11 M10 7V4Q12 1 14 4V11 M14 6Q16 3 18 6V12 M18 9Q20 7 21 10V16Q21 23 13 22L8 21L2 14Q2 10 6 14Z",
            Tool.Lasso => "M8 17C-3 16 0 3 12 3C26 3 25 17 12 17Q5 16 7 22 M8 17Q12 20 14 16",
            Tool.PolygonLasso => "M4 5L20 3L16 19L9 16L3 21Z",
            Tool.MagicWand => "M3 21L17 7 M14 5L19 10 M6 2V8 M3 5H9 M21 14V20 M18 17H24",
            Tool.CloneStamp => "M9 14V10Q5 2 12 2Q19 2 15 10V14 M5 14H19L21 20H3Z M4 23H20",
            Tool.Heal => "M3 9L9 3Q12 1 15 4L21 10Q23 13 20 16L15 21Q12 23 9 20L3 14Q1 12 3 9Z M7 7L17 17 M9 13L10 12 M13 9L12 10",
            Tool.Smudge => "M3 18Q12 9 13 4Q15 1 17 4Q18 6 15 11Q24 10 20 17Q16 23 8 21 M3 22H10",
            Tool.Liquify => "M2 6C8 0 16 12 22 6 M2 12C8 6 16 18 22 12 M2 18C8 12 16 24 22 18",
            Tool.BlurBrush => "M12 2Q4 12 4 16A8 7 0 0 0 20 16Q20 12 12 2Z M8 16Q8 19 12 19",
            _ => "M4 4H20V20H4Z"
        };
        var drawing = new GeometryDrawing(null, new Pen(Theme.Text, 1.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
        return new Image { Source = new DrawingImage(drawing), Width = 19, Height = 19, Stretch = Stretch.Uniform };
    }
}
