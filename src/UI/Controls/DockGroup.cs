using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// One tab of a 간결한 화면 panel group. Content is created on first use and is the live panel
// itself (the friendly screen's panes move in here), so both screens share one implementation.
// A tab belongs to one group at a time and moves between groups as the user rearranges the dock.
internal sealed class DockTab(string key, string title, string glyph, string tip, Func<UIElement> content)
{
    public string Key { get; } = key;
    public string Title { get; set; } = title;
    public string Glyph { get; } = glyph;
    public string Tip { get; } = tip;
    public Func<UIElement> Content { get; } = content;
}

// A panel group of the 간결한 화면: a 24 DIP tab row with the group menu (☰) over the open tab's
// content. The dock stacks groups and sizes them by Weight, folds a Collapsed group into its icon
// strip (opening it as a flyout beside the strip on demand) or shows a Floating group in its own
// tool window. Tabs can be added, removed and reordered while the group is live.
internal sealed class DockGroup : Border
{
    public const double HeaderHeight = 24;
    public string Key { get; }
    public IReadOnlyList<DockTab> Tabs => tabs;
    public int ActiveIndex { get; private set; } = -1;
    public DockTab ActiveTab => tabs.Count == 0 ? NoTab : tabs[Math.Clamp(ActiveIndex, 0, tabs.Count - 1)];
    // An emptied group leaves the dock at once; until then it reports no open tab.
    static readonly DockTab NoTab = new("", "", "", "", () => new Border());
    public bool Collapsed { get; set; }
    /// <summary>Shown in its own tool window (FloatingWindow while 간결한 화면 shows it).</summary>
    public bool Floating { get; set; }
    public Window? FloatingWindow { get; set; }
    /// <summary>The floating window's last bounds (DIPs), kept after docking so the next float opens there.</summary>
    public Rect? FloatBounds { get; set; }
    /// <summary>True while the dock shows (간결한 화면); otherwise tabs only remember which one is open.</summary>
    public bool Hosted { get; set; }
    public double Weight { get; set; }
    public double DefaultWeight { get; }
    public bool DefaultCollapsed { get; }
    public Button MenuButton { get; }
    /// <summary>📌 도킹: puts a flyout or floating group back into the stack.</summary>
    public Button DockButton { get; }
    internal IReadOnlyList<Button> TabButtons => tabButtons;
    internal Border Body => body;
    internal FrameworkElement Header => header;
    internal string TitleList => string.Join(" · ", tabs.Select(t => t.Title));
    readonly List<DockTab> tabs;
    readonly List<Button> tabButtons = [];
    readonly Border body = new() { ClipToBounds = true };
    readonly DockPanel header;
    readonly WrapPanel strip = new() { Orientation = Orientation.Horizontal };
    readonly Action<DockGroup> toggle;
    readonly Action<DockGroup, FrameworkElement, DockTab?>? dragSource;
    public event Action<DockGroup>? ActiveChanged;

