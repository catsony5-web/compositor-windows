using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Routed keyboard events need a source, but this never creates an HWND,
    // takes desktop focus, or sends input to an existing editor window.
    sealed class PanelKeyboardSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    internal static void RunPanelNavigationTests(Action<string, Action> test)
    {
        test("panel resize and workspace arrows preserve layers and redo", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.doc = new Document { Width = 8, Height = 8, Name = "패널 키보드 테스트" };
                window.doc.Add(new Layer { Name = "이미지", Pixels = Raster.Solid(8, 8, Colors.Red), X = 3, Y = 4 });
                window.history.Reset(window.doc); window.InitializeWorkspace();
                window.NudgeSelected(new Vector(1, 0)); window.Undo();
                if (!window.history.CanRedo) throw new Exception("Could not prepare redo state");
                var pixels = window.doc.Active!.Pixels;
                var grip = Descendants(window).OfType<Thumb>().Single(control =>
                    AutomationProperties.GetName(control) == "오른쪽 패널 너비 조절");
                var root = (FrameworkElement)window.Content;
                var source = new PanelKeyboardSource { RootVisual = root };
                void Layout()
                {
                    root.Measure(new Size(1280, 900)); root.Arrange(new Rect(0, 0, 1280, 900)); root.UpdateLayout();
                }
                void Press(Key key, UIElement? target = null)
                {
                    var control = target ?? grip;
                    var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    control.RaiseEvent(e);
                    if (!e.Handled) { e.RoutedEvent = Keyboard.KeyDownEvent; control.RaiseEvent(e); }
                    if (!e.Handled) throw new Exception("Panel control did not consume its arrow key");
                    Layout();
                }
                Layout();
                Press(Key.Left);
                if (window.rightPanelColumn.Width.Value != 404) throw new Exception("Left arrow did not widen the panel");
                Press(Key.Right);
                if (window.rightPanelColumn.Width.Value != 396) throw new Exception("Right arrow did not narrow the panel");
                for (int i = 0; i < 30; i++) Press(Key.Left);
                if (window.rightPanelColumn.Width.Value != 520) throw new Exception("Panel exceeded its maximum width");
                for (int i = 0; i < 40; i++) Press(Key.Right);
                if (window.rightPanelColumn.Width.Value != 324) throw new Exception("Panel exceeded its minimum width");
                Press(Key.Right, window.workspaceSwitch!);
                if (!window.designWorkspace || window.workspaceSwitch!.IsChecked != true)
                    throw new Exception("Right arrow did not select the design workspace");
                Press(Key.Right, window.workspaceSwitch!);
                if (!window.designWorkspace) throw new Exception("Selecting the active workspace toggled it off");
                Press(Key.Left, window.workspaceSwitch!);
                if (window.designWorkspace || window.workspaceSwitch!.IsChecked != false)
                    throw new Exception("Left arrow did not select the photo workspace");
                if (window.doc.Active!.X != 3 || window.doc.Active.Y != 4 || !ReferenceEquals(pixels, window.doc.Active.Pixels)
                    || window.history.CanUndo || !window.history.CanRedo || window.history.Dirty(window.doc))
                    throw new Exception("Resizing the panel edited the document or changed its history");
            }
            finally { window.renderCts?.Cancel(); window.jobCts?.Cancel(); window.pendingInspectorCommit = null; }
        });

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
