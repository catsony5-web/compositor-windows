using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunTextPropertiesTests(Action<string, Action> test, string directory)
    {
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static TextSpec Spec() => new() { Content = "한글 Ab\n두 번째", FontFamily = "Malgun Gothic", FontSize = 24, ColorArgb = 0xFF224466 };
        var window = new MainWindow(null) { headlessTesting = true };
        void Reset()
        {
            window.renderCts?.Cancel(); window.jobCts?.Cancel(); window.pendingInspectorCommit = null;
            window.doc = new Document { Width = 300, Height = 200, Name = "문자 속성 테스트" }; window.doc.Add(DocumentFeatures.CreateText(Spec(), 12, 15));
            window.history = new History(); window.history.Reset(window.doc);
            window.tabs.Clear(); window.InitializeWorkspace(); window.activeTab = 0; window.canvas.Document = window.doc;
            window.BuildProperties();
        }
        void Case(string name, Action action) => test("Text properties: " + name, () => { Reset(); action(); });
        Case("new text creates an editable layer and selects the properties workspace", () =>
        {
            int count = window.doc.Layers.Count; window.OpenTextProperties(null, new Point(40, 50));
            Assert(window.doc.Layers.Count == count + 1 && window.doc.Active is { Kind: LayerKind.Text, X: 40, Y: 50 }, "Canvas creation did not create editable text at its click location");
            Assert(window.studioPage == 1 && window.textPropertiesPanel != null, "Text creation did not open persistent properties");
            window.Undo(); Assert(window.doc.Layers.Count == count, "Creation cannot be undone");
        });
        Case("content size leading and tracking apply together in one undo", () =>
        {
            var panel = window.textPropertiesPanel!; panel.EditorForTesting.Text = "변경한 글\n한 번에 적용";
            panel.SizeForTesting.Text = "31"; panel.LeadingForTesting.Text = "48"; panel.TrackingForTesting.Text = "120";
            Assert(panel.TryApply(), "Valid character settings were rejected");
            Assert(window.doc.Active!.Text is { FontSize: 31, LineHeight: 48, Tracking: 120 } && window.doc.Active.Text.Content.Contains("한 번에"), "A character field was discarded");
            window.Undo(); Assert(window.doc.Active!.Text == Spec() && !window.history.CanUndo, "Typography edit did not undo atomically");
        });
        Case("invalid values remain inline and preserve pixels and history", () =>
        {
            var panel = window.textPropertiesPanel!; var pixels = window.doc.Active!.Pixels;
            panel.SizeForTesting.Text = "not a size";
            Assert(!panel.TryApply() && panel.ValidationForTesting.Contains("글자 크기"), "Invalid size was not reported inline");
            Assert(ReferenceEquals(pixels, window.doc.Active.Pixels) && !window.history.CanUndo, "Invalid properties modified the layer");
            panel.SizeForTesting.Text = "25"; Assert(panel.TryApply(), "Corrected size could not be applied");
        });
        Case("unchanged properties preserve redo and stale controls cannot write", () =>
        {
            var panel = window.textPropertiesPanel!; panel.SizeForTesting.Text = "32"; panel.TryApply(); window.Undo();
            Assert(window.textPropertiesPanel!.TryApply() && window.history.CanRedo, "No-op properties erased redo");
            panel.EditorForTesting.Text = "오래된 패널";
            Assert(!panel.TryApply() && window.doc.Active!.Text == Spec(), "Detached properties wrote stale content");
        });
        Case("tracking expands shaped Korean text and preserves combining clusters", () =>
        {
            var plain = Spec() with { Content = "한글AB" }; var normal = DocumentFeatures.RenderText(plain);
            var tracked = DocumentFeatures.RenderText(plain with { Tracking = 250 });
            Assert(tracked.Width - normal.Width is >= 16 and <= 20, "Tracking is not reflected in the rendered raster");
            var accent = plain with { Content = "e\u0301", FontFamily = "Segoe UI" };
            var a = DocumentFeatures.RenderText(accent); var b = DocumentFeatures.RenderText(accent with { Tracking = 500 });
            Assert(Math.Abs(a.Width - b.Width) <= 1, "Tracking split a combining-character cluster");
        });
        Case("line spacing and tracking preserve uncropped glyph bounds", () =>
        {
            var spec = Spec() with { Content = "글르 italic\n한글", Italic = true, Tracking = 90, LineHeight = 60 };
            var raster = DocumentFeatures.RenderText(spec);
            Assert(raster.Height > DocumentFeatures.RenderText(spec with { LineHeight = 0 }).Height, "Leading did not change output height");
            Assert(Enumerable.Range(0, raster.Width).All(x => raster.Data[x * 4 + 3] == 0 && raster.Data[((raster.Height - 1) * raster.Width + x) * 4 + 3] == 0), "Top or bottom glyph edges were clipped");
            Assert(Enumerable.Range(0, raster.Height).All(y => raster.Data[(y * raster.Width) * 4 + 3] == 0 && raster.Data[(y * raster.Width + raster.Width - 1) * 4 + 3] == 0), "Left or right glyph edges were clipped");
        });
        Case("typography updates retain masks warp corners and saved editable settings", () =>
        {
            var layer = window.doc.Active!; layer.Mask = Enumerable.Repeat((byte)255, layer.Pixels.Width * layer.Pixels.Height).ToArray();
            layer.Warp = new WarpQuad(new Point(1, 2), new Point(180, 8), new Point(170, 110), new Point(3, 98));
            layer.Scale = 1.2; layer.Rotation = 17; var before = layer.Document(new Point());
            var spec = Spec() with { FontSize = 36, Tracking = 80, LineHeight = 50 }; DocumentFeatures.UpdateText(layer, spec);
            Assert(layer.Mask!.Length == layer.Pixels.Width * layer.Pixels.Height && layer.Mask.All(value => value == 255), "Typography destroyed the mask");
            Assert((layer.Document(new Point()) - before).Length < .000001, "Typography shifted an existing warp");
            string path = Path.Combine(directory, "typography.moruproj"); ProjectStore.Save(window.doc, path); var loaded = ProjectStore.Load(path);
            Assert(loaded.Active!.Text == spec && loaded.Active.Mask!.SequenceEqual(layer.Mask), "Project roundtrip discarded typography or mask");
        });
        Case("canvas alignment handles transformed text and remains undoable", () =>
        {
            window.doc.Active!.Rotation = 15; window.history.Reset(window.doc); var original = window.doc.Active.X;
            window.AlignTextToCanvas(window.doc.ActiveId, window.doc, "left");
            var layer = window.doc.Active!; var points = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }.Select(p => layer.Document(p));
            Assert(Math.Abs(points.Min(p => p.X)) < .00001, "Rotated text did not align to the canvas edge");
            window.Undo(); Assert(window.doc.Active!.X == original, "Canvas alignment did not undo");
        });
        Case("Compositor export preserves advanced typography as pixels with a warning", () =>
        {
            var layer = window.doc.Active!; DocumentFeatures.UpdateText(layer, Spec() with { Tracking = 100, LineHeight = 42 });
            string path = Path.Combine(directory, "typography-" + Guid.NewGuid().ToString("N") + ".comp");
            var warnings = CompositorPackage.Export(window.doc, path); var loaded = CompositorPackage.Import(path).Document;
            Assert(warnings.Any(warning => warning.Contains("자간·줄 간격")), "Unsupported typography loss was not reported");
            Assert(loaded.Active!.Kind == LayerKind.Raster && loaded.Active.Text == null && loaded.Active.Pixels.Data.SequenceEqual(layer.Pixels.Data), "Compositor export changed rendered typography");
            Assert(layer.Kind == LayerKind.Text && layer.Text!.Tracking == 100, "Export destroyed source editable text");
        });
        Case("Enter contract keeps content multiline and applies numeric properties", () =>
        {
            Assert(!TextPropertiesPanel.ShouldApplyKey(Key.Enter, ModifierKeys.None, true, false, false), "Plain content Enter must create a newline");
            Assert(TextPropertiesPanel.ShouldApplyKey(Key.Enter, ModifierKeys.Control, true, false, false), "Ctrl+Enter must apply content");
            Assert(TextPropertiesPanel.ShouldApplyKey(Key.Enter, ModifierKeys.None, false, false, false), "Numeric Enter must apply properties");
            Assert(!TextPropertiesPanel.ShouldApplyKey(Key.Enter, ModifierKeys.None, false, true, false), "Dropdown Enter must retain its selection action");
            Assert(!TextPropertiesPanel.ShouldApplyKey(Key.Enter, ModifierKeys.Control, true, false, true), "IME composition Enter must pass through");
            Assert(!TextPropertiesPanel.ShouldApplyKey(Key.ImeProcessed, ModifierKeys.Control, false, false, false, Key.Enter), "IME processed Enter must pass through");
        });
        Case("Korean IME draft and composition-end Enter cannot commit twice", () =>
        {
            int commits = 0; var panel = new TextPropertiesPanel(Spec(), _ => { commits++; return null; }, color => color, _ => { });
            var input = panel.EditorForTesting;
            var composition = new TextComposition(InputManager.Current, input, "가");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            input.Text = "가나다";
            Assert(!panel.TryApply() && commits == 0, "An active Korean composition committed the document");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
            Assert(!panel.TryApply() && commits == 0, "Composition-end input dispatch committed twice");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            Assert(panel.TryApply() && commits == 1, "Committed Korean text could not be applied after composition");
        });
    }
}
