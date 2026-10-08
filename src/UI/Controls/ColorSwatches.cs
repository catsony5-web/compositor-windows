using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class ColorSwatches : Grid
{
    readonly Button front, back, exchange, defaults;
    readonly double scale;
    /// <summary><paramref name="scale"/> enlarges the whole overlapped pair (toolbar 1, color panel header 1.5).</summary>
    public ColorSwatches(Action editForeground, Action editBackground, Action swap, Action reset, double scale = 1)
    {
        this.scale = scale;
        Width = 70 * scale; Height = 74 * scale; Margin = new Thickness(2, 8, 2, 10);
        back = Swatch(editBackground, "배경색 선택", 28 * scale, 26 * scale, 32 * scale);
        front = Swatch(editForeground, "전경색 선택", 8 * scale, 6 * scale, 32 * scale);
        Children.Add(back); Children.Add(front);
        exchange = Theme.IconButton(Theme.Glyphs.Swap, swap, "전경색 / 배경색 교환 · X", 22, 13);
        exchange.Margin = new Thickness(44 * scale, 2, 0, 0); exchange.HorizontalAlignment = HorizontalAlignment.Left; exchange.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(exchange);
        defaults = Theme.IconButton(Theme.Glyphs.Reset, reset, "기본색: 검정 / 흰색 · D", 22, 12);
        defaults.Margin = new Thickness(2, 50 * scale, 0, 0); defaults.HorizontalAlignment = HorizontalAlignment.Left; defaults.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(defaults);
        foreach (var child in new FrameworkElement[] { this, front, back, exchange, defaults }) Density.Mark(child, DensityRole.Keep);
    }
    static Button Swatch(Action click, string name, double x, double y, double size)
    {
        var b = Theme.Button("", click, name); b.Width = b.Height = size; b.MinHeight = 0; b.Padding = new Thickness(0);
        b.BorderBrush = Theme.Brush("#C9D0DA"); b.BorderThickness = new Thickness(1.5);
        b.HorizontalAlignment = HorizontalAlignment.Left; b.VerticalAlignment = VerticalAlignment.Top; b.Margin = new Thickness(x, y, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(b, name); return b;
    }

    /// <summary>간결한 화면: a 34 DIP pair of 20 DIP chips with the swap and reset buttons in the corners.</summary>
    public void SetCompact(bool compact)
    {
        if (compact)
        {
            Width = 34; Height = 40; Margin = new Thickness(0, 6, 0, 8);
            Place(front, 2, 2, 20); Place(back, 12, 12, 20);
            exchange.Width = exchange.Height = 14; exchange.Margin = new Thickness(22, 0, 0, 0);
            defaults.Width = defaults.Height = 14; defaults.Margin = new Thickness(0, 26, 0, 0);
        }
        else
        {
            Width = 70 * scale; Height = 74 * scale; Margin = new Thickness(2, 8, 2, 10);
            Place(back, 28 * scale, 26 * scale, 32 * scale); Place(front, 8 * scale, 6 * scale, 32 * scale);
            exchange.Width = exchange.Height = 22; exchange.Margin = new Thickness(44 * scale, 2, 0, 0);
            defaults.Width = defaults.Height = 22; defaults.Margin = new Thickness(2, 50 * scale, 0, 0);
        }
        front.BorderThickness = back.BorderThickness = new Thickness(compact ? 1 : 1.5);
        static void Place(Button swatch, double x, double y, double size) { swatch.Width = swatch.Height = size; swatch.Margin = new Thickness(x, y, 0, 0); }
    }

    public void SetColors(Color foreground, Color background)
    {
        front.Background = new SolidColorBrush(foreground); back.Background = new SolidColorBrush(background);
        front.ToolTip = $"전경색 #{foreground.R:X2}{foreground.G:X2}{foreground.B:X2} · 클릭하여 선택";
        back.ToolTip = $"배경색 #{background.R:X2}{background.G:X2}{background.B:X2} · 클릭하여 선택";
    }
}
