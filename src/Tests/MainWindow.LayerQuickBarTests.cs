using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunLayerQuickBarTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        test("layer quick bar edits the active layer's blend and opacity as single undo steps", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8 };
                document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(8, 8, Colors.Red) });
                window.AddTab(document, null);
                Check(window.layerQuickBlend!.IsEnabled && window.layerQuickOpacity!.Value == 100, "Quick bar must reflect the active layer");
                Check(window.layerEmptyHint!.Visibility == Visibility.Collapsed, "Empty hint shown with layers present");
                window.layerQuickBlend.SelectedItem = window.layerQuickBlend.Items.OfType<ComboBoxItem>().Single(i => (BlendMode)i.Tag == BlendMode.Multiply);
                Check(window.doc.Active!.Blend == BlendMode.Multiply && window.history.CanUndo, "Blend change not applied through history");
                window.layerQuickOpacity!.Value = 40;
                Check(Math.Abs(window.doc.Active!.Opacity - .4) < 1e-9 && window.layerQuickOpacityValue!.Text == "40%", "Opacity change not applied");
                window.Undo();
                // A thumb drag previews the number only and records one history step on release.
                var slider = window.layerQuickOpacity!;
                slider.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                foreach (var v in new[] { 90d, 70d, 55d }) slider.Value = v;
                Check(Math.Abs(window.doc.Active!.Opacity - 1) < 1e-9 && window.layerQuickOpacityValue!.Text == "55%", "Dragging must not edit the layer before release");
                slider.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                Check(Math.Abs(window.doc.Active!.Opacity - .55) < 1e-9, "Release must apply the dragged opacity");
                window.Undo();
                Check(Math.Abs(window.doc.Active!.Opacity - 1) < 1e-9 && window.history.CanUndo, "One undo must revert the whole drag");
                window.Undo();
                Check(Math.Abs(window.doc.Active!.Opacity - 1) < 1e-9 && window.layerQuickOpacity.Value == 100, "Undo must restore and resync opacity");
                window.doc.Active!.Locked = true; window.BuildLayers();
                Check(!window.layerQuickBlend.IsEnabled && !window.layerQuickOpacity.IsEnabled, "Locked layer must disable quick edits");
            }
            finally { window.StopRenderingForShutdown(); }
        });
        test("layer panel explains an empty category and offers group, mask and adjustment buttons", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8 };
                document.Add(new Layer { Name = "사진", Pixels = Raster.Solid(8, 8, Colors.Red) });
                window.AddTab(document, null);
                window.layerCategory = LayerCategory.Drawing; window.BuildLayers();
                Check(window.layerEmptyHint!.Visibility == Visibility.Visible && window.layerEmptyHint.Text.Contains("도면"), "Empty drawing category must show a hint");
                window.layerCategory = LayerCategory.Photo; window.BuildLayers();
                Check(window.layerEmptyHint.Visibility == Visibility.Collapsed, "Hint must hide when layers exist");
                var names = new[] { "선택 레이어 그룹 만들기", "레이어 마스크 추가", "조정 레이어 추가" };
                var footerButtons = FindAll<Button>(window.layersPane!).Select(b => b.ToolTip as string ?? System.Windows.Automation.AutomationProperties.GetName(b)).ToArray();
                foreach (var name in names) Check(footerButtons.Contains(name), "Footer button missing: " + name);
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("tool options bar shows the tool icon with its name and uses icon toggles", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8 };
                document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(8, 8, Colors.Red) });
                window.AddTab(document, null);
                window.SetTool(Tool.Bucket);
                Check(window.toolCaption.Text == ToolDisplayName(Tool.Bucket) && window.toolCaptionIcon.Content is FrameworkElement, "Tool identity must show icon and name");
                var toggles = window.bucketOptions.Children.OfType<OptionToggle>().ToArray();
                Check(toggles.Length == 2 && toggles.All(t => t.ToolTip is string tip && tip.Length > 10), "Bucket options must be icon toggles with tooltips");
                toggles[0].IsChecked = false; toggles[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!window.bucketContiguous, "Toggle must reach the fill setting");
                window.SetTool(Tool.Move);
                Check(window.autoSelectToggle.Visibility == Visibility.Visible && window.autoSelectToggle.IsChecked == true, "Move tool shows the auto-select toggle");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("zoom entry hint names the active mode limit and alignment labels are whole sentences", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8 };
                document.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(8, 8, Colors.Red) });
                window.AddTab(document, null);
                window.canvas.DesignMode = false;
                Check(window.ZoomRangeMessage.Contains("1600%"), "Photo mode must say 1600%: " + window.ZoomRangeMessage);
                window.canvas.DesignMode = true;
                Check(window.ZoomRangeMessage.Contains("6400%"), "Design mode must say 6400%");
                window.zoomBox!.Text = "abc"; window.CommitZoomText();
                Check(window.status.Text == window.ZoomRangeMessage, "Invalid zoom must show the mode-specific hint");
                window.canvas.DesignMode = false;
            }
            finally { window.StopRenderingForShutdown(); }
            var panelNames = new[] { "단락 왼쪽 정렬", "단락 가운데 정렬", "단락 오른쪽 정렬" };
            string previous = Loc.Language;
            try { Loc.Use("en"); foreach (var key in panelNames) Check(Loc.T(key) != key && !Loc.T(key).Contains('단'), "Missing whole-sentence translation: " + key); }
            finally { Loc.Use(previous); }
        });

        static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match) yield return match;
                foreach (var nested in FindAll<T>(child)) yield return nested;
            }
        }
    }
}
