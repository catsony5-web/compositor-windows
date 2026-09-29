using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace Compositor.Windows;

// 사용 목적 (App/UserProfiles.cs) applied to the editor: tool rail groups, the first
// right-panel tab's sections and caption, and the start screen's quick starts. Every
// ordering decision comes from the profile table; this file only assembles it. The
// default profile leaves every builder untouched.
public sealed partial class MainWindow
{
    UserProfile userProfile = UserProfiles.Default;
    // False until the user picks a purpose (start screen, View menu, palette) or a saved layout restores one.
    bool userProfileChosen;
    readonly Dictionary<string, MenuItem> profileMenuItems = [];
    SegmentedChoice<string>? startProfileChoice;
    TextBlock? startProfileSummary, startProfileHint;

    internal UserProfile CurrentUserProfile => userProfile;

    // pickedByUser: also switch to the profile's work mode. A restored layout keeps its saved mode.
    internal void SetUserProfile(string id, bool pickedByUser = true)
    {
        var next = UserProfiles.Find(id) ?? UserProfiles.Default;
        bool changed = !ReferenceEquals(next, userProfile);
        userProfile = next; userProfileChosen = true;
        foreach (var (key, item) in profileMenuItems) item.IsChecked = key == next.Id;
        startProfileChoice?.Select(next.Id);
        UpdateStartProfile();
        if (changed)
        {
            CommitFocusedInspectorField(); CancelGesture();
            BuildWorkspaceTools(); ApplyWorkspaceStudio();
            // Show the rearranged first tab without moving or activating a user-positioned pane.
            ShowStudioPage(0, false);
            studioScroll.Height = PreferredStudioHeight(ActualHeight);
        }
        if (pickedByUser && next.DesignWorkspace is { } design) SetWorkspaceMode(design);
        if (pickedByUser) status.Text = $"사용 목적: {Loc.T(next.Name)} · 도구와 패널 순서를 맞췄습니다. 모든 기능은 그대로입니다.";
    }

    // Unlisted or repeated tools never disappear: they follow in a last group.
    (string Caption, Tool[] Tools)[] CurrentToolGroups(bool design)
    {
        if (userProfile.ToolGroups is not { } groups) return WorkspaceToolGroups(design);
        var seen = new HashSet<Tool>();
        var result = groups.Select(g => (g.Caption, Tools: g.Tools.Where(t => toolButtons.ContainsKey(t) && seen.Add(t)).ToArray())).Where(g => g.Tools.Length > 0).ToList();
        var missing = toolButtons.Keys.Where(t => !seen.Contains(t)).ToArray();
        if (missing.Length > 0) result.Add(("기타", missing));
        return [.. result];
    }

    void BuildFirstTab(StackPanel panel)
    {
        var order = designWorkspace ? userProfile.DesignSections : userProfile.PhotoSections;
        if (order == null) { if (designWorkspace) BuildDesignActions(panel); else BuildPhotoActions(panel); return; }
        ArrangeSections(panel, order);
    }

    sealed record SectionRun(string Title, string? Detail, List<UIElement> Rows);

    // A built panel as runs of one section header and its rows; rows before the first
    // header (the 사진 현상 card) form a run titled `lead`.
    static List<SectionRun> SectionRuns(StackPanel panel, string lead, string? leadDetail = null)
    {
        var runs = new List<SectionRun>();
        foreach (UIElement child in panel.Children)
        {
            if (child is SectionHeader header) runs.Add(new(header.Key, header.ToolTip as string, []));
            else { if (runs.Count == 0) runs.Add(new(lead, leadDetail, [])); runs[^1].Rows.Add(child); }
        }
        panel.Children.Clear();
        return runs;
    }

    // Sections come from the photo and design panels and the profile's own sections, placed
    // in the profile's order; the current mode's unlisted sections follow in their usual order,
    // so actions added to either panel later still appear.
    void ArrangeSections(StackPanel panel, IReadOnlyList<string> order)
    {
        static StackPanel Built(Action<StackPanel> build) { var built = new StackPanel(); build(built); return built; }
        const string leadDetail = "원본을 유지하며 사진과 렌더 이미지의 빛과 색을 보정합니다.";
        var photo = SectionRuns(Built(BuildPhotoActions), UserProfiles.PhotoLead, leadDetail);
        var design = SectionRuns(Built(BuildDesignActions), UserProfiles.PhotoLead, leadDetail);
        var own = SectionRuns(Built(BuildProfileSections), "");
        var current = designWorkspace ? design : photo;
        var pool = new Dictionary<string, SectionRun>(StringComparer.Ordinal);
        foreach (var run in own.Concat(current).Concat(designWorkspace ? photo : design)) pool.TryAdd(run.Title, run);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        void Place(SectionRun run)
        {
            if (run.Title.Length == 0 || !placed.Add(run.Title)) return;
            WorkspaceSection(panel, run.Title, run.Detail);
            foreach (var row in run.Rows) panel.Children.Add(row);
        }
        foreach (var title in order) if (pool.TryGetValue(title, out var run)) Place(run);
        foreach (var run in current) Place(pool[run.Title]);
    }

