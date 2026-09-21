using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    void InteractionKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && (jobCts != null || dragging || panning || polygonInProgress || resizingBrush))
        { jobCts?.Cancel(); CancelGesture(); ResetInteractionTransient(); Refresh(false); e.Handled = true; return; }
        // Resize grips and workspace/color switches own their arrow keys before
        // the window can turn them into layer movement. OriginalSource also covers
        // routed input before a newly focused control is reported by Keyboard.
        if (e.OriginalSource is Thumb or GlassSwitch || Keyboard.FocusedElement is TextBoxBase or ComboBox or System.Windows.Controls.Slider or SaturationValuePad or Thumb or GlassSwitch) return;
        if (tool == Tool.Move && dragging && key is Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        { if (key is Key.LeftAlt or Key.RightAlt) suppressAltMenu = true; UpdatePointerModifiers(); e.Handled = true; return; }
        if (key == Key.Space) { ClearPointerHover(); Mouse.UpdateCursor(); }
        if (resizingBrush) { e.Handled = true; return; }
        if (polygonInProgress && key is Key.Enter or Key.Back)
        {
            if (key == Key.Enter) Guard(FinishPolygon);
            else if (lassoPoints.Count > 0) { lassoPoints.RemoveAt(lassoPoints.Count - 1); canvas.GesturePoints = lassoPoints.ToArray(); canvas.InvalidateVisual(); }
            e.Handled = true; return;
        }
        if (ExecuteEditorShortcut(key, Keyboard.Modifiers)) e.Handled = true;
    }

    // Shared by real key routing and offscreen command checks. Text-field focus
    // is filtered by InteractionKey before an editor shortcut can run.
    bool ExecuteEditorShortcut(Key key, ModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift), alt = modifiers.HasFlag(ModifierKeys.Alt);
        Action? action;
        if (!HasDocument)
        {
            action = ctrl ? key switch { Key.N => NewDocument, Key.O => shift ? Import : Open, Key.V => Paste, _ => null }
                : key == Key.F1 ? Help : null;
            if (action != null) { Guard(action); return true; }
            // Consume document commands without touching the placeholder model. Leave Tab,
            // menu navigation and unrecognized keys available to the empty workspace's UI.
            return ctrl ? key is Key.S or Key.W or Key.E or Key.Z or Key.Y or Key.J or Key.T or Key.L or Key.U or Key.G or Key.I or Key.D or Key.A or Key.C or Key.Back or Key.Delete or Key.D0 or Key.NumPad0 or Key.D1 or Key.NumPad1 or Key.Tab
                : (alt && (key is Key.Back or Key.Delete)) || key is Key.Delete or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down || (shift && key == Key.F5);
        }
        if (ctrl) action = key switch
        {
            Key.N => NewDocument, Key.O => shift ? Import : Open, Key.S => () => Save(shift), Key.W => CloseTab,
            Key.E => shift ? Export : MergeDown, Key.Z => shift ? Redo : Undo, Key.Y => shift ? () => SetProof(!cmykProof) : Redo, Key.J => Duplicate,
            Key.T => Transform, Key.L => Levels, Key.U => () => ShowAdjustment(AdjustmentKind.HueSaturation),
            Key.G => alt ? ToggleClipping : shift ? UngroupSelected : GroupSelected,
            Key.I => shift ? InvertSelection : () => Adjust("invert"), Key.D => () => { selection = null; Refresh(false); },
            Key.A when shift => () => ShowAdjustment(AdjustmentKind.PhotoDevelop),
            Key.A => () => { selection = new Selection(new Rect(0, 0, doc.Width, doc.Height)); Refresh(false); },
            Key.C => CopyMerged, Key.V => Paste, Key.Back or Key.Delete when !alt => FillBackground,
            Key.D0 or Key.NumPad0 => () => { canvas.Fit(); UpdateStatus(); },
            Key.D1 or Key.NumPad1 => () => { canvas.Zoom = 1; canvas.Pan = new(); canvas.InvalidateVisual(); UpdateStatus(); },
            Key.Tab when tabs.Count > 0 => () => SwitchTab((activeTab + (shift ? tabs.Count - 1 : 1)) % tabs.Count), _ => null
        };
        else if (alt && key is Key.Back or Key.Delete) action = Fill;
        else action = key switch
        {
            Key.V => () => SetTool(Tool.Move), Key.B => () => SetTool(Tool.Brush), Key.E => () => SetTool(Tool.Eraser),
            Key.M => () => SetTool(shift ? Tool.EllipseSelect : Tool.RectangleSelect), Key.C => () => SetTool(Tool.Crop),
            Key.U => () => SetTool(shift ? Tool.Ellipse : Tool.Rectangle), Key.G => () => SetTool(shift ? Tool.Gradient : Tool.Bucket),
            Key.T => () => SetTool(Tool.Text), Key.I => () => SetTool(Tool.Eyedropper), Key.H => () => SetTool(Tool.Hand),
            Key.L => () => SetTool(shift ? Tool.PolygonLasso : Tool.Lasso), Key.W => () => SetTool(Tool.MagicWand),
            Key.S => () => SetTool(Tool.CloneStamp), Key.J => () => SetTool(Tool.Heal), Key.R => () => SetTool(shift ? Tool.Liquify : Tool.Smudge), Key.K => () => SetTool(Tool.BlurBrush),
            Key.F5 when shift => ContentFill, Key.Delete => ClearPixels,
            Key.Escape => () => { CancelGesture(); ResetInteractionTransient(); selection = null; Refresh(false); },
            Key.OemOpenBrackets => () => { brushSize = Math.Max(1, brushSize - 5); UpdateBrushLabel(); },
            Key.OemCloseBrackets => () => { brushSize = Math.Min(MaxBrushSize, brushSize + 5); UpdateBrushLabel(); },
            Key.D => ResetColors, Key.X => SwapColors,
            Key.Left => () => NudgeSelected(new Vector(shift ? -10 : -1, 0)), Key.Right => () => NudgeSelected(new Vector(shift ? 10 : 1, 0)),
            Key.Up => () => NudgeSelected(new Vector(0, shift ? -10 : -1)), Key.Down => () => NudgeSelected(new Vector(0, shift ? 10 : 1)),
            Key.F1 => Help, _ => null
        };
        if (action == null) return false;
        Guard(action); return true;
    }
}
