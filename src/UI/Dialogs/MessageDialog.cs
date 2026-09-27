using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Compositor.Windows;

public enum NoticeKind { Information, Warning, Error }

// Themed replacement for the system MessageBox so notices keep the editor's dark surfaces.
// The message is selectable and can be copied, which helps when reporting an error.
public sealed class MessageDialog : Window
{
    internal string Message { get; }

    internal MessageDialog(Window? owner, string message, string title, NoticeKind kind, double width = 460)
    {
        Message = message;
        DialogShell.Prepare(this, owner != null && owner.IsLoaded ? owner : null, title);
        Width = width; MinWidth = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; Content = root;
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);

        var heading = new DockPanel();
        var (glyph, color) = kind switch
        {
            NoticeKind.Error => ("M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z M9 9L15 15 M15 9L9 15", Theme.Danger),
            NoticeKind.Warning => ("M12 3L22 20H2Z M12 9V14 M12 17.2V17.3", Theme.Warning),
            _ => ("M12 3A9 9 0 1 0 12 21A9 9 0 1 0 12 3Z M12 11V16.5 M12 7.6V7.7", Theme.Accent)
        };
        var icon = Theme.Glyph(glyph, 22, color, 1.8); icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(icon);
        string headingText = title != "Morupixel" ? title : kind switch { NoticeKind.Error => "작업을 완료하지 못했습니다", NoticeKind.Warning => "확인해 주세요", _ => "Morupixel" };
        var titleBlock = Theme.Label(headingText, Theme.HeadingSize); titleBlock.FontWeight = FontWeights.SemiBold; titleBlock.Margin = new Thickness(0);
        heading.Children.Add(titleBlock); root.Children.Add(heading);

        var body = new TextBox
        {
            Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(34, 10, 0, 0), MinHeight = 0,
            MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = Theme.Muted, Cursor = Cursors.IBeam
        };
        System.Windows.Automation.AutomationProperties.SetName(body, "알림 내용");
        root.Children.Add(body);

        var okay = DialogShell.Primary("확인", Close); okay.IsDefault = true; okay.IsCancel = true;
        if (kind == NoticeKind.Error)
        {
            var copy = Theme.Styled(DialogShell.Secondary("내용 복사", () => { try { Clipboard.SetText(message); } catch (System.Runtime.InteropServices.ExternalException) { } }), "GhostButton");
            root.Children.Add(DialogShell.Footer(copy, okay));
        }
        else root.Children.Add(DialogShell.Footer(okay));
        Loaded += (_, _) => okay.Focus();
    }

    public static void Show(Window? owner, string message, string title = "Morupixel", NoticeKind kind = NoticeKind.Warning, double width = 460)
        => new MessageDialog(owner, message, title, kind, width).ShowDialog();
}
