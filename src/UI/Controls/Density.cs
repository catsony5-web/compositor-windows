using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// What a control is, for the 간결한 화면 density pass (Density.Mark).
public enum DensityRole { None, Keep, Glyph, Description, Tile, TileLabel, Feature, Badge, Row }

// 간결한 화면 density. The main window root carries Density.Compact, an inherited flag, so every
// control inside it - including panels built later or moved in from elsewhere - is told when it
// enters or leaves a compact tree. Controls keep the friendly sizes in their own code; here,
// entering a compact tree steps the type down (14/13 → 12, 12 → 11), lowers control heights and
// paddings, turns icon tiles into icon buttons and hides description lines (their tooltips keep
// the full text). Every value changed here is remembered and restored when the control leaves
// the compact tree, so 친절한 화면 is never altered. Only plain local values are touched: values
// from styles, bindings and resources are left to Theme.xaml.
public static class Density
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached("Compact", typeof(bool), typeof(Density),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnCompactChanged));
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached("Role", typeof(DensityRole), typeof(Density), new PropertyMetadata(DensityRole.None));
    static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached("Saved", typeof(Dictionary<DependencyProperty, object>), typeof(Density), new PropertyMetadata(null));

    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);
    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);
    public static DensityRole GetRole(DependencyObject element) => (DensityRole)element.GetValue(RoleProperty);
    public static T Mark<T>(T element, DensityRole role) where T : DependencyObject { element.SetValue(RoleProperty, role); return element; }

    static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || GetRole(element) == DensityRole.Keep) return;
        if ((bool)e.NewValue) Apply(element); else Restore(element);
    }

    // Applies the compact values to one element (the inherited flag does this automatically; tests call it directly).
    internal static void Apply(FrameworkElement element)
    {
        if (element.GetValue(SavedProperty) != null) return;
        var role = GetRole(element);
        switch (role)
        {
            case DensityRole.Description or DensityRole.TileLabel:
                Set(element, UIElement.VisibilityProperty, Visibility.Collapsed); return;
            case DensityRole.Glyph:
                if (element.Width is > 16 and <= 24) { Set(element, FrameworkElement.WidthProperty, 16d); Set(element, FrameworkElement.HeightProperty, 16d); }
                return;
            case DensityRole.Badge:
                Set(element, FrameworkElement.WidthProperty, 24d); Set(element, FrameworkElement.HeightProperty, 24d);
                if (element is Border badge) { Set(badge, Border.CornerRadiusProperty, new CornerRadius(3)); Set(badge, FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)); }
                return;
            case DensityRole.Row:
                if (Plain(element, FrameworkElement.MarginProperty) is Thickness row && row.Bottom > 4) Set(element, FrameworkElement.MarginProperty, row with { Bottom = 4 });
                return;
        }
        if (element is SectionHeader header)
        {
            if (Plain(header, FrameworkElement.MarginProperty) is Thickness outer) Set(header, FrameworkElement.MarginProperty, new Thickness(outer.Left, Math.Min(outer.Top, 4), outer.Right, 0));
            if (header.Content is Border { Child: FrameworkElement row } && Plain(row, FrameworkElement.MarginProperty) is Thickness inner)
                Set(row, FrameworkElement.MarginProperty, new Thickness(inner.Left, Math.Min(inner.Top, 6), inner.Right, Math.Min(inner.Bottom, 3)));
        }
        if (element is TextBlock text) StepFont(text, TextBlock.FontSizeProperty);
        if (element is not Control control) return;
        StepFont(control, Control.FontSizeProperty);
        if (role == DensityRole.Tile)
        {
            // An icon tile becomes an icon button; its name stays in the tooltip and accessible name.
            Set(control, FrameworkElement.MinHeightProperty, 0d); Set(control, Control.PaddingProperty, new Thickness(2, 5, 2, 5)); Set(control, FrameworkElement.MarginProperty, new Thickness(1));
            return;
        }
        if (role == DensityRole.Feature)
        {
            Set(control, Control.PaddingProperty, new Thickness(6, 4, 8, 4)); Set(control, FrameworkElement.MarginProperty, new Thickness(1, 1, 1, 4));
            return;
        }
        if (Plain(control, FrameworkElement.MinHeightProperty) is double minimum && minimum >= 28)
            Set(control, FrameworkElement.MinHeightProperty, minimum >= 36 ? 28d : minimum >= 32 ? 26d : 24d);
        // Square icon buttons step down with the rest (28/30 → 24, 32/34 → 26).
        if (Plain(control, FrameworkElement.HeightProperty) is double height && Plain(control, FrameworkElement.WidthProperty) is double width && height == width && height >= 28 && height <= 36)
        {
            double size = height >= 32 ? 26 : 24;
            Set(control, FrameworkElement.WidthProperty, size); Set(control, FrameworkElement.HeightProperty, size);
        }
        if (Plain(control, Control.PaddingProperty) is Thickness padding && (padding.Top > 3 || padding.Bottom > 3 || padding.Left > 8 || padding.Right > 8))
            Set(control, Control.PaddingProperty, new Thickness(Math.Min(padding.Left, 8), Math.Min(padding.Top, 3), Math.Min(padding.Right, 8), Math.Min(padding.Bottom, 3)));
    }

    // Puts back every value Apply changed.
    internal static void Restore(FrameworkElement element)
    {
        if (element.GetValue(SavedProperty) is not Dictionary<DependencyProperty, object> saved) return;
        element.ClearValue(SavedProperty);
        foreach (var (property, value) in saved)
        {
            if (value == DependencyProperty.UnsetValue) element.ClearValue(property);
            else element.SetValue(property, value);
        }
    }

    // 14 and 13 → 12, 12 → 11; titles (15 and up) and smaller text keep their size.
    static void StepFont(FrameworkElement element, DependencyProperty property)
    {
        if (Plain(element, property) is not double size) return;
        double next = size is >= 12.5 and < 15 ? 12 : size is >= 11.5 and < 12.5 ? 11 : size;
        if (next != size) Set(element, property, next);
    }

    // The local value, unless it is unset or an expression (binding, resource or template reference).
    static object? Plain(DependencyObject element, DependencyProperty property)
    {
        var value = element.ReadLocalValue(property);
        return value == DependencyProperty.UnsetValue || value is Expression ? null : value;
    }

    static void Set(FrameworkElement element, DependencyProperty property, object value)
    {
        if (element.GetValue(SavedProperty) is not Dictionary<DependencyProperty, object> saved)
            element.SetValue(SavedProperty, saved = []);
        if (!saved.ContainsKey(property))
        {
            var local = element.ReadLocalValue(property);
            if (local is Expression) return;
            saved[property] = local;
        }
        element.SetValue(property, value);
    }

    // Visits a detached tree once (offscreen captures and checks): the inherited flag only reaches
    // elements that are in a tree, and a parked pane is not.
    internal static void ApplyTree(DependencyObject root, bool compact)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        void Walk(DependencyObject node)
        {
            if (!seen.Add(node)) return;
            if (node is FrameworkElement element && GetRole(element) != DensityRole.Keep) { if (compact) Apply(element); else Restore(element); }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
            if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
    }
}
