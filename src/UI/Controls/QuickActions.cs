using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// Icon-led panel commands. Each control keeps the complete command name as its
// accessible name and tooltip, so a short visible label never loses meaning.
public static class QuickActions
{
    // Icon above a short label, for groups of peer commands (adjustment types, creation).
    public static Button Tile(string glyph, string label, Action action, string tooltip, string? name = null)
    {
        var button = Theme.Button("", action, tooltip);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Theme.Glyph(glyph, 20, Theme.Text); icon.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(icon);
        content.Children.Add(Density.Mark(new KeepWordsTextBlock { Text = label, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) }, DensityRole.TileLabel));
        Density.Mark(button, DensityRole.Tile);
        button.Content = content; button.MinHeight = 62; button.Padding = new Thickness(4, 9, 4, 8); button.Margin = new Thickness(3);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(button, name ?? label);
        return button;
    }

    // Icon beside a label, for commands whose names need more room.
    public static Button Command(string glyph, string label, Action action, string tooltip, string? name = null)
    {
        var button = Theme.Button("", action, tooltip);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); content.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Theme.Glyph(glyph, 18, Theme.Muted); icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(icon);
        var text = new KeepWordsTextBlock { Text = label, FontSize = Theme.BodySize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(text, 1); content.Children.Add(text);
        button.Content = content; button.MinHeight = 40; button.Padding = new Thickness(10, 6, 8, 6); button.Margin = new Thickness(3);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, name ?? label);
        return button;
    }

    public const double TileWidth = 84, CommandWidth = 150;

    // Up to `columns` columns; below minColumnWidth per column the grid drops columns
    // so Korean labels are not broken mid-word in narrow or floating panels.
    public static UniformGrid Grid(int columns, IEnumerable<Button> buttons, double minColumnWidth = 0)
    {
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-1, 0, -1, 4) };
        foreach (var button in buttons) grid.Children.Add(button);
        if (minColumnWidth > 0)
            grid.SizeChanged += (_, e) => { if (e.WidthChanged) grid.Columns = Math.Clamp((int)(e.NewSize.Width / minColumnWidth), 1, columns); };
        return grid;
    }

    // Icon-only buttons in one recessed track, for spatial choices such as alignment.
    public static Border IconStrip(IEnumerable<(string Glyph, string Name, Action Run)> actions, out Button[] buttons)
    {
        var grid = new UniformGrid { Rows = 1 };
        var created = new List<Button>();
        foreach (var (glyph, name, run) in actions)
        {
            var button = Theme.IconButton(glyph, run, name, Theme.ControlHeight + 2, 18);
            button.Width = double.NaN; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Margin = new Thickness(1);
            created.Add(button); grid.Children.Add(button);
        }
        buttons = created.ToArray();
        return new Border { Background = Theme.Input, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Margin = new Thickness(2, 2, 2, 8), Child = grid };
    }

    // A prominent entry point: accent icon, title, one-line description and chevron.
    public static Button Feature(string glyph, string title, string description, Action action, string tooltip)
    {
        var button = Theme.Button("", action, tooltip);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var badge = Density.Mark(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = Theme.Selected, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = Theme.Glyph(glyph, 20, Theme.Accent) }, DensityRole.Badge);
        content.Children.Add(badge);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new KeepWordsTextBlock { Text = title, FontSize = Theme.BodySize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        labels.Children.Add(Density.Mark(new KeepWordsTextBlock { Text = description, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, DensityRole.Description));
        System.Windows.Controls.Grid.SetColumn(labels, 1); content.Children.Add(labels);
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0 0 L 4 4 L 0 8"), Stroke = Theme.Subtle, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(arrow, 2); content.Children.Add(arrow);
        button.Content = content; button.Padding = new Thickness(10, 10, 12, 10); button.Margin = new Thickness(2, 2, 2, 6); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Density.Mark(button, DensityRole.Feature);
        AutomationProperties.SetName(button, title);
        return button;
    }
}
