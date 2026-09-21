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
    TextBlock? brushTipName;
    Image? selectedBrushPreview;
    bool syncingBrushTips;

    FrameworkElement BuildBrushTipControls()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Section("브러시 모양"));
        var shapes = new UniformGrid { Columns = 4 };
        foreach (var tip in BrushTip.BuiltIns)
        {
            var content = new StackPanel();
            content.Children.Add(new Image { Source = tip.Preview(Colors.White, 40), Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 5) });
            var name = Theme.Label(tip.Name, Theme.CaptionSize); name.TextAlignment = TextAlignment.Center;
            content.Children.Add(name);
            var button = Theme.Button("", () => SelectBrushTip(tip), tip.Name + " 브러시");
            button.Content = content; button.Padding = new Thickness(3, 5, 3, 5);
            AutomationProperties.SetName(button, tip.Name + " 브러시");
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
        panel.Children.Add(customBrushPicker);
        panel.Children.Add(Theme.Button("이미지로 브러시 추가", () => Guard(ImportBrushTip), "PNG · JPEG · BMP · TIFF\n투명 이미지는 불투명한 부분, 흰 배경 이미지는 어두운 부분을 모양으로 사용합니다."));
        var selected = new Grid { Margin = new Thickness(3, 9, 3, 5) };
        selected.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); selected.ColumnDefinitions.Add(new ColumnDefinition());
        selectedBrushPreview = new Image { Width = 42, Height = 42, Margin = new Thickness(0, 0, 10, 0) };
        selected.Children.Add(selectedBrushPreview);
        brushTipName = Theme.Label("", Theme.BodySize); Grid.SetColumn(brushTipName, 1); selected.Children.Add(brushTipName);
        panel.Children.Add(selected);
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
        if (selectedBrushPreview != null) selectedBrushPreview.Source = brushTip.Preview(Colors.White, 48);
        if (customBrushPicker != null)
        {
            syncingBrushTips = true;
            try { customBrushPicker.SelectedItem = brushTip.IsCustom ? customBrushTips.FirstOrDefault(t => t.Id == brushTip.Id) : null; }
            finally { syncingBrushTips = false; }
        }
        if (studioHardness != null) studioHardness.IsEnabled = !brushTip.IsCustom || IsRetouch(tool);
    }

    void UpdateBrushTipCursor()
    {
        canvas.BrushTipPreview = tool is Tool.Brush or Tool.Eraser && !ReferenceEquals(brushTip, BrushTip.Round)
            ? brushTip.Preview(Colors.White, 128, inset: false) : null;
        canvas.BrushTipAspectRatio = brushTip.Width / (double)brushTip.Height;
        canvas.BrushTipAngle = brushAngle;
        hardnessSlider.IsEnabled = !brushTip.IsCustom || tool is not (Tool.Brush or Tool.Eraser);
        if (studioHardness != null) studioHardness.IsEnabled = !brushTip.IsCustom || IsRetouch(tool);
        canvas.InvalidateVisual();
    }
}
