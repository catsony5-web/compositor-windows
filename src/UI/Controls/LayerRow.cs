using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Compositor.Windows;

// Full-row selection sits behind the independent visibility, group, and lock buttons.
// Columns: 0 visibility · 1 group expander · 2 thumbnail · 3 labels · 4 lock.
// 간결한 화면 (compact) rows are 26 DIP: a 20 DIP thumbnail and the name on one line; the kind
// and opacity line moves into the row's tooltip.
public sealed class LayerRow : Grid
{
    public Button DragHandle { get; }
    // The visibility eye; a press here never starts a reorder drag (that starts on DragHandle).
    public Button Eye { get; }
    public bool Compact { get; }
    readonly string layerName;
    public LayerRow(Layer layer, bool selected, Action select, Action<bool> setVisible, Action toggleLock, bool expanded = true, Action? toggleExpand = null, string? description = null, bool compact = false)
    {
        Compact = compact;
        MinHeight = compact ? 26 : 40; layerName = layer.Name;
        Background = selected ? Theme.Selected : Brushes.Transparent;
        double eyeColumn = compact ? 24 : 28, expandColumn = compact ? 12 : 14, thumbColumn = compact ? 26 : 34, thumbSize = compact ? 20 : 28;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(eyeColumn) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(expandColumn) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(thumbColumn) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(eyeColumn) });
        if (selected)
            Children.Add(new Border { Width = 2, Background = Theme.Accent, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = compact ? new Thickness(0, 4, 0, 4) : new Thickness(0, 7, 0, 7), IsHitTestVisible = false });

        var content = new Grid { IsHitTestVisible = false, Margin = new Thickness(eyeColumn + expandColumn, 0, eyeColumn, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(thumbColumn) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        var thumb = new Border
        {
            Width = thumbSize, Height = thumbSize, CornerRadius = new CornerRadius(compact ? 2 : 5), BorderThickness = new Thickness(1), BorderBrush = Theme.Stroke,
            Background = ThumbnailBacking(layer), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        thumb.Child = layer.Kind is LayerKind.Group or LayerKind.Adjustment
            ? Theme.Glyph(layer.Kind == LayerKind.Adjustment ? Theme.Glyphs.Adjustment : layer.Style != null ? Theme.Glyphs.Style : Theme.Glyphs.Folder, compact ? 14 : 16, compact ? Theme.Muted : Theme.Accent, 1.6)
            : new Image { Source = (layer.Material is { } fill ? MaterialRenderer.PatternThumbnail(fill) : null) ?? layer.Pixels.Thumbnail(), Stretch = Stretch.Uniform, Margin = new Thickness(compact ? 1 : 2) };
        content.Children.Add(thumb);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = compact ? new Thickness(6, 2, 4, 2) : new Thickness(8, 4, 4, 4) };
        labels.Children.Add(Loc.Keep(new TextBlock { Text = layer.Name, FontSize = Theme.BodySize, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = layer.Name }));
        var kind = layer.Kind switch { LayerKind.Material when LinePatterns.IsPattern(layer.Material?.Asset) => "해치 패턴", LayerKind.Material => "재료 맵핑", LayerKind.Vector => "벡터 원본", LayerKind.Shape => "벡터 도형", LayerKind.Text => "텍스트", LayerKind.Adjustment => "조정", LayerKind.Group when layer.Style != null => "스타일 그룹", LayerKind.Group => "그룹", _ => "이미지" };
        var detail = description ?? $"{kind} · {layer.Opacity * 100:0}%";
        if (layer.Clipped) detail += " · 클리핑";
        if (layer.Mask != null) detail += " · 마스크";
        labels.Children.Add(new TextBlock { Text = detail, Foreground = Theme.Subtle, FontSize = Theme.CaptionSize, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Visibility = compact ? Visibility.Collapsed : Visibility.Visible });
        SetColumn(labels, 1); content.Children.Add(labels);

        var selector = DragHandle = new Button
        {
            Content = content, Margin = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            ToolTip = compact ? $"{layer.Name} · {detail}\n선택 · Shift+클릭: 범위 선택 · Ctrl+클릭: 추가/빼기" : $"{layer.Name} 선택 · Shift+클릭: 범위 선택 · Ctrl+클릭: 추가/빼기", Focusable = false
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

        var visible = Eye = IconButton(EyeIcon(layer.Visible, compact), EyeTip(layer.Visible), () => setVisible(!layer.Visible), compact);
        AutomationProperties.SetName(visible, $"레이어 표시: {layer.Name}, {(layer.Visible ? "표시됨" : "숨김")}");
        Children.Add(visible);

        if (layer.Kind == LayerKind.Group && toggleExpand != null)
        {
            var chevron = Theme.Glyph(expanded ? Theme.Glyphs.ChevronDown : Theme.Glyphs.ChevronRight, compact ? 10 : 12, Theme.Muted, 2);
            chevron.HorizontalAlignment = HorizontalAlignment.Center; chevron.VerticalAlignment = VerticalAlignment.Center;
            var expand = IconButton(chevron, expanded ? "그룹 접기" : "그룹 펼치기", toggleExpand, compact);
            expand.Width = expandColumn;
            AutomationProperties.SetName(expand, $"그룹 {(expanded ? "접기" : "펼치기")}: {layer.Name}");
            SetColumn(expand, 1); Children.Add(expand);
        }

        var locked = IconButton(LockIcon(layer.Locked, compact), layer.Locked ? "레이어 잠금 해제" : "레이어 잠금", toggleLock, compact);
        AutomationProperties.SetName(locked, $"레이어 잠금: {layer.Name}, {(layer.Locked ? "잠김" : "잠금 해제")}");
        SetColumn(locked, 4); Children.Add(locked);
    }

    // Drawing line work is mostly dark; it sits on paper as it does on the canvas.
    // Linework and transparent hatch patterns show on paper.
    internal static Brush ThumbnailBacking(Layer layer) => layer.Kind == LayerKind.Vector || layer.Vector != null || LinePatterns.IsPattern(layer.Material?.Asset) ? Theme.Paper : Theme.Input;

    static Button IconButton(UIElement icon, string tooltip, Action click, bool compact = false)
    {
        var button = Theme.Styled(Theme.Button("", click, tooltip), "IconButton");
        button.Content = icon;
        button.Width = compact ? 22 : 26; button.Height = compact ? 22 : 28;
        if (compact) Density.Mark(button, DensityRole.Keep);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Focusable = false;
        return button;
    }

    // Shows a new state while a drag across the eyes is still running, without rebuilding the row.
    public void ShowVisible(bool visible)
    {
        Eye.Content = EyeIcon(visible, Compact); Eye.ToolTip = EyeTip(visible);
        AutomationProperties.SetName(Eye, $"레이어 표시: {layerName}, {(visible ? "표시됨" : "숨김")}");
    }

    internal static string EyeTip(bool visible) => string.Join("\n", visible ? "레이어 숨기기" : "레이어 표시",
        "Shift+클릭: 마지막으로 누른 눈(없으면 맨 위 레이어)의 상태를 여기까지 적용", "Alt+클릭: 이 레이어만 보기",
        "위아래로 드래그(Shift+드래그도 가능): 지나간 눈에 같은 상태 적용 · Esc 취소");

    static UIElement EyeIcon(bool visible, bool compact = false) => Theme.Glyph(visible ? Theme.Glyphs.Eye : Theme.Glyphs.EyeSlash, compact ? 15 : 18, visible ? Theme.Muted : Theme.Subtle);

    // Locked layers show a filled, bright lock; unlocked rows keep a quiet open shackle.
    static UIElement LockIcon(bool locked, bool compact = false) => Theme.Glyph(locked ? Theme.Glyphs.Lock : Theme.Glyphs.LockOpen, compact ? 14 : 17, locked ? Theme.Text : Theme.Subtle);
}
