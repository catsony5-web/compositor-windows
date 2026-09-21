using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunCloseConfirmationTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<Button> Buttons(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                if (child is Button button) yield return button;
                foreach (var nested in Buttons(child)) yield return nested;
            }
        }
        test("close confirmation uses explicit keyboard-accessible actions and dismissal cancels", () =>
        {
            foreach (var (label, expected) in new[]
            {
                ("취소", DocumentCloseChoice.Cancel), ("저장하지 않고 닫기", DocumentCloseChoice.Discard), ("저장 후 닫기", DocumentCloseChoice.Save)
            })
            {
                var dialog = new SaveChangesDialog(null, "도면_수정본 · 저장 대상");
                var buttons = Buttons(dialog).ToArray();
                var save = buttons.Single(button => Equals(button.Content, "저장 후 닫기"));
                var cancel = buttons.Single(button => Equals(button.Content, "취소"));
                Check(buttons.Length == 3 && buttons.All(button => button.Focusable), "Close choices are not reachable by keyboard");
                Check(save.IsDefault && cancel.IsCancel && !buttons.Single(button => Equals(button.Content, "저장하지 않고 닫기")).IsDefault,
                    "Default or Escape action could discard the document");
                Check(dialog.Choice == DocumentCloseChoice.Cancel && !dialog.IsLoaded, "A dialog accepted a choice before the user acted");
                buttons.Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.Choice == expected && !dialog.IsLoaded, "Actual button event returned the wrong close decision");
            }
            var dismissed = new SaveChangesDialog(null, "닫기 취소 확인"); dismissed.Close();
            Check(dismissed.Choice == DocumentCloseChoice.Cancel, "Window dismissal implicitly discarded changes");
        });

        test("close confirmation preserves dirty work on cancel and cancelled save and saves before allowing close", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            Check(window.ConfirmDiscard(_ => throw new Exception("Prompted without a document")), "Empty workspace did not allow close");
            var document = NewDocumentDialog.CreateDocument("평면도_수정 · 저장 확인", "8", "6", 1);
            window.AddTab(document, null);
            Check(window.ConfirmDiscard(_ => throw new Exception("Prompted for a clean document")), "Clean document did not allow close");
            window.Edit("이동", () => document.Active!.X = 3);
            var history = window.history; Guid revision = document.Revision;
            int prompts = 0, saves = 0;
            DocumentCloseChoice Choose(DocumentCloseChoice choice, string name)
            { prompts++; Check(name == document.Name, "Prompt showed the wrong document name"); return choice; }
            bool CancelSave() { saves++; return false; }
            Check(!window.ConfirmDiscard(name => Choose(DocumentCloseChoice.Cancel, name), CancelSave) && saves == 0, "Cancel saved or closed dirty work");
            Check(!window.ConfirmDiscard(name => Choose(DocumentCloseChoice.Save, name), CancelSave) && saves == 1, "A cancelled or failed save allowed close");
            Check(window.ConfirmDiscard(name => Choose(DocumentCloseChoice.Discard, name), CancelSave) && saves == 1, "Discard invoked Save or prevented explicit close");
            Check(window.tabs.Count == 1 && ReferenceEquals(window.doc, document) && ReferenceEquals(window.history, history)
                && window.doc.Revision == revision && window.history.Dirty(document) && window.history.CanUndo,
                "Choosing how to close changed the document or discarded its history");
            string destination = Path.Combine(directory, "close-confirmation.moruproj"); window.projectPath = destination;
            Check(window.ConfirmDiscard(name => Choose(DocumentCloseChoice.Save, name), () => window.Save(false)), "Successful save did not allow close");
            var saved = ProjectStore.Load(destination);
            Check(prompts == 4 && saved.Name == document.Name && saved.Active!.X == 3 && !window.history.Dirty(window.doc), "Save did not persist the current document before close");
            window.CloseTab(); Check(!window.HasDocument, "Saved document could not close to the empty workspace");
        });
    }
}
