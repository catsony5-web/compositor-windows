using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

// 스케치 사진 정리 in the editor (not the drawings' 선 정리, which restyles CAD line weights): the 이미지
// menu item (also Ctrl+K and the ribbon), the photo panel's 스케치 사진 section (placed early by the
// 건축학과 profile) and a photo layer's right-click menu open the dialog for the active or clicked photo
// layer. The full-resolution cleanup runs off the UI thread (Esc cancels) and lands as one undo step
// (SketchLayers.Place) only if the document did not change meanwhile.
public sealed partial class MainWindow
{
    // Self-tests answer the dialog instead of showing it.
    internal Func<SketchCleanupDialog, bool>? sketchDialogRunner;
    // The last cleanup started from a command, for self-tests and offscreen captures to wait on.
    internal Task<bool>? lastSketchCleanup;

    void CleanSketchPhoto() => lastSketchCleanup = CleanSketchPhotoAsync();

    internal async Task<bool> CleanSketchPhotoAsync()
    {
        CommitFocusedInspectorField(); CancelGesture();
        if (!HasDocument) return false;
        if (doc.Active is not { } photo || !SketchLayers.IsPhoto(photo))
        { status.Text = "스케치를 찍은 사진(이미지) 레이어를 먼저 선택하세요. 텍스트·도형·도면·그룹에서는 실행하지 않습니다."; return false; }
        if (IsLockedWithParents(photo)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return false; }
        var source = SketchLayers.Source(photo);
        var dialog = new SketchCleanupDialog(headlessTesting ? null : this, source, photo.Name);
        bool accepted = sketchDialogRunner?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!accepted || dialog.Result is not { } choice) return false;
        jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var historyAtStart = history; var photoId = photo.Id;
        bool Shown() => ReferenceEquals(document, doc) && (jobCts == null || ReferenceEquals(jobCts, cts));
        status.Text = "스케치를 정리하는 중…  Esc: 취소";
        try
        {
            var result = await Task.Run(() => SketchCleanup.Clean(source, choice.Corners, choice.Options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { if (Shown()) status.Text = "스케치 정리를 취소했습니다."; return false; }
            if (!ReferenceEquals(document, doc) || revision != doc.Revision || !ReferenceEquals(historyAtStart, history))
            {
                if (ReferenceEquals(historyAtStart, history)) status.Text = "정리하는 동안 문서가 바뀌어 결과를 넣지 않았습니다. 다시 실행하세요.";
                return false;
            }
            SketchLayers.Placement? placed = null;
            Edit("스케치 사진 정리", () =>
            {
                placed = SketchLayers.Place(doc, photoId, result.Lines, choice.WhiteBackground, LocalizedSketchNames());
                // The new group opens in the layer panel with the line layer selected.
                knownLayerGroups.Add(placed.GroupId); collapsedGroups.Remove(placed.GroupId);
                selectedLayers.Clear(); selectedLayers.Add(placed.LineLayerId); doc.ActiveId = placed.LineLayerId;
                selection = null; maskEditing = false; pendingLayerReveal = placed.LineLayerId;
            });
            if (placed == null || !doc.Layers.Any(l => l.Id == placed.GroupId)) return false;
            if (placed.CanvasResized) canvas.Fit();
            status.Text = "스케치를 정리했습니다 · 원본 사진은 숨겨 두었습니다. 남은 얼룩은 지우개로 지울 수 있습니다.";
            return true;
        }
        catch (OperationCanceledException) { if (Shown()) status.Text = "스케치 정리를 취소했습니다."; return false; }
        catch (InvalidDataException error) { if (headlessTesting) throw; if (ReferenceEquals(document, doc)) MessageDialog.Show(this, error.Message, "스케치 사진 정리"); return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    static SketchLayers.Names LocalizedSketchNames() => new(Loc.T("스케치 정리"), Loc.T("스케치 선"), Loc.T("흰 바탕"));

    // ---- AI connection (clean_sketch) ----------------------------------------------------------

    // Extra fields of the last automation edit's result (clean_sketch: its layers, corners, detection).
    JsonObject? automationStepDetails;

    // The same cleanup as the dialog with its settings from the request, placed on the candidate document.
    async Task<JsonObject> AutomationCleanSketchAsync(Document candidate, Layer photo, JsonObject args, CancellationToken token)
    {
        if (!SketchLayers.IsPhoto(photo)) throw new AutomationFault("wrong_layer_kind", "스케치를 찍은 사진(이미지) 레이어를 지정하세요.");
        var source = SketchLayers.Source(photo);
        Point[]? corners = null;
        if (args["corners"] is JsonArray given)
        {
            try { corners = SketchCleanup.Corners(AutomationCatalog.MaterialPoints(given), source.Width, source.Height); }
            catch (InvalidDataException e) { throw new AutomationFault("invalid_arguments", e.Message); }
        }
        string color = AString(args, "lineColor", "original");
        var options = new SketchOptions
        {
            Flatten = ABool(args, "flatten", true),
            Threshold = args.ContainsKey("threshold") ? ANumber(args, "threshold") : null,
            SpeckSize = args.ContainsKey("speckSize") ? (int)ANumber(args, "speckSize") : null,
            Boldness = ANumber(args, "boldness"),
            LineColor = color switch { "original" => SketchLineColor.Original, "black" => SketchLineColor.Black, _ => SketchLineColor.Custom },
            CustomColor = color.StartsWith('#') ? 0xFF000000 | uint.Parse(color[1..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) : new SketchOptions().CustomColor
        };
        SketchResult result;
        try { result = await Task.Run(() => SketchCleanup.Clean(source, corners, options, token), token); }
        catch (InvalidDataException e) { throw new AutomationFault("sketch_cleanup_failed", e.Message); }
        var names = LocalizedSketchNames();
        if (args.ContainsKey("name")) names = names with { Group = AString(args, "name") };
        SketchLayers.Placement placement;
        try { placement = SketchLayers.Place(candidate, photo.Id, result.Lines, AString(args, "background", "white") == "white", names); }
        catch (InvalidOperationException e) { throw new AutomationFault("capacity_exceeded", e.Message); }
        static JsonNode Points(Point[] points) => new JsonArray(points.Select(p => (JsonNode?)new JsonObject { ["x"] = Math.Round(p.X, 2), ["y"] = Math.Round(p.Y, 2) }).ToArray());
        return new JsonObject
        {
            ["groupId"] = placement.GroupId.ToString(), ["lineLayerId"] = placement.LineLayerId.ToString(),
            ["backgroundLayerId"] = placement.BackgroundLayerId?.ToString(), ["photoLayerId"] = photo.Id.ToString(),
            ["flattened"] = result.Corners != null, ["corners"] = result.Corners is { } used ? Points(used) : null,
            ["cornerSpace"] = "photo layer pixels",
            ["detected"] = result.Detection?.Found, ["confidence"] = result.Detection is { } found ? Math.Round(found.Confidence, 3) : null,
            ["threshold"] = Math.Round(result.Threshold, 4), ["automaticThreshold"] = Math.Round(result.AutomaticThreshold, 4), ["speckSize"] = result.SpeckSize,
            ["width"] = result.Lines.Width, ["height"] = result.Lines.Height, ["canvasResized"] = placement.CanvasResized
        };
    }

    // A photo layer's right-click menu. Each item first makes the clicked row the selected layer.
    ContextMenu PhotoLayerMenu(Guid id)
    {
        var menu = new ContextMenu();
        void Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Guard(() => { if (doc.Layers.Any(l => l.Id == id)) { SelectLayer(id); run(); } });
            menu.Items.Add(item);
        }
        Item("스케치 사진 정리…", CleanSketchPhoto);
        menu.Items.Add(new Separator());
        Item("이름 변경…", Rename);
        Item("레이어 복제", Duplicate);
        Item("레이어 삭제", DeleteLayer);
        return menu;
    }

    // ---- Offscreen review --------------------------------------------------------------------

    // The dialog on a synthetic phone photo (found sheet, live result) at its default and minimum size
    // and with other choices, then the editor after cleaning an opened photo (the canvas becomes the
    // sheet) and a photo placed on a board.
    void RenderSketchPreviews(Action<Window, string, int, int> capture)
    {
        var sample = SyntheticSketch.Photo(1600, 1200);
        string name = Loc.T("스케치 사진");
        void Dialog(string file, int width, int height, Action<SketchCleanupDialog>? setup = null)
        {
            var dialog = new SketchCleanupDialog(null, sample.Photo, name + ".jpg");
            try { dialog.DetectNow(); setup?.Invoke(dialog); dialog.RefreshNow(); capture(dialog, file, width, height); }
            finally { dialog.Close(); }
        }
        Dialog("sketch-dialog", 1120, 820);
        Dialog("sketch-dialog-narrow", 760, 600);
        Dialog("sketch-dialog-options", 1120, 820, d => { d.SetLineColor(SketchLineColor.Black); d.SetWhiteBackground(false); d.SetBoldness(40); });
        var previousRunner = sketchDialogRunner; bool design = designWorkspace;
        try
        {
            sketchDialogRunner = d => { d.DetectNow(); return d.Accept(); };
            var opened = new Document { Width = sample.Photo.Width, Height = sample.Photo.Height, Name = name };
            opened.Add(new Layer { Name = Loc.T("원본"), Pixels = sample.Photo });
            AddTab(opened, null); SetWorkspaceMode(false);
            WaitOnDispatcher(CleanSketchPhotoAsync);
            ShowStudioPage(0); composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
            capture(this, "sketch-result", 1480, 920);
            var board = new Document { Width = 1600, Height = 1000, Name = Loc.T("발표 보드") };
            board.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(1600, 1000, Color.FromRgb(0xF3, 0xF1, 0xEC)) });
            var placedPhoto = new Layer { Name = name, Pixels = sample.Photo, X = 820, Y = 140, Scale = .42 };
            board.Add(placedPhoto);
            AddTab(board, null);
            WaitOnDispatcher(CleanSketchPhotoAsync);
            composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
            capture(this, "sketch-board-result", 1480, 920);
        }
        finally { sketchDialogRunner = previousRunner; SetWorkspaceMode(design); }
    }
}
