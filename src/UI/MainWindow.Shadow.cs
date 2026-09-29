using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// Shadows for the selected layers: a menu/command-palette entry (레이어 › 그림자 추가…), one
// inspector hook, the settings dialog and regeneration. The shadow is a Multiply raster layer
// directly below its sources; its ShadowSpec keeps it editable.
public sealed partial class MainWindow
{
    ShadowSpec? lastShadowSpec;
    // Self-tests answer the dialog instead of showing it.
    internal Func<ShadowDialog, bool>? shadowDialogHandler;

    void AddShadowMenuItems(MenuItem layerMenu)
    {
        layerMenu.Items.Add(new Separator());
        foreach (var (label, action) in new (string, Action)[] { ("그림자 추가…", AddShadow), ("그림자 다시 만들기", RegenerateShadow) })
        {
            var item = DocumentControl(new MenuItem { Header = label, Foreground = Theme.Text });
            item.Click += (_, _) => { if (HasDocument) Guard(action); };
            layerMenu.Items.Add(item);
        }
    }

    // Inspector rows in the appearance section: add for any drawable layer, edit/regenerate for a shadow.
    void AddShadowActions(Layer layer)
    {
        if (layer.Kind == LayerKind.Adjustment) return;
        if (layer.Shadow == null)
        {
            // Creating a shadow leaves the source untouched, so a locked source can still cast one.
            properties.Children.Add(Theme.ActionRow("그림자 추가", () => Guard(() => { CommitFocusedInspectorField(); AddShadow(); }),
                "선택한 레이어 아래에 사실적인 그림자나 그림자 형태를 새 레이어로 만듭니다.", Theme.Glyphs.Shadow));
            return;
        }
        properties.Children.Add(InspectorAction("그림자 편집", EditShadow, "방향·길이·부드러움과 색을 바꿔 이 그림자를 다시 만듭니다.", layer, Theme.Glyphs.Shadow));
        properties.Children.Add(InspectorAction("원본에 맞춰 다시 만들기", RegenerateShadow, "원본 레이어를 옮기거나 고친 뒤, 같은 설정으로 그림자를 다시 계산합니다.", layer, Theme.Glyphs.Revert));
    }

    internal void AddShadow()
    {
        CommitFocusedInspectorField(); CancelGesture();
        if (!HasDocument) return;
        if (doc.Active?.Shadow != null) { EditShadow(); return; }
        Guid[] sources;
        try { sources = ShadowRenderer.ResolveSources(doc, ExportSelectionIds()); }
        catch (InvalidOperationException e) { status.Text = e.Message; return; }
        var first = doc.Layers.Single(l => l.Id == sources[0]);
        if (first.ParentId is { } parentId && IsLockedWithParents(doc.Layers.Single(l => l.Id == parentId)))
        { status.Text = "그림자를 넣을 그룹의 잠금을 먼저 해제하세요."; return; }
        var dialog = new ShadowDialog(ShadowDialogOwner, doc, InitialShadowSpec(sources, first));
        if (!ShowShadowDialog(dialog)) return;
        lastShadowSpec = dialog.Spec;
        CreateShadow(dialog.Spec);
    }

    // A window that was never shown cannot own a dialog (offscreen self-tests).
    Window? ShadowDialogOwner => headlessTesting ? null : this;

    bool ShowShadowDialog(ShadowDialog dialog) => shadowDialogHandler?.Invoke(dialog) ?? dialog.ShowDialog() == true;

    // Reuse the last settings (same sun for a whole board) when they suit the kind of source.
    ShadowSpec InitialShadowSpec(Guid[] sources, Layer first)
    {
        bool drawing = DrawingLayers.Categories(doc)[first.Id] == LayerCategory.Drawing;
        var spec = lastShadowSpec is { } last && (drawing ? last.Projection == ShadowProjection.Plan : last.Projection != ShadowProjection.Plan)
            ? last : ShadowSpec.Default(drawing ? ShadowProjection.Plan : ShadowProjection.Drop);
        return spec with { Sources = sources };
    }

