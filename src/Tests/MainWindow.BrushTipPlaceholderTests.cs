using System.Windows;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunBrushTipPlaceholderTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        test("custom image brush drop-down shows an empty-state placeholder instead of a blank box", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.customBrushTips.Clear(); window.BuildBrushTipControls();
                Check(window.customBrushPicker is { IsEnabled: false, SelectedItem: null }, "An empty list must leave the picker disabled");
                Check(window.customBrushPlaceholder is { Visibility: Visibility.Visible, Text: "저장한 브러시 없음" }, "No saved brushes placeholder is missing");
                var tip = BrushTip.FromAlpha("잎사귀", 4, 4, Enumerable.Repeat((byte)255, 16).ToArray());
                window.customBrushTips.Add(tip); window.UpdateCustomBrushList();
                Check(window.customBrushPlaceholder is { Visibility: Visibility.Visible, Text: "저장한 브러시 선택" }, "A list with brushes but no choice must invite a choice");
                window.customBrushPicker!.SelectedItem = tip;
                Check(window.brushTip == tip && window.customBrushPlaceholder!.Visibility == Visibility.Collapsed, "Choosing a saved brush must hide the placeholder");
                window.SelectBrushTip(BrushTip.Round);
                Check(window.customBrushPicker.SelectedItem == null && window.customBrushPlaceholder!.Visibility == Visibility.Visible, "Returning to a built-in shape must show the placeholder again");
            }
            finally { window.StopRenderingForShutdown(); }
        });
    }
}
