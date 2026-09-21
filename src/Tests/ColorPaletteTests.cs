using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public static class ColorPaletteTests
{
    public static void Run(Action<string, Action> test)
    {
        test("color harmony uses opposite, neighboring and triadic hues with alpha retained", () =>
        {
            var color = Color.FromArgb(127, 255, 0, 0);
            var complementary = ColorHarmony.Create(color, ColorHarmonyKind.Complementary);
            var analogous = ColorHarmony.Create(color, ColorHarmonyKind.Analogous);
            var triadic = ColorHarmony.Create(color, ColorHarmonyKind.Triadic);
            if (complementary[1] != Color.FromArgb(127, 0, 255, 255) || triadic[1] != Color.FromArgb(127, 0, 255, 0) || triadic[2] != Color.FromArgb(127, 0, 0, 255)) throw new Exception("Harmony hue rotation is incorrect");
            if (Math.Abs(ColorValues.ToHsv(analogous[0]).H - 330) > .2 || Math.Abs(ColorValues.ToHsv(analogous[2]).H - 30) > .2) throw new Exception("Analogous colors must stay 30 degrees from the base");
            foreach (var kind in Enum.GetValues<ColorHarmonyKind>())
                if (ColorHarmony.Create(color, kind).Any(c => c.A != 127)) throw new Exception("Picking a harmony changed foreground alpha");
        });
        test("black and white receive useful harmony companions while retaining exact base color", () =>
        {
            foreach (var color in new[] { Colors.Black, Colors.White }) foreach (var kind in Enum.GetValues<ColorHarmonyKind>())
            {
                var colors = ColorHarmony.Create(color, kind);
                if (!colors.Contains(color) || colors.Distinct().Count() < 3 || colors.All(c => c.R == c.G && c.G == c.B)) throw new Exception("Neutral palette cannot produce colored companions");
            }
        });
        test("palette external synchronization is silent and does not reset black latent saturation", () =>
        {
            var panel = new ColorPalettePanel(); int events = 0; panel.ColorChanged += _ => events++;
            panel.SetColor(Colors.Red); panel.SetColor(Colors.Red);
            if (events != 0 || panel.SelectedColor != Colors.Red) throw new Exception("External foreground echo raised a color-change event");
            var pad = panel.Children.OfType<SaturationValuePad>().Single();
            pad.AdjustByKey(Key.Down, true);
            panel.SetColor(panel.SelectedColor);
            if (events != 1 || Math.Abs(pad.Value - .9) > .001) throw new Exception("Foreground echo quantized the HSV pointer");
            for (int i = 0; i < 10; i++) pad.AdjustByKey(Key.Down, true);
            panel.SetColor(panel.SelectedColor); pad.AdjustByKey(Key.Up, true);
            if (panel.SelectedColor.R < 24 || panel.SelectedColor.G != 0 || panel.SelectedColor.B != 0) throw new Exception("Passing through black lost the latent red hue or saturation");
        });
        test("palette keyboard adjustment is bounded and recommendation chip publishes one selection", () =>
        {
            var panel = new ColorPalettePanel(); panel.SetColor(Colors.Red);
            var pad = panel.Children.OfType<SaturationValuePad>().Single();
            for (int i = 0; i < 20; i++) { pad.AdjustByKey(Key.Left, true); pad.AdjustByKey(Key.Up, true); }
            if (pad.Saturation != 0 || pad.Value != 1 || pad.AdjustByKey(Key.Tab)) throw new Exception("Keyboard picker does not clamp or traps Tab");
            panel.SetColor(Colors.Red); int count = 0; panel.ColorChanged += _ => count++;
            var chips = panel.Children.OfType<UniformGrid>().Last();
            ((Button)chips.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (panel.SelectedColor != Colors.Cyan || count != 1) throw new Exception("Recommendation chip did not select complementary cyan exactly once");
        });
        test("palette lays out at narrow floating-panel width without horizontal overflow", () =>
        {
            var panel = new ColorPalettePanel();
            panel.Measure(new Size(248, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 248, panel.DesiredSize.Height)); panel.UpdateLayout();
            foreach (FrameworkElement child in panel.Children)
                if (child.ActualWidth + child.Margin.Left + child.Margin.Right > 248.1) throw new Exception("Palette control overflows a 280px floating panel");
        });
    }
}
