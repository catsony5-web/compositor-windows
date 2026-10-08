namespace Compositor.Windows;

// 사용 목적 (user profile): what the editor puts first for one kind of work. A profile only
// reorders and relabels; every tool, right-panel section and menu command stays reachable.
// Each choice left null keeps the default editor exactly, so 기본 is the unchanged editor.
// Add a profile by adding one row to All (docs/PROFILES.md). Section names are right-panel
// section titles (Theme.Section); a listed title that does not exist is skipped, and
// sections of the mode's panel that a profile does not list follow in their usual order.
public sealed record UserProfile(string Id, string Name, string Summary, string Glyph)
{
    /// <summary>First right-panel tab label and its pane caption; null keeps 보정/디자인 by mode.</summary>
    public string? TabCaption { get; init; }
    public string? PaneCaption { get; init; }
    /// <summary>First-tab section titles in order for photo and design mode; null keeps that mode's panel as built.</summary>
    public string[]? PhotoSections { get; init; }
    public string[]? DesignSections { get; init; }
    /// <summary>Tool rail groups for both modes; null keeps the mode's groups. Unlisted tools are appended.</summary>
    public (string Caption, Tool[] Tools)[]? ToolGroups { get; init; }
    /// <summary>Work mode chosen when the user picks the profile (never on restore); null keeps the current mode.</summary>
    public bool? DesignWorkspace { get; init; }
    /// <summary>Start screen quick sizes; null keeps MainWindow.QuickSizes.</summary>
    public (string Label, string Caption, string Width, string Height, bool Millimeters, string Dpi, int Background)[]? QuickSizes { get; init; }
    /// <summary>Adds a 도면 가져오기 tile after the quick sizes.</summary>
    public bool QuickDrawingImport { get; init; }
}

public static class UserProfiles
{
    public const string DefaultId = "default", ArchitectureId = "architecture";
    /// <summary>Title for the photo panel's leading 사진 현상 card when a profile places it below other sections.</summary>
    public const string PhotoLead = "사진 보정";
    /// <summary>Sections only profiles place (MainWindow.Profiles.cs). 리터치 there is the photo panel's 리터치 plus the blur brush and eraser.</summary>
    public const string LineCleanup = "선 정리", Retouch = "리터치";
    /// <summary>The photo panel's 스케치 사진 정리 section (a photographed hand sketch to line art), not the drawings' 선 정리.</summary>
    public const string SketchPhoto = "스케치 사진";
    /// <summary>The 디자인 스타일 section of the photo and design panels (MainWindow.DesignStyles.cs).</summary>
    public const string DesignStyle = "디자인 스타일";
    /// <summary>The design panel's 점경 (entourage library) section (MainWindow.Entourage.cs).</summary>
    public const string Entourage = "점경";

    public static readonly UserProfile Default = new(DefaultId, "기본", "사진 보정과 디자인 작업을 고르게 보여 줍니다.", Theme.Glyphs.Image);

    // Architecture students: plan drawings (DWG/DXF/PDF) to presentation boards.
    public static readonly UserProfile Architecture = new(ArchitectureId, "건축학과", "도면 선 정리와 리터치를 먼저 보여 주고, 발표 보드 크기로 시작합니다.", Theme.Glyphs.Plan)
    {
        TabCaption = "도면", PaneCaption = "도면 작업",
        PhotoSections = [LineCleanup, Retouch, "배치와 그룹", DesignStyle, SketchPhoto, Entourage, PhotoLead, "조정 레이어", "선택과 마스크"],
        DesignSections = [LineCleanup, Retouch, "배치와 그룹", DesignStyle, SketchPhoto, Entourage, PhotoLead, "조정 레이어", "만들기", "캔버스에 정렬", "색상과 내보내기"],
        ToolGroups =
        [
            ("선택", [Tool.Move, Tool.MagicWand, Tool.PolygonLasso, Tool.RectangleSelect, Tool.Lasso, Tool.EllipseSelect]),
            ("리터치", [Tool.Heal, Tool.CloneStamp, Tool.BlurBrush, Tool.Eraser, Tool.Brush, Tool.Smudge, Tool.Liquify]),
            ("보드", [Tool.Text, Tool.Rectangle, Tool.Ellipse, Tool.Artboard, Tool.Crop]),
            ("색상", [Tool.Bucket, Tool.Eyedropper, Tool.Gradient]),
            ("보기", [Tool.Hand])
        ],
        DesignWorkspace = true,
        QuickSizes =
        [
            ("A1 가로", "841 × 594 mm\n150 DPI", "841", "594", true, "150", 1),
            ("A2 가로", "594 × 420 mm\n200 DPI", "594", "420", true, "200", 1),
            ("A3 가로", "420 × 297 mm\n300 DPI", "420", "297", true, "300", 1)
        ],
        QuickDrawingImport = true
    };

    public static readonly UserProfile[] All = [Default, Architecture];

    public static UserProfile? Find(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);
}
