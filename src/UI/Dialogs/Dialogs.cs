using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public static class Dialogs
{
    public static string[]? Fields(Window owner, string title, params (string Label, string Value)[] fields)
    {
        var (dialog, boxes) = CreateFields(owner, title, fields);
        return dialog.ShowDialog() == true ? boxes.Select(b => b.Text).ToArray() : null;
    }

    // Used by 15 commands (layer name, transform, canvas size, filters). Built on DialogShell.
    internal static (Window Dialog, List<TextBox> Boxes) CreateFields(Window? owner, string title, (string Label, string Value)[] fields)
    {
        var dialog = new Window { Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        DialogShell.Prepare(dialog, owner, title);
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; dialog.Content = panel;
        panel.Children.Add(DialogShell.Title(title));
        var boxes = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(DialogShell.FieldLabel(field.Label));
            var box = new TextBox { Text = field.Value, MinWidth = 280, Margin = new Thickness(0) };
            System.Windows.Automation.AutomationProperties.SetName(box, field.Label);
            boxes.Add(box); panel.Children.Add(box);
        }
        var cancel = DialogShell.Secondary("취소", () => dialog.DialogResult = false); cancel.IsCancel = true;
        var okay = DialogShell.Primary("적용", () => dialog.DialogResult = true); okay.IsDefault = true;
        panel.Children.Add(DialogShell.Footer(cancel, okay));
        dialog.Loaded += (_, _) => { if (boxes.Count > 0) { boxes[0].Focus(); boxes[0].SelectAll(); } };
        return (dialog, boxes);
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
