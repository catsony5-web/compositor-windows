using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly StackPanel bucketOptions = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel wandOptions = new() { Orientation = Orientation.Horizontal };
    Slider? wandToleranceSlider;

    void BuildWandOptions()
    {
        wandOptions.Children.Add(Theme.Label("허용 오차"));
        var value = Theme.Label(wandTolerance.ToString("0"), 11, Theme.Muted); value.Width = 28;
        wandToleranceSlider = Slider(0, 255, wandTolerance, 105, v =>
        {
            wandTolerance = Math.Round(v); value.Text = wandTolerance.ToString("0"); jobCts?.Cancel(); ShowInteractionHint();
        });
        wandToleranceSlider.TickFrequency = 1; wandToleranceSlider.IsSnapToTickEnabled = true;
        wandToleranceSlider.ToolTip = "작게 설정하면 더 비슷한 색만 선택합니다. 0~255 · 방향키로 1씩 조절";
        System.Windows.Automation.AutomationProperties.SetName(wandToleranceSlider, "마술봉 허용 오차");
        var connected = new CheckBox { Content = "연결 영역", IsChecked = wandContiguous, Foreground = Theme.Text, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "끄면 문서 전체의 비슷한 색을 선택합니다. Ctrl+클릭으로도 전체 색을 선택할 수 있습니다." };
        connected.Click += (_, _) => { wandContiguous = connected.IsChecked == true; jobCts?.Cancel(); };
        var edges = new CheckBox { Content = "가장자리 보정", IsChecked = wandAntialias, Foreground = Theme.Text, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "선과 색이 섞인 경계 픽셀을 부드럽게 선택합니다. 경계 너머로 선택을 확장하지 않습니다." };
        edges.Click += (_, _) => { wandAntialias = edges.IsChecked == true; jobCts?.Cancel(); };
        wandOptions.Children.Add(wandToleranceSlider); wandOptions.Children.Add(value); wandOptions.Children.Add(connected); wandOptions.Children.Add(edges);
    }

    void BuildBucketOptions()
    {
        bucketOptions.Children.Add(Theme.Label("허용 오차"));
        var value = Theme.Label(bucketTolerance.ToString("0"), 11, Theme.Muted);
        value.Width = 28;
        var tolerance = Slider(0, 255, bucketTolerance, 92, v => { bucketTolerance = Math.Round(v); value.Text = bucketTolerance.ToString("0"); });
        tolerance.ToolTip = "색상 차이 허용 범위 (0~255)";
        bucketOptions.Children.Add(tolerance); bucketOptions.Children.Add(value);
        var connected = new CheckBox { Content = "연결 영역", IsChecked = bucketContiguous, Foreground = Theme.Text, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "끄면 문서 전체에서 같은 색 영역을 채웁니다." };
        connected.Click += (_, _) => bucketContiguous = connected.IsChecked == true;
        var merged = new CheckBox { Content = "표시된 레이어 참조", IsChecked = bucketSampleMerged, Foreground = Theme.Text, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "화면의 경계를 읽고 선택한 레이어에 채웁니다. 끄면 현재 레이어만 참조합니다." };
        merged.Click += (_, _) => bucketSampleMerged = merged.IsChecked == true;
        bucketOptions.Children.Add(connected); bucketOptions.Children.Add(merged);
    }
}
