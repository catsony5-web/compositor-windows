using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    const double MaxBrushSize = 1000;
    bool resizingBrush, resizeMoved, suppressAltMenu;
    Point resizeScreen, resizePoint;
    double resizeInitial;
    MouseButton resizeButton;
    bool BrushTool => tool is Tool.Brush or Tool.Eraser || IsRetouch(tool);

    void ChooseColor(bool background)
    {
        var selected = Dialogs.ColorPicker(this, background ? backgroundColor : foreground, background ? "배경색" : "전경색");
        if (selected is not { } color) return;
        if (background) backgroundColor = color; else foreground = color;
        UpdateColor(); canvas.Focus();
    }
    void SwapColors() { (foreground, backgroundColor) = (backgroundColor, foreground); UpdateColor(); }
    void ResetColors() { foreground = Colors.Black; backgroundColor = Colors.White; UpdateColor(); }
    void UpdateToolOptions()
    {
        toolCaption.Text = ToolDisplayName(tool);
        brushOptions.Visibility = BrushTool ? Visibility.Visible : Visibility.Collapsed;
        opacityOptions.Visibility = BrushTool || tool is Tool.Gradient or Tool.Rectangle or Tool.Ellipse or Tool.Bucket ? Visibility.Visible : Visibility.Collapsed;
        bucketOptions.Visibility = tool == Tool.Bucket ? Visibility.Visible : Visibility.Collapsed;
        wandOptions.Visibility = tool == Tool.MagicWand ? Visibility.Visible : Visibility.Collapsed;
        gradientOptions.Visibility = tool == Tool.Gradient ? Visibility.Visible : Visibility.Collapsed;
        autoSelectToggle.Visibility = tool == Tool.Move ? Visibility.Visible : Visibility.Collapsed;
        toolCaption.ToolTip = BrushTool ? "Alt + 좌우 드래그: 크기" : tool == Tool.Eyedropper ? "클릭: 전경색 · Alt+클릭: 배경색" : null;
    }
    bool BeginBrushResize(Point screen, Point point, MouseButton button, bool alt)
    {
        if (!alt || !BrushTool || dragging || panning || resizingBrush || button is not (MouseButton.Left or MouseButton.Right)) return false;
        resizingBrush = true; resizeMoved = false; resizeScreen = screen; resizePoint = point; resizeInitial = brushSize; resizeButton = button;
        suppressAltMenu = true; canvas.BrushPoint = point; canvas.BrushRadius = brushSize / 2;
        canvas.BrushHud = $"{brushSize:0} px"; canvas.Cursor = Cursors.SizeWE;
        if (!headlessTesting && !canvas.CaptureMouse()) { EndBrushResize(true); return true; }
        canvas.InvalidateVisual(); return true;
    }
    void MoveBrushResize(Point screen)
    {
        if (!resizingBrush) return;
        double dx = screen.X - resizeScreen.X;
        // A small click tolerance keeps Alt+click sampling reliable for clone/heal.
        if ((screen - resizeScreen).Length >= 4) resizeMoved = true;
        double size = resizeMoved ? Math.Clamp(Math.Round(resizeInitial + dx * 2), 1, MaxBrushSize) : resizeInitial;
        if (brushSize == size) return;
        brushSize = size; UpdateBrushLabel(); canvas.BrushHud = $"{brushSize:0} px";
        canvas.InvalidateVisual();
    }
    void EndBrushResize(bool cancel)
    {
        if (!resizingBrush) return;
        bool sample = !cancel && !resizeMoved && resizeButton == MouseButton.Left && tool is Tool.CloneStamp or Tool.Heal;
        resizingBrush = false;
        if (cancel) brushSize = resizeInitial;
        canvas.BrushHud = null; canvas.Cursor = Cursors.Cross; UpdateBrushLabel();
        if (!headlessTesting) canvas.ReleaseMouseCapture();
        UpdateStatus(); ShowInteractionHint();
        if (sample) SetCloneAnchor(resizePoint);
    }
    void SetCloneAnchor(Point point)
    {
        if (doc.Active is not { } layer || IsLockedWithParents(layer) || !CanPaintActiveLayer()) return;
        var local = layer.Local(point);
        if (local.X < 0 || local.Y < 0 || local.X >= layer.Pixels.Width || local.Y >= layer.Pixels.Height)
        { status.Text = "선택한 레이어의 이미지 안에서 원본 위치를 지정하세요."; return; }
        cloneAnchorLayer = layer.Id; cloneAnchorDocument = doc; cloneAnchorLocal = local; cloneSource = point;
        status.Text = $"참조 위치 지정됨 ({point.X:0}, {point.Y:0}) · 드래그하여 적용";
    }
}
