using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Tool-option switch: icon + short name, details in the tooltip. On = Selected surface with an Accent outline.</summary>
public sealed class OptionToggle : ToggleButton
{
    readonly Border surface;
    readonly FrameworkElement icon;

    public OptionToggle(string glyph, string label, string tooltip, bool isChecked)
    {
        icon = Theme.Glyph(glyph, 15, Theme.Text, 1.6); icon.VerticalAlignment = VerticalAlignment.Center;
        var text = Theme.Label(label, Theme.CaptionSize, Theme.Text); text.Margin = new Thickness(6, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(icon); row.Children.Add(text);
        surface = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 9, 0), MinHeight = 28, Child = row };
        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Template = template; Content = surface; Cursor = Cursors.Hand; Focusable = true;
        VerticalAlignment = VerticalAlignment.Center; Margin = new Thickness(4, 0, 4, 0);
        SetResourceReference(FocusVisualStyleProperty, "UiFocusRing");
        ToolTip = tooltip; AutomationProperties.SetName(this, label); AutomationProperties.SetHelpText(this, tooltip);
        IsChecked = isChecked;
        Checked += (_, _) => Paint(); Unchecked += (_, _) => Paint();
        MouseEnter += (_, _) => Paint(); MouseLeave += (_, _) => Paint();
        IsEnabledChanged += (_, _) => Paint();
        Paint();
    }

    void Paint()
    {
        bool on = IsChecked == true;
        surface.Background = on ? Theme.Selected : IsMouseOver ? Theme.Surface : Brushes.Transparent;
        surface.BorderBrush = on ? Theme.Accent : Theme.Line;
        Opacity = IsEnabled ? 1 : .45;
    }
}