    // Sections only profiles place. A title shared with a mode panel section replaces it.
    void BuildProfileSections(StackPanel panel)
    {
        WorkspaceSection(panel, UserProfiles.LineCleanup, "가져온 도면의 선을 레이어 역할(구조·창호·가구·치수)에 맞춰 정리합니다.");
        panel.Children.Add(QuickActions.Feature(Theme.Glyphs.LineWeight, "선 정리 다시 적용", "벽은 굵고 진하게, 가구·치수는 가늘고 연하게",
            Run(ReapplyLineCleanup), "선택한 도면(선택이 없으면 문서 전체)의 선 굵기와 농도를 레이어 역할에 맞춰 다시 정리 · 한 번에 실행 취소"));
        WorkspaceCommands(panel,
            (Theme.Glyphs.Import, "도면 가져오기", ImportDrawing, "DWG·DXF·PDF 도면을 열거나 지금 보드에 레이어로 가져오기", null),
            (Theme.Glyphs.LayerStack, "도면 레이어 보기", ShowDrawingLayers, "레이어 카드에 도면 레이어 목록 표시", null),
            (Theme.Glyphs.Text, "치수·문자 표시 전환", ToggleAnnotationLayers, "치수·문자 역할의 도면 레이어를 한 번에 숨기거나 다시 표시", null),
            (Theme.Glyphs.Palette, "해치 재질 표시 전환", ToggleMaterialLayers, "가져올 때 채운 해치 재질 레이어를 한 번에 숨기거나 다시 표시", null));
        WorkspaceSection(panel, UserProfiles.Retouch, "잡티를 지우고 빈 곳을 주변 픽셀로 채웁니다.");
        WorkspaceCommands(panel,
            (ToolIcons.PathData(Tool.Heal), "복구 브러시", () => SetTool(Tool.Heal), "Alt+클릭으로 참조 위치 지정 후 드래그", null),
            (ToolIcons.PathData(Tool.CloneStamp), "복제 도장", () => SetTool(Tool.CloneStamp), "Alt+클릭으로 참조 위치 지정 후 복제", null),
            (ToolIcons.PathData(Tool.BlurBrush), "흐림 브러시", () => SetTool(Tool.BlurBrush), "드래그한 곳만 부드럽게 흐리기", null),
            (Theme.Glyphs.FillSelection, "주변으로 채우기", FillFromSurroundings, "제거할 부분을 선택한 뒤 주변 픽셀로 채우기", null),
            (ToolIcons.PathData(Tool.Eraser), "지우개", () => SetTool(Tool.Eraser), "드래그하여 픽셀 지우기", null),
            (ToolIcons.PathData(Tool.Brush), "브러시 설정", () => ShowStudioPage(3), "크기와 경도, 브러시 프리셋", null));
    }

    void ReapplyLineCleanup() => _ = ReapplyLineCleanupAsync();

