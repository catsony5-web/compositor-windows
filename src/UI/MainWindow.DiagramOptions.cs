using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

// Tool options bar of 선 · 곡선 and 지시선: what the next line or callout gets. The selected object
// itself is changed in the properties panel (MainWindow.DiagramProperties.cs).
public sealed partial class MainWindow
{
    readonly StackPanel diagramOptions = new() { Orientation = Orientation.Horizontal };
    SegmentedChoice<bool>? lineModeChoice;
    SegmentedChoice<CalloutLeader>? calloutLeaderChoice;
    ComboBox? lineDashBox, lineStartBox, lineEndBox, calloutMarkBox;
    Slider? lineWidthSlider;
    TextBlock? lineWidthLabel;
    FrameworkElement[] lineOnlyOptions = [], calloutOnlyOptions = [];

    internal static readonly (StrokeDash Value, string Name)[] DashChoices = [(StrokeDash.Solid, "실선"), (StrokeDash.Dotted, "점선"), (StrokeDash.Dashed, "파선"), (StrokeDash.DashDot, "일점쇄선")];
    internal static readonly (LineMark Value, string Name)[] MarkChoices = [(LineMark.None, "없음"), (LineMark.Arrow, "화살표"), (LineMark.OpenArrow, "열린 화살표"), (LineMark.Dot, "채운 점"), (LineMark.Ring, "고리"), (LineMark.Bar, "막대")];
    internal static readonly (StrokeCap Value, string Name)[] CapChoices = [(StrokeCap.Round, "둥근 끝"), (StrokeCap.Square, "각진 끝"), (StrokeCap.Flat, "평평한 끝")];

    internal static ComboBox ChoiceBox<T>((T Value, string Name)[] choices, T selected, string name, Action<T> changed, double minWidth = 0)
    {
        var box = PropertyRows.Choice(name);
        foreach (var (value, label) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item); if (EqualityComparer<T>.Default.Equals(value, selected)) box.SelectedItem = item;
        }
        if (minWidth > 0) box.MinWidth = minWidth;
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: T value }) changed(value); };
        return box;
    }
    static void SelectChoice<T>(ComboBox? box, T value)
    {
        if (box == null) return;
        foreach (var item in box.Items.OfType<ComboBoxItem>()) if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value)) { if (!ReferenceEquals(box.SelectedItem, item)) box.SelectedItem = item; return; }
    }

    void BuildDiagramOptions()
    {
        lineModeChoice = new SegmentedChoice<bool>([(false, "직선"), (true, "매끄러운 곡선")], lineCurve) { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
            ToolTip = "직선: 클릭한 점을 곧게 잇기 · 곡선: 클릭한 점을 모두 지나는 매끄러운 선 (Shift+P)" };
        lineModeChoice.Changed += curve => { lineCurve = curve; UpdateDiagramOverlay(); ShowInteractionHint(); UpdateStatus(); };
        calloutLeaderChoice = new SegmentedChoice<CalloutLeader>([(CalloutLeader.Elbow, "꺾은 지시선"), (CalloutLeader.Straight, "곧은 지시선")], calloutLeader)
        { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), ToolTip = "꺾은 지시선: 대상에서 꺾여 라벨까지 수평으로 · 곧은 지시선: 대상에서 라벨까지 곧게" };
        calloutLeaderChoice.Changed += leader => calloutLeader = leader;

        var widthCaption = OptionLabel("두께", "새 선과 지시선의 두께 (px)");
        lineWidthLabel = Theme.Label($"{lineWidth:0.#} px", Theme.CaptionSize, Theme.Muted); lineWidthLabel.MinWidth = 38; lineWidthLabel.VerticalAlignment = VerticalAlignment.Center;
        lineWidthSlider = Slider(.5, 24, lineWidth, 90, v => { lineWidth = Math.Round(v * 2) / 2; if (lineWidthLabel != null) lineWidthLabel.Text = $"{lineWidth:0.#} px"; UpdateDiagramOverlay(); });
        lineWidthSlider.ToolTip = "선 두께 0.5~24px · 끝 모양 크기도 함께 바뀝니다"; System.Windows.Automation.AutomationProperties.SetName(lineWidthSlider, "새 선 두께");

        Grid Captioned(string caption, string tip, FrameworkElement input)
        {
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = OptionLabel(caption, tip); label.Margin = new Thickness(0, 0, 6, 0); row.Children.Add(label);
            input.Margin = new Thickness(0); input.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(input, 1); row.Children.Add(input);
            return row;
        }
        lineDashBox = ChoiceBox(DashChoices, lineDash, "새 선 모양", dash => { if (tool == Tool.Callout) calloutDash = dash; else lineDash = dash; UpdateDiagramOverlay(); }, 92);
        lineStartBox = ChoiceBox(MarkChoices, lineStart, "새 선의 시작 끝 모양", mark => { lineStart = mark; UpdateDiagramOverlay(); }, 92);
        lineEndBox = ChoiceBox(MarkChoices, lineEnd, "새 선의 끝 모양", mark => { lineEnd = mark; UpdateDiagramOverlay(); }, 92);
        calloutMarkBox = ChoiceBox(MarkChoices, calloutMark, "새 지시선의 대상 쪽 끝 모양", mark => calloutMark = mark, 92);
        var start = Captioned("시작", "첫 점의 끝 모양 (화살표·점·막대)", lineStartBox);
        var end = Captioned("끝", "마지막 점의 끝 모양 (화살표·점·막대)", lineEndBox);
        var mark = Captioned("대상 쪽", "지시선이 가리키는 쪽의 끝 모양", calloutMarkBox);
        diagramOptions.Children.Add(lineModeChoice); diagramOptions.Children.Add(calloutLeaderChoice);
        diagramOptions.Children.Add(widthCaption); diagramOptions.Children.Add(lineWidthSlider); diagramOptions.Children.Add(lineWidthLabel);
        diagramOptions.Children.Add(Captioned("선 모양", "실선 · 점선 · 파선 · 일점쇄선", lineDashBox));
        diagramOptions.Children.Add(start); diagramOptions.Children.Add(end); diagramOptions.Children.Add(mark);
        lineOnlyOptions = [lineModeChoice, start, end]; calloutOnlyOptions = [calloutLeaderChoice, mark];
        diagramOptions.Visibility = Visibility.Collapsed;
    }

    void SyncDiagramOptions()
    {
        lineModeChoice?.Select(lineCurve); calloutLeaderChoice?.Select(calloutLeader);
        SelectChoice(lineDashBox, tool == Tool.Callout ? calloutDash : lineDash);
        SelectChoice(lineStartBox, lineStart); SelectChoice(lineEndBox, lineEnd); SelectChoice(calloutMarkBox, calloutMark);
        if (lineWidthSlider != null && Math.Abs(lineWidthSlider.Value - lineWidth) > .001) lineWidthSlider.Value = lineWidth;
        diagramOptions.Visibility = DiagramTool(tool) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in lineOnlyOptions) element.Visibility = tool == Tool.Line ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in calloutOnlyOptions) element.Visibility = tool == Tool.Callout ? Visibility.Visible : Visibility.Collapsed;
    }
}
