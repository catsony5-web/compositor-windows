using System.IO;
using System.Text;
using System.Windows;

namespace Compositor.Windows;

public sealed record Artboard(Guid Id, string Name, double X, double Y, double Width, double Height)
{
    [System.Text.Json.Serialization.JsonIgnore] public Rect Bounds => new(X, Y, Width, Height);
    public override string ToString() => Name;
}

public static class ArtboardEditing
{
    public const int MaxArtboards = 128;
    public static IReadOnlyList<Artboard> Visible(Document doc) => doc.Artboards.Count > 0 ? doc.Artboards
        : [new Artboard(Guid.Empty, "대지 1", 0, 0, doc.Width, doc.Height)];
    public static Rect Bounds(Document doc)
    {
        var bounds = Rect.Empty;
        foreach (var board in Visible(doc)) bounds.Union(board.Bounds);
        return bounds;
    }
    static void ValidateBoard(Artboard board)
    {
        if (board == null || string.IsNullOrWhiteSpace(board.Name) || Encoding.UTF8.GetByteCount(board.Name) > 4096 ||
            !double.IsFinite(board.X) || !double.IsFinite(board.Y) || !double.IsFinite(board.Width) || !double.IsFinite(board.Height) ||
            Math.Abs(board.X) > 100_000 || Math.Abs(board.Y) > 100_000 || board.Width < 1 || board.Height < 1 ||
            board.Width > Raster.MaxDimension || board.Height > Raster.MaxDimension)
            throw new InvalidDataException("대지 이름과 위치·크기를 확인해 주세요. 크기는 1px 이상이어야 합니다.");
    }
    public static void Validate(Document doc)
    {
        if (doc.Artboards == null || doc.Artboards.Count > MaxArtboards) throw new InvalidDataException($"대지는 {MaxArtboards}개까지 만들 수 있습니다.");
        var ids = new HashSet<Guid>();
        foreach (var board in doc.Artboards)
        {
            ValidateBoard(board);
            if (board.Id == Guid.Empty || !ids.Add(board.Id) || board.X < 0 || board.Y < 0 || board.Bounds.Right > doc.Width || board.Bounds.Bottom > doc.Height)
                throw new InvalidDataException("대지 범위 또는 ID가 올바르지 않습니다.");
        }
    }
    // The workspace grows to preserve all pixels. A negative extension translates
    // every root and existing artboard together, without rasterizing any source.
    public static (Guid Id, Vector Offset) Set(Document doc, Artboard board, bool add = false)
    {
        ValidateBoard(board);
        var boards = Visible(doc).ToList();
        int index = add ? -1 : boards.FindIndex(b => b.Id == board.Id);
        if (!add && index < 0) throw new InvalidOperationException("수정할 대지가 없습니다.");
        if (add && boards.Count >= MaxArtboards) throw new InvalidOperationException($"대지는 {MaxArtboards}개까지 만들 수 있습니다.");
        var id = board.Id == Guid.Empty || add ? Guid.NewGuid() : board.Id;
        board = board with { Id = id };
        if (add) boards.Add(board); else boards[index] = board;
        for (int i = 0; i < boards.Count; i++) if (boards[i].Id == Guid.Empty) boards[i] = boards[i] with { Id = Guid.NewGuid() };
        var extent = new Rect(0, 0, doc.Width, doc.Height);
        foreach (var item in boards) extent.Union(item.Bounds);
        var offset = new Vector(-Math.Floor(Math.Min(0, extent.Left)), -Math.Floor(Math.Min(0, extent.Top)));
        int width = checked((int)Math.Ceiling(extent.Right + offset.X)), height = checked((int)Math.Ceiling(extent.Bottom + offset.Y));
        Raster.ValidateSize(width, height);
        var candidate = doc.Snapshot(); candidate.Width = width; candidate.Height = height;
        candidate.Artboards = boards.Select(b => b with { X = b.X + offset.X, Y = b.Y + offset.Y }).ToList();
        foreach (var layer in candidate.Layers.Where(l => l.ParentId == null)) { layer.X += offset.X; layer.Y += offset.Y; }
        candidate.Validate();
        doc.Width = width; doc.Height = height; doc.Artboards = candidate.Artboards; doc.Layers = candidate.Layers;
        return (id, offset);
    }
    public static void Remove(Document doc, Guid id)
    {
        if (doc.Artboards.Count <= 1) throw new InvalidOperationException("마지막 대지는 삭제할 수 없습니다.");
        if (doc.Artboards.RemoveAll(b => b.Id == id) == 0) throw new InvalidOperationException("삭제할 대지가 없습니다.");
    }
    public static void Crop(Document doc, Rect bounds)
    {
        doc.Artboards = doc.Artboards.Select(board => (Board: board, Rect: Rect.Intersect(board.Bounds, bounds)))
            .Where(item => !item.Rect.IsEmpty && item.Rect.Width >= 1 && item.Rect.Height >= 1)
            .Select(item => item.Board with { X = item.Rect.X - bounds.X, Y = item.Rect.Y - bounds.Y, Width = item.Rect.Width, Height = item.Rect.Height }).ToList();
    }
    public static Document ExportDocument(Document doc, Guid id)
    {
        var board = Visible(doc).Single(b => b.Id == id);
        var result = doc.Snapshot(); result.Name = board.Name;
        result.Width = checked((int)Math.Ceiling(board.Width)); result.Height = checked((int)Math.Ceiling(board.Height)); result.Artboards = [];
        foreach (var layer in result.Layers.Where(l => l.ParentId == null)) { layer.X -= board.X; layer.Y -= board.Y; }
        result.Validate(); return result;
    }
}
