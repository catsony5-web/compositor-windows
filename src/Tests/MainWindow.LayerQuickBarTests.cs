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
