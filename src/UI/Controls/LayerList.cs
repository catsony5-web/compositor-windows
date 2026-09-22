using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

internal sealed record LayerListEntry(Layer Layer, int Depth, bool Selected, bool Expanded, Guid[]? GroupMembers = null, string? Description = null);

// Store small row descriptions, not thousands of WPF controls and thumbnails.
// The internal ScrollViewer supplies a finite viewport to the virtualizing panel.
internal sealed class LayerList : ListBox
{
    long refreshVersion;
    public Func<LayerListEntry, LayerRow>? CreateRow { get; set; }

    public LayerList()
    {
        Background = Brushes.Transparent; BorderThickness = new Thickness(0);
        Padding = new Thickness(0); Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(.5));
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Page);
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.Name = "PART_ScrollViewer";
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(FocusableProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroll };

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(FocusableProperty, false));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(TemplateProperty,
            new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
        ItemContainerStyle = itemStyle;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ListBoxItem container && item is LayerListEntry entry)
            container.Content = CreateRow?.Invoke(entry);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem container) container.Content = null;
        base.ClearContainerForItemOverride(element, item);
    }

    public void SetEntries(IReadOnlyList<LayerListEntry> entries, Guid? reveal = null)
    {
        double offset = ScrollHost()?.VerticalOffset ?? 0;
        long version = ++refreshVersion;
        ItemsSource = entries;
        var target = reveal is { } id ? entries.FirstOrDefault(entry => entry.Layer.Id == id) : null;
        if (target != null) ScrollIntoView(target);
        // A refreshed source and the requested row are realized during layout.
        // Ignore callbacks from a document or selection that is already gone.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != refreshVersion) return;
            if (target != null) ScrollIntoView(target);
            else ScrollHost()?.ScrollToVerticalOffset(offset);
        }));
    }

    ScrollViewer? ScrollHost() => GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
}
