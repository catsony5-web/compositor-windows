using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Collapsible section title (Theme.Section). Folding hides the following siblings in the
// same panel up to the next header, so panels keep their flat child structure. Hidden
// siblings get their previous Visibility back when the section opens again. The folded
// titles are shared by every panel and saved with the workspace layout.
public sealed class SectionHeader : ToggleButton
{
    static readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    // Sections that start folded (e.g. the tone grid) stay folded until the user opens them in this session.
    static readonly HashSet<string> openedDefaults = new(StringComparer.Ordinal);
    readonly bool foldedByDefault;
    static readonly List<WeakReference<SectionHeader>> live = [];
    readonly Dictionary<UIElement, Visibility> hidden = [];
    readonly RotateTransform turn = new();
    readonly FrameworkElement chevron;
    public string Key { get; }
    public bool Folded => IsChecked == true;

    public SectionHeader(string title, bool rule = true, bool foldedByDefault = false)
    {
        Key = title; this.foldedByDefault = foldedByDefault;
        var label = Theme.Label(title, Theme.BodySize); label.FontWeight = FontWeights.SemiBold; label.VerticalAlignment = VerticalAlignment.Center;
        chevron = Theme.Glyph(Theme.Glyphs.ChevronDown, 12, Theme.Muted);
        chevron.RenderTransformOrigin = new Point(.5, .5); chevron.RenderTransform = turn; chevron.VerticalAlignment = VerticalAlignment.Center; chevron.Margin = new Thickness(8, 0, 2, 0);
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 6), Background = Brushes.Transparent };
        DockPanel.SetDock(chevron, Dock.Right); row.Children.Add(chevron); row.Children.Add(label);
        var template = new ControlTemplate(typeof(ToggleButton));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        Template = template; Cursor = Cursors.Hand;
        Content = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, rule ? 1 : 0, 0, 0), Background = Brushes.Transparent, Child = row };
        if (!rule) row.Margin = new Thickness(0, 2, 0, 6);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; Margin = new Thickness(0, rule ? 10 : 0, 0, 2);
        SetResourceReference(FocusVisualStyleProperty, "UiFocusRing");
        AutomationProperties.SetName(this, title); AutomationProperties.SetHelpText(this, "섹션 접기 또는 펼치기");
        IsChecked = StartsFolded(title, foldedByDefault); UpdateChevron();
        Checked += (_, _) => { collapsed.Add(Key); if (this.foldedByDefault) openedDefaults.Remove(Key); Apply(); };
        Unchecked += (_, _) => { collapsed.Remove(Key); if (this.foldedByDefault) openedDefaults.Add(Key); Apply(); };
        MouseEnter += (_, _) => chevron.Opacity = 1;
        MouseLeave += (_, _) => chevron.Opacity = .75;
        chevron.Opacity = .75;
        lock (live) { live.RemoveAll(reference => !reference.TryGetTarget(out _)); live.Add(new WeakReference<SectionHeader>(this)); }
    }

    // A folded header added to a panel applies itself once the builder has added the
    // section's rows, before the panel renders.
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (Folded && Parent is Panel) Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Apply));
    }

    internal void Apply()
    {
        UpdateChevron();
        if (Parent is not Panel panel) { hidden.Clear(); return; }
        if (Folded)
        {
            for (int i = panel.Children.IndexOf(this) + 1; i < panel.Children.Count && panel.Children[i] is not SectionHeader; i++)
            {
                var child = panel.Children[i];
                if (hidden.ContainsKey(child)) continue;
                hidden[child] = child.Visibility; child.Visibility = Visibility.Collapsed;
            }
            return;
        }
        // Rows whose visibility changed while folded keep the newer value.
        foreach (var (child, previous) in hidden)
            if (child.Visibility == Visibility.Collapsed && panel.Children.Contains(child)) child.Visibility = previous;
        hidden.Clear();
    }

    static bool StartsFolded(string key, bool byDefault) => collapsed.Contains(key) || (byDefault && !openedDefaults.Contains(key));

    void UpdateChevron() => turn.Angle = Folded ? -90 : 0;

    public static string[] CollapsedKeys => collapsed.Order(StringComparer.Ordinal).ToArray();

    public static void SetCollapsedKeys(IEnumerable<string> keys)
    {
        collapsed.Clear(); collapsed.UnionWith(keys);
        SectionHeader[] headers;
        lock (live) headers = live.Select(reference => reference.TryGetTarget(out var header) ? header : null).OfType<SectionHeader>().ToArray();
        foreach (var header in headers) header.IsChecked = StartsFolded(header.Key, header.foldedByDefault);
    }
}
