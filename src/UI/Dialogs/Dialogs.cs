using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Dialogs
{
    public static string[]? Fields(Window owner, string title, params (string Label, string Value)[] fields)
    {
        var dialog = new Window { Title = title, Owner = owner, Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Panel, Foreground = Theme.Text, FontFamily = Theme.UiFont };
        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) }; dialog.Content = panel;
        var heading = Theme.Label(title, 18); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(3, 0, 3, 15); panel.Children.Add(heading);
        var boxes = new List<TextBox>();
        foreach (var field in fields)
        {
            var caption = Theme.Label(field.Label, 11, Theme.Muted); caption.Margin = new Thickness(3, 8, 3, 2); panel.Children.Add(caption);
            var box = new TextBox { Text = field.Value, MinWidth = 280 }; boxes.Add(box); panel.Children.Add(box);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 19, 0, 0) };
        var cancel = Theme.Button("취소", () => dialog.DialogResult = false); cancel.IsCancel = true;
        var okay = Theme.Button("적용", () => dialog.DialogResult = true); okay.IsDefault = true; okay.Background = Theme.Primary;
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
    public static Color? ColorPicker(Window owner, Color initial, string label = "전경색")
    {
        var dialog = new ColorPickerDialog(owner, initial, label);
        return dialog.ShowDialog() == true ? dialog.SelectedColor : null;
    }
}
