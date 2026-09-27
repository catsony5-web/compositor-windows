using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class ColorSwatches : Grid
{
    readonly Button front, back;
    public ColorSwatches(Action editForeground, Action editBackground, Action swap, Action reset)
    {
        Width = 70; Height = 74; Margin = new Thickness(2, 8, 2, 10);
        back = Swatch(editBackground, "배경색 선택", 28, 26);
        front = Swatch(editForeground, "전경색 선택", 8, 6);
        Children.Add(back); Children.Add(front);
        var exchange = Theme.IconButton(Theme.Glyphs.Swap, swap, "전경색 / 배경색 교환 · X", 22, 13);
        exchange.Margin = new Thickness(44, 2, 0, 0); exchange.HorizontalAlignment = HorizontalAlignment.Left; exchange.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(exchange);
        var defaults = Theme.IconButton(Theme.Glyphs.Reset, reset, "기본색: 검정 / 흰색 · D", 22, 12);
        defaults.Margin = new Thickness(2, 50, 0, 0); defaults.HorizontalAlignment = HorizontalAlignment.Left; defaults.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(defaults);
    }
    static Button Swatch(Action click, string name, double x, double y)
    {
        var b = Theme.Button("", click, name); b.Width = b.Height = 32; b.MinHeight = 0; b.Padding = new Thickness(0);
        b.BorderBrush = Theme.Brush("#C9D0DA"); b.BorderThickness = new Thickness(1.5);
        b.HorizontalAlignment = HorizontalAlignment.Left; b.VerticalAlignment = VerticalAlignment.Top; b.Margin = new Thickness(x, y, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(b, name); return b;
    }
    public void SetColors(Color foreground, Color background)
    {
        front.Background = new SolidColorBrush(foreground); back.Background = new SolidColorBrush(background);
        front.ToolTip = $"전경색 #{foreground.R:X2}{foreground.G:X2}{foreground.B:X2} · 클릭하여 선택";
        back.ToolTip = $"배경색 #{background.R:X2}{background.G:X2}{background.B:X2} · 클릭하여 선택";
    }
}
