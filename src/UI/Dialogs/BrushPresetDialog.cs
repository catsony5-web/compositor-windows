using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// 현재 브러시를 프리셋으로 저장: a stroke sample of the current brush, its settings and a name.
// Accept sets PresetName; the caller adds the preset to 내 프리셋.
public sealed class BrushPresetDialog : Window
{
    readonly TextBox name;
    readonly string fallback;

    public string? PresetName { get; private set; }

    public BrushPresetDialog(Window? owner, BrushTip tip, BrushPreset settings, string suggestedName)
    {
        fallback = BrushPreset.CleanName(suggestedName);
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        DialogShell.Prepare(this, owner, "브러시 프리셋 저장");
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; Content = panel;
        panel.Children.Add(DialogShell.Title("현재 브러시를 프리셋으로 저장"));
        panel.Children.Add(DialogShell.Subtitle("모양, 크기, 경도, 농도, 모양 회전, 찍는 간격을 함께 저장합니다. 내 프리셋을 누르면 한 번에 적용됩니다."));
        var stroke = new Image
        {
            Height = 64, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch,
            Source = BrushPreview.Stroke(tip, settings.Size, settings.Hardness, settings.Spacing, settings.Angle, 360, 64).Bitmap()
        };
        AutomationProperties.SetName(stroke, "브러시 획 미리보기");
        var card = DialogShell.Card(stroke, Theme.Canvas); card.Padding = new Thickness(8); card.Margin = new Thickness(0, 14, 0, 10);
        panel.Children.Add(card);
        var tipName = Value(tip.Name); if (tip.IsCustom) Loc.Keep(tipName);
        panel.Children.Add(PropertyRows.Pair("모양", tipName, "크기", Value($"{settings.Size:0} px")));
        panel.Children.Add(PropertyRows.Pair("경도", Value($"{settings.Hardness * 100:0}%"), "농도", Value($"{settings.Opacity * 100:0}%")));
        panel.Children.Add(PropertyRows.Pair("모양 회전", Value($"{settings.Angle:0}°"), "찍는 간격", Value($"{settings.Spacing * 100:0}%")));
        name = PropertyRows.Input(fallback, "프리셋 이름"); name.MaxLength = BrushPreset.MaxNameLength;
        panel.Children.Add(PropertyRows.Field("프리셋 이름", name, new Thickness(2, 6, 2, 0)));
        var cancel = DialogShell.Secondary("취소", () => DialogResult = false); cancel.IsCancel = true;
        var accept = DialogShell.Primary("저장", () => { Accept(); DialogResult = true; }); accept.IsDefault = true;
        panel.Children.Add(DialogShell.Footer(cancel, accept));
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    static TextBlock Value(string text)
    {
        var value = Theme.Label(text, Theme.BodySize); value.TextWrapping = TextWrapping.Wrap; value.Margin = new Thickness(0);
        return value;
    }

    internal void SetName(string value) => name.Text = value;

    internal void Accept() => PresetName = BrushPreset.CleanName(name.Text, fallback);
}
