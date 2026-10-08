using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// One tab of a 간결한 화면 panel group. Content is created on first use and is the live panel
// itself (the friendly screen's panes move in here), so both screens share one implementation.
internal sealed record DockTab(string Key, string Title, string Glyph, string Tip, Func<UIElement> Content);

// A stacked, docked panel group of the 간결한 화면: a 24 DIP tab row with the group menu (☰)
// over the open tab's content. The dock arranges groups, sizes them by Weight and folds a
// Collapsed group into its icon strip.
internal sealed class DockGroup : Border
{
    public const double HeaderHeight = 24;
    public string Key { get; }
    public IReadOnlyList<DockTab> Tabs { get; }
    public int ActiveIndex { get; private set; } = -1;
    public DockTab ActiveTab => Tabs[Math.Max(0, ActiveIndex)];
    public bool Collapsed { get; set; }
    /// <summary>True while the dock shows (간결한 화면); otherwise tabs only remember which one is open.</summary>
    public bool Hosted { get; set; }
    public double Weight { get; set; }
    public double DefaultWeight { get; }
    public bool DefaultCollapsed { get; }
    public Button MenuButton { get; }
    internal IReadOnlyList<Button> TabButtons => tabButtons;
    internal Border Body => body;
    readonly List<Button> tabButtons = [];
    readonly Border body = new() { ClipToBounds = true };
    readonly string[] titles;
    public event Action<DockGroup>? ActiveChanged;

    public DockGroup(string key, IReadOnlyList<DockTab> tabs, double weight, bool collapsed, Action<DockGroup> showMenu, Action<DockGroup> toggle)
    {
        Key = key; Tabs = tabs; Weight = DefaultWeight = weight; Collapsed = DefaultCollapsed = collapsed;
        titles = tabs.Select(t => t.Title).ToArray();
        Background = Theme.Panel; Density.Mark(this, DensityRole.Keep);
        var root = new DockPanel { LastChildFill = true };
        var header = new DockPanel { Background = Theme.Header, MinHeight = HeaderHeight, LastChildFill = true };
        MenuButton = Theme.IconButton(Theme.Glyphs.Menu, () => showMenu(this), "패널 그룹 메뉴 · 접기와 배치 초기화", 22, 14);
        MenuButton.Margin = new Thickness(0, 1, 2, 1); MenuButton.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(MenuButton, $"{string.Join(" · ", titles)} 그룹 메뉴");
        Density.Mark(MenuButton, DensityRole.Keep);
        DockPanel.SetDock(MenuButton, Dock.Right); header.Children.Add(MenuButton);
        // Tabs wrap to a second row rather than clipping a long translated name.
        var strip = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i; var tab = tabs[i];
            var button = new Button { Content = new TextBlock { Text = tab.Title, FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center }, ToolTip = tab.Tip, Focusable = true };
            button.Template = TabTemplate();
            button.MinHeight = HeaderHeight; button.Padding = new Thickness(8, 0, 8, 0); button.Cursor = Cursors.Hand;
            button.Click += (_, _) => Select(index);
            button.MouseDoubleClick += (_, e) => { toggle(this); e.Handled = true; };
            AutomationProperties.SetName(button, $"{tab.Title} 패널");
            Density.Mark(button, DensityRole.Keep); Density.Mark((FrameworkElement)button.Content, DensityRole.Keep);
            button.MouseEnter += (_, _) => { if (index != ActiveIndex && button.Content is TextBlock label) label.Foreground = Theme.Text; };
            button.MouseLeave += (_, _) => Paint();
            tabButtons.Add(button); strip.Children.Add(button);
        }
        header.Children.Add(strip);
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && ReferenceEquals(e.OriginalSource, header)) { toggle(this); e.Handled = true; } };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(body);
        Child = root;
        AutomationProperties.SetName(this, $"{string.Join(" · ", titles)} 패널 그룹");
    }

    static ControlTemplate TabTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border)) { Name = "Surface" };
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Control.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Control.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(presenter); template.VisualTree = surface;
        return template;
    }

    /// <summary>Shows a tab: its content moves into the group (out of wherever it was) and the tab row follows.</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        bool changed = index != ActiveIndex;
        ActiveIndex = index;
        if (!Collapsed && Hosted) Show();
        Paint();
        if (changed) ActiveChanged?.Invoke(this);
    }

    /// <summary>Puts the open tab's content into the body (after unfolding or a screen switch).</summary>
    public void Show()
    {
        if (ActiveIndex < 0) ActiveIndex = 0;
        var content = Tabs[ActiveIndex].Content();
        if (ReferenceEquals(body.Child, content)) return;
        body.Child = null;
        Detach(content);
        body.Child = content;
    }

    /// <summary>Lets go of the content so a pane can return to the friendly screen or another host.</summary>
    public void Release() { Hosted = false; body.Child = null; }

    public void SetTitle(int index, string title)
    {
        titles[index] = title;
        if (tabButtons[index].Content is TextBlock text) text.Text = title;
        AutomationProperties.SetName(tabButtons[index], $"{title} 패널");
    }

    public string Title(int index) => titles[index];

    internal void Paint()
    {
        for (int i = 0; i < tabButtons.Count; i++)
        {
            bool on = i == ActiveIndex;
            var button = tabButtons[i];
            button.Background = on ? Theme.Panel : Brushes.Transparent;
            button.Foreground = on ? Theme.Text : Theme.Muted;
            button.BorderBrush = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            if (button.Content is TextBlock text) text.Foreground = on ? Theme.Text : Theme.Muted;
            AutomationProperties.SetItemStatus(button, on ? "열림" : "");
        }
        Background = Theme.Panel;
    }

    // A live element has one parent: take it from a panel, decorator or content host first.
    internal static void Detach(UIElement element)
    {
        switch (LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element))
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element): decorator.Child = null; break;
            case ContentControl host when ReferenceEquals(host.Content, element): host.Content = null; break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, element): presenter.Content = null; break;
        }
    }
}
