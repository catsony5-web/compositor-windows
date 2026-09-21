using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly StackPanel bucketOptions = new() { Orientation = Orientation.Horizontal };

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
