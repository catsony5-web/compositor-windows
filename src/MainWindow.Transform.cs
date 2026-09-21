using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public static class TransformHandles
{
    public static Point[] Points(Document doc, Layer layer, double zoom)
    {
        var corners = new[] { new Point(0, 0), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) }.Select(p => DocumentFeatures.ToDocumentSpace(doc, layer, p)).ToArray();
        var result = new Point[9]; Array.Copy(corners, result, 4);
        for (int i = 0; i < 4; i++) result[4 + i] = corners[i] + (corners[(i + 1) % 4] - corners[i]) * .5;
        var direction = result[4] - result[6]; if (direction.Length < .001) direction = new Vector(0, -1); direction.Normalize();
        result[8] = result[4] + direction * (26 / Math.Max(.01, zoom)); return result;
    }
    public static int HitTest(Document doc, Layer layer, Point documentPoint, double zoom)
    {
        var points = Points(doc, layer, zoom); double tolerance = 7 / Math.Max(.01, zoom);
        for (int i = points.Length - 1; i >= 0; i--) if ((points[i] - documentPoint).Length <= tolerance) return i;
        return -1;
    }
}

public sealed partial class MainWindow
{
    sealed record HandleGesture(int Handle, bool Distort, Layer Original, Point StartParent, Point AnchorParent, Point PivotParent);
    HandleGesture? handleGesture;

    bool TryBeginTransformHandle(Point point, Point screen)
    {
        if (tool != Tool.Move || doc.Active is not { } layer || layer.Locked) return false;
        var parent = layer.ParentId;
        while (parent is { } id)
        {
            var group = doc.Layers.Find(l => l.Id == id); if (group == null || group.Locked) return false; parent = group.ParentId;
        }
        int handle = TransformHandles.HitTest(doc, layer, point, canvas.Zoom); if (handle < 0) return false;
        var original = layer.Snapshot(); var parentPoint = DocumentFeatures.ToParentSpace(doc, layer, point);
        var handles = TransformHandles.Points(doc, layer, canvas.Zoom);
        int opposite = handle < 4 ? (handle + 2) % 4 : handle < 8 ? 4 + (handle - 4 + 2) % 4 : 0;
        var pivot = original.Document(new Point(layer.Pixels.Width / 2.0, layer.Pixels.Height / 2.0));
        var anchor = handle == 8 ? pivot : DocumentFeatures.ToParentSpace(doc, layer, handles[opposite]);
        handleGesture = new(handle, handle < 4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control), original, parentPoint, anchor, pivot);
        beforeGesture = doc.Snapshot(); start = point; screenStart = screen; dragging = true; moveStarted = true; canvas.CaptureMouse();
        status.Text = handle == 8 ? "회전 · Shift: 15° 단위" : handleGesture.Distort ? "원근 왜곡 · 볼록 사각형 범위 · Esc: 취소" : "크기 조절 · Shift: 비율 고정 · Ctrl+꼭짓점: 원근 왜곡";
        return true;
    }
    bool MoveTransformHandle(Point point)
    {
        if (handleGesture is not { } gesture || doc.Active is not { } layer) return false;
        var old = gesture.Original; var parentPoint = DocumentFeatures.ToParentSpace(doc, old, point);
        var candidate = old.Snapshot();
        if (gesture.Distort)
        {
            var quad = old.Warp?.Corners ?? [new Point(0, 0), new Point(old.Pixels.Width, 0), new Point(old.Pixels.Width, old.Pixels.Height), new Point(0, old.Pixels.Height)];
            var inverse = old.Matrix; inverse.Invert(); var pointerStart = inverse.Transform(gesture.StartParent); var pointerNow = inverse.Transform(parentPoint);
            quad[gesture.Handle] += pointerNow - pointerStart;
            candidate.Warp = new(quad[0], quad[1], quad[2], quad[3]);
            try { candidate.Warp.Validate(); } catch (System.IO.InvalidDataException) { status.Text = "꼭짓점이 교차하는 왜곡은 적용할 수 없습니다."; return true; }
        }
        else if (gesture.Handle == 8)
        {
            var first = gesture.StartParent - gesture.PivotParent; var current = parentPoint - gesture.PivotParent;
            double rotation = old.Rotation + Math.Atan2(current.Y, current.X) * 180 / Math.PI - Math.Atan2(first.Y, first.X) * 180 / Math.PI;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) rotation = Math.Round(rotation / 15) * 15;
            candidate.Rotation = Math.Clamp(rotation, -36000, 36000);
            var movedPivot = candidate.Document(new Point(old.Pixels.Width / 2.0, old.Pixels.Height / 2.0));
            candidate.X += gesture.PivotParent.X - movedPivot.X; candidate.Y += gesture.PivotParent.Y - movedPivot.Y;
        }
        else
        {
            var unrotate = Matrix.Identity; unrotate.Rotate(-old.Rotation);
            var initial = unrotate.Transform(gesture.StartParent - gesture.AnchorParent); var current = unrotate.Transform(parentPoint - gesture.AnchorParent);
            bool resizeX = gesture.Handle < 4 || gesture.Handle is 5 or 7, resizeY = gesture.Handle < 4 || gesture.Handle is 4 or 6;
            double fx = resizeX && Math.Abs(initial.X) > 1e-6 ? Math.Max(.0001, current.X / initial.X) : 1;
            double fy = resizeY && Math.Abs(initial.Y) > 1e-6 ? Math.Max(.0001, current.Y / initial.Y) : 1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { double f = !resizeX ? fy : !resizeY ? fx : Math.Abs(fx - 1) >= Math.Abs(fy - 1) ? fx : fy; fx = fy = f; }
            candidate.ScaleX = Math.Clamp(old.ScaleX * fx, .01, 20); candidate.ScaleY = Math.Clamp(old.ScaleY * fy, .01, 20);
            var anchorLocal = old.Local(gesture.AnchorParent); var movedAnchor = candidate.Document(anchorLocal);
            candidate.X += gesture.AnchorParent.X - movedAnchor.X; candidate.Y += gesture.AnchorParent.Y - movedAnchor.Y;
        }
        candidate.X = Math.Clamp(candidate.X, -100000, 100000); candidate.Y = Math.Clamp(candidate.Y, -100000, 100000);
        layer.X = candidate.X; layer.Y = candidate.Y; layer.ScaleX = candidate.ScaleX; layer.ScaleY = candidate.ScaleY; layer.Rotation = candidate.Rotation; layer.Warp = candidate.Warp;
        RenderGesture(); return true;
    }
    bool EndTransformHandle()
    {
        if (handleGesture == null) return false;
        string label = handleGesture.Distort ? "원근 왜곡" : handleGesture.Handle == 8 ? "레이어 회전" : "레이어 크기";
        handleGesture = null; dragging = false;
        var before = beforeGesture; beforeGesture = null; canvas.ReleaseMouseCapture();
        try { doc.Validate(); if (before != null) history.Commit(label, before, doc); }
        catch { if (before != null) doc = before; throw; }
        finally { canvas.Document = doc; Refresh(); }
        return true;
    }
    bool CancelTransformHandle()
    {
        if (handleGesture == null) return false;
        handleGesture = null; dragging = false; if (beforeGesture != null) doc = beforeGesture; beforeGesture = null;
        canvas.ReleaseMouseCapture(); canvas.Document = doc; Refresh(); return true;
    }
}
