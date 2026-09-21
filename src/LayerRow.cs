using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// Selection and visibility are sibling controls so a toggle cannot also select
// the row. Use Click (after mouse-up), rather than rebuilding a row on mouse-down.
public sealed class LayerRow : Grid
{
    public LayerRow(Layer layer, bool selected, Action select, Action<bool> setVisible, Action toggleLock, bool expanded = true, Action? toggleExpand = null)
    {
        Height = 61; Margin = new Thickness(0, 2, 0, 2);
        Background = selected ? Theme.Brush("#35483F") : Theme.Brush("#272C35");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var content = new Grid { IsHitTestVisible = false, Margin = new Thickness(30, 0, 32, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        if (layer.Kind is LayerKind.Group or LayerKind.Adjustment)
            content.Children.Add(new TextBlock { Text = layer.Kind == LayerKind.Group ? "▤" : "◐", FontSize = 26, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        else content.Children.Add(new Image { Source = layer.Pixels.Bitmap(), Width = 40, Height = 38, Stretch = Stretch.Uniform, Margin = new Thickness(3) });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = layer.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 3, 3), FontSize = 11 });
        labels.Children.Add(new TextBlock { Text = $"{(layer.Clipped ? "↳ " : "")}{(layer.Kind == LayerKind.Text ? "T · " : "")}{layer.Blend} · {layer.Opacity * 100:0}%{(layer.Mask != null ? " · 마스크" : "")}", Foreground = Theme.Muted, FontSize = 9, Margin = new Thickness(7, 0, 0, 0) });
        SetColumn(labels, 1); content.Children.Add(labels);

        var selector = new Button
        {
            Content = content, Margin = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            ToolTip = $"{layer.Name} 선택", Focusable = false
        };
        // Fill the complete row, including the empty space around its name.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        border.AppendChild(presenter); template.VisualTree = border; selector.Template = template;
        AutomationProperties.SetName(selector, $"레이어 선택: {layer.Name}");
        selector.Click += (_, _) => select();
        SetColumnSpan(selector, 4); Children.Add(selector);
        if (layer.Kind == LayerKind.Group && toggleExpand != null)
        {
            var expand = Theme.Button(expanded ? "▾" : "▸", toggleExpand, expanded ? "그룹 접기" : "그룹 펼치기"); expand.Padding = new Thickness(0); expand.Margin = new Thickness(3, 13, 3, 13);
            SetColumn(expand, 1); Children.Add(expand);
        }

        var visible = new CheckBox
        {
            IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "표시 / 숨김 (선택은 바뀌지 않습니다)"
        };
        AutomationProperties.SetName(visible, $"레이어 표시: {layer.Name}");
        visible.Click += (_, _) => setVisible(visible.IsChecked == true);
        Children.Add(visible);

        var locked = Theme.Button(layer.Locked ? "●" : "○", toggleLock, "잠금 / 해제 (선택은 바뀌지 않습니다)");
        locked.FontSize = 13; locked.Padding = new Thickness(0); locked.Margin = new Thickness(2, 12, 3, 12);
        AutomationProperties.SetName(locked, $"레이어 잠금: {layer.Name}");
        SetColumn(locked, 3); Children.Add(locked);
    }
}
