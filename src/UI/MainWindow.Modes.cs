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
            workspaceTools = new StackPanel { Margin = new Thickness(4, 6, 4, 4) };
            host.Children.Insert(index, workspaceTools);
        }
        // Clear the old grids first: live tool buttons have exactly one WPF parent.
        foreach (var grid in workspaceTools.Children.OfType<UniformGrid>()) grid.Children.Clear();
        workspaceTools.Children.Clear();
        photoToolOrder = CurrentToolGroups(false).SelectMany(group => group.Tools).ToArray();
        foreach (var group in CurrentToolGroups(designWorkspace))
        {
            if (workspaceTools.Children.Count > 0) workspaceTools.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(8, 5, 8, 5) });
            var grid = new UniformGrid { Columns = 2 };
            System.Windows.Automation.AutomationProperties.SetName(grid, group.Caption + " 도구");
            foreach (var item in group.Tools) grid.Children.Add(toolButtons[item]);
            workspaceTools.Children.Add(grid);
        }
        FitToolRail();
    }

    // The tool rail keeps every tool and the color swatches in view on short windows: it first
    // tightens the buttons and gaps, then adds columns (widening the rail) before it would scroll.
    readonly ColumnDefinition toolRailColumn = new() { Width = new GridLength(ToolRailWidth) };
    readonly StackPanel toolRailContent = new();
    ScrollViewer? toolRailScroll;
    internal int toolRailLayout;
    const double ToolRailWidth = 84, ToolButtonHeight = 34;
    internal static readonly (int Columns, double Button, double Gap)[] ToolRailLayouts = [(2, ToolButtonHeight, 5), (2, 30, 3), (3, 30, 3), (3, 28, 2), (4, 28, 2)];
    // 간결한 화면: one column of 32 DIP icon buttons, tightened and then doubled on short windows.
    internal static readonly (int Columns, double Button, double Gap)[] CompactToolRailLayouts = [(1, 32, 4), (1, 30, 3), (1, 28, 2), (2, 28, 2), (2, 26, 1), (3, 26, 1)];
    (int Columns, double Button, double Gap)[] RailLayouts => screenCompact ? CompactToolRailLayouts : ToolRailLayouts;

    void FitToolRail()
    {
        if (toolRailScroll == null || workspaceTools == null) return;
        double available = toolRailScroll.ActualHeight;
        if (!(available > 0)) { ApplyToolRailLayout(toolRailLayout); return; }
        try
        {
            for (int i = 0; i < RailLayouts.Length; i++)
            {
                ApplyToolRailLayout(i);
                // Measure skips elements that are not dirty; invalidate the chain down to the changed grids.
                foreach (var grid in workspaceTools.Children.OfType<UniformGrid>()) grid.InvalidateMeasure();
                workspaceTools.InvalidateMeasure(); toolRailContent.InvalidateMeasure();
                toolRailContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (toolRailContent.DesiredSize.Height <= available + .5) return;
            }
        }
        // The trial measure used an open constraint; lay the rail out again in its real viewport.
        finally { toolRailContent.InvalidateMeasure(); }
    }

    void ApplyToolRailLayout(int index)
    {
        index = Math.Clamp(index, 0, RailLayouts.Length - 1);
        var (columns, button, gap) = RailLayouts[index]; toolRailLayout = index;
        if (workspaceTools != null)
            foreach (var child in workspaceTools.Children)
            {
                if (child is UniformGrid grid) grid.Columns = columns;
                else if (child is Border separator) separator.Margin = new Thickness(8, gap, 8, gap);
            }
        foreach (var tool in toolButtons.Values)
        {
            tool.Height = button;
            if (screenCompact) { tool.Width = button; tool.HorizontalAlignment = HorizontalAlignment.Center; }
            else { tool.ClearValue(WidthProperty); tool.ClearValue(HorizontalAlignmentProperty); }
        }
        // Each extra column is one button wide (the button is square in the two-column rail).
        // 간결한 화면: square buttons with their 1.5 DIP margins, the column's 3 DIP insets and the border.
        toolRailColumn.Width = new GridLength(screenCompact ? columns * (button + 3) + 7 : ToolRailWidth + (columns - 2) * (button + 4));
    }

    FrameworkElement BuildWorkspaceSwitch()
    {
        workspaceSwitch = new GlassSwitch("사진 편집", "디자인", 168) { Margin = new Thickness(8, 0, 6, 0),
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
