using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunStudioUiTests(Action<string, Action> test, string directory)
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

        test("recent documents keep the newest first, deduplicate, cap and ignore a corrupt store", () =>
        {
            string store = Path.Combine(directory, "recent-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                string first = Path.Combine(directory, "첫 작업.moruproj"), second = Path.Combine(directory, "second.png");
                RecentDocuments.Add(first, store); RecentDocuments.Add(second, store);
                var list = RecentDocuments.Add(first.ToUpperInvariant(), store);
                Check(list.Count == 2 && string.Equals(list[0], Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase), "Reopening did not move the document to the top once");
                for (int i = 0; i < 12; i++) RecentDocuments.Add(Path.Combine(directory, $"file-{i}.png"), store);
                list = RecentDocuments.Load(store);
                Check(list.Count == RecentDocuments.Limit && list[0].EndsWith("file-11.png"), "Recent list exceeded its limit or lost order");
                Check(RecentDocuments.Remove(list[0], store).Count == RecentDocuments.Limit - 1, "Removal failed");
                File.WriteAllText(store, "{ not json");
                Check(RecentDocuments.Load(store).Count == 0, "A corrupt store was not treated as empty");
                File.WriteAllText(store, "[\"relative.png\", null, \"\"]");
                Check(RecentDocuments.Load(store).Count == 0, "Relative or empty entries were accepted");
            }
            finally { if (File.Exists(store)) File.Delete(store); }
        });

        test("headless windows never read or write the user's recent documents", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.RememberRecent(Path.Combine(directory, "private.moruproj"));
                Check(window.recentDocuments.Count == 0 && window.recentPanel.Visibility == Visibility.Collapsed, "Headless run recorded a recent document");
                window.recentDocuments = [Path.Combine(directory, "a.moruproj"), Path.Combine(directory, "b.png")];
                window.RebuildRecentDocuments();
                var rows = window.recentPanel.Children.OfType<Button>().ToArray();
                Check(rows.Length == 2 && AutomationProperties.GetName(rows[0]) == "최근 문서 열기: a.moruproj", "Recent rows lack complete accessible names");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("field dialog labels every input and ends with the primary action", () =>
        {
            var (dialog, boxes) = Dialogs.CreateFields(null, "캔버스 크기", [("너비 (px)", "10"), ("높이 (px)", "20")]);
            try
            {
                Check(boxes.Count == 2 && AutomationProperties.GetName(boxes[1]) == "높이 (px)" && boxes[1].Text == "20", "Field inputs lost labels or values");
                var buttons = Descendants(dialog).OfType<Button>().ToArray();
                Check(buttons.Length == 2 && buttons[0].IsCancel && buttons[1].IsDefault && (string)buttons[1].Content == "적용", "Dialog actions are not cancel then primary");
            }
            finally { dialog.Close(); }
        });

        test("message dialog keeps the message selectable and offers copy for errors only", () =>
        {
            var error = new MessageDialog(null, "첫 줄\n둘째 줄", "저장하지 못했습니다", NoticeKind.Error);
            var notice = new MessageDialog(null, "안내", "Morupixel", NoticeKind.Information);
            try
            {
                var body = Descendants(error).OfType<TextBox>().Single();
                Check(body.IsReadOnly && body.Text == error.Message, "Message is not a selectable read-only field");
                Check(Descendants(error).OfType<Button>().Any(b => (string)b.Content == "내용 복사"), "Error notice lacks a copy action");
                Check(!Descendants(notice).OfType<Button>().Any(b => (string)b.Content == "내용 복사"), "Information notice offered copy");
                Check(Descendants(notice).OfType<Button>().Single().IsCancel, "Notice cannot be dismissed with Esc");
            }
            finally { error.Close(); notice.Close(); }
        });
    }
}