    void EditShadow()
    {
        CommitFocusedInspectorField(); CancelGesture();
        if (doc.Active is not { Shadow: { } spec } layer) { status.Text = "그림자 레이어를 선택하세요."; return; }
        if (IsLockedWithParents(layer)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        var dialog = new ShadowDialog(ShadowDialogOwner, doc, spec, layer.Id);
        if (!ShowShadowDialog(dialog)) return;
        lastShadowSpec = dialog.Spec;
        EditShadowLayer(layer.Id, dialog.Spec);
    }

    void RegenerateShadow()
    {
        CommitFocusedInspectorField(); CancelGesture();
        if (doc.Active is not { Shadow: not null } layer) { status.Text = "그림자 레이어를 선택하세요. 원본을 바꾼 뒤 그림자를 다시 계산합니다."; return; }
        if (IsLockedWithParents(layer)) { status.Text = "잠긴 레이어입니다. 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        RunShadowEdit("그림자 다시 만들기", () => ShadowRenderer.Regenerate(doc, layer.Id));
    }

    internal Layer? CreateShadow(ShadowSpec spec)
    {
        Layer? created = null;
        RunShadowEdit("그림자 추가", () =>
        {
            var first = doc.Layers.Find(l => spec.Sources.Contains(l.Id)) ?? throw new InvalidOperationException("그림자를 만들 레이어를 먼저 선택하세요.");
            int count = spec.Sources.Count(id => doc.Layers.Any(l => l.Id == id));
            created = ShadowRenderer.Insert(doc, spec, Loc.T("그림자") + " · " + first.Name + (count > 1 ? $" +{count - 1}" : ""));
            selectedLayers.Clear(); selectedLayers.Add(created.Id); doc.ActiveId = created.Id; maskEditing = false;
        });
        if (created != null && doc.Layers.Contains(created)) status.Text = "그림자를 만들었습니다 · 원본을 바꾼 뒤에는 ‘원본에 맞춰 다시 만들기’로 갱신하세요.";
        return created;
    }

    internal void EditShadowLayer(Guid id, ShadowSpec spec) => RunShadowEdit("그림자 편집", () => ShadowRenderer.Regenerate(doc, id, spec));

    // One history step; the wait cursor covers full-resolution work on large canvases.
    void RunShadowEdit(string label, Action action)
    {
        if (headlessTesting) { Edit(label, action); return; }
        Mouse.OverrideCursor = Cursors.Wait;
        try { Edit(label, action); }
        finally { Mouse.OverrideCursor = null; }
    }

    // ---- Offscreen review --------------------------------------------------------------

    void RenderShadowPreviews(Action<Window, string, int, int> capture, Action<FrameworkElement, string, int, int> capturePane)
    {
        var site = ShadowSampleDocument(out var buildings);
        AddTab(site, null); SetWorkspaceMode(true);
        var plan = ShadowSpec.Default(ShadowProjection.Plan) with { Height = 110, Sources = [buildings] };
        void Dialog(Document document, ShadowSpec spec, string name)
        {
            var dialog = new ShadowDialog(null, document, spec);
            try { dialog.RefreshPreviewNow(); capture(dialog, name, 1080, 800); }
            finally { dialog.Close(); }
        }
        Dialog(doc, plan, "shadow-realistic");
        Dialog(doc, ShadowSpec.Default(ShadowProjection.Plan, ShadowStyle.Shape) with { Height = 110, Sources = [buildings] }, "shadow-shape");
        Dialog(doc, ShadowSpec.Default(ShadowProjection.Plan, ShadowStyle.Shape) with { Height = 110, OutlineOnly = true, OutlineWidth = 3, Sources = [buildings] }, "shadow-shape-outline");
        var board = ShadowBoardDocument(out var figures);
        Dialog(board, ShadowSpec.Default(ShadowProjection.Ground) with { Sources = figures }, "shadow-ground");
        CreateShadow(plan);
        composite = Imaging.Render(doc); canvas.Composite = composite.Bitmap();
        ShowStudioPage(1); BuildProperties(); capture(this, "shadow-editor", 1480, 920);
        capturePane(studioPanes[1], "shadow-properties", 360, 900);
    }

    // A small site plan: a lawn, a group of building footprints and a few trees.
    internal static Document ShadowSampleDocument(out Guid buildings)
    {
        var document = new Document { Width = 1600, Height = 1000, Name = Loc.T("그림자 예시 · 배치도") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(1600, 1000, Colors.White) });
        var lawn = VectorShapes.Create(new ShapeSpec { Width = 1400, Height = 820, CornerRadius = 24, FillArgb = 0xFFE4ECD8, StrokeEnabled = true, StrokeArgb = 0xFF97A68A, StrokeWidth = 2 }, 100, 90);
        lawn.Name = Loc.T("잔디밭"); document.Add(lawn);
        var group = DocumentFeatures.CreateGroup(document, Loc.T("건물")); document.Add(group);
        var footprints = new[] { (260, 300, 360, 220), (760, 220, 200, 340), (1080, 560, 280, 160), (330, 640, 150, 170) };
        for (int i = 0; i < footprints.Length; i++)
        {
            var (x, y, w, h) = footprints[i];
            var building = VectorShapes.Create(new ShapeSpec { Width = w, Height = h, FillArgb = 0xFFF6F4EF, StrokeEnabled = true, StrokeArgb = 0xFF2E3238, StrokeWidth = 3 }, x, y);
            building.Name = Loc.T("건물") + $" {i + 1}"; building.ParentId = group.Id; document.Add(building);
        }
        var trees = DocumentFeatures.CreateGroup(document, Loc.T("수목")); document.Add(trees);
        foreach (var (x, y) in new[] { (700, 660), (820, 700), (1180, 300), (1290, 380), (560, 180) })
        {
            var tree = VectorShapes.Create(new ShapeSpec { Kind = ShapeKind.Ellipse, Width = 78, Height = 78, FillArgb = 0xFFB5D1A4, StrokeEnabled = true, StrokeArgb = 0xFF6E8C60, StrokeWidth = 2 }, x, y);
            tree.Name = Loc.T("수목"); tree.ParentId = trees.Id; document.Add(tree);
        }
        buildings = group.Id; document.ActiveId = group.Id;
        return document;
    }

    // A presentation board with a standing person and a tree cut out on a floor line.
    internal static Document ShadowBoardDocument(out Guid[] figures)
    {
        var document = new Document { Width = 1400, Height = 900, Name = Loc.T("그림자 예시 · 보드") };
        document.Add(new Layer { Name = Loc.T("배경"), Pixels = Raster.Solid(1400, 900, Color.FromRgb(0xF3, 0xF1, 0xEC)) });
        var floor = VectorShapes.Create(new ShapeSpec { Width = 1400, Height = 3, FillArgb = 0xFFB9B4AA }, 0, 700); floor.Name = Loc.T("바닥선"); document.Add(floor);
        var ink = new SolidColorBrush(Color.FromRgb(0x3F, 0x46, 0x52)); ink.Freeze();
        var person = new Layer
        {
            Name = Loc.T("사람"), X = 430, Y = 440,
            Pixels = Imaging.Draw(96, 260, dc =>
            {
                dc.DrawEllipse(ink, null, new Point(48, 24), 19, 19);
                dc.DrawRoundedRectangle(ink, null, new Rect(24, 48, 48, 112), 16, 16);
                dc.DrawRoundedRectangle(ink, null, new Rect(27, 140, 18, 120), 7, 7);
                dc.DrawRoundedRectangle(ink, null, new Rect(51, 140, 18, 120), 7, 7);
            })
        };
        document.Add(person);
        var leaf = new SolidColorBrush(Color.FromRgb(0x7F, 0xA3, 0x6B)); leaf.Freeze();
        var bark = new SolidColorBrush(Color.FromRgb(0x6B, 0x54, 0x43)); bark.Freeze();
        var tree = new Layer
        {
            Name = Loc.T("나무"), X = 760, Y = 380,
            Pixels = Imaging.Draw(220, 320, dc =>
            {
                dc.DrawRectangle(bark, null, new Rect(100, 170, 20, 150));
                dc.DrawEllipse(leaf, null, new Point(110, 100), 100, 96);
            })
        };
        document.Add(tree);
        figures = [person.Id, tree.Id];
        return document;
    }
}
