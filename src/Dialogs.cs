using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Dialogs
{
    public static string[]? Fields(Window owner, string title, params (string Label, string Value)[] fields)
    {
        var dialog = new Window { Title = title, Owner = owner, Width = 390, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Panel, Foreground = Theme.Text, FontFamily = new FontFamily("Malgun Gothic") };
        var panel = new StackPanel { Margin = new Thickness(22) }; dialog.Content = panel;
        panel.Children.Add(Theme.Label(title, 19));
        var boxes = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(Theme.Label(field.Label, 12, Theme.Muted));
            var box = new TextBox { Text = field.Value, MinWidth = 280 }; boxes.Add(box); panel.Children.Add(box);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = Theme.Button("취소", () => dialog.DialogResult = false); cancel.IsCancel = true;
        var okay = Theme.Button("적용", () => dialog.DialogResult = true); okay.IsDefault = true; okay.Background = Theme.Brush("#285848");
        row.Children.Add(cancel); row.Children.Add(okay); panel.Children.Add(row);
        dialog.Loaded += (_, _) => { boxes[0].Focus(); boxes[0].SelectAll(); };
        return dialog.ShowDialog() == true ? boxes.Select(b => b.Text).ToArray() : null;
    }
    public static double Number(string text, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
            throw new ArgumentException($"{min} ~ {max} 사이의 숫자를 입력하세요. 소수점은 . 을 사용합니다.");
        return n;
    }
    public static Color? ColorPicker(Window owner, Color initial)
    {
        var d = new Window { Title = "전경색", Width = 348, Height = 300, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Theme.Panel, Foreground = Theme.Text };
        var stack = new StackPanel { Margin = new Thickness(20) }; d.Content = stack;
        stack.Children.Add(Theme.Label("전경색 선택", 18));
        var hex = new TextBox { Text = $"#{initial.R:X2}{initial.G:X2}{initial.B:X2}" };
        var colors = new WrapPanel();
        foreach (var c in new[] { "#FFFFFF", "#000000", "#A2E8CD", "#77ADFF", "#FF6F6F", "#FFD478", "#C7A4FF", "#F29ACC", "#256A59", "#20466C", "#A8413C", "#D28A43", "#585F70", "#B5BECC", "#E8DDCD", "#7ACBD4" })
        {
            var b = Theme.Button(" ", () => hex.Text = c); b.Width = 32; b.Height = 30; b.Padding = new Thickness(0); b.Background = Theme.Brush(c); colors.Children.Add(b);
        }
        stack.Children.Add(colors); stack.Children.Add(Theme.Label("HEX 색상 코드", 12, Theme.Muted)); stack.Children.Add(hex);
        Color result = initial;
        var apply = Theme.Button("색상 적용", () => { try { result = (Color)ColorConverter.ConvertFromString(hex.Text); d.DialogResult = true; } catch { MessageBox.Show(d, "#RRGGBB 형식으로 입력하세요."); } });
        stack.Children.Add(apply); return d.ShowDialog() == true ? result : null;
    }
}
