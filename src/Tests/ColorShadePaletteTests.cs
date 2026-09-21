using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ColorShadePaletteTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        static UniformGrid Grid(ColorPalettePanel panel) => panel.Children.OfType<UniformGrid>()
            .Single(grid => AutomationProperties.GetName(grid) == "선택 색상 톤 그리드");
        static Color[] Chips(UniformGrid grid) => grid.Children.OfType<Button>().Select(button => (Color)button.Tag).ToArray();
        static double Brightness(Color color) => .2126 * color.R + .7152 * color.G + .0722 * color.B;

        test("tone palette retains exact base and alpha with neighboring hues and ordered tints", () =>
        {
            var source = Color.FromArgb(91, 210, 36, 95);
            var colors = ColorShadePalette.Create(source);
            Check(colors.Length == 63 && colors[ColorShadePalette.BaseIndex] == source, "Exact original color is absent from the center of the tone palette");
            Check(colors.All(color => color.A == source.A), "Tone selection changed foreground alpha");
            var (h, s, v) = ColorValues.ToHsv(source);
            var left = colors[3 * 9]; var right = colors[3 * 9 + 8];
            Check(left == ColorValues.FromHsv(h - 40, s, v, source.A) && right == ColorValues.FromHsv(h + 40, s, v, source.A), "Neighbor hues were not wrapped around the color wheel correctly");
            for (int column = 0; column < ColorShadePalette.Columns; column++)
                for (int row = 1; row < ColorShadePalette.Rows; row++)
                    Check(Brightness(colors[row * 9 + column]) < Brightness(colors[(row - 1) * 9 + column]), "Tone columns must progress from light to dark");
        });
        test("neutral tone palettes remain achromatic with usable light and dark variants", () =>
        {
            foreach (var source in new[] { Colors.Black, Colors.White, Colors.Gray, Color.FromArgb(37, 128, 128, 128) })
            {
                var colors = ColorShadePalette.Create(source);
                Check(colors[ColorShadePalette.BaseIndex] == source, "Neutral source was replaced with an invented hue");
                Check(colors.All(color => color.R == color.G && color.G == color.B && color.A == source.A), "Neutral palette introduced color or lost alpha");
                Check(colors.Distinct().Count() > 14, "Neutral palette offers too few usable shades");
            }
        });
        test("tone chip selection emits once and keeps the palette stable through owner echoes", () =>
        {
            var panel = new ColorPalettePanel();
            var source = Color.FromArgb(109, 240, 96, 62); panel.SetColor(source);
            var grid = Grid(panel); var before = Chips(grid); int events = 0;
            panel.ColorChanged += color => { events++; panel.SetColor(color); };
            var choice = (Button)grid.Children[1];
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(events == 1 && panel.SelectedColor == before[1], "A tone did not publish its exact color once");
            Check(panel.ShadeBaseColor == source && Chips(grid).SequenceEqual(before), "Selecting a tone shifted the palette unexpectedly");
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(events == 1, "Picking the current tone emitted a duplicate event");
            ((Button)grid.Children[ColorShadePalette.BaseIndex]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(events == 2 && panel.SelectedColor == source, "The exact base color could not be restored from the same grid");
        });
        test("external foreground and continuous picker edits establish the next tone palette", () =>
        {
            var panel = new ColorPalettePanel(); int events = 0;
            panel.ColorChanged += _ => events++;
            panel.SetColor(Colors.Blue);
            Check(events == 0 && panel.ShadeBaseColor == Colors.Blue && Chips(Grid(panel))[ColorShadePalette.BaseIndex] == Colors.Blue, "External source synchronization was not silent or did not replace the base");
            panel.Children.OfType<SaturationValuePad>().Single().AdjustByKey(Key.Down, true);
            Check(events == 1 && panel.ShadeBaseColor == panel.SelectedColor && panel.SelectedColor != Colors.Blue, "Continuous picker and tone grid diverged");
        });
        test("tone reset adopts the chosen foreground without emitting another color change", () =>
        {
            var panel = new ColorPalettePanel(); panel.SetColor(Colors.OrangeRed); int events = 0;
            panel.ColorChanged += _ => events++;
            ((Button)Grid(panel).Children[10]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var chosen = panel.SelectedColor;
            panel.Children.OfType<Button>().Single(button => Equals(button.Content, "현재 색을 기준으로"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(events == 1 && panel.ShadeBaseColor == chosen && Chips(Grid(panel))[ColorShadePalette.BaseIndex] == chosen, "Rebasing changed the foreground or did not adopt its selected tone");
        });
        test("tone chips have readable accessible names and remain inside narrow panels", () =>
        {
            var panel = new ColorPalettePanel();
            panel.Measure(new Size(248, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 248, panel.DesiredSize.Height)); panel.UpdateLayout();
            var grid = Grid(panel);
            foreach (Button button in grid.Children)
            {
                string name = AutomationProperties.GetName(button);
                Check(name.Contains('#') && name.Contains('행') && name.Contains('열'), "An unlabeled tone cannot be identified with assistive technology");
                Check(button.Focusable && KeyboardNavigation.GetIsTabStop(button), "Tone swatches cannot receive keyboard activation");
                var bounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= 248.1, "Tone swatches overflow the narrow color panel");
            }
        });
    }
}
