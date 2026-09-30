using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    BrushTip brushTip = BrushTip.Round;
    double brushSpacing = .10, brushAngle;
    readonly List<BrushTip> customBrushTips = [];
    readonly Dictionary<BrushTip, Button> brushTipButtons = [];
    ComboBox? customBrushPicker;
    TextBlock? customBrushPlaceholder;
    TextBlock? brushTipName;
    Image? selectedBrushPreview;
    readonly List<(Image Image, double Size, double Hardness)> brushPresetPreviews = [];

    internal static string TipGlyph(BrushTip tip) => tip.Id switch
    {
        "square" => Theme.Glyphs.TipSquare, "diamond" => Theme.Glyphs.TipDiamond, "star" => Theme.Glyphs.TipStar, _ => Theme.Glyphs.TipRound
    };
    bool syncingBrushTips;

    FrameworkElement BuildBrushTipControls()
    {
        var panel = new StackPanel();
        // Current brush as a real stroke sample; follows shape, size, hardness, angle and spacing.
        var current = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        current.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); current.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        brushTipName = Theme.Label("", Theme.BodySize); brushTipName.FontWeight = FontWeights.SemiBold; brushTipName.Margin = new Thickness(2, 0, 2, 4); current.Children.Add(brushTipName);
        selectedBrushPreview = new Image { Height = 64, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(selectedBrushPreview, "현재 브러시 획 미리보기");
        var stage = new Border { Background = Theme.Canvas, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = selectedBrushPreview };
        Grid.SetRow(stage, 1); current.Children.Add(stage);
        panel.Children.Add(current);
        panel.Children.Add(Theme.Section("브러시 모양"));
        var shapes = new UniformGrid { Columns = 4 };
        foreach (var tip in BrushTip.BuiltIns)
        {
            var button = QuickActions.Tile(TipGlyph(tip), tip.Name, () => SelectBrushTip(tip), tip.Name + " 브러시", tip.Name + " 브러시");
            brushTipButtons[tip] = button; shapes.Children.Add(button);
        }
        panel.Children.Add(shapes);
        panel.Children.Add(Theme.Label("사용자 이미지 브러시", Theme.CaptionSize, Theme.Muted));
        customBrushPicker = new ComboBox { MinHeight = 36, Margin = new Thickness(2, 3, 2, 5) };
        var tipLabel = new FrameworkElementFactory(typeof(TextBlock));
        tipLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(BrushTip.Name)));
        tipLabel.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        tipLabel.SetValue(FrameworkElement.MaxWidthProperty, 280d);
        customBrushPicker.ItemTemplate = new DataTemplate { VisualTree = tipLabel };
        AutomationProperties.SetName(customBrushPicker, "저장한 사용자 브러시");
        customBrushPicker.SelectionChanged += (_, _) =>
        {
            if (!syncingBrushTips && customBrushPicker.SelectedItem is BrushTip tip) SelectBrushTip(tip);
        };
        // A closed drop-down with nothing selected says why it is empty instead of showing a blank box.
        customBrushPlaceholder = Theme.Label("", Theme.CaptionSize, Theme.Subtle);
        customBrushPlaceholder.Margin = new Thickness(11, 3, 30, 5); customBrushPlaceholder.IsHitTestVisible = false;
        customBrushPlaceholder.TextTrimming = TextTrimming.CharacterEllipsis; customBrushPlaceholder.TextWrapping = TextWrapping.NoWrap;
        var pickerHost = new Grid(); pickerHost.Children.Add(customBrushPicker); pickerHost.Children.Add(customBrushPlaceholder);
        panel.Children.Add(pickerHost);
        panel.Children.Add(Theme.Button("이미지로 브러시 추가", () => Guard(ImportBrushTip), "PNG · JPEG · BMP · TIFF\n투명 이미지는 불투명한 부분, 흰 배경 이미지는 어두운 부분을 모양으로 사용합니다."));
        UpdateBrushTipControls(); UpdateCustomBrushList();
        return panel;
    }

    void LoadCustomBrushTips()
    {
        try
        {
            customBrushTips.Clear(); customBrushTips.AddRange(BrushTipStore.LoadAll());
            UpdateCustomBrushList();
        }
        catch (Exception error) { status.Text = "사용자 브러시를 읽지 못했습니다: " + error.Message; }
    }

    void ImportBrushTip()
    {
        var open = new OpenFileDialog
        {
            Title = "브러시 모양으로 사용할 이미지",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|모든 파일|*.*"
        };
        if (open.ShowDialog(this) != true) return;
        var tip = BrushTipStore.Import(open.FileName);
        BrushTipStore.Save(tip);
        customBrushTips.RemoveAll(item => item.Id == tip.Id);
        customBrushTips.Add(tip); UpdateCustomBrushList(); SelectBrushTip(tip);
        status.Text = "사용자 브러시 저장 · " + tip.Name;
    }

    void UpdateCustomBrushList()
    {
        if (customBrushPicker == null) return;
        syncingBrushTips = true;
        try
        {
            customBrushPicker.ItemsSource = customBrushTips.OrderBy(t => t.Name).ToArray();
            customBrushPicker.SelectedItem = brushTip.IsCustom ? customBrushTips.FirstOrDefault(t => t.Id == brushTip.Id) : null;
            customBrushPicker.IsEnabled = customBrushTips.Count > 0;
            customBrushPicker.ToolTip = customBrushTips.Count > 0 ? "저장한 이미지 모양 선택" : "이미지로 브러시를 추가하면 여기에 표시됩니다";
        }
        finally { syncingBrushTips = false; }
        UpdateCustomBrushPlaceholder();
    }

    void UpdateCustomBrushPlaceholder()
    {
        if (customBrushPicker == null || customBrushPlaceholder == null) return;
        customBrushPlaceholder.Text = customBrushTips.Count == 0 ? "저장한 브러시 없음" : "저장한 브러시 선택";
        customBrushPlaceholder.Visibility = customBrushPicker.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void SelectBrushTip(BrushTip tip)
    {
        brushTip = tip;
        if (tool != Tool.Eraser) SetTool(Tool.Brush);
        UpdateBrushTipControls(); UpdateBrushTipCursor();
    }

    void UpdateBrushTipControls()
    {
        foreach (var pair in brushTipButtons)
        {
            bool active = pair.Key.Id == brushTip.Id;
            pair.Value.Background = active ? Theme.Selected : Theme.Surface;
            pair.Value.BorderBrush = active ? Theme.Accent : Theme.Line;
        }
        if (brushTipName != null) brushTipName.Text = brushTip.Name;
        UpdateBrushStrokePreviews();
        if (customBrushPicker != null)
        {
            syncingBrushTips = true;
            try { customBrushPicker.SelectedItem = brushTip.IsCustom ? customBrushTips.FirstOrDefault(t => t.Id == brushTip.Id) : null; }
            finally { syncingBrushTips = false; }
            UpdateCustomBrushPlaceholder();
        }
        if (studioHardness != null) studioHardness.IsEnabled = !brushTip.IsCustom || IsRetouch(tool);
    }

    /// <summary>Redraws the current-brush stroke and each preset's stroke in the active shape.</summary>
    internal void UpdateBrushStrokePreviews()
    {
        // The page is rebuilt when shown, so a hidden panel need not repaint on every size drag.
        if (IsLoaded && selectedBrushPreview is { IsVisible: false }) return;
        if (selectedBrushPreview != null)
            selectedBrushPreview.Source = BrushPreview.Stroke(brushTip, brushSize, hardness, brushSpacing, brushAngle, 280, 64).Bitmap();
        foreach (var (image, size, edge) in brushPresetPreviews)
            image.Source = BrushPreview.Stroke(brushTip, size, edge, brushSpacing, brushAngle, 200, 40).Bitmap();
    }

    void UpdateBrushTipCursor()
    {
        canvas.BrushTipPreview = tool is Tool.Brush or Tool.Eraser && !ReferenceEquals(brushTip, BrushTip.Round)
            ? brushTip.Preview(Colors.White, 128, inset: false) : null;
        canvas.BrushTipAspectRatio = brushTip.Width / (double)brushTip.Height;
        canvas.BrushTipAngle = brushAngle;
        UpdateBrushStrokePreviews();
        hardnessSlider.IsEnabled = !brushTip.IsCustom || tool is not (Tool.Brush or Tool.Eraser);
        if (studioHardness != null) studioHardness.IsEnabled = !brushTip.IsCustom || IsRetouch(tool);
        canvas.InvalidateVisual();
    }
}
