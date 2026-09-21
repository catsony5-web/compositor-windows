using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Demo
{
    public static Document Create()
    {
        var doc = new Document { Name = "고요한 풍경 · 샘플", Width = 1280, Height = 800 };
        doc.Add(new Layer { Name = "01 · 새벽 하늘", Pixels = Imaging.Draw(1280, 800, dc =>
        {
            var gradient = new LinearGradientBrush(); gradient.StartPoint = new Point(0, 0); gradient.EndPoint = new Point(0, 1);
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#102F38"), 0)); gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#719C92"), .62)); gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#D8C9A0"), 1));
            dc.DrawRectangle(gradient, null, new Rect(0, 0, 1280, 800));
        }) });
        doc.Add(new Layer { Name = "02 · 달", X = 858, Y = 130, Pixels = Imaging.Draw(146, 146, dc => dc.DrawEllipse(Theme.Brush("#F0E4C6"), null, new Point(73, 73), 71, 71)) });
        doc.Add(new Layer { Name = "03 · 먼 능선", Pixels = Imaging.Draw(1280, 800, dc =>
        {
            Polygon(dc, "#5A827C", new Point(0, 520), new Point(200, 390), new Point(290, 432), new Point(476, 285), new Point(685, 457), new Point(850, 363), new Point(1028, 458), new Point(1184, 341), new Point(1280, 445), new Point(1280, 800), new Point(0, 800));
            Polygon(dc, "#95B2A0", new Point(476, 285), new Point(404, 345), new Point(455, 330), new Point(490, 351), new Point(517, 340));
        }) });
        doc.Add(new Layer { Name = "04 · 산과 호수", Pixels = Imaging.Draw(1280, 800, dc =>
        {
            Polygon(dc, "#315C58", new Point(0, 562), new Point(212, 477), new Point(403, 522), new Point(625, 426), new Point(903, 555), new Point(1060, 489), new Point(1280, 537), new Point(1280, 800), new Point(0, 800));
            dc.DrawRectangle(new LinearGradientBrush(Theme.Brush("#7FA598").Color, Theme.Brush("#21484B").Color, 90), null, new Rect(0, 586, 1280, 214));
            var random = new Random(27);
            for (int i = 0; i < 65; i++) { double y = 599 + random.Next(188), x = random.Next(1280); dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(45, 199, 219, 191)), 1), new Point(x, y), new Point(x + random.Next(12, 95), y)); }
            Polygon(dc, "#163D3C", new Point(0, 660), new Point(198, 592), new Point(310, 620), new Point(385, 701), new Point(650, 800), new Point(0, 800));
            Polygon(dc, "#123435", new Point(910, 800), new Point(1110, 697), new Point(1280, 679), new Point(1280, 800));
        }) });
        doc.Add(new Layer { Name = "05 · 타이포그래피", Pixels = Imaging.Draw(1280, 800, dc =>
        {
            Text(dc, "F I E L D   N O T E S     /     0 1", 67, 61, 17, "#DCEAD6", "Segoe UI");
            Text(dc, "고요한 풍경", 62, 113, 66, "#F3EEDA", "Malgun Gothic");
            Text(dc, "A little space to create.", 68, 218, 23, "#D3E0CE", "Segoe UI");
            dc.DrawLine(new Pen(Theme.Brush("#DAE3CF"), 1), new Point(68, 705), new Point(1212, 705));
            Text(dc, "COMPOSITOR     /     MADE ON WINDOWS", 68, 727, 14, "#E5E8D4", "Segoe UI");
            Text(dc, "레이어를 선택하고, 나만의 장면을 만들어보세요.", 820, 725, 14, "#D3DECB", "Malgun Gothic");
        }) });
        doc.ActiveId = doc.Layers[1].Id; return doc;
    }
    static void Text(DrawingContext dc, string text, double x, double y, double size, string color, string font)
        => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(font), size, Theme.Brush(color), 1), new Point(x, y));
    static void Polygon(DrawingContext dc, string color, params Point[] points)
    {
        var geometry = new StreamGeometry(); using (var g = geometry.Open()) { g.BeginFigure(points[0], true, true); g.PolyLineTo(points.Skip(1).ToArray(), true, false); } geometry.Freeze(); dc.DrawGeometry(Theme.Brush(color), null, geometry);
    }
}