    // Restyling re-reads and redraws every line layer (up to tens of thousands for an object
    // import), so it runs on an isolated STA like the import itself: the window stays responsive,
    // Esc cancels, and the result becomes one undo step only if the document did not change.
    // Only an explicit layer selection limits it; the active layer alone (e.g. the last object of a
    // drawing just placed on the board) is not a selection. Roles follow the import dialog's
    // remembered choices. True when lines were restyled.
    internal async Task<bool> ReapplyLineCleanupAsync()
    {
        if (!HasDocument) return false;
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var historyAtStart = history;
        // A newer run replaced this one: leave the status to it.
        bool Shown() => ReferenceEquals(document, doc) && (jobCts == null || ReferenceEquals(jobCts, cts));
        var candidate = doc.Snapshot();
        Guid[] scope = selectedLayers.Contains(doc.ActiveId) ? ExportSelectionIds() : [];
        var roles = LoadImportSettings().CadRoles;
        var progress = new Progress<(int Done, int Total)>(p => { if (ReferenceEquals(jobCts, cts)) status.Text = $"선 정리 다시 적용 중… {p.Done:N0} / {p.Total:N0}  Esc: 취소"; });
        status.Text = "선 정리 다시 적용 중…  Esc: 취소";
        try
        {
            var result = await CompatibilityImport.OnSta(() => DrawingLineCleanup.Apply(candidate, scope, roles, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { if (Shown()) status.Text = "선 정리를 취소했습니다."; return false; }
            if (!ReferenceEquals(document, doc) || revision != doc.Revision || !ReferenceEquals(historyAtStart, history))
            {
                if (ReferenceEquals(historyAtStart, history) && (jobCts == null || ReferenceEquals(jobCts, cts))) status.Text = "정리하는 동안 문서가 바뀌어 선 정리를 적용하지 않았습니다. 다시 실행하세요.";
                return false;
            }
            if (result.Restyled == 0)
            {
                status.Text = result.Unchanged > 0 ? "도면 선이 이미 레이어 역할에 맞게 정리되어 있습니다."
                    : result.Locked > 0 ? "잠긴 도면 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."
                    : "정리할 도면 선이 없습니다. 벡터로 가져온 도면의 레이어 이름(WALL · DOOR · FURN · DIM · 벽 · 창호 등)으로 역할을 찾습니다.";
                return false;
            }
            Edit("선 정리 다시 적용", () => { doc = candidate; maskEditing = false; });
            status.Text = result.Unknown > 0 ? $"선 정리를 다시 적용했습니다: 레이어 {result.Restyled:N0}개 · 역할을 알 수 없는 레이어 {result.Unknown:N0}개는 그대로 두었습니다"
                : $"선 정리를 다시 적용했습니다: 레이어 {result.Restyled:N0}개";
            return true;
        }
        catch (OperationCanceledException) { if (Shown()) status.Text = "선 정리를 취소했습니다."; return false; }
        catch (Exception error) { if (headlessTesting) throw; if (ReferenceEquals(document, doc)) MessageDialog.Show(this, error.Message, "선 정리 다시 적용"); return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    // Offscreen renders and self-tests run a background step to completion on the calling
    // dispatcher, the way the shown window would.
    internal static T WaitOnDispatcher<T>(Func<Task<T>> start)
    {
        var previous = SynchronizationContext.Current; var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = start();
            if (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    const string DrawingFilter = "도면 (DWG / DXF / PDF)|*.dwg;*.dxf;*.pdf;*.ai|CAD 도면 (DWG / DXF)|*.dwg;*.dxf|PDF / AI (PDF 호환)|*.pdf;*.ai|모든 파일|*.*";

    // Without a document each drawing opens in its own tab; with one it is placed on the board as layers.
    void ImportDrawing()
    {
        var dialog = new OpenFileDialog { Title = "도면 가져오기", Filter = DrawingFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }

    void ShowDrawingLayers()
    {
        CommitFocusedInspectorField(); layerCategory = LayerCategory.Drawing; BuildLayers();
    }

    void ToggleAnnotationLayers() => ToggleLayerVisibility(DrawingLineCleanup.RoleLayers(doc, DrawingRole.Annotation, LoadImportSettings().CadRoles), "치수·문자 숨기기", "치수·문자 표시",
        n => $"치수·문자 레이어 {n:N0}개를 숨겼습니다.", n => $"치수·문자 레이어 {n:N0}개를 다시 표시했습니다.",
        "치수·문자 역할의 도면 레이어가 없습니다. 레이어 이름(DIM · TEXT · ANNO · 치수 · 문자 등)으로 찾습니다.");

    // Only the hatch fills made on import; materials the user applied to selections stay as they are.
    void ToggleMaterialLayers() => ToggleLayerVisibility(doc.Layers.Where(DrawingLineCleanup.IsHatchMaterial).Select(l => l.Id).ToArray(), "해치 재질 숨기기", "해치 재질 표시",
        n => $"해치 재질 레이어 {n:N0}개를 숨겼습니다.", n => $"해치 재질 레이어 {n:N0}개를 다시 표시했습니다.",
        "해치 재질 레이어가 없습니다. 도면을 가져올 때 ‘재질 추천으로 채우기’를 고르면 만들어집니다.");

    // Hides every target while any is visible, otherwise shows them all; one undo step.
    void ToggleLayerVisibility(Guid[] ids, string hideLabel, string showLabel, Func<int, string> hidden, Func<int, string> shown, string missing)
    {
        if (!HasDocument) return;
        if (ids.Length == 0) { status.Text = missing; return; }
        var targets = ids.ToHashSet();
        bool show = doc.Layers.Where(l => targets.Contains(l.Id)).All(l => !l.Visible);
        Edit(show ? showLabel : hideLabel, () => { foreach (var layer in doc.Layers.Where(l => targets.Contains(l.Id))) layer.Visible = show; });
        status.Text = show ? shown(ids.Length) : hidden(ids.Length);
    }

    MenuItem BuildProfileMenu()
    {
        var menu = new MenuItem { Header = "사용 목적", ToolTip = "하는 작업에 맞춰 도구와 오른쪽 패널의 순서, 시작 화면을 바꿉니다. 모든 기능은 그대로 남습니다." };
        foreach (var profile in UserProfiles.All)
        {
            string id = profile.Id;
            var item = new MenuItem { Header = profile.Name, IsCheckable = true, IsChecked = ReferenceEquals(profile, userProfile), ToolTip = profile.Summary };
            item.Click += (_, _) => Guard(() => SetUserProfile(id));
            profileMenuItems[id] = item; menu.Items.Add(item);
        }
        return menu;
    }

    // The first-run choice sits on the start screen itself: no dialog, and the hint below it
    // disappears once a purpose is chosen. The View menu keeps the same choice.
    FrameworkElement BuildStartProfileChoice()
    {
        var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var caption = Theme.Label("사용 목적", Theme.BodySize, Theme.Muted); caption.FontWeight = FontWeights.SemiBold; caption.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(caption);
        var choice = startProfileChoice = new SegmentedChoice<string>(UserProfiles.All.Select(p => (p.Id, p.Name)), userProfile.Id) { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(choice, "사용 목적");
        foreach (var (profile, button) in UserProfiles.All.Zip(choice.Buttons))
        {
            string id = profile.Id;
            button.ToolTip = profile.Summary; button.Padding = new Thickness(16, 4, 16, 4); button.MinHeight = 28;
            button.Click += (_, _) => Guard(() => SetUserProfile(id));
        }
        row.Children.Add(choice); host.Children.Add(row);
        TextBlock Line(string text, System.Windows.Media.Brush color)
        {
            var line = Theme.Label(text, Theme.CaptionSize, color);
            line.HorizontalAlignment = HorizontalAlignment.Center; line.TextAlignment = TextAlignment.Center; line.MaxWidth = 480; line.Margin = new Thickness(0, 6, 0, 0);
            host.Children.Add(line); return line;
        }
        startProfileSummary = Line(userProfile.Summary, Theme.Muted);
        startProfileHint = Line("하는 작업에 맞게 고르세요. 나중에 보기 → 사용 목적에서 바꿀 수 있습니다.", Theme.Subtle);
        startProfileHint.Margin = new Thickness(0, 2, 0, 0);
        startProfileHint.Visibility = userProfileChosen ? Visibility.Collapsed : Visibility.Visible;
        return host;
    }

    void UpdateStartProfile()
    {
        if (startProfileSummary != null) startProfileSummary.Text = userProfile.Summary;
        if (startProfileHint != null) startProfileHint.Visibility = userProfileChosen ? Visibility.Collapsed : Visibility.Visible;
        RebuildQuickStart();
    }

    // Offscreen review of the 건축학과 profile: the start screen, an imported plan before and
    // after 선 정리 in the editor, and the first tab in design and photo mode.
    static void RenderProfilePreviews(string directory)
    {
        var start = new MainWindow(null) { headlessTesting = true };
        try { start.SetUserProfile(UserProfiles.ArchitectureId); start.RenderPreview(Path.Combine(directory, "profile-architecture-startup.png")); }
        finally { start.StopRenderingForShutdown(); }
        string plan = Path.Combine(directory, "평면 예시.dxf");
        if (!File.Exists(plan)) return;
        var editor = new MainWindow(null) { headlessTesting = true };
        try
        {
            editor.SetUserProfile(UserProfiles.ArchitectureId);
            var drawing = CompatibilityImport.ReadAsync(plan, new(CadLongEdge: 1400, CadLayout: "*Model_Space", CadStructure: CadImportStructure.Objects, GroupDrawingObjects: true)).GetAwaiter().GetResult().Document;
            editor.AddTab(drawing, null);
            editor.RenderPreview(Path.Combine(directory, "profile-architecture-editor.png"));
            WaitOnDispatcher(editor.ReapplyLineCleanupAsync);
            editor.RenderPreview(Path.Combine(directory, "profile-architecture-cleanup.png"));
            editor.RenderPane(editor.studioPanes[0], Path.Combine(directory, "profile-architecture-actions.png"), 360, 1320);
            editor.SetWorkspaceMode(false);
            editor.RenderPane(editor.studioPanes[0], Path.Combine(directory, "profile-architecture-photo-actions.png"), 360, 1180);
        }
        finally { editor.StopRenderingForShutdown(); }
    }

    void RenderPane(StudioPane pane, string path, int width, int height)
    {
        RemovePane(pane); ((FrameworkElement)Content).UpdateLayout();
        var host = new Border { Width = width, Height = height, Background = Theme.Header, Child = pane };
        try
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); Loc.PrepareOffscreen(host); image.Render(host);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(path); encoder.Save(output);
        }
        finally { host.Child = null; ShowStudioPage(studioPage, false); }
    }
}
