using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal const string CompatibilityExportMenu = "PDF · PSD · AI로 내보내기";
    // Remembered import settings live in %LOCALAPPDATA%\Morupixel. Headless checks and offscreen
    // renders keep them in memory unless a test store path is given.
    internal string? importSettingsStore;
    ImportSettings? sessionImportSettings;
    // Tests run the dialog's steps without showing a window.
    internal Func<CompatibilityDialog, bool>? importDialogRunner;
    string? ImportSettingsPath => importSettingsStore ?? (headlessTesting ? null : ImportSettingsStore.DefaultStorePath);
    internal ImportSettings LoadImportSettings() => ImportSettingsPath is { } store ? ImportSettingsStore.Load(store) : sessionImportSettings ?? new();
    internal void SaveImportSettings(ImportSettings settings)
    {
        sessionImportSettings = settings.Sanitized();
        if (ImportSettingsPath is { } store) ImportSettingsStore.Save(settings, store);
    }

    // One settings dialog per file kind, prefilled with the last settings; "apply to all" imports
    // every file of that kind with them. DWG/DXF skip the dialog when asked to, unless Shift is held.
    // Returns false when a placement was cancelled.
    bool ReadCompatibilityDocuments(IReadOnlyList<string> paths, bool placeAsLayer, List<ImportedFile> results, out bool quick)
    {
        quick = false;
        bool ask = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        foreach (var family in paths.GroupBy(CompatibilityImport.Family))
        {
            var pending = family.ToList();
            while (pending.Count > 0)
            {
                var settings = LoadImportSettings();
                bool direct = !ask && family.Key == ImportFamily.Cad && settings.CadSkipDialog;
                var dialog = new CompatibilityDialog(headlessTesting ? null : this, pending[0], placeAsLayer, settings, pending, direct);
                bool accepted = importDialogRunner?.Invoke(dialog) ?? dialog.ShowDialog() == true;
                if (!accepted || dialog.Results.Count == 0)
                {
                    if (placeAsLayer) return false;
                    break; // Cancelling one kind leaves the other chosen files.
                }
                if (dialog.UsedSettings is { } used) SaveImportSettings(used);
                quick |= dialog.Direct;
                results.AddRange(dialog.Results);
                pending.RemoveRange(0, Math.Min(pending.Count, dialog.Results.Count));
            }
        }
        return true;
    }

    void OpenCompatibility(string path) => OpenCompatibilityFiles([path]);

    // Each chosen drawing opens in its own tab, as when opening them one by one.
    internal void OpenCompatibilityFiles(IReadOnlyList<string> paths)
    {
        int room = 8 - tabs.Count;
        if (room <= 0) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        var chosen = paths.Take(room).ToArray();
        var results = new List<ImportedFile>();
        ReadCompatibilityDocuments(chosen, false, results, out bool quick);
        int opened = 0;
        foreach (var item in results)
        {
            if (item.Document is not { } imported) continue;
            AddTab(imported, null); opened++; RememberRecent(item.Path);
            if (imported.Layers.Any(l => l.Vector != null)) SetWorkspaceMode(true);
        }
        if (opened > 0)
            status.Text = quick ? $"이전 가져오기 설정으로 {opened}개 파일을 열었습니다 · 설정 창을 보려면 Shift를 누른 채 여세요."
                : opened > 1 ? $"호환 파일 {opened}개 가져오기 완료 · 원본은 변경하지 않았습니다." : "호환 파일 가져오기 완료 · 원본은 변경하지 않았습니다.";
        ReportImportProblems(results, paths.Count - chosen.Length);
    }

    void ReportImportProblems(IReadOnlyList<ImportedFile> results, int skipped)
    {
        var lines = results.Where(r => r.Error != null).Select(r => $"{Path.GetFileName(r.Path)}: {r.Error}").ToList();
        if (skipped > 0) lines.Add($"열린 문서는 최대 8개라서 파일 {skipped}개는 열지 않았습니다.");
        if (lines.Count == 0) return;
        if (headlessTesting) { status.Text = string.Join(" · ", lines); return; }
        MessageDialog.Show(this, string.Join("\n", lines), "가져오지 못한 파일", NoticeKind.Warning);
    }

    // Opens several chosen or dropped files: projects and images one by one, drawings together.
    internal void OpenPaths(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Where(p => !CompatibilityImport.Supports(p))) OpenPath(path);
        var drawings = paths.Where(CompatibilityImport.Supports).ToArray();
        if (drawings.Length > 0) OpenCompatibilityFiles(drawings);
    }

    void DropFiles(string[] files)
    {
        foreach (var file in files.Where(f => !CompatibilityImport.Supports(f)))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".moruproj" or ".cwproj" or ".comp") OpenPath(file); else ImportFiles([file]);
        }
        var drawings = files.Where(CompatibilityImport.Supports).ToArray();
        if (drawings.Length > 0) OpenCompatibilityFiles(drawings);
    }

    // File menu: bring the DWG/DXF settings dialog back after "don't ask again".
    void AddImportSettingsMenu(MenuItem file)
    {
        var item = new MenuItem { Header = "도면 가져오기 설정 다시 묻기" };
        item.Click += (_, _) => Guard(AskImportSettingsAgain);
        var anchor = file.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Header, "레이어로 가져오기…"));
        file.Items.Insert(anchor == null ? file.Items.Count : file.Items.IndexOf(anchor) + 1, item);
    }

    internal void AskImportSettingsAgain()
    {
        var settings = LoadImportSettings();
        if (settings.CadSkipDialog) SaveImportSettings(settings with { CadSkipDialog = false });
        status.Text = "다음 DWG·DXF 가져오기부터 설정 창을 다시 표시합니다.";
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
