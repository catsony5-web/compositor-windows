using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Compositor.Windows;

// Full-row selection sits behind the independent visibility, group, and lock buttons.
// Columns: 0 visibility · 1 group expander · 2 thumbnail · 3 labels · 4 lock.
public sealed class LayerRow : Grid
{
    public Button DragHandle { get; }
    public LayerRow(Layer layer, bool selected, Action select, Action<bool> setVisible, Action toggleLock, bool expanded = true, Action? toggleExpand = null, string? description = null)
    {
        MinHeight = 40;
        Background = selected ? Theme.Selected : Brushes.Transparent;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        if (selected)
            Children.Add(new Border { Width = 2, Background = Theme.Accent, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 7), IsHitTestVisible = false });

        var content = new Grid { IsHitTestVisible = false, Margin = new Thickness(42, 0, 28, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        var thumb = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = Theme.Stroke,
            Background = Theme.Input, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        thumb.Child = layer.Kind is LayerKind.Group or LayerKind.Adjustment
            ? Theme.Glyph(layer.Kind == LayerKind.Group ? Theme.Glyphs.Folder : Theme.Glyphs.Adjustment, 16, Theme.Accent, 1.6)
            : new Image { Source = layer.Pixels.Thumbnail(), Stretch = Stretch.Uniform, Margin = new Thickness(2) };
        content.Children.Add(thumb);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4, 4, 4) };
        labels.Children.Add(new TextBlock { Text = layer.Name, FontSize = Theme.BodySize, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = layer.Name });
        var kind = layer.Kind switch { LayerKind.Material => "재료 맵핑", LayerKind.Vector => "벡터 원본", LayerKind.Shape => "벡터 도형", LayerKind.Text => "텍스트", LayerKind.Adjustment => "조정", LayerKind.Group => "그룹", _ => "이미지" };
        var detail = description ?? $"{kind} · {layer.Opacity * 100:0}%";
        if (layer.Clipped) detail += " · 클리핑";
        if (layer.Mask != null) detail += " · 마스크";
        labels.Children.Add(new TextBlock { Text = detail, Foreground = Theme.Subtle, FontSize = Theme.CaptionSize, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
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
        if (!selected)
        {
            MouseEnter += (_, _) => Background = Theme.RowHover;
            MouseLeave += (_, _) => Background = Brushes.Transparent;
        }

        var visible = IconButton(EyeIcon(layer.Visible), layer.Visible ? "레이어 숨기기" : "레이어 표시", () => setVisible(!layer.Visible));
        AutomationProperties.SetName(visible, $"레이어 표시: {layer.Name}, {(layer.Visible ? "표시됨" : "숨김")}");
        Children.Add(visible);

        if (layer.Kind == LayerKind.Group && toggleExpand != null)
        {
            var chevron = new Path
            {
                Data = Geometry.Parse(expanded ? "M 1 3 L 5 7 L 9 3" : "M 3 1 L 7 5 L 3 9"), Stroke = Theme.Muted, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var expand = IconButton(chevron, expanded ? "그룹 접기" : "그룹 펼치기", toggleExpand);
            expand.Width = 14;
            AutomationProperties.SetName(expand, $"그룹 {(expanded ? "접기" : "펼치기")}: {layer.Name}");
            SetColumn(expand, 1); Children.Add(expand);
        }

        var locked = IconButton(LockIcon(layer.Locked), layer.Locked ? "레이어 잠금 해제" : "레이어 잠금", toggleLock);
        AutomationProperties.SetName(locked, $"레이어 잠금: {layer.Name}, {(layer.Locked ? "잠김" : "잠금 해제")}");
        SetColumn(locked, 4); Children.Add(locked);
    }

    static Button IconButton(UIElement icon, string tooltip, Action click)
    {
        var button = Theme.Styled(Theme.Button("", click, tooltip), "IconButton");
        button.Content = icon;
        button.Width = 26; button.Height = 28;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Focusable = false;
        return button;
    }

    static UIElement EyeIcon(bool visible)
    {
        var icon = new Grid { Width = 18, Height = 18 };
        var stroke = visible ? Theme.Muted : Theme.Subtle;
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 1,9 C 4.5,3.8 13.5,3.8 17,9 C 13.5,14.2 4.5,14.2 1,9 Z"),
            Stroke = stroke, StrokeThickness = 1.4, Fill = Brushes.Transparent, StrokeLineJoin = PenLineJoin.Round
        });
        icon.Children.Add(new Ellipse
        {
            Width = 4.5, Height = 4.5, Fill = stroke,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (!visible) icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 2.5,15.5 L 15.5,2.5"), Stroke = Theme.Subtle,
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        return icon;
    }

    static UIElement LockIcon(bool locked)
    {
        var icon = new Grid { Width = 18, Height = 18 };
        var stroke = locked ? Theme.Text : Theme.Subtle;
        icon.Children.Add(new Path
        {
            Data = locked ? Geometry.Parse("M 5,8 V 5.5 C 5,1 13,1 13,5.5 V 8") : Geometry.Parse("M 5.5,8 V 5.5 C 5.5,1 13.5,1 13.5,5.5"),
            Stroke = stroke, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse("M 3.5,8 H 14.5 V 16 H 3.5 Z"), Stroke = stroke,
            StrokeThickness = 1.5, Fill = Brushes.Transparent, StrokeLineJoin = PenLineJoin.Round
        });
        return icon;
    }
}
