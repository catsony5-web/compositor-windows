using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

// 내비게이터 (간결한 화면): the whole document as a thumbnail with the canvas viewport outlined.
// Pressing or dragging asks to center the view on that point; arrow keys move the view by a
// tenth of the visible area. Drawing only reads the last thumbnail and viewport, so it is cheap
// enough to follow every canvas render.
internal sealed class NavigatorView : FrameworkElement
{
    public BitmapSource? Thumbnail { get; private set; }
    public Size DocumentSize { get; private set; }
    /// <summary>The visible part of the document, in document pixels.</summary>
    public Rect Viewport { get; private set; }
    /// <summary>A document point to center the view on.</summary>
    public event Action<Point>? Navigate;

    public NavigatorView()
    {
        Focusable = true; Cursor = Cursors.Hand; MinHeight = 32; ClipToBounds = true;
        ToolTip = "누르거나 끌어서 화면 이동 · 방향키로도 이동";
        AutomationProperties.SetName(this, "내비게이터");
        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); NavigateAt(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { NavigateAt(e.GetPosition(this)); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } };
        GotKeyboardFocus += (_, _) => InvalidateVisual(); LostKeyboardFocus += (_, _) => InvalidateVisual();
        KeyDown += (_, e) =>
        {
            if (Viewport.IsEmpty || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            double dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            var center = new Point(Viewport.X + Viewport.Width / 2 + dx * Viewport.Width / 10, Viewport.Y + Viewport.Height / 2 + dy * Viewport.Height / 10);
            Navigate?.Invoke(center); e.Handled = true;
        };
    }

    public void SetDocument(BitmapSource? thumbnail, Size size) { Thumbnail = thumbnail; DocumentSize = size; InvalidateVisual(); }

    public void SetViewport(Rect viewport) { if (viewport == Viewport) return; Viewport = viewport; InvalidateVisual(); }

    // Where the thumbnail sits: the document fitted inside the control with a small inset.
    internal Rect ImageRect(Size area)
    {
        if (DocumentSize.Width <= 0 || DocumentSize.Height <= 0) return Rect.Empty;
        double inset = 6, width = Math.Max(1, area.Width - inset * 2), height = Math.Max(1, area.Height - inset * 2);
        double scale = Math.Min(width / DocumentSize.Width, height / DocumentSize.Height);
        double w = DocumentSize.Width * scale, h = DocumentSize.Height * scale;
        return new Rect((area.Width - w) / 2, (area.Height - h) / 2, w, h);
    }

    /// <summary>The document point under a point of this control, clamped to the document.</summary>
    internal Point ToDocument(Point point, Size area)
    {
        var image = ImageRect(area);
        if (image.IsEmpty) return new Point();
        double x = (point.X - image.X) / image.Width * DocumentSize.Width, y = (point.Y - image.Y) / image.Height * DocumentSize.Height;
        return new Point(Math.Clamp(x, 0, DocumentSize.Width), Math.Clamp(y, 0, DocumentSize.Height));
    }

    /// <summary>A press or drag at a point of this control: center the view on the document point under it.</summary>
    internal void NavigateAt(Point point) { if (!ImageRect(RenderSize).IsEmpty) Navigate?.Invoke(ToDocument(point, RenderSize)); }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(RenderSize);
        dc.DrawRectangle(Theme.Stage, null, area);
        var image = ImageRect(RenderSize);
        if (image.IsEmpty) return;
        dc.DrawRectangle(Brushes.White, null, image);
        if (Thumbnail != null) dc.DrawImage(Thumbnail, image);
        dc.DrawRectangle(null, new Pen(Theme.Line, 1), image);
        if (Viewport.IsEmpty) return;
        double sx = image.Width / DocumentSize.Width, sy = image.Height / DocumentSize.Height;
        var view = new Rect(image.X + Viewport.X * sx, image.Y + Viewport.Y * sy, Viewport.Width * sx, Viewport.Height * sy);
        view.Intersect(area);
        if (view.IsEmpty) return;
        // The outline is red so it reads on any image; it is the one strong color in the panel.
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x4A, 0x4A)), IsKeyboardFocused ? 2 : 1.5);
        dc.DrawRectangle(null, outline, new Rect(view.X + .75, view.Y + .75, Math.Max(1, view.Width - 1.5), Math.Max(1, view.Height - 1.5)));
    }
}
