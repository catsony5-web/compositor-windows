using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    sealed class WorkspaceTab
    {
        public readonly Guid Id = Guid.NewGuid();
        public Document Document = null!;
        public History History = null!;
        public Selection? Selection;
        public string? Path;
        public double Zoom;
        public Vector Pan;
        public Guid ArtboardId;
        public List<(bool Vertical, double Position)> Guides = [];
    }
    readonly List<WorkspaceTab> tabs = [];
    readonly StackPanel tabsBar = new() { Orientation = Orientation.Horizontal };
    readonly HashSet<Guid> selectedLayers = [];
    readonly HashSet<Guid> collapsedGroups = [];
    readonly HashSet<Guid> knownLayerGroups = [];
    int activeTab = -1;
    CancellationTokenSource? jobCts;
    readonly List<Point> lassoPoints = [];
    SelectionCombine selectionMode = SelectionCombine.Replace;
    Point? cloneSource;
    Point lastRetouch;
    Raster? cloneSnapshot;
    bool snapping = true, polygonInProgress;
    double wandTolerance = 16;
    bool wandContiguous = true, wandAntialias = true;

    static (Tool Tool, string Icon, string Name, string Key)[] AdvancedToolDefinitions() =>
    [ (Tool.Lasso,"L","올가미","L"), (Tool.PolygonLasso,"⌁","다각형 올가미","Shift+L"), (Tool.MagicWand,"✧","마술봉","W"),
      (Tool.CloneStamp,"S","복제 도장 / Alt+클릭으로 원본 지정","S"), (Tool.Heal,"J","복구 브러시","J"),
      (Tool.Smudge,"≈","스머지","R"), (Tool.Liquify,"~","액화","Shift+R"), (Tool.BlurBrush,"◉","흐림 브러시","K"), (Tool.Artboard,"▧","대지 편집","Shift+O") ];

    void InitializeWorkspace()
    {
        if (tabs.Count != 0) return;
        tabs.Add(new WorkspaceTab { Document = doc, History = history, Zoom = canvas.Zoom });
        activeTab = 0;
        selectedLayers.Add(doc.ActiveId); foreach (var g in doc.Layers.Where(l => l.Kind == LayerKind.Group)) collapsedGroups.Add(g.Id);
    }
    void StoreTab()
    {
        if (tabs.Count == 0) return;
        var tab = tabs[activeTab]; tab.Document = doc; tab.History = history; tab.Path = projectPath;
        tab.Selection = selection; tab.Zoom = canvas.Zoom; tab.Pan = canvas.Pan;
        tab.Guides = canvas.Guides.ToList();
        tab.ArtboardId = selectedArtboard;
    }
    void AddTab(Document document, string? path)
    {
        if (tabs.Count >= 8) throw new InvalidOperationException("열린 문서는 최대 8개입니다. 다른 문서를 저장하고 닫아주세요.");
        CancelGesture(); StoreTab();
        var h = new History(); h.Reset(document);
        tabs.Add(new WorkspaceTab { Document = document, History = h, Path = path });
        LoadTab(tabs.Count - 1); canvas.Fit(); RebuildTabs();
    }
    void SwitchTab(int index) { if (index < 0 || index >= tabs.Count || index == activeTab) return; CancelGesture(); StoreTab(); LoadTab(index); }
    void LoadTab(int index)
    {
        jobCts?.Cancel(); renderCts?.Cancel(); activeTab = index;
        var tab = tabs[index]; doc = tab.Document; history = tab.History; projectPath = tab.Path; selection = tab.Selection;
        selectedArtboard = tab.ArtboardId; sourceLayerSelection = null;
        selectedLayers.Clear(); if (doc.ActiveId != Guid.Empty) selectedLayers.Add(doc.ActiveId);
        maskEditing = false; canvas.Document = doc; canvas.Composite = null; composite = null;
        canvas.Guides.Clear(); canvas.Guides.AddRange(tab.Guides);
        canvas.Zoom = tab.Zoom > 0 ? tab.Zoom : .65; canvas.Pan = tab.Pan; Refresh();
    }
    void CloseTab()
    {
        if (!HasDocument) return;
        if (!ConfirmDiscard()) return;
        CancelGesture(); tabs.RemoveAt(activeTab);
        if (tabs.Count == 0) { EnterEmptyWorkspace(); return; }
        LoadTab(Math.Min(activeTab, tabs.Count - 1)); canvas.Fit();
    }
    bool ConfirmAllTabs()
    {
        StoreTab(); int initial = activeTab;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (!tabs[i].History.Dirty(tabs[i].Document)) continue;
            SwitchTab(i); if (!ConfirmDiscard()) return false; StoreTab();
        }
        if (initial >= 0 && initial < tabs.Count) SwitchTab(initial); return true;
    }
    void OpenPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".comp" && Directory.Exists(path)) { var result = CompositorPackage.Import(path); AddTab(result.Document, null); ShowImportWarnings(result.Warnings); }
        else if (ext is ".moruproj" or ".cwproj") OpenProject(path);
        else if (CompatibilityImport.Supports(path)) OpenCompatibility(path);
        else OpenImage(path);
    }
    int FindPathTab(string path)
    {
        StoreTab(); string full = Path.GetFullPath(path);
        return tabs.FindIndex(t => t.Path != null && string.Equals(Path.GetFullPath(t.Path), full, StringComparison.OrdinalIgnoreCase));
    }
    void EnsureSavePathAvailable(string path)
    {
        int owner = FindPathTab(path);
        if (owner >= 0 && owner != activeTab) throw new InvalidOperationException("이 파일은 다른 문서 탭에서 편집 중입니다. 그 탭에서 저장하거나 새 파일 이름을 사용하세요.");
    }
    void ImportCompositor()
    {
        var picker = new OpenFolderDialog { Title = "Compositor .comp 패키지 폴더 선택" };
        if (picker.ShowDialog(this) == true) { var result = CompositorPackage.Import(picker.FolderName); AddTab(result.Document, null); ShowImportWarnings(result.Warnings); }
    }
    void ShowImportWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0) MessageBox.Show(this, string.Join("\n", warnings), "가져오기 안내", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    void ExportCompositor()
    {
        var picker = new OpenFolderDialog { Title = "새 .comp 패키지를 만들 상위 폴더 선택" };
        if (picker.ShowDialog(this) != true) return;
        string safeName = string.Concat(doc.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string path = Path.Combine(picker.FolderName, safeName + ".comp");
        var warnings = CompositorPackage.Export(doc, path); ShowImportWarnings(warnings); status.Text = "Compositor 패키지 저장 완료 · " + path;
    }

    async void RunRasterJob(string name, Func<Layer, Selection?, CancellationToken, Raster> operation)
    {
        if (doc.Active == null || IsLockedWithParents(doc.Active)) return;
        if (doc.Active.Kind is LayerKind.Group or LayerKind.Adjustment) { status.Text = "픽셀 또는 텍스트 레이어를 선택하세요."; return; }
        if (selection != null && HasTransformedParent(doc.Active)) { status.Text = "변형된 그룹의 부분 보정은 먼저 그룹 변형을 초기화하거나 병합하세요."; return; }
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var layer = doc.Active.Snapshot(); var selected = selection;
        status.Text = name + " 처리 중…  Esc: 취소";
        try
        {
            var result = await Task.Run(() => operation(layer, selected, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(document, doc) || revision != doc.Revision || !ReferenceEquals(selected, selection)) return;
            Edit(name, () => { var target = doc.Layers.Single(l => l.Id == layer.Id); DocumentFeatures.Rasterize(target); target.Pixels = result; });
        }
        catch (OperationCanceledException) { if (ReferenceEquals(document, doc)) status.Text = "작업을 취소했습니다."; }
        catch (Exception e) { if (ReferenceEquals(document, doc)) MessageBox.Show(this, e.Message, name, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
    IEnumerable<(Layer Layer, int Depth)> LayerDisplayRows()
    {
        // Build the hierarchy once. Large CAD groups must not scan the entire
        // document again for every visible row or ancestor.
        var children = doc.Layers.ToLookup(layer => layer.ParentId);
        return Walk(null, 0);
        IEnumerable<(Layer Layer, int Depth)> Walk(Guid? parent, int depth)
        {
            if (depth > 16) yield break;
            foreach (var layer in children[parent].Reverse())
            {
                yield return (layer, depth);
                if (layer.Kind == LayerKind.Group && !collapsedGroups.Contains(layer.Id))
                    foreach (var child in Walk(layer.Id, depth + 1)) yield return child;
            }
        }
    }
    void AddAdvancedProperties(Layer layer)
    {
        if (layer.Kind == LayerKind.Text)
            properties.Children.Add(InspectorAction("텍스트 내용과 서식 편집", EditText, "문자·단락 속성에서 내용과 서식을 편집합니다.", layer));
        if (layer.Kind == LayerKind.Adjustment)
            properties.Children.Add(InspectorAction("조정 레이어 효과 편집", EditAdjustment, "선택한 조정 레이어의 효과와 값을 변경합니다.", layer));
        properties.Children.Add(InspectorAction(layer.Clipped ? "클리핑 마스크 해제" : "클리핑 마스크 만들기", ToggleClipping,
            layer.Clipped ? "아래 레이어에 적용된 클리핑을 해제합니다." : "아래 레이어의 불투명 영역 안에 표시합니다.", layer));
        properties.Children.Add(InspectorAction("선택 레이어 그룹 만들기", () => WorkspaceArrange(GroupSelected, true), "선택한 연속 레이어를 하나의 그룹으로 묶습니다.", layer));
    }
    void GroupSelected()
    {
        var ids = selectedLayers.Where(id => doc.Layers.Any(l => l.Id == id)).ToArray();
        if (ids.Length == 0 && doc.ActiveId != Guid.Empty) ids = [doc.ActiveId];
        Edit("그룹 만들기", () => { var g = ids.Length > 0 ? DocumentFeatures.Group(doc, ids, "새 그룹") : DocumentFeatures.CreateGroup(doc); if (ids.Length == 0) doc.Add(g); selectedLayers.Clear(); selectedLayers.Add(g.Id); });
    }
    void UngroupSelected() { if (doc.Active?.Kind == LayerKind.Group) Edit("그룹 해제", () => { DocumentFeatures.Ungroup(doc, doc.ActiveId); selectedLayers.Clear(); }); }
    void MoveToGroup()
    {
        if (doc.Active is not { } active) return;
        var groups = doc.Layers.Where(l => l.Kind == LayerKind.Group && l.Id != active.Id).ToList();
        var fields = Dialogs.Fields(this, "그룹으로 이동", ("그룹 번호: 0 = 최상위\n" + string.Join("\n", groups.Select((g, i) => $"{i + 1}: {g.Name}")), "0"));
        if (fields == null) return; int n = (int)Dialogs.Number(fields[0], 0, groups.Count);
        Guid? target = n == 0 ? null : groups[n - 1].Id;
        GuardReparent(active, target);
        Edit("그룹 이동", () => active.ParentId = target);
    }
    void GuardReparent(Layer layer, Guid? parent)
    {
        if (layer.ParentId == parent) return;
        bool transformed = HasTransformedParent(layer);
        if (parent is { } id) { var group = doc.Layers.Single(l => l.Id == id); transformed |= !group.Matrix.IsIdentity || group.Warp != null || HasTransformedParent(group); }
        if (transformed) throw new InvalidOperationException("레이어 위치를 보존하기 위해, 변형된 그룹 사이 이동은 먼저 그룹 변형을 초기화하거나 병합하세요.");
    }
    void ReorderDrop(Guid moving, Guid target, bool above = true, bool intoGroup = true)
    {
        if (moving == target) return;
        var source = doc.Layers.Single(l => l.Id == moving); var destination = doc.Layers.Single(l => l.Id == target);
        Guid? parent = intoGroup && destination.Kind == LayerKind.Group ? destination.Id : destination.ParentId;
        if (IsLockedWithParents(source) || IsLockedWithParents(destination)) throw new InvalidOperationException("잠긴 레이어나 그룹으로는 이동할 수 없습니다.");
        for (Guid? ancestor = parent; ancestor != null; ancestor = doc.Layers.Single(l => l.Id == ancestor).ParentId)
            if (ancestor == moving) throw new InvalidOperationException("그룹을 자신의 하위 레이어로 이동할 수 없습니다.");
        GuardReparent(source, parent);
        Edit("레이어 순서 변경", () =>
        {
            doc.Layers.Remove(source); source.ParentId = parent;
            doc.Layers.Insert(doc.Layers.IndexOf(destination) + (above ? 1 : 0), source);
            if (parent != null) collapsedGroups.Remove(parent.Value);
        });
    }
    void ToggleClipping() => EditLayer("클리핑 마스크", l => l.Clipped = !l.Clipped);
    void RasterizeActive() => EditLayer("픽셀 레이어로 변환", DocumentFeatures.Rasterize);
    void MergeDown()
    {
        if (doc.Active is not { } top || top.Kind == LayerKind.Group || IsLockedWithParents(top)) return;
        int index = doc.Layers.IndexOf(top); var lower = doc.Layers.Take(index).LastOrDefault(l => l.ParentId == top.ParentId);
        if (lower == null || lower.Kind == LayerKind.Group || lower.Locked) { status.Text = "같은 그룹의 아래쪽에 병합 가능한 레이어가 필요합니다."; return; }
        if (top.Blend != BlendMode.Normal || lower.Blend != BlendMode.Normal || top.Clipped || lower.Clipped || top.Kind == LayerKind.Adjustment || lower.Kind == LayerKind.Adjustment)
        { status.Text = "합성 결과 보존을 위해 Normal 모드의 일반 레이어끼리 병합하세요. 전체 병합도 가능합니다."; return; }
        if (doc.Layers.Skip(index + 1).FirstOrDefault(l => l.ParentId == top.ParentId)?.Clipped == true)
        { status.Text = "이 레이어를 참조하는 클리핑 레이어를 먼저 병합하거나 해제하세요."; return; }
        Edit("아래 레이어와 병합", () =>
        {
            var group = top.ParentId is { } id ? doc.Layers.Single(l => l.Id == id) : null;
            var temporary = new Document { Width = group?.Pixels.Width ?? doc.Width, Height = group?.Pixels.Height ?? doc.Height };
            var a = lower.Snapshot(); var b = top.Snapshot(); a.ParentId = b.ParentId = null; temporary.Add(a); temporary.Add(b);
            var pixels = Imaging.Render(temporary); var parent = top.ParentId; var name = lower.Name;
            int position = doc.Layers.IndexOf(lower); doc.Layers.Remove(top); doc.Layers.Remove(lower);
            var merged = new Layer { Name = name, Pixels = pixels, ParentId = parent }; doc.Layers.Insert(position, merged); doc.ActiveId = merged.Id;
        });
    }
    void CopyLayerToTab()
    {
        if (doc.Active is not { } l || tabs.Count < 2) { status.Text = "먼저 다른 문서를 여세요."; return; }
        if (l.Kind == LayerKind.Group) { status.Text = "그룹은 개별 레이어를 선택해 복사하세요."; return; }
        if (HasTransformedParent(l)) { status.Text = "변형된 그룹의 레이어는 먼저 선택 픽셀을 새 레이어로 추출한 뒤 복사하세요."; return; }
        var f = Dialogs.Fields(this, "다른 문서에 레이어 복사", (string.Join("\n", tabs.Select((t, i) => $"{i + 1}: {t.Document.Name}")), "1"));
        if (f == null) return; int index = (int)Dialogs.Number(f[0], 1, tabs.Count) - 1;
        var copy = l.Snapshot(); copy.Id = Guid.NewGuid(); copy.ParentId = null; copy.Clipped = false;
        SwitchTab(index); Edit("다른 문서의 레이어 추가", () => doc.Add(copy));
    }
    void ShowAdjustment(AdjustmentKind kind)
    {
        var dialog = new AdjustmentDialog(this, doc, new AdjustmentSpec { Kind = kind }, selection: selection);
        if (dialog.ShowDialog() != true) return;
        Edit("조정 레이어 추가", () =>
        {
            var layer = DocumentFeatures.CreateAdjustment(doc, dialog.Spec);
            if (selection != null) layer.Mask = SelectionTools.Mask(selection, doc.Width, doc.Height);
            doc.Add(layer);
        });
    }
    void EditAdjustment()
    {
        if (doc.Active?.Adjustment is not { } spec) return;
        var layer = doc.Active; var dialog = new AdjustmentDialog(this, doc, spec, layer.Id);
        if (dialog.ShowDialog() == true) EditLayer("조정 레이어 편집", l => l.Adjustment = dialog.Spec);
    }
    void EditText() { if (doc.Active?.Text is { }) EditTextLayer(doc.Active); else status.Text = "텍스트 레이어를 선택하세요."; }
    void EditTextLayer(Layer? layer, Point? point = null) => OpenTextProperties(layer, point);
    void InvertSelection() { var current = selection; int w = doc.Width, h = doc.Height; RunSelectionJob(ct => SelectionTools.Invert(current, w, h)); }
    void ModifySelection(string mode)
    {
        if (selection == null) return;
        var f = Dialogs.Fields(this, "선택 영역 " + mode, ("반경 (px)", "5")); if (f == null) return;
        int radius = (int)Dialogs.Number(f[0], 1, 100);
        var current = selection; int w = doc.Width, h = doc.Height;
        RunSelectionJob(ct => mode switch { "feather" => SelectionTools.Feather(current, w, h, radius, ct), "expand" => SelectionTools.Expand(current, w, h, radius, ct), _ => SelectionTools.Contract(current, w, h, radius, ct) });
    }
    void SelectAlpha()
    {
        if (doc.Active is not { } layer) return;
        var source = LayerDocument(layer); int w = doc.Width, h = doc.Height;
        RunSelectionJob(ct => { var image = Imaging.Render(source, ct); var alpha = new byte[w * h]; for (int i = 0; i < alpha.Length; i++) alpha[i] = image.Data[i * 4 + 3]; return SelectionTools.FromMask(alpha, w, h); });
    }
    async void RunSelectionJob(Func<CancellationToken, Selection> operation)
    {
        jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource(); var document = doc; var revision = doc.Revision; var previous = selection;
        status.Text = "선택 영역 계산 중…  Esc: 취소";
        try { var result = await Task.Run(() => operation(cts.Token), cts.Token); if (!cts.IsCancellationRequested && ReferenceEquals(document, doc) && revision == doc.Revision && ReferenceEquals(previous, selection)) { selection = result; Refresh(false); } }
        catch (OperationCanceledException) { }
        catch (Exception e) { status.Text = e.Message; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
    Document LayerDocument(Layer layer)
    {
        var keep = new HashSet<Guid> { layer.Id }; bool changed;
        do { changed = false; foreach (var child in doc.Layers) if (child.ParentId is { } p && keep.Contains(p)) changed |= keep.Add(child.Id); } while (changed);
        var parent = layer.ParentId; while (parent is { } id) { keep.Add(id); parent = doc.Layers.Single(l => l.Id == id).ParentId; }
        var source = doc.Snapshot(); source.Layers.RemoveAll(l => !keep.Contains(l.Id)); foreach (var l in source.Layers) { l.Visible = true; l.Clipped = false; } source.ActiveId = layer.Id; return source;
    }
    void MoveSelectionOutline()
    {
        if (selection == null) return; var f = Dialogs.Fields(this, "선택 윤곽 이동", ("가로 이동 (px)", "10"), ("세로 이동 (px)", "0")); if (f == null) return;
        int dx = (int)Dialogs.Number(f[0], -8192, 8192), dy = (int)Dialogs.Number(f[1], -8192, 8192);
        var mask = SelectionTools.Mask(selection, doc.Width, doc.Height); var moved = new byte[mask.Length];
        for (int y = 0; y < doc.Height; y++) for (int x = 0; x < doc.Width; x++) { int xx = x - dx, yy = y - dy; if (xx >= 0 && yy >= 0 && xx < doc.Width && yy < doc.Height) moved[y * doc.Width + x] = mask[yy * doc.Width + xx]; }
        selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)) { Coverage = moved, CanvasWidth = doc.Width, CanvasHeight = doc.Height }; Refresh(false);
    }
    void ExtractSelection()
    {
        if (selection == null || doc.Active is not { } layer) return;
        if (layer.Kind == LayerKind.Adjustment) { status.Text = "조정 레이어는 독립 픽셀을 포함하지 않습니다."; return; }
        var pixels = Imaging.Render(LayerDocument(layer));
        for (int y = 0; y < doc.Height; y++) for (int x = 0; x < doc.Width; x++) { int i = (y * doc.Width + x) * 4; pixels.Data[i + 3] = Imaging.Byte(pixels.Data[i + 3] * selection.Weight(x + .5, y + .5)); }
        Edit("선택 픽셀 복제", () => doc.Add(new Layer { Name = layer.Name + " 선택", Pixels = pixels })); SetTool(Tool.Move);
    }
    void ApplySelection(Selection incoming) { selection = SelectionTools.Combine(selection, incoming, doc.Width, doc.Height, selectionMode); Refresh(false); }
    void ContentFill()
    {
        if (selection == null) { status.Text = "제거할 부분을 먼저 선택하세요."; return; }
        RunRasterJob("내용 인식 채우기", (l, s, ct) => RetouchTools.ContentAwareFill(l, s!, ct));
    }
    void MotionBlur()
    {
        var f = Dialogs.Fields(this, "모션 블러", ("각도 (°)", "0"), ("거리 (px)", "20")); if (f == null) return;
        double angle = Dialogs.Number(f[0], -360, 360); int distance = (int)Dialogs.Number(f[1], 1, 200);
        RunRasterJob("모션 블러", (l,s,ct) => AdvancedFilters.MotionBlur(l, angle, distance, s, ct));
    }
    void Noise()
    {
        var f = Dialogs.Fields(this, "노이즈", ("양 (0~100%)", "12"), ("단색 (1) / 컬러 (0)", "1")); if (f == null) return;
        double amount = Dialogs.Number(f[0], 0, 100); bool mono = f[1] != "0";
        RunRasterJob("노이즈", (l,s,ct) => AdvancedFilters.AddNoise(l, amount, 728, mono, s, ct));
    }
    void Lens()
    {
        var f = Dialogs.Fields(this, "렌즈 왜곡 보정", ("왜곡 (-1~1)", "0.1"), ("확대 배율 (0.5~2)", "1")); if (f == null) return;
        double amount = Dialogs.Number(f[0], -1, 1), zoom = Dialogs.Number(f[1], .5, 2);
        RunRasterJob("렌즈 보정", (l,s,ct) => AdvancedFilters.LensCorrection(l, amount, zoom, s, ct));
    }
    void RemoveColorBackground()
    {
        var f = Dialogs.Fields(this, "배경색 제거 · 가장자리와 비슷한 색", ("색상 허용 오차 (0~255)", "35"), ("페더 (px)", "1")); if (f == null) return;
        double tolerance = Dialogs.Number(f[0], 0, 255), feather = Dialogs.Number(f[1], 0, 20);
        RunRasterJob("배경색 제거", (l,s,ct) => AdvancedFilters.ApplySelection(l, RetouchTools.RemoveBackgroundByColor(l, tolerance, feather, ct), s));
    }
    async void RemoveAiBackground()
    {
        if (doc.Active is not { } layer || IsLockedWithParents(layer) || layer.Kind is LayerKind.Group or LayerKind.Adjustment) return;
        CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var snapshot = layer.Snapshot();
        status.Text = "AI 배경 제거 중…  U²-NetP · 이 컴퓨터에서 처리 · Esc: 취소";
        try
        {
            var mask = await Task.Run(() => BackgroundRemoval.CreateMask(snapshot.Pixels, cancellationToken: cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(document, doc) || revision != doc.Revision) return;
            Edit("AI 배경 제거", () => { layer.Mask = BackgroundRemoval.CombineMasks(mask, snapshot.Mask); maskEditing = false; });
            status.Text = "AI 배경 제거 완료 · 마스크 브러시로 가장자리를 다듬을 수 있습니다.";
        }
        catch (OperationCanceledException) { if (ReferenceEquals(document, doc)) status.Text = "AI 배경 제거를 취소했습니다."; }
        catch (Exception e) { if (ReferenceEquals(document, doc)) MessageBox.Show(this, e.Message, "AI 배경 제거", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
    void FeatherMask()
    {
        if (doc.Active?.Mask == null) return;
        var f = Dialogs.Fields(this, "마스크 페더", ("반경 (px)", "5")); if (f == null) return;
        int radius = (int)Dialogs.Number(f[0], 1, 100);
        EditLayer("마스크 페더", l => { var s = new Selection(new Rect(0, 0, l.Pixels.Width, l.Pixels.Height)) { Coverage = l.Mask, CanvasWidth = l.Pixels.Width, CanvasHeight = l.Pixels.Height }; l.Mask = SelectionTools.Mask(SelectionTools.Feather(s, l.Pixels.Width, l.Pixels.Height, radius), l.Pixels.Width, l.Pixels.Height); });
    }
    void AddGuide()
    {
        var f = Dialogs.Fields(this, "가이드 추가", ("방향 (가로 / 세로)", "세로"), ("위치 (px)", (doc.Width / 2).ToString())); if (f == null) return;
        canvas.Guides.Add((f[0] == "세로", Dialogs.Number(f[1], 0, Math.Max(doc.Width, doc.Height)))); canvas.InvalidateVisual();
    }
    static bool IsRetouch(Tool t) => t is Tool.CloneStamp or Tool.Heal or Tool.Smudge or Tool.Liquify or Tool.BlurBrush;
    void RetouchAt(Point point)
    {
        if (doc.Active is not { } layer || beforeGesture?.Active == null) return;
        double radius = brushSize / 2;
        if (tool == Tool.CloneStamp && cloneSource is { } source && cloneSnapshot != null)
            layer.Pixels = RetouchTools.Clone(layer, cloneSnapshot, source + (point - start), point, radius, hardness, brushOpacity, selection);
        else if (tool == Tool.Heal)
        {
            Point healingSource = cloneSource is { } sample ? sample + (point - start) : point + new Vector(radius * 2.2, 0);
            layer.Pixels = RetouchTools.Heal(layer, cloneSnapshot ?? beforeGesture.Active.Pixels, healingSource, point, radius, hardness, brushOpacity, selection);
        }
        else if (tool == Tool.Smudge) layer.Pixels = RetouchTools.Smudge(layer, lastRetouch, point, radius, brushOpacity * .65, selection);
        else if (tool == Tool.Liquify) layer.Pixels = RetouchTools.Liquify(layer, lastRetouch, point, radius, brushOpacity * .7, selection);
        else if (tool == Tool.BlurBrush)
        {
            layer.Pixels = RetouchTools.Blur(layer, point, radius, hardness, brushOpacity, selection, Math.Clamp((int)(brushSize / 15), 1, 10));
        }
        lastRetouch = point;
    }
    void FinishPolygon()
    {
        if (lassoPoints.Count >= 3) ApplySelection(SelectionTools.Polygon(doc.Width, doc.Height, lassoPoints));
        lassoPoints.Clear(); polygonInProgress = false; canvas.GesturePoints = null; canvas.InvalidateVisual();
    }
}
