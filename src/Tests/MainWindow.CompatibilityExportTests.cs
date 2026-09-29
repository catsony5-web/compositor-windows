using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunCompatibilityExportTests(Action<string, Action> test)
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

        test("PDF · PSD · AI export dialog lists each file type by name with one line of explanation", () =>
        {
            var dialog = new CompatibilityExportDialog(null, Demo.Create());
            try
            {
                var buttons = dialog.Formats.Buttons;
                Check(buttons.Select(AutomationProperties.GetName).SequenceEqual(CompatibilityExport.Choices.Select(c => c.Title)), "The list does not show every choice by name");
                Check(buttons.All(b => AutomationProperties.GetHelpText(b).Length > 0 && b.Content is Grid), "A choice lost its explanation");
                Check(dialog.Formats.Selected == CompatibilityExportFormat.PdfSingle && dialog.KeepVectors.Visibility == Visibility.Visible && dialog.KeepVectors.IsChecked == true,
                    "The single-page PDF must start selected and keep the sample's text as vectors");
                buttons[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(dialog.Formats.Selected == CompatibilityExportFormat.PsdLayers && dialog.KeepVectors.Visibility == Visibility.Collapsed, "Choosing .psd did not switch the options");
                Check(dialog.DetailTexts.Any(t => t.Contains("픽셀 레이어로 바뀝니다")) && dialog.DetailTexts.Any(t => t.Contains("그룹 1개")), "The dialog must say that text becomes pixels: " + string.Join(" | ", dialog.DetailTexts));
                dialog.Formats.Select(CompatibilityExportFormat.AiLayers);
                Check(dialog.DetailTexts.Any(t => t.Contains("PDF 레이어 2개")) && dialog.DetailTexts.Any(t => t.Contains(".ai")) && dialog.SaveButton.IsEnabled, "The .ai choice was not explained");
                Check(dialog.Title.Contains("PDF · PSD · AI로 내보내기"), "The window title does not match the menu");
            }
            finally { dialog.Close(); }
        });

        test("PDF · PSD · AI export dialog disables saving and explains a limit", () =>
        {
            var many = new Document { Width = 8, Height = 8, Name = "많은 레이어" };
            for (int i = 0; i < Document.MaxLayers + 1; i++) many.Add(VectorShapes.Create(new ShapeSpec { Width = 2, Height = 2 }));
            var dialog = new CompatibilityExportDialog(null, many, CompatibilityExportFormat.PsdLayers);
            try
            {
                Check(!dialog.SaveButton.IsEnabled && dialog.Report.Problem != null && dialog.DetailTexts.First() == dialog.Report.Problem, "Too many layers must block saving with the reason first");
                dialog.Formats.Select(CompatibilityExportFormat.PsdSingle);
                Check(dialog.SaveButton.IsEnabled && dialog.Report.Problem == null, "The single-image .psd must stay available");
            }
            finally { dialog.Close(); }
        });

        test("file menu, ribbon and command palette use the export names from the dialog", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                window.AddTab(NewDocumentDialog.CreateDocument("내보내기", "32", "32", 0), null);
                var commands = window.BuildCommandRegistry();
                foreach (var choice in CompatibilityExport.Choices)
                {
                    var command = commands.SingleOrDefault(c => c.Id == $"menu:파일/{CompatibilityExportMenu}/{choice.Title}…");
                    Check(command != null && command.Title == choice.Title + "…" && command.Category == "파일 › " + CompatibilityExportMenu && command.IsAvailable(), "Missing menu command for " + choice.Title);
                }
                Check(!commands.Any(c => c.Title.Contains("PDF / PSD")), "The old combined menu entry is still listed");
                Check(window.RibbonGroups("파일").Any(g => g.Title == CompatibilityExportMenu && g.Items.Length == CompatibilityExport.Choices.Count), "The ribbon does not show the export group");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("image export dialog links to the PDF · PSD · AI export", () =>
        {
            var export = ExportDialog.Create(null, Demo.Create());
            try
            {
                var link = Descendants(export).OfType<Button>().SingleOrDefault(b => AutomationProperties.GetName(b) == "PDF · PSD · AI로 내보내기…");
                Check(link != null && link.IsEnabled, "The image export dialog does not offer the other formats");
            }
            finally { export.Close(); }
        });
    }
}
