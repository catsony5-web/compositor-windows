using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public static class TextEditorDialogTests
{
    // KeyEventArgs requires a presentation source even when routed directly.
    // This source has no HWND and never injects keys into the desktop.
    sealed class OffscreenSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
        static TextSpec Initial() => new() { Content = "처음", FontFamily = "Segoe UI", FontSize = 24, ColorArgb = 0xFF102030 };
        static KeyEventArgs KeyPress(Key key) => new(Keyboard.PrimaryDevice, new OffscreenSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        test("text editor Enter applies the current multiline content", () =>
        {
            bool? result = null;
            var dialog = new TextEditorDialog(null, Initial(), value => result = value);
            dialog.EditorForTesting.Text = "첫 줄\n둘째 줄";
            var key = KeyPress(Key.Enter);
            dialog.EditorForTesting.RaiseEvent(key);
            Assert(result == true, "Enter should apply the dialog");
            Assert(key.Handled, "Enter should be consumed by the apply command");
            Assert(dialog.Spec.Content == "첫 줄\n둘째 줄", "Applying must preserve multiline text");
        });

        test("text editor Escape cancels without changing the spec", () =>
        {
            bool? result = null;
            var dialog = new TextEditorDialog(null, Initial(), value => result = value);
            dialog.EditorForTesting.Text = "변경했지만 취소";
            var key = KeyPress(Key.Escape);
            dialog.EditorForTesting.RaiseEvent(key);
            Assert(result == false, "Escape should cancel the dialog");
            Assert(key.Handled, "Escape should be consumed by the cancel command");
            Assert(dialog.Spec.Content == "처음", "Cancel must leave the original spec intact");
        });

        test("text editor keyboard contract distinguishes newline and apply", () =>
        {
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.None, false, false) == TextEditorDialog.KeyAction.Apply);
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.Control, false, false) == TextEditorDialog.KeyAction.Apply);
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.Shift, false, false) == TextEditorDialog.KeyAction.InsertNewline);
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, false, false) == TextEditorDialog.KeyAction.InsertNewline);
            Assert(TextEditorDialog.ResolveKey(Key.Escape, ModifierKeys.None, false, false) == TextEditorDialog.KeyAction.Cancel);
            Assert(TextEditorDialog.ResolveKey(Key.Tab, ModifierKeys.None, false, false) == TextEditorDialog.KeyAction.Ignore);
        });

        test("text editor apply button commits multiline content offscreen", () =>
        {
            bool? result = null;
            var dialog = new TextEditorDialog(null, Initial(), value => result = value);
            dialog.EditorForTesting.Text = "버튼 첫 줄\n버튼 둘째 줄";
            dialog.ApplyButtonForTesting.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(result == true && dialog.Spec.Content == "버튼 첫 줄\n버튼 둘째 줄", "Apply button must commit the same multiline spec as Enter");
        });

        test("text editor leaves IME Enter available for composition commit", () =>
        {
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.None, true, false) == TextEditorDialog.KeyAction.ImePassThrough, "Active composition Enter must not apply");
            Assert(TextEditorDialog.ResolveKey(Key.Enter, ModifierKeys.None, false, true) == TextEditorDialog.KeyAction.ImePassThrough, "Composition-end Enter must not apply twice");
            Assert(TextEditorDialog.ResolveKey(Key.ImeProcessed, ModifierKeys.None, false, false, Key.Enter) == TextEditorDialog.KeyAction.ImePassThrough, "IME processed Enter must pass through");
        });

        test("text editor keeps multiline input and default/cancel buttons configured", () =>
        {
            var dialog = new TextEditorDialog(null, Initial(), _ => { });
            Assert(dialog.EditorForTesting.AcceptsReturn && dialog.EditorForTesting.TextWrapping == System.Windows.TextWrapping.Wrap);
            Assert(dialog.ApplyButtonForTesting.IsDefault, "Apply must be the default button");
            Assert(dialog.CancelButtonForTesting.IsCancel, "Cancel must be the cancel button");
        });
    }
}
