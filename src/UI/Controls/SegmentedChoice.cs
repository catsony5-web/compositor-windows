using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Segment row on an Input track; only the chosen piece rises to Surface (DESIGN_SYSTEM: 세그먼트).</summary>
public sealed class SegmentedChoice<T> : Border
{
    readonly List<(T Value, Button Button)> items = [];
    T selected;
    public event Action<T>? Changed;

    public SegmentedChoice(IEnumerable<(T Value, string Caption)> choices, T initial)
    {
        selected = initial;
        var row = new UniformGrid { Rows = 1 };
        foreach (var (value, caption) in choices)
        {
            var button = new Button { Content = caption, MinWidth = 0 };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            System.Windows.Automation.AutomationProperties.SetName(button, caption);
            button.Click += (_, _) => Select(value);
            items.Add((value, button)); row.Children.Add(button);
        }
        Background = Theme.Input; BorderBrush = Theme.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8); Padding = new Thickness(2); Child = row;
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
        foreach (var (value, button) in items)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, selected);
            button.Background = on ? Theme.Surface : Brushes.Transparent; button.BorderBrush = on ? Theme.Stroke : Brushes.Transparent;
            button.Foreground = on ? Theme.Text : Theme.Muted; button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
