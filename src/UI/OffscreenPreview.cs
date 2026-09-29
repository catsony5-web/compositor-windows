using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Compositor.Windows;

// Offscreen review captures render a window's content without the window itself. Dialog roots
// keep their inner spacing as a Margin that the window background normally paints, so a bare
// render shows the content flush against a transparent edge. Hosting the content on the window
// background (with the window's inherited text settings) makes the capture match the real dialog.
internal static class OffscreenPreview
{
    internal static Border Host(Window window)
    {
        var content = (UIElement)window.Content; window.Content = null;
        var host = new Border { Background = window.Background, Child = content, UseLayoutRounding = window.UseLayoutRounding, SnapsToDevicePixels = window.SnapsToDevicePixels };
        host.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        host.SetValue(TextElement.FontSizeProperty, window.FontSize);
        host.SetValue(TextElement.ForegroundProperty, window.Foreground);
        host.FlowDirection = window.FlowDirection; host.Language = window.Language;
        return host;
    }
}
