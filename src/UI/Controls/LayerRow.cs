using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Compositor.Windows;

// Full-row selection sits behind the independent visibility, group, and lock buttons.
public sealed class LayerRow : Grid
{
    public Button DragHandle { get; }
    public LayerRow(Layer layer, bool selected, Action select, Action<bool> setVisible, Action toggleLock, bool expanded = true, Action? toggleExpand = null)
    {
        MinHeight = 60;
        Background = selected ? Theme.Selected : Theme.Surface;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var content = new Grid { IsHitTestVisible = false, Margin = new Thickness(44, 0, 30, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        if (layer.Kind is LayerKind.Group or LayerKind.Adjustment)
        {
            content.Children.Add(new TextBlock
            {
                Text = layer.Kind == LayerKind.Group ? "▤" : "◐", FontSize = 22,
                Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            content.Children.Add(new Image
            {
                Source = layer.Pixels.Thumbnail(), Width = 34, Height = 34,
                Stretch = Stretch.Uniform, Margin = new Thickness(2)
            });
        }

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 8, 4, 8) };
        labels.Children.Add(new TextBlock { Text = layer.Name, FontSize = Theme.BodySize, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, ToolTip = layer.Name });
        var kind = layer.Kind switch { LayerKind.Shape => "벡터 도형", LayerKind.Text => "텍스트", LayerKind.Adjustment => "조정", LayerKind.Group => "그룹", _ => "이미지" };
        var detail = $"{kind} · {layer.Opacity * 100:0}%";
        if (layer.Clipped) detail += " · 클리핑";
        if (layer.Mask != null) detail += " · 마스크";
        labels.Children.Add(new TextBlock { Text = detail, Foreground = Theme.Muted, FontSize = Theme.CaptionSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        SetColumn(labels, 1); content.Children.Add(labels);

        var selector = DragHandle = new Button
        {
            Content = content, Margin = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            ToolTip = $"{layer.Name} 선택", Focusable = false
        };
        // The default Button template limits its clickable content area.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        border.AppendChild(presenter); template.VisualTree = border; selector.Template = template;
        AutomationProperties.SetName(selector, $"레이어 선택: {layer.Name}");
        selector.Click += (_, _) => select();
        SetColumnSpan(selector, 5); Children.Add(selector);

        var visible = IconButton(EyeIcon(layer.Visible), layer.Visible ? "레이어 숨기기" : "레이어 표시", () => setVisible(!layer.Visible));
        AutomationProperties.SetName(visible, $"레이어 표시: {layer.Name}, {(layer.Visible ? "표시됨" : "숨김")}");
        Children.Add(visible);

        if (layer.Kind == LayerKind.Group && toggleExpand != null)
        {
            var expand = IconButton(new TextBlock
            {
                Text = expanded ? "▾" : "▸", Foreground = Theme.Text,
                FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }, expanded ? "그룹 접기" : "그룹 펼치기", toggleExpand);
            AutomationProperties.SetName(expand, $"그룹 {(expanded ? "접기" : "펼치기")}: {layer.Name}");
            SetColumn(expand, 1); Children.Add(expand);
        }

        var locked = IconButton(LockIcon(layer.Locked), layer.Locked ? "레이어 잠금 해제" : "레이어 잠금", toggleLock);
        AutomationProperties.SetName(locked, $"레이어 잠금: {layer.Name}, {(layer.Locked ? "잠김" : "잠금 해제")}");
        SetColumn(locked, 4); Children.Add(locked);
    }

    static Button IconButton(UIElement icon, string tooltip, Action click)
    {
        var button = Theme.Button("", click, tooltip);
        button.Content = icon;
        button.Width = 26; button.Height = 32;
        button.Padding = new Thickness(0); button.Margin = new Thickness(1, 0, 1, 0);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.Focusable = false;
        return button;
    }

    static UIElement EyeIcon(bool visible)
    {
        var icon = new Grid { Width = 20, Height = 20 };
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 1,10 C 5,4 15,4 19,10 C 15,16 5,16 1,10 Z"),
            Stroke = visible ? Theme.Text : Theme.Muted, StrokeThickness = 1.5,
            Fill = Brushes.Transparent
        });
        icon.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, Fill = visible ? Theme.Text : Theme.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (!visible) icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 2,18 L 18,2"), Stroke = Theme.Muted,
            StrokeThickness = 2
        });
        return icon;
    }

    static UIElement LockIcon(bool locked)
    {
        var icon = new Grid { Width = 20, Height = 20 };
        icon.Children.Add(new Path
        {
            Data = locked ? Geometry.Parse("M 5,9 V 6 C 5,0 15,0 15,6 V 9") : Geometry.Parse("M 6,9 V 6 C 6,0 16,0 16,6"),
            Stroke = locked ? Theme.Text : Theme.Muted, StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 4,9 H 16 V 18 H 4 Z"), Stroke = locked ? Theme.Text : Theme.Muted,
            StrokeThickness = 1.7, Fill = Brushes.Transparent
        });
        icon.Children.Add(new Ellipse
        {
            Width = 2, Height = 2, Fill = locked ? Theme.Text : Theme.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5)
        });
        return icon;
    }
}
