using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// A single live visual moves between a dock and its owned tool window.
internal sealed class StudioPane : GlassPanel
{
    public string Caption { get; private set; }
    public int Page { get; }
    public string Location { get; set; } = "right";
    public bool Pinned { get; private set; }
    public Window? Floating { get; set; }
    readonly Button pin;
    readonly TextBlock title;
    public StudioPane(string caption, int page, UIElement content, Action<StudioPane, string> move, Action<StudioPane, Point> drag)
    {
        Caption = caption; Page = page;
        ShowReflection = false; Background = Theme.Panel; BorderBrush = Theme.Line; CornerRadius = new CornerRadius(7);
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var root = new DockPanel(); Child = root;
        var header = new DockPanel { MinHeight = 38, Background = Brushes.Transparent, Margin = new Thickness(10, 2, 6, 2) };
        var headerBorder = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Theme.Panel, Child = header };
        DockPanel.SetDock(headerBorder, Dock.Top); root.Children.Add(headerBorder);
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        pin = Theme.Button("고정", () => { }); pin.Padding = new Thickness(7, 3, 7, 3); pin.FontSize = Theme.CaptionSize; pin.MinHeight = 30; pin.Margin = new Thickness(2); pin.Background = Brushes.Transparent; pin.BorderBrush = Brushes.Transparent;
        pin.Click += (_, _) => { Pinned = !Pinned; pin.Content = Pinned ? "해제" : "고정"; pin.Background = Pinned ? Theme.Selected : Brushes.Transparent; };
        pin.ToolTip = "패널을 현재 위치에 고정 / 이동 허용"; controls.Children.Add(pin);
        var options = Theme.Button("⋯", () => { }); options.Padding = new Thickness(7, 0, 7, 0); options.MinHeight = 30; options.Margin = new Thickness(2); options.Background = Brushes.Transparent; options.BorderBrush = Brushes.Transparent; options.ToolTip = "패널 이동과 도킹"; controls.Children.Add(options);
        var menu = new ContextMenu(); options.ContextMenu = menu;
        foreach (var (label, destination) in new[] { ("분리하여 이동", "float"), ("왼쪽에 도킹", "left"), ("오른쪽에 도킹", "right") })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => { if (!Pinned) move(this, destination); }; menu.Items.Add(item); }
        options.Click += (_, _) => { menu.PlacementTarget = options; menu.IsOpen = true; };
        title = Theme.Label(caption, Theme.BodySize); title.FontWeight = FontWeights.SemiBold; title.Cursor = Cursors.SizeAll; title.ToolTip = "제목을 끌어서 패널 이동 · ⋯ 도킹 위치 선택"; header.Children.Add(title);
        Point? start = null;
        title.MouseLeftButtonDown += (_, e) => { if (Pinned) return; start = e.GetPosition(title); title.CaptureMouse(); e.Handled = true; };
        title.MouseLeftButtonUp += (_, _) => { start = null; title.ReleaseMouseCapture(); };
        title.MouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(title);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var screen = title.PointToScreen(point); start = null; title.ReleaseMouseCapture(); drag(this, screen); e.Handled = true;
        };
        root.Children.Add(content);
    }
    internal void Unlock() { Pinned = false; pin.Content = "고정"; pin.Background = Brushes.Transparent; }
    internal void SetCaption(string value) { Caption = value; title.Text = value; if (Floating != null) Floating.Title = "Morupixel · " + value; }
}
