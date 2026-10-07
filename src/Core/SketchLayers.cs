using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// Where 스케치 사진 정리 puts its result, as one edit of the document: a group (스케치 정리) holding the
// transparent line layer (스케치 선) above an optional white layer (흰 바탕). The photo stays in the
// document, hidden. Placement rule:
//  - The photo is the document's only layer and there are no artboards (a photo opened to be cleaned
//    up): the canvas becomes the flattened sheet and the result fills it at 1:1. The hidden photo keeps
//    its position.
//  - Otherwise (a photo placed on a board): the canvas keeps its size; the group goes directly above
//    the photo in the same parent, and the sheet is centered and uniformly scaled to fit the photo's
//    bounds (the axis-aligned box of its transformed corners in that parent's space).
public static class SketchLayers
{
    public sealed record Names(string Group, string Lines, string Background);
    public sealed record Placement(Guid GroupId, Guid LineLayerId, Guid? BackgroundLayerId, bool CanvasResized);

    public static readonly Names Korean = new("스케치 정리", "스케치 선", "흰 바탕");

    // A pixel layer the cleanup can read: an ordinary image layer (not text, a shape, a drawing or a fill).
    public static bool IsPhoto(Layer? layer) => layer is { Kind: LayerKind.Raster };

    // The photo as the cleanup reads it: the layer's pixels with its mask multiplied into the alpha
    // (hidden parts read as paper).
    public static Raster Source(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Mask is not { } mask) return layer.Pixels;
        var copy = layer.Pixels.Clone();
        for (int i = 0; i < mask.Length; i++) copy.Data[i * 4 + 3] = (byte)(copy.Data[i * 4 + 3] * mask[i] / 255);
        return copy;
    }

    public static bool FillsDocument(Document doc, Layer photo) => doc.Layers.Count == 1 && doc.Layers[0].Id == photo.Id && doc.Artboards.Count == 0;

    public static Placement Place(Document doc, Guid photoId, Raster lines, bool whiteBackground, Names? names = null)
    {
        ArgumentNullException.ThrowIfNull(doc); ArgumentNullException.ThrowIfNull(lines);
        names ??= Korean;
        var photo = doc.Layers.FirstOrDefault(l => l.Id == photoId) ?? throw new InvalidOperationException("사진 레이어가 없습니다.");
        if (!IsPhoto(photo)) throw new InvalidDataException("스케치를 찍은 사진(이미지) 레이어를 선택하세요.");
        int width = lines.Width, height = lines.Height;
        bool resize = FillsDocument(doc, photo);
        double x = 0, y = 0, scale = 1;
        if (resize)
        {
            Raster.ValidateSize(width, height);
            ArtboardEditing.Crop(doc, new Rect(0, 0, width, height));
            doc.Width = width; doc.Height = height;
        }
        else
        {
            var corners = new[] { new Point(0, 0), new Point(photo.Pixels.Width, 0), new Point(photo.Pixels.Width, photo.Pixels.Height), new Point(0, photo.Pixels.Height) }.Select(photo.Document).ToArray();
            double left = corners.Min(p => p.X), right = corners.Max(p => p.X), top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);
            scale = Math.Clamp(Math.Min((right - left) / width, (bottom - top) / height), .01, 20);
            x = left + (right - left - width * scale) / 2; y = top + (bottom - top - height * scale) / 2;
        }
        // A group's surface is its parent's (the document for a root group); an equal empty surface is shared.
        var parent = photo.ParentId is { } parentId ? doc.Layers.Single(l => l.Id == parentId) : null;
        int surfaceWidth = parent?.Pixels.Width ?? doc.Width, surfaceHeight = parent?.Pixels.Height ?? doc.Height;
        var surface = doc.Layers.FirstOrDefault(l => l.Kind == LayerKind.Group && l.Pixels.Width == surfaceWidth && l.Pixels.Height == surfaceHeight)?.Pixels
            ?? new Raster(surfaceWidth, surfaceHeight);
        var group = new Layer { Name = names.Group, Kind = LayerKind.Group, Pixels = surface, ParentId = photo.ParentId };
        var created = new List<Layer> { group };
        Layer? background = null;
        if (whiteBackground)
        {
            background = new Layer { Name = names.Background, Pixels = Raster.Solid(width, height, Colors.White), ParentId = group.Id, X = x, Y = y, Scale = scale };
            created.Add(background);
        }
        var line = new Layer { Name = names.Lines, Pixels = lines, ParentId = group.Id, X = x, Y = y, Scale = scale };
        created.Add(line);
        // Validated additions first (capacity, memory), then the stacking: right above the photo.
        foreach (var layer in created) doc.Add(layer);
        foreach (var layer in created) doc.Layers.Remove(layer);
        doc.Layers.InsertRange(doc.Layers.IndexOf(photo) + 1, created);
        photo.Visible = false;
        doc.ActiveId = line.Id;
        return new Placement(group.Id, line.Id, background?.Id, resize);
    }
}
