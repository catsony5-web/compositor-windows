using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunStartFocusTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        test("start screen spans the body until a document opens and keeps panel widths", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.UpdateDocumentAvailability();
                bool Focused() => Grid.GetColumn(w.stageCard!) == 0 && Grid.GetColumnSpan(w.stageCard!) == 4
                    && new[] { w.toolRail!, w.rightPanelHost!, w.leftPanelHost!, w.optionCard! }.All(e => e.Visibility == Visibility.Collapsed);
                Check(Focused() && w.optionRow!.Height.Value < OptionRowHeight, "The empty workspace kept the editing chrome");
                double width = w.rightPanelColumn.Width.Value;
                Button Quick(string label) => Descendants(w.emptyWorkspace!).OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("빠른 시작: " + label) == true);
                Check(QuickSizes.All(size => Quick(size.Label).ToolTip is string { Length: > 0 }), "Quick sizes lack buttons or tooltips");
                Click(Quick("세로 4:5"));
                Check(w.HasDocument && w.doc.Width == 1080 && w.doc.Height == 1350 && !w.history.Dirty(w.doc), "The 4:5 quick start did not open a clean 1080 × 1350 document");
                Check(Grid.GetColumn(w.stageCard!) == 2 && Grid.GetColumnSpan(w.stageCard!) == 1 && w.toolRail!.Visibility == Visibility.Visible
                    && w.rightPanelHost!.Visibility == Visibility.Visible && w.optionCard!.Visibility == Visibility.Visible && w.optionRow!.Height.Value == OptionRowHeight, "Opening a document did not restore the workspace");
                Check(w.rightPanelColumn.Width.Value == width, "Start focus changed the saved panel width");
                w.CloseTab();
                Click(Quick("A4 인쇄"));
                Check(w.doc.Dpi == 150 && Math.Abs(w.doc.Width - 1240) <= 1 && Math.Abs(w.doc.Height - 1754) <= 1, "The A4 quick start did not use 150 DPI millimetres");
                w.CloseTab();
                Check(!w.HasDocument && Focused(), "Closing the last document did not return to the focused start screen");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("status bar zoom steps, parses typed values, clamps and follows shortcuts", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(NewDocumentDialog.CreateDocument("배율", "400", "300", 1), null);
                static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
                w.SetZoom(1.5); Check(Near(w.canvas.Zoom, 1.5) && w.zoomBox!.Text == "150%", "Setting the zoom did not update the control");
                w.StepZoom(1); Check(Near(w.canvas.Zoom, 2), "Zoom in skipped the next step");
                w.StepZoom(-1); w.StepZoom(-1); Check(Near(w.canvas.Zoom, 1), "Zoom out skipped a step");
                w.zoomBox!.Text = "250%"; Check(w.CommitZoomText() && Near(w.canvas.Zoom, 2.5), "A typed percentage was not applied");
                w.zoomBox!.Text = "3x"; Check(w.CommitZoomText() && Near(w.canvas.Zoom, 3), "A typed factor was not applied");
                w.zoomBox!.Text = "크게"; Check(!w.CommitZoomText() && Near(w.canvas.Zoom, 3) && w.zoomBox!.Text == "300%", "Invalid text changed the zoom or was kept");
                Check(w.ExecuteEditorShortcut(Key.OemPlus, ModifierKeys.Control) && Near(w.canvas.Zoom, 4), "Ctrl++ did not zoom in");
                Check(w.ExecuteEditorShortcut(Key.OemMinus, ModifierKeys.Control) && Near(w.canvas.Zoom, 3), "Ctrl+- did not zoom out");
                w.canvas.Pan = new Vector(20, 10);
                Check(w.ExecuteEditorShortcut(Key.D1, ModifierKeys.Control) && Near(w.canvas.Zoom, 1) && w.canvas.Pan == new Vector(), "Ctrl+1 did not show actual size");
                w.SetZoom(1000); Check(Near(w.canvas.Zoom, 16), "Photo mode zoom escaped its limit");
                w.zoomBox!.SelectedIndex = Array.FindIndex(ZoomChoices, c => c.Label == "50%"); Check(Near(w.canvas.Zoom, .5), "Choosing a preset did not zoom");
                var registry = w.BuildCommandRegistry();
                Check(registry.Any(c => c.Title == "확대" && c.Shortcut == "Ctrl++") && registry.Any(c => c.Title == "축소" && c.Shortcut == "Ctrl+-"), "Zoom commands are missing from the menu");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("status bar RGB and CMYK segments follow the proof state", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(NewDocumentDialog.CreateDocument("인쇄색", "16", "16", 1), null);
                Check(w.rgbView!.FontWeight == FontWeights.SemiBold && w.cmykView!.FontWeight == FontWeights.Normal, "RGB was not the initial view");
                Click(w.cmykView!);
                Check(w.cmykProof && w.cmykView!.FontWeight == FontWeights.SemiBold, "The CMYK segment did not enable the print preview");
                w.SetProof(false);
                Check(!w.cmykProof && w.rgbView!.FontWeight == FontWeights.SemiBold, "The segments did not follow the menu or shortcut");
                Check(!w.documentInfo.Text.Contains("RGB") && w.documentInfo.Text.Contains("DPI"), "Document info repeats the color view");
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }
}
