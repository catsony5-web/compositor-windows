using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class CanvasView
{
    public bool ArtboardMode { get; set; }
    public Guid SelectedArtboardId { get; set; }
    public Artboard? ArtboardDraft { get; set; }
    public bool ArtboardDraftIsNew { get; set; }
    public Rect? ObjectMarquee { get; set; }
    public bool CrossingSelection { get; set; }
    public IReadOnlySet<Guid> SelectedObjectIds { get; set; } = new HashSet<Guid>();
    static Point[] ArtboardHandles(Rect r) => [r.TopLeft, new(r.X + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Y + r.Height / 2), r.BottomRight, new(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Y + r.Height / 2)];
    public static int ArtboardHandleAt(Rect bounds, Point point, double zoom)
    {
        var points = ArtboardHandles(bounds);
        for (int i = 0; i < points.Length; i++) if (Math.Abs(points[i].X - point.X) * zoom <= 7 && Math.Abs(points[i].Y - point.Y) * zoom <= 7) return i;
        return -1;
    }
    Geometry ArtboardClip(bool screen)
    {
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var board in ArtboardEditing.Visible(Document!))
        {
            var b = board.Bounds;
            if (screen) b = new Rect(Origin.X + b.X * Zoom, Origin.Y + b.Y * Zoom, b.Width * Zoom, b.Height * Zoom);
            geometry.Children.Add(new RectangleGeometry(b));
        }
        return geometry;
    }
    void DrawArtboards(DrawingContext dc)
    {
        if (Document == null || Document.Artboards.Count == 0 && !ArtboardMode) return;
        var boards = ArtboardEditing.Visible(Document).ToList();
        if (ArtboardDraft is { } draft)
        {
            if (ArtboardDraftIsNew) boards.Add(draft);
            else { int index = boards.FindIndex(b => b.Id == draft.Id); if (index >= 0) boards[index] = draft; }
        }
        foreach (var board in boards)
        {
            bool selected = ArtboardMode && (ArtboardDraftIsNew && ReferenceEquals(board, ArtboardDraft) || !ArtboardDraftIsNew && board.Id == SelectedArtboardId || ArtboardDraft == null && board.Id == SelectedArtboardId);
            var pen = new Pen(selected ? Theme.Accent : Theme.Brush("#667383"), (selected ? 1.5 : 1) / Zoom);
            if (ReferenceEquals(board, ArtboardDraft)) pen.DashStyle = DashStyles.Dash;
            dc.DrawRectangle(null, pen, board.Bounds);
            var label = new FormattedText(board.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Theme.UiFont.GetTypefaces().First(), 12 / Zoom, selected ? Theme.Text : Theme.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            label.MaxTextWidth = Math.Max(1, board.Width); label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(board.X, board.Y - 22 / Zoom));
            if (selected) foreach (var point in ArtboardHandles(board.Bounds)) dc.DrawRectangle(Theme.Panel, pen, new Rect(point.X - 3.5 / Zoom, point.Y - 3.5 / Zoom, 7 / Zoom, 7 / Zoom));
        }
    }
    void DrawObjectSelection(DrawingContext dc)
    {
        if (ShowLayerBounds && SelectedObjectIds.Count > 1 && Document != null)
        {
            var lookup = Document.Layers.ToDictionary(l => l.Id); var bounds = Rect.Empty; int drawn = 0;
            var children = Document.Layers.ToLookup(l => l.ParentId); var outlined = new HashSet<Guid>();
            void Include(Guid id)
            {
                if (!lookup.TryGetValue(id, out var item) || !outlined.Add(id)) return;
                if (item.Kind == LayerKind.Group) foreach (var child in children[id]) Include(child.Id);
            }
            foreach (var id in SelectedObjectIds) Include(id);
            var pen = new Pen(Theme.Accent, 1 / Zoom);
            foreach (var layer in Document.Layers.Where(l => outlined.Contains(l.Id) && l.Kind != LayerKind.Group))
            {
                var points = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) };
                for (Layer? ancestor = layer; ancestor != null; ancestor = ancestor.ParentId is { } id ? lookup[id] : null)
                    for (int i = 0; i < points.Length; i++) points[i] = ancestor.Document(points[i]);
                foreach (var point in points) bounds.Union(point);
                if (++drawn <= 1000) for (int i = 0; i < 4; i++) dc.DrawLine(pen, points[i], points[(i + 1) % 4]);
            }
            if (!bounds.IsEmpty) dc.DrawRectangle(null, new Pen(Theme.Accent, 1 / Zoom) { DashStyle = DashStyles.Dash }, bounds);
        }
        if (ObjectMarquee is { } rectangle)
        {
            var color = CrossingSelection ? Color.FromRgb(65, 209, 154) : Color.FromRgb(101, 164, 250);
            var pen = new Pen(new SolidColorBrush(color), 1 / Zoom) { DashStyle = CrossingSelection ? DashStyles.Dash : DashStyles.Solid };
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)), pen, rectangle);
        }
    }
}
