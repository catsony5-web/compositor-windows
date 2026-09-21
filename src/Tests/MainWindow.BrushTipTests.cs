using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunBrushTipIntegrationTests(Action<string, Action> test)
    {
        test("brush-tip panel routes real controls to cursor and stroke without editing settings history", () =>
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
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                // Never show this window or raise Loaded: saved presets are not read from the user's AppData.
                Check(!window.IsLoaded && window.customBrushTips.Count == 0, "Headless test read saved user brushes");
                window.doc = new Document { Width = 40, Height = 40, Name = "브러시 연결 검증" };
                window.doc.Add(new Layer { Name = "원본", Pixels = Raster.Solid(40, 40, Colors.Transparent) });
                window.history.Reset(window.doc); window.InitializeWorkspace();
                window.NudgeSelected(new Vector(1, 0)); window.Undo();
                Check(window.history.CanRedo && !window.history.Dirty(window.doc), "Unable to prepare clean redo history");
                window.SetTool(Tool.Move);
                var original = window.doc.Active!.Pixels; var originalBytes = original.Data.ToArray();
                var revision = window.doc.Revision;

                window.brushTipButtons[BrushTip.Square].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.tool == Tool.Brush && ReferenceEquals(window.brushTip, BrushTip.Square), "Builtin shape button did not activate its brush");
                Check(window.canvas.BrushTipPreview != null && window.hardnessSlider.IsEnabled && window.studioHardness!.IsEnabled,
                    "Builtin shape cursor or hardness state was not updated");
                Slider Parameter(string name) => Descendants(window.studioContents[3]).OfType<Slider>()
                    .Single(control => AutomationProperties.GetName(control) == name);
                Parameter("모양 회전 °").Value = 90;
                Parameter("찍는 간격 %").Value = 150;
                Parameter("크기 px").Value = 16;
                Check(window.brushAngle == 90 && window.brushSpacing == 1.5 && window.brushSize == 16, "Brush sliders did not publish their displayed values");

                var custom = BrushTip.FromAlpha("테스트용 가로 팁", 8, 2, Enumerable.Repeat((byte)255, 16).ToArray());
                window.customBrushTips.Add(custom); window.UpdateCustomBrushList();
                window.SetTool(Tool.Eraser);
                window.customBrushPicker!.SelectedItem = custom;
                Check(window.tool == Tool.Eraser && ReferenceEquals(window.brushTip, custom), "Choosing a saved shape changed the eraser tool");
                Check(window.canvas.BrushTipPreview != null && window.canvas.BrushTipAspectRatio == 4 && window.canvas.BrushTipAngle == 90,
                    "Custom cursor lost the tip's aspect or rotation");
                Check(!window.hardnessSlider.IsEnabled && !window.studioHardness!.IsEnabled && window.selectedBrushPreview!.Source != null,
                    "Custom tip did not disable both hardness controls or update its thumbnail");
                var preview = Raster.FromBitmap(window.canvas.BrushTipPreview!);
                Check(preview.Data[(64 * 128 + 100) * 4 + 3] > 0 && preview.Data[(100 * 128 + 64) * 4 + 3] == 0,
                    "Custom cursor bitmap distorted the wide silhouette before rotation");
                window.SetTool(Tool.BlurBrush);
                Check(window.canvas.BrushTipPreview == null && window.hardnessSlider.IsEnabled && window.studioHardness!.IsEnabled,
                    "Image tip hardness restriction leaked into the retouch tools");
                window.brushTipButtons[BrushTip.Round].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.tool == Tool.Brush && window.canvas.BrushTipPreview == null && window.hardnessSlider.IsEnabled && window.studioHardness!.IsEnabled,
                    "Returning to round did not restore its cursor and hardness");
                window.SetTool(Tool.Eraser); window.customBrushPicker.SelectedItem = custom;
                Check(ReferenceEquals(original, window.doc.Active!.Pixels) && original.Data.SequenceEqual(originalBytes)
                    && window.doc.Revision == revision && !window.history.CanUndo && window.history.CanRedo && !window.history.Dirty(window.doc)
                    && window.doc.Active.X == 0 && window.doc.Active.Y == 0 && !window.IsLoaded,
                    "Changing brush settings edited pixels, geometry, dirty state or redo history");

                // Exercise the same construction used by InteractionDown on a disposable document, without mouse input.
                var settingsDocument = window.doc;
                try
                {
                    window.doc = new Document { Width = 40, Height = 40 };
                    window.doc.Add(new Layer { Pixels = Raster.Solid(40, 40, Colors.Blue) });
                    var source = window.doc.Active!.Pixels;
                    var liveStroke = window.CreateActiveBrushStroke();
                    liveStroke.Point(new Point(8, 20)); liveStroke.Point(new Point(32, 20));
                    byte Alpha(int x, int y) => window.doc.Active.Pixels.Data[(y * 40 + x) * 4 + 3];
                    Check(Alpha(8, 26) == 0 && Alpha(32, 26) == 0 && Alpha(14, 20) == 255 && Alpha(20, 20) == 255,
                        "Live stroke did not receive the selected image tip, 90° rotation, 150% spacing, diameter or eraser mode");
                    Check(source.Data[3] == 255 && !ReferenceEquals(source, window.doc.Active.Pixels), "Live stroke mutated its source raster");
                }
                finally { window.doc = settingsDocument; }
            }
            finally
            {
                window.renderCts?.Cancel(); window.jobCts?.Cancel(); window.pendingInspectorCommit = null;
            }
        });
    }
}
