using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Shared building blocks so every editor dialog uses the same chrome, type scale and actions:
// panel background, 18 DIP title, muted subtitle, caption field labels, and a right-aligned
// footer with one primary action. See docs/DESIGN_SYSTEM.md.
public static class DialogShell
{
    public static void Prepare(Window window, Window? owner, string caption)
    {
        if (owner != null) window.Owner = owner;
        window.Title = "Morupixel · " + caption; window.Icon = Theme.BrandIcon;
        window.WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false; window.Background = Theme.Panel; window.Foreground = Theme.Text;
        window.FontFamily = Theme.UiFont; window.FontSize = Theme.BodySize;
        window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
    }

    public static TextBlock Title(string text)
    {
        var title = Theme.Label(text, Theme.TitleSize); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0);
        return title;
    }

    public static TextBlock Subtitle(string text)
    {
        var subtitle = Theme.Label(text, Theme.BodySize, Theme.Muted); subtitle.Margin = new Thickness(0, 4, 0, 0);
        return subtitle;
    }

    // Small muted label placed above an input.
    public static TextBlock FieldLabel(string text)
    {
        var label = Theme.Label(text, Theme.CaptionSize, Theme.Muted); label.Margin = new Thickness(1, 10, 1, 4);
        return label;
    }

    public static TextBlock Note(string text)
    {
        var note = Theme.Label(text, Theme.CaptionSize, Theme.Subtle); note.Margin = new Thickness(1, 6, 1, 0);
        return note;
    }

    public static TextBlock ErrorText()
    {
        var error = Theme.Label("", Theme.CaptionSize, Theme.Danger); error.Margin = new Thickness(1, 8, 1, 0);
        return error;
    }

    public static Button Primary(string text, Action action)
    {
        var button = Theme.Styled(Theme.Button(text, action), "PrimaryButton");
        button.MinWidth = 88; button.MinHeight = 34; button.Padding = new Thickness(16, 5, 16, 5); button.Margin = new Thickness(0);
        return button;
    }

    public static Button Secondary(string text, Action action)
    {
        var button = Theme.Button(text, action);
        button.MinWidth = 80; button.MinHeight = 34; button.Padding = new Thickness(14, 5, 14, 5); button.Margin = new Thickness(0);
        return button;
    }

    // Right-aligned action row; the last button is conventionally the primary action.
    public static StackPanel Footer(params Button[] buttons)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(buttons[i]);
        }
        return footer;
    }

    // A recessed card for previews and grouped settings inside a dialog.
    public static Border Card(UIElement child, Brush? background = null) => new()
    {
        Background = background ?? Theme.Stage, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Child = child
    };
}
