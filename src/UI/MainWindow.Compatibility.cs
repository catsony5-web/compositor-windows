using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal const string CompatibilityExportMenu = "PDF · PSD · AI로 내보내기";
    Document? ReadCompatibilityDocument(string path, bool placeAsLayer = false)
    {
        var dialog = new CompatibilityDialog(this, path, placeAsLayer);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
    void OpenCompatibility(string path)
    {
        if (tabs.Count >= 8) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        if (ReadCompatibilityDocument(path) is { } imported) { AddTab(imported, null); if (imported.Layers.Any(l => l.Vector != null)) SetWorkspaceMode(true); status.Text = "호환 파일 가져오기 완료 · 원본은 변경하지 않았습니다."; }
    }
    void ExportCompatibility(CompatibilityExportFormat? format = null)
    {
        CommitFocusedInspectorField(); CancelGesture(); new CompatibilityExportDialog(this, doc, format).ShowDialog();
    }
    // File › PDF · PSD · AI로 내보내기 › one entry per file type, named exactly as in the dialog.
    // The command palette reads the same menu, so both show the same words.
    MenuItem BuildCompatibilityExportMenu()
    {
        var parent = DocumentControl(new MenuItem { Header = CompatibilityExportMenu });
        foreach (var choice in CompatibilityExport.Choices)
        {
            var format = choice.Format;
            var item = DocumentControl(new MenuItem { Header = choice.Title + "…", ToolTip = choice.Description });
            item.Click += (_, _) => { if (HasDocument) Guard(() => ExportCompatibility(format)); };
            parent.Items.Add(item);
        }
        return parent;
    }
}
