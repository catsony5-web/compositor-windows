using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Shared property rows for the inspector, text, shape and artboard panels: 12 Muted
// captions above 30 DIP inputs, 8 DIP between columns. Rows are Grids with the inputs
// as direct children so panel code and checks can reach them without extra wrappers.
public static class PropertyRows
{
    const double Gap = 4;

    public static TextBlock Caption(string text)
    {
        var caption = Theme.Label(text, Theme.CaptionSize, Theme.Muted);
        caption.TextWrapping = TextWrapping.Wrap; caption.Margin = new Thickness(0, 0, 0, 3);
        return caption;
    }

    // Caption above one full-width input.
    public static Grid Field(string caption, FrameworkElement input, Thickness? margin = null)
    {
        var grid = new Grid { Margin = margin ?? new Thickness(2, 0, 2, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Caption(caption));
        input.Margin = new Thickness(0); Grid.SetRow(input, 1); grid.Children.Add(input);
        return grid;
    }

    // Two captioned inputs side by side in equal columns.
    public static Grid Pair(string leftCaption, FrameworkElement left, string rightCaption, FrameworkElement right, Thickness? margin = null)
    {
        var grid = new Grid { Margin = margin ?? new Thickness(2, 0, 2, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var leftLabel = Caption(leftCaption); leftLabel.Margin = new Thickness(0, 0, Gap, 3);
        var rightLabel = Caption(rightCaption); rightLabel.Margin = new Thickness(Gap, 0, 0, 3);
        left.Margin = new Thickness(0, 0, Gap, 0); right.Margin = new Thickness(Gap, 0, 0, 0);
        Grid.SetRow(left, 1); Grid.SetColumn(rightLabel, 1); Grid.SetColumn(right, 1); Grid.SetRow(right, 1);
        grid.Children.Add(leftLabel); grid.Children.Add(left); grid.Children.Add(rightLabel); grid.Children.Add(right);
        return grid;
    }

    // Body-size label on the left, compact value on the right.
    public static Grid Inline(string caption, FrameworkElement value, double valueWidth = 78, Thickness? margin = null)
    {
        var grid = new Grid { Margin = margin ?? new Thickness(2, 0, 2, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
        var label = Theme.Label(caption, Theme.BodySize); label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(label);
        value.Margin = new Thickness(0); Grid.SetColumn(value, 1); grid.Children.Add(value);
        return grid;
    }

    public static TextBox Input(string text, string name)
    {
        var box = new TextBox { Text = text, MinHeight = Theme.ControlHeight, Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        return box;
    }

    public static TextBox NumberBox(double value, string name) => NumberBox(value.ToString("0.##", CultureInfo.InvariantCulture), name);

    public static TextBox NumberBox(string text, string name)
    {
        var box = Input(text, name); box.HorizontalContentAlignment = HorizontalAlignment.Right;
        return box;
    }

    public static ComboBox Choice(string name)
    {
        var box = new ComboBox { MinHeight = Theme.ControlHeight, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(box, name);
        return box;
    }

    // Swatch plus label, e.g. a hex value or "글자 색상".
    public static Button ColorChip(Color color, string text, Action click, string tooltip)
    {
        var chip = Theme.Button("", click, tooltip);
        chip.MinHeight = Theme.ControlHeight; chip.Padding = new Thickness(8, 3, 8, 3); chip.Margin = new Thickness(0);
        SetChipColor(chip, color, text);
        AutomationProperties.SetName(chip, tooltip);
        return chip;
    }

    public static void SetChipColor(Button chip, Color color, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = Theme.Label(text, Theme.CaptionSize); label.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(label);
        chip.Content = content;
    }
}
