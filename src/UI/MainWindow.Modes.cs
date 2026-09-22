using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    bool designWorkspace;
    GlassSwitch? workspaceSwitch;
    Panel? workspaceTools;
    Tool[] photoToolOrder = [];

    static (string Caption, Tool[] Tools)[] WorkspaceToolGroups(bool design) => design
        ? [("만들기", [Tool.Move, Tool.Text, Tool.Rectangle, Tool.Ellipse, Tool.Artboard]),
           ("색상", [Tool.Eyedropper, Tool.Gradient, Tool.Bucket, Tool.Brush]),
           ("선택", [Tool.RectangleSelect, Tool.EllipseSelect, Tool.Lasso, Tool.PolygonLasso, Tool.MagicWand]),
           ("사진", [Tool.Crop, Tool.Eraser, Tool.Heal, Tool.CloneStamp, Tool.BlurBrush, Tool.Smudge, Tool.Liquify]),
           ("보기", [Tool.Hand])]
        : [("선택", [Tool.Move, Tool.Crop, Tool.RectangleSelect, Tool.EllipseSelect, Tool.Lasso, Tool.PolygonLasso, Tool.MagicWand]),
           ("리터치", [Tool.Brush, Tool.Heal, Tool.CloneStamp, Tool.Eraser, Tool.BlurBrush, Tool.Smudge, Tool.Liquify]),
           ("만들기", [Tool.Text, Tool.Rectangle, Tool.Ellipse, Tool.Gradient, Tool.Bucket, Tool.Eyedropper]),
           ("보기", [Tool.Hand, Tool.Artboard])];

    void BuildWorkspaceTools()
    {
        if (workspaceTools == null) return;
        if (workspaceTools is UniformGrid original && original.Parent is Panel host)
        {
            int index = host.Children.IndexOf(original);
            original.Children.Clear(); host.Children.RemoveAt(index);
            workspaceTools = new StackPanel { Margin = new Thickness(4, 4, 4, 8) };
            host.Children.Insert(index, workspaceTools);
        }
        // Clear the old grids first: live tool buttons have exactly one WPF parent.
        foreach (var grid in workspaceTools.Children.OfType<UniformGrid>()) grid.Children.Clear();
        workspaceTools.Children.Clear();
        photoToolOrder = WorkspaceToolGroups(false).SelectMany(group => group.Tools).ToArray();
        foreach (var group in WorkspaceToolGroups(designWorkspace))
        {
            var caption = Theme.Label(group.Caption, 12, Theme.Muted);
            caption.Margin = new Thickness(5, 8, 3, 3); workspaceTools.Children.Add(caption);
            var grid = new UniformGrid { Columns = 2 };
            foreach (var item in group.Tools) grid.Children.Add(toolButtons[item]);
            workspaceTools.Children.Add(grid);
        }
    }

    FrameworkElement BuildWorkspaceSwitch()
    {
        workspaceSwitch = new GlassSwitch("사진 편집", "디자인", 184) { Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "사진 편집: 선택·리터치·보정 / 디자인: 도형·문자·배치 · 같은 문서에서 전환합니다" };
        workspaceSwitch.Click += (_, _) => SetWorkspaceMode(workspaceSwitch.IsChecked == true);
        return workspaceSwitch;
    }
    void SetWorkspaceMode(bool design)
    {
        if (designWorkspace == design) return;
        CommitFocusedInspectorField(); CancelGesture(); designWorkspace = design;
        canvas.DesignMode = design; canvas.CancelDesignPreview(); canvas.InvalidateVisual();
        if (workspaceSwitch != null) workspaceSwitch.IsChecked = design;
        BuildWorkspaceTools(); ApplyWorkspaceStudio();
        // Use the docked shortcut panel without moving or activating any user-positioned pane.
        ShowStudioPage(0, false);
        studioScroll.Height = PreferredStudioHeight(ActualHeight);
        status.Text = design ? "디자인 · 벡터 원본을 확대 배율에 맞춰 표시합니다" : "사진 편집 · 원본 해상도의 픽셀을 편집합니다";
    }
}
