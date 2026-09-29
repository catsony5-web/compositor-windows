using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Compositor.Windows;

/// <summary>
/// A short vertical list of named options, each with a one-line explanation; exactly one is
/// chosen (DESIGN_SYSTEM: 선택 목록). The chosen row uses the Selected surface and an Accent
/// outline; Up/Down move the choice. Titles and explanations wrap, so translations never clip.
/// </summary>
public sealed class ChoiceList<T> : StackPanel
{
    readonly List<(T Value, Button Button, TextBlock Title)> items = [];
    T selected;
    public event Action<T>? Changed;

    public ChoiceList(IEnumerable<(T Value, string Title, string Description, string? Glyph)> choices, T initial)
    {
        selected = initial;
        foreach (var (value, title, description, glyph) in choices)
        {
            var button = Theme.Styled(new Button(), "InspectorAction");
            button.Padding = new Thickness(10, 8, 10, 8); button.Margin = new Thickness(0, 0, 0, 6); button.MinHeight = 48;
            button.BorderThickness = new Thickness(1); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(glyph == null ? 0 : 30) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            if (glyph != null) { var icon = Theme.Glyph(glyph, 18, Theme.Muted); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 1, 0, 0); icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon); }
            var text = new StackPanel(); Grid.SetColumn(text, 1); row.Children.Add(text);
            var name = new KeepWordsTextBlock { Text = title, FontSize = Theme.BodySize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var note = new KeepWordsTextBlock { Text = description, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            text.Children.Add(name); text.Children.Add(note);
            button.Content = row; button.ToolTip = description;
            AutomationProperties.SetName(button, title); AutomationProperties.SetHelpText(button, description);
            button.Click += (_, _) => Select(value);
            button.PreviewKeyDown += (_, e) =>
            {
                int index = items.FindIndex(i => ReferenceEquals(i.Button, button)), next = e.Key switch { Key.Down => index + 1, Key.Up => index - 1, _ => -1 };
                if (e.Key is not (Key.Down or Key.Up) || next < 0 || next >= items.Count) return;
                Select(items[next].Value); items[next].Button.Focus(); e.Handled = true;
            };
            items.Add((value, button, name)); Children.Add(button);
        }
        Paint();
    }

    public T Selected => selected;
    internal IReadOnlyList<Button> Buttons => items.Select(i => i.Button).ToArray();

    public void Select(T value)
    {
        if (EqualityComparer<T>.Default.Equals(selected, value)) return;
        selected = value; Paint(); Changed?.Invoke(value);
    }

    void Paint()
    {
        foreach (var (value, button, title) in items)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, selected);
            button.Background = on ? Theme.Selected : System.Windows.Media.Brushes.Transparent; button.BorderBrush = on ? Theme.Accent : Theme.Line;
            title.Foreground = on ? Theme.Text : Theme.Muted;
            AutomationProperties.SetItemStatus(button, on ? "선택됨" : "");
        }
    }
}