    public DockGroup(string key, IEnumerable<DockTab> tabs, double weight, bool collapsed, Action<DockGroup> showMenu, Action<DockGroup> toggle,
        Action<DockGroup>? dockBack = null, Action<DockGroup, FrameworkElement, DockTab?>? dragSource = null)
    {
        Key = key; this.tabs = tabs.ToList(); Weight = DefaultWeight = weight; Collapsed = DefaultCollapsed = collapsed;
        this.toggle = toggle; this.dragSource = dragSource;
        Background = Theme.Panel; Density.Mark(this, DensityRole.Keep);
        var root = new DockPanel { LastChildFill = true };
        header = new DockPanel { Background = Theme.Header, MinHeight = HeaderHeight, LastChildFill = true, ToolTip = "빈 곳을 끌어 그룹 순서 바꾸기 · 두 번 누르면 접기" };
        MenuButton = Theme.IconButton(Theme.Glyphs.Menu, () => showMenu(this), "패널 그룹 메뉴 · 접기와 배치 초기화", 22, 14);
        MenuButton.Margin = new Thickness(0, 1, 2, 1); MenuButton.VerticalAlignment = VerticalAlignment.Top;
        Density.Mark(MenuButton, DensityRole.Keep);
        DockPanel.SetDock(MenuButton, Dock.Right); header.Children.Add(MenuButton);
        DockButton = Theme.IconButton(Theme.Glyphs.Pin, () => dockBack?.Invoke(this), "도킹 · 이 그룹을 오른쪽 도크에 다시 넣습니다", 22, 14);
        DockButton.Margin = new Thickness(0, 1, 0, 1); DockButton.VerticalAlignment = VerticalAlignment.Top; DockButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(DockButton, "도킹");
        Density.Mark(DockButton, DensityRole.Keep);
        DockPanel.SetDock(DockButton, Dock.Right); header.Children.Add(DockButton);
        // Tabs wrap to a second row rather than clipping a long translated name.
        header.Children.Add(strip);
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && ReferenceEquals(e.OriginalSource, header)) { toggle(this); e.Handled = true; } };
        dragSource?.Invoke(this, header, null);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(body);
        Child = root;
        RebuildTabs();
    }

    // One button per tab; the dock wires each button (and the header) as a drag source.
    void RebuildTabs()
    {
        strip.Children.Clear(); tabButtons.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var button = new Button { Content = new TextBlock { Text = tab.Title, FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center }, ToolTip = $"{tab.Tip} · 끌어서 다른 그룹으로 옮기기", Focusable = true };
            button.Template = TabTemplate();
            button.MinHeight = HeaderHeight; button.Padding = new Thickness(8, 0, 8, 0); button.Cursor = Cursors.Hand;
            button.Click += (_, _) => Select(tabs.IndexOf(tab));
            button.MouseDoubleClick += (_, e) => { toggle(this); e.Handled = true; };
            AutomationProperties.SetName(button, $"{tab.Title} 패널");
            Density.Mark(button, DensityRole.Keep); Density.Mark((FrameworkElement)button.Content, DensityRole.Keep);
            button.MouseEnter += (_, _) => { if (tabs.IndexOf(tab) != ActiveIndex && button.Content is TextBlock label) label.Foreground = Theme.Text; };
            button.MouseLeave += (_, _) => Paint();
            dragSource?.Invoke(this, button, tab);
            tabButtons.Add(button); strip.Children.Add(button);
        }
        string names = TitleList;
        AutomationProperties.SetName(MenuButton, $"{names} 그룹 메뉴");
        AutomationProperties.SetName(this, $"{names} 패널 그룹");
        Paint();
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
        if (index < 0 || index >= tabs.Count) return;
        bool changed = index != ActiveIndex;
        ActiveIndex = index;
        if (Hosted) Show();
        Paint();
        if (changed) ActiveChanged?.Invoke(this);
    }

    /// <summary>Adds a tab at <paramref name="index"/> (clamped); the open tab stays open.</summary>
    public void Insert(int index, DockTab tab)
    {
        index = Math.Clamp(index, 0, tabs.Count);
        tabs.Insert(index, tab);
        if (ActiveIndex >= index) ActiveIndex++;
        RebuildTabs();
    }

    /// <summary>Takes a tab out. When it was open, its neighbour opens; an empty group shows nothing.</summary>
    public bool Remove(DockTab tab)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0) return false;
        bool wasActive = index == ActiveIndex;
        if (wasActive) body.Child = null;
        tabs.RemoveAt(index);
        if (tabs.Count == 0) { ActiveIndex = -1; body.Child = null; RebuildTabs(); return true; }
        if (index < ActiveIndex) ActiveIndex--;
        RebuildTabs();
        if (wasActive) { ActiveIndex = -1; Select(Math.Min(index, tabs.Count - 1)); }
        return true;
    }

    /// <summary>Moves a tab inside the group; the open tab stays open.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= tabs.Count) return;
        to = Math.Clamp(to, 0, tabs.Count - 1);
        if (from == to) return;
        var active = ActiveIndex >= 0 ? tabs[ActiveIndex] : null;
        var tab = tabs[from]; tabs.RemoveAt(from); tabs.Insert(to, tab);
        if (active != null) ActiveIndex = tabs.IndexOf(active);
        RebuildTabs();
    }

    /// <summary>Puts the open tab's content into the body (after unfolding or a screen switch).</summary>
    public void Show()
    {
        if (tabs.Count == 0) return;
        if (ActiveIndex < 0) ActiveIndex = 0;
        var content = tabs[ActiveIndex].Content();
        if (ReferenceEquals(body.Child, content)) return;
        body.Child = null;
        Detach(content);
        body.Child = content;
    }

    /// <summary>Lets go of the content so a pane can return to the friendly screen or another host.</summary>
    public void Release() { Hosted = false; body.Child = null; }

    /// <summary>The 📌 도킹 button shows while the group is a flyout or floats.</summary>
    public void SetDockButtonVisible(bool visible) => DockButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void SetTitle(int index, string title)
    {
        tabs[index].Title = title;
        if (tabButtons[index].Content is TextBlock text) text.Text = title;
        AutomationProperties.SetName(tabButtons[index], $"{title} 패널");
        string names = TitleList;
        AutomationProperties.SetName(MenuButton, $"{names} 그룹 메뉴");
        AutomationProperties.SetName(this, $"{names} 패널 그룹");
    }

    public string Title(int index) => tabs[index].Title;

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
