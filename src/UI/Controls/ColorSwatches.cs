using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class ColorSwatches : Grid
{
    readonly Button front, back;
    public ColorSwatches(Action editForeground, Action editBackground, Action swap, Action reset)
    {
        Width = 76; Height = 102; Margin = new Thickness(2, 8, 2, 8);
        back = Swatch(editBackground, "배경색 선택", 29, 26);
        front = Swatch(editForeground, "전경색 선택", 7, 7);
        Children.Add(back); Children.Add(front);
        var exchange = Theme.Button("⇄", swap, "전경색 / 배경색 교환 · X");
        exchange.Width = 25; exchange.Height = 24; exchange.Padding = new Thickness(0); exchange.Margin = new Thickness(47, 0, 0, 0); exchange.HorizontalAlignment = HorizontalAlignment.Left; exchange.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(exchange);
        var defaults = Theme.Button("◩", reset, "기본색: 검정 / 흰색 · D");
        defaults.Width = 23; defaults.Height = 23; defaults.Padding = new Thickness(0); defaults.Margin = new Thickness(3, 46, 0, 0); defaults.HorizontalAlignment = HorizontalAlignment.Left; defaults.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(defaults);
        Children.Add(new TextBlock { Text = "전경 / 배경", FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) });
    }
    static Button Swatch(Action click, string name, double x, double y)
    {
        var b = Theme.Button("", click, name); b.Width = b.Height = 37; b.Padding = new Thickness(0); b.BorderBrush = Theme.Brush("#B8BFCA");
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
