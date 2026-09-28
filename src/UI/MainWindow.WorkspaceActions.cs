using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Section titles fold their groups; the detail explains the group on hover.
    static void WorkspaceSection(StackPanel panel, string title, string? detail = null)
    {
        var header = Theme.Section(title, panel.Children.Count > 0);
        if (detail != null) header.ToolTip = detail;
        panel.Children.Add(header);
    }

    Action Run(Action action) => () => Guard(() => { CommitFocusedInspectorField(); action(); });

    // Peer commands as icon tiles: glyph, short label, complete name and tooltip.
    void WorkspaceTiles(StackPanel panel, int columns, params (string Glyph, string Label, Action Run, string Tip)[] actions) =>
        panel.Children.Add(QuickActions.Grid(columns, actions.Select(a => QuickActions.Tile(a.Glyph, a.Label, Run(a.Run), a.Tip)), QuickActions.TileWidth));

    // Longer commands as icon rows in two columns.
    void WorkspaceCommands(StackPanel panel, params (string Glyph, string Label, Action Run, string Tip, string? Name)[] actions) =>
        panel.Children.Add(QuickActions.Grid(2, actions.Select(a => QuickActions.Command(a.Glyph, a.Label, Run(a.Run), a.Tip, a.Name)), QuickActions.CommandWidth));

    void BuildPhotoActions(StackPanel panel)
    {
        panel.Children.Add(QuickActions.Feature(Theme.Glyphs.Camera, "사진 현상", "화이트 밸런스 · 명암 · 질감을 한 번에 조절",
            Run(() => ShowAdjustment(AdjustmentKind.PhotoDevelop)), "Camera Raw 방식의 사진 보정 · 수정 가능한 조정 레이어로 적용"));
        WorkspaceSection(panel, "조정 레이어", "원본을 유지하며 빛과 색을 보정합니다. 미리보기 후 조정 레이어로 추가됩니다.");
        WorkspaceTiles(panel, 3,
            (Theme.Glyphs.Exposure, "노출", () => ShowAdjustment(AdjustmentKind.Exposure), "노출 · 미리보기 후 조정 레이어 추가"),
            (Theme.Glyphs.Levels, "레벨", () => ShowAdjustment(AdjustmentKind.Levels), "입력·출력 레벨과 감마 조절"),
            (Theme.Glyphs.Curves, "곡선", () => ShowAdjustment(AdjustmentKind.Curves), "RGB와 각 채널의 톤 곡선 조절"),
            (Theme.Glyphs.HueSaturation, "색조 / 채도", () => ShowAdjustment(AdjustmentKind.HueSaturation), "색조·채도·명도 조절"),
            (Theme.Glyphs.GradientMap, "그라데이션 맵", () => ShowAdjustment(AdjustmentKind.GradientMap), "명암에 따라 색상 매핑"),
            (Theme.Glyphs.Grain, "그레인", () => ShowAdjustment(AdjustmentKind.Grain), "필름 입자 추가"));
        panel.Children.Add(Theme.ActionRow("선택한 조정 레이어 편집", Run(EditAdjustment), "선택한 조정 레이어의 값을 다시 편집", Theme.Glyphs.Sliders));

        WorkspaceSection(panel, "선택과 마스크", "선택 영역을 만들고 마스크나 새 레이어로 바꿉니다.");
        WorkspaceCommands(panel,
            (Theme.Glyphs.SelectAlpha, "불투명 픽셀 선택", SelectAlpha, "활성 레이어의 불투명도를 선택 영역으로 불러오기", null),
            (Theme.Glyphs.Mask, "선택 영역 마스크", () => WorkspacePixelAction(AddMask), "현재 선택 영역으로 마스크 만들기 · 선택이 없으면 전체 표시", null),
            (Theme.Glyphs.Duplicate, "선택 픽셀 복제", ExtractSelection, "선택한 픽셀을 새 레이어에 복사", null),
            (Theme.Glyphs.Sparkle, "AI 배경 제거", () => WorkspacePixelAction(RemoveAiBackground), "이 컴퓨터에서 배경을 분석해 레이어 마스크 만들기", null));
        WorkspaceSection(panel, "리터치", "잡티를 지우고 빈 곳을 주변 픽셀로 채웁니다.");
        WorkspaceCommands(panel,
            (ToolIcons.PathData(Tool.Heal), "복구 브러시", () => SetTool(Tool.Heal), "Alt+클릭으로 참조 위치 지정 후 드래그", null),
            (ToolIcons.PathData(Tool.CloneStamp), "복제 도장", () => SetTool(Tool.CloneStamp), "Alt+클릭으로 참조 위치 지정 후 복제", null),
            (Theme.Glyphs.FillSelection, "내용 인식 채우기", ContentFill, "제거할 부분을 선택한 뒤 주변 픽셀로 채우기", null),
            (ToolIcons.PathData(Tool.Brush), "브러시 설정", () => ShowStudioPage(3), "크기와 경도, 브러시 프리셋", null));
    }

    void BuildDesignActions(StackPanel panel)
    {
        WorkspaceSection(panel, "만들기", "도형·텍스트를 이미지와 함께 배치합니다.");
        WorkspaceTiles(panel, 4,
            (Theme.Glyphs.Text, "새 텍스트", () => OpenTextProperties(null), "편집 가능한 텍스트 추가 후 문자·단락 속성 열기"),
            (ToolIcons.PathData(Tool.Rectangle), "사각형", () => SetTool(Tool.Rectangle), "캔버스에서 드래그하여 편집 가능한 사각형 만들기"),
            (ToolIcons.PathData(Tool.Ellipse), "타원", () => SetTool(Tool.Ellipse), "캔버스에서 드래그하여 편집 가능한 타원 만들기"),
            (Theme.Glyphs.FillStroke, "채우기 · 선", () => ShowStudioPage(1), "선택한 도형의 채우기·선 색상과 두께 조절"));
        WorkspaceSection(panel, "캔버스에 정렬", "선택한 이미지·도형·텍스트 각각을 캔버스 기준으로 정렬합니다.");
        panel.Children.Add(QuickActions.IconStrip(CanvasAlignments.Select(a => (a.Glyph, a.Name, Run(() => AlignWorkspaceLayers(a.Direction)))), out _));
        WorkspaceSection(panel, "배치와 그룹");
        WorkspaceCommands(panel,
            (Theme.Glyphs.Forward, "앞으로 한 단계", () => WorkspaceArrange(() => Reorder(1)), "활성 레이어를 같은 그룹 안에서 한 단계 앞으로 이동", null),
            (Theme.Glyphs.Backward, "뒤로 한 단계", () => WorkspaceArrange(() => Reorder(-1)), "활성 레이어를 같은 그룹 안에서 한 단계 뒤로 이동", null),
            (Theme.Glyphs.GroupAdd, "그룹 만들기", () => WorkspaceArrange(GroupSelected, true), "선택한 연속 레이어를 그룹으로 묶기 · Ctrl+G", null),
            (Theme.Glyphs.GroupRemove, "그룹 해제", () => WorkspaceArrange(UngroupSelected), "효과나 변형이 없는 선택 그룹 해제 · Ctrl+Shift+G", null));
        WorkspaceSection(panel, "색상과 내보내기");
        WorkspaceCommands(panel,
            (Theme.Glyphs.Palette, "견본 · 추천 색상", () => ShowStudioPage(2), "색상 견본·채도와 명도 팔레트·추천 색상", null),
            (Theme.Glyphs.Export, "레이어 내보내기", ExportSelectedLayers, "선택한 레이어만 PNG·JPEG·TIFF로 내보내기", "선택 레이어 내보내기"));
    }

    // Shared by the design panel and the text panel's canvas alignment.
    internal static readonly (string Glyph, string Name, string Direction)[] CanvasAlignments =
    [
        (Theme.Glyphs.AlignLeft, "캔버스 왼쪽 정렬", "left"), (Theme.Glyphs.AlignCenter, "캔버스 가로 중앙 정렬", "center"), (Theme.Glyphs.AlignRight, "캔버스 오른쪽 정렬", "right"),
        (Theme.Glyphs.AlignTop, "캔버스 위쪽 정렬", "top"), (Theme.Glyphs.AlignMiddle, "캔버스 세로 중앙 정렬", "middle"), (Theme.Glyphs.AlignBottom, "캔버스 아래쪽 정렬", "bottom")
    ];

    void WorkspacePixelAction(Action action)
    {
        if (doc.Active is not { } layer || layer.Kind is LayerKind.Group or LayerKind.Adjustment)
        { status.Text = "이미지·도형·텍스트 레이어를 먼저 선택하세요."; return; }
        if (IsLockedWithParents(layer)) { status.Text = "레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        action();
    }

    void WorkspaceArrange(Action action, bool multiple = false)
    {
        CancelGesture();
        var ids = multiple ? ExportSelectionIds() : doc.Active == null ? [] : new[] { doc.ActiveId };
        if (ids.Length == 0) { status.Text = "배치할 레이어를 먼저 선택하세요."; return; }
        if (doc.Layers.Where(layer => ids.Contains(layer.Id)).Any(IsLockedWithParents))
        { status.Text = "선택한 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        // Creation may leave the previous row selected. Match the active-layer
        // fallback used by export/alignment before the legacy group command reads it.
        if (multiple) { selectedLayers.Clear(); foreach (var id in ids) selectedLayers.Add(id); }
        action();
    }

    void AlignWorkspaceLayers(string direction)
    {
        if (direction is not ("left" or "center" or "right" or "top" or "middle" or "bottom")) return;
        CommitFocusedInspectorField();
        CancelGesture();
        var ids = ExportSelectionIds();
        var layers = doc.Layers.Where(layer => ids.Contains(layer.Id)).ToArray();
        if (layers.Length == 0) { status.Text = "정렬할 레이어를 먼저 선택하세요."; return; }
        if (layers.Any(layer => layer.Kind is LayerKind.Group or LayerKind.Adjustment))
        { status.Text = "정렬할 이미지·도형·텍스트를 선택하세요. 그룹은 안의 레이어를 선택하세요."; return; }
        if (layers.Any(IsLockedWithParents)) { status.Text = "선택한 레이어와 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        var moves = new List<(Layer Layer, Vector Delta)>();
        foreach (var layer in layers)
        {
            for (var parent = layer.ParentId; parent is { } id;)
            {
                var ancestor = doc.Layers.Single(item => item.Id == id);
                if (ancestor.Warp != null) { status.Text = "왜곡된 그룹 안의 레이어는 그룹 밖으로 이동한 뒤 정렬하세요."; return; }
                parent = ancestor.ParentId;
            }
            var corners = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }
                .Select(point => DocumentFeatures.ToDocumentSpace(doc, layer, point)).ToArray();
            double left = corners.Min(p => p.X), right = corners.Max(p => p.X), top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);
            double dx = direction switch { "left" => -left, "center" => (doc.Width - left - right) / 2, "right" => doc.Width - right, _ => 0 };
            double dy = direction switch { "top" => -top, "middle" => (doc.Height - top - bottom) / 2, "bottom" => doc.Height - bottom, _ => 0 };
            var origin = DocumentFeatures.ToParentSpace(doc, layer, new Point());
            var target = DocumentFeatures.ToParentSpace(doc, layer, new Point(dx, dy));
            if ((target - origin).LengthSquared > 1e-12) moves.Add((layer, target - origin));
        }
        if (moves.Count == 0) return;
        Edit("선택 레이어 캔버스 정렬", () => { foreach (var move in moves) { move.Layer.X += move.Delta.X; move.Layer.Y += move.Delta.Y; } });
    }
}
