using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Compositor.Windows;

// Quick blend/opacity row above the layer list, the empty-list hint, and the
// group · mask · adjustment buttons in the list footer.
public sealed partial class MainWindow
{
    ComboBox? layerQuickBlend;
    Slider? layerQuickOpacity;
    TextBlock? layerQuickOpacityValue;
    TextBlock? layerEmptyHint;
    bool syncingLayerQuickBar, layerOpacityDragging;

    FrameworkElement BuildLayerQuickBar()
    {
        var row = new Grid { Margin = new Thickness(8, 0, 8, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layerQuickBlend = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 0, 8, 0), ToolTip = "선택 레이어의 혼합 모드" };
        foreach (var mode in Enum.GetValues<BlendMode>()) layerQuickBlend.Items.Add(new ComboBoxItem { Content = BlendLabel(mode), Tag = mode });
        System.Windows.Automation.AutomationProperties.SetName(layerQuickBlend, "레이어 혼합 모드");
        layerQuickBlend.SelectionChanged += (_, _) =>
        {
            if (syncingLayerQuickBar || doc.Active is not { } active || layerQuickBlend.SelectedItem is not ComboBoxItem { Tag: BlendMode mode } || active.Blend == mode) return;
            Guard(() => EditLayer("혼합 모드", l => l.Blend = mode));
        };
        row.Children.Add(layerQuickBlend);
        layerQuickOpacity = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "선택 레이어의 불투명도 · 놓으면 적용" };
        System.Windows.Automation.AutomationProperties.SetName(layerQuickOpacity, "레이어 불투명도");
        Grid.SetColumn(layerQuickOpacity, 1); row.Children.Add(layerQuickOpacity);
        layerQuickOpacityValue = Theme.Label("100%", Theme.CaptionSize, Theme.Text); layerQuickOpacityValue.MinWidth = 36; layerQuickOpacityValue.TextAlignment = TextAlignment.Right; layerQuickOpacityValue.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(layerQuickOpacityValue, 2); row.Children.Add(layerQuickOpacityValue);
        // Dragging shows the number only; one history step is recorded when the thumb is released.
        layerQuickOpacity.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => layerOpacityDragging = true));
        layerQuickOpacity.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { layerOpacityDragging = false; CommitQuickOpacity(); }));
        layerQuickOpacity.ValueChanged += (_, e) =>
        {
            layerQuickOpacityValue.Text = $"{e.NewValue:0}%";
            if (!syncingLayerQuickBar && !layerOpacityDragging) CommitQuickOpacity();
        };
        return row;
    }

    internal void CommitQuickOpacity()
    {
        if (layerQuickOpacity == null || doc.Active is not { } active) return;
        double value = Math.Round(layerQuickOpacity.Value) / 100;
        if (Math.Abs(active.Opacity - value) < .0001) return;
        Guard(() => EditLayer("불투명도", l => l.Opacity = value));
    }

    void UpdateLayerQuickBar(int shownEntries)
    {
        if (layerQuickBlend == null || layerQuickOpacity == null) return;
        syncingLayerQuickBar = true;
        try
        {
            var active = HasDocument ? doc.Active : null;
            bool editable = active != null && !IsLockedWithParents(active);
            layerQuickBlend.IsEnabled = layerQuickOpacity.IsEnabled = editable;
            layerQuickBlend.SelectedItem = layerQuickBlend.Items.OfType<ComboBoxItem>().FirstOrDefault(i => active != null && i.Tag is BlendMode m && m == active.Blend);
            if (!layerOpacityDragging) layerQuickOpacity.Value = active == null ? 100 : Math.Round(active.Opacity * 100);
            layerQuickOpacityValue!.Text = $"{layerQuickOpacity.Value:0}%";
        }
        finally { syncingLayerQuickBar = false; }
        if (layerEmptyHint != null)
        {
            layerEmptyHint.Visibility = shownEntries == 0 ? Visibility.Visible : Visibility.Collapsed;
            layerEmptyHint.Text = !HasDocument ? "문서를 열거나 새로 만들면 레이어가 여기에 표시됩니다."
                : layerCategory == LayerCategory.Drawing ? "도면 레이어가 없습니다.\nDWG·DXF·PDF 도면을 가져오면 원본 레이어별로 표시됩니다."
                : "사진 레이어가 없습니다.\n아래 + 단추로 새 레이어를 만들거나 이미지를 가져오세요.";
        }
    }

    TextBlock BuildLayerEmptyHint()
    {
        layerEmptyHint = Theme.Label("", Theme.BodySize, Theme.Muted);
        layerEmptyHint.TextWrapping = TextWrapping.Wrap; layerEmptyHint.TextAlignment = TextAlignment.Center;
        layerEmptyHint.Margin = new Thickness(18, 24, 18, 0); layerEmptyHint.VerticalAlignment = VerticalAlignment.Top; layerEmptyHint.IsHitTestVisible = false;
        layerEmptyHint.Visibility = Visibility.Collapsed;
        return layerEmptyHint;
    }

    Button LayerAdjustmentButton(Func<string, string, Action, Button> make)
    {
        Button? button = null;
        button = make(Theme.Glyphs.Adjustment, "조정 레이어 추가", () =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
            foreach (var (label, kind) in new[] { ("노출…", AdjustmentKind.Exposure), ("레벨…", AdjustmentKind.Levels), ("곡선…", AdjustmentKind.Curves), ("색조 / 채도…", AdjustmentKind.HueSaturation), ("그라데이션 맵…", AdjustmentKind.GradientMap), ("그레인…", AdjustmentKind.Grain), ("한계값…", AdjustmentKind.Threshold), ("망점 (하프톤)…", AdjustmentKind.Halftone), ("종이·인쇄 질감…", AdjustmentKind.PaperTexture), ("빛 번짐…", AdjustmentKind.Glow), ("사진 현상…", AdjustmentKind.PhotoDevelop) })
            {
                var item = new MenuItem { Header = label }; var chosen = kind;
                item.Click += (_, _) => Guard(() => ShowAdjustment(chosen));
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        });
        return button;
    }
}
