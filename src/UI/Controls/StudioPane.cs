using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// A single live visual moves between a dock and its owned tool window.
// Docked on the right, page panes are embedded in the tabbed card and hide their own header;
// the tab strip then offers the same docking menu.
internal sealed class StudioPane : GlassPanel
{
    public string Caption { get; private set; }
    public int Page { get; }
    public string Location { get; set; } = "right";
    public bool Pinned { get; private set; }
    public Window? Floating { get; set; }
    readonly Button pin;
    readonly TextBlock title;
    readonly Border headerBorder;
    readonly StackPanel titleRow;
    readonly ContextMenu menu = new();
    public StudioPane(string caption, int page, UIElement content, Action<StudioPane, string> move, Action<StudioPane, Point> drag)
    {
        Caption = caption; Page = page;
        ShowReflection = false; Background = Theme.Panel; BorderBrush = Theme.Line; CornerRadius = new CornerRadius(10);
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var root = new DockPanel(); Child = root;
        var header = new DockPanel { MinHeight = 36, Background = Brushes.Transparent, Margin = new Thickness(12, 0, 5, 0) };
        headerBorder = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Child = header };
        DockPanel.SetDock(headerBorder, Dock.Top); root.Children.Add(headerBorder);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        pin = Theme.IconButton(Theme.Glyphs.Pin, () => { }, "패널을 현재 위치에 고정 / 이동 허용", 26, 14);
        System.Windows.Automation.AutomationProperties.SetName(pin, "패널 고정");
        pin.Click += (_, _) => { Pinned = !Pinned; pin.Background = Pinned ? Theme.Selected : Brushes.Transparent; };
        controls.Children.Add(pin);
        var options = Theme.IconButton(Theme.Glyphs.More, () => { }, "패널 이동과 도킹", 26, 16); controls.Children.Add(options);
        options.ContextMenu = menu;
        foreach (var (label, destination) in new[] { ("분리하여 이동", "float"), ("왼쪽에 도킹", "left"), ("오른쪽에 도킹", "right") })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => { if (!Pinned) move(this, destination); }; menu.Items.Add(item); }
        options.Click += (_, _) => ShowDockMenu(options);
        titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "제목을 끌어서 패널 이동 · ⋯ 도킹 위치 선택" };
        title = Theme.Label(caption, Theme.BodySize); title.FontWeight = FontWeights.SemiBold; titleRow.Children.Add(title); header.Children.Add(titleRow);
        Point? start = null;
        titleRow.MouseLeftButtonDown += (_, e) => { if (Pinned) return; start = e.GetPosition(titleRow); titleRow.CaptureMouse(); e.Handled = true; };
        titleRow.MouseLeftButtonUp += (_, _) => { start = null; titleRow.ReleaseMouseCapture(); };
        titleRow.MouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(titleRow);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var screen = titleRow.PointToScreen(point); start = null; titleRow.ReleaseMouseCapture(); drag(this, screen); e.Handled = true;
        };
        root.Children.Add(content);
    }
    internal void Unlock() { Pinned = false; pin.Background = Brushes.Transparent; }
    internal void SetCaption(string value) { Caption = value; title.Text = value; if (Floating != null) Floating.Title = "Morupixel · " + value; }
    // Secondary header text such as an item count, shown after the title.
    internal void AddHeaderDetail(FrameworkElement detail) { detail.Margin = new Thickness(8, 0, 0, 0); titleRow.Children.Add(detail); }
    internal void ShowDockMenu(UIElement target) { menu.PlacementTarget = target; menu.IsOpen = true; }
    internal void SetEmbedded(bool embedded)
    {
        headerBorder.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        BorderThickness = new Thickness(embedded ? 0 : 1);
        Background = embedded ? Brushes.Transparent : Theme.Panel;
        CornerRadius = new CornerRadius(embedded ? 0 : 10);
    }
}
