namespace Compositor.Windows;

public sealed partial class MainWindow
{
    Document? ReadCompatibilityDocument(string path, bool placeAsLayer = false)
    {
        var dialog = new CompatibilityDialog(this, path, placeAsLayer);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
    void OpenCompatibility(string path)
    {
        if (tabs.Count >= 8) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        if (ReadCompatibilityDocument(path) is { } imported) { AddTab(imported, null); status.Text = "호환 파일 가져오기 완료 · 원본은 변경하지 않았습니다."; }
    }
    void ExportCompatibility()
    {
        CommitFocusedInspectorField(); CancelGesture(); new CompatibilityExportDialog(this, doc).ShowDialog();
    }
}
