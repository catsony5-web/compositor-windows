using System.IO;

namespace Compositor.Windows;

/// <summary>
/// 피사체를 글자 앞으로: a copy of a photo layer, cut out to its subject with an editable layer mask,
/// placed directly above a title text layer so the subject stands in front of the big type while
/// the photo itself stays behind it. The cut-out mask comes from the caller (the local subject model
/// in the app), so this document step is fast and testable.
/// </summary>
public static class SubjectFront
{
    public sealed record Pair(Layer Photo, Layer Title);

    public const string Usage = "사진 레이어 하나와 텍스트 레이어 하나를 함께 선택하세요. 사진만 선택하면 바로 위쪽의 텍스트 레이어를, 텍스트만 선택하면 바로 아래쪽의 사진 레이어를 씁니다.";

    /// <summary>
    /// The photo and title a selection stands for: one photo and one text layer, a photo with a text
    /// layer above it, or a text layer with a photo below it (nearest in the same stack). Null with a
    /// sentence saying what to select when the selection does not fit.
    /// </summary>
    public static Pair? Resolve(Document document, IReadOnlyCollection<Guid> selected, out string? problem)
    {
        problem = null;
        var layers = document.Layers.Where(l => selected.Contains(l.Id)).ToArray();
        Layer? photo, title;
        if (layers.Length == 2 && layers.Count(IsPhoto) == 1 && layers.Count(IsTitle) == 1) { photo = layers.Single(IsPhoto); title = layers.Single(IsTitle); }
        else if (layers.Length == 1 && IsPhoto(layers[0]))
        {
            photo = layers[0]; title = Nearest(document, photo, true, IsTitle);
            if (title == null) { problem = "선택한 사진 위에 텍스트 레이어가 없습니다. 제목 텍스트 레이어를 사진 위에 두거나 사진과 텍스트 레이어를 함께 선택하세요."; return null; }
        }
        else if (layers.Length == 1 && IsTitle(layers[0]))
        {
            title = layers[0]; photo = Nearest(document, title, false, IsPhoto);
            if (photo == null) { problem = "선택한 텍스트 아래에 사진 레이어가 없습니다. 사진 레이어를 텍스트 아래에 두거나 사진과 텍스트 레이어를 함께 선택하세요."; return null; }
        }
        else { problem = Usage; return null; }
        if (photo.ParentId != title.ParentId) { problem = "사진과 텍스트 레이어를 같은 그룹 안에 두세요."; return null; }
        if (document.Layers.IndexOf(title) < document.Layers.IndexOf(photo))
        { problem = "텍스트 레이어가 사진 아래에 있습니다. 텍스트 레이어를 사진 위로 옮긴 뒤 다시 실행하세요."; return null; }
        return new Pair(photo, title);
    }

    static bool IsPhoto(Layer layer) => layer.Kind == LayerKind.Raster;
    static bool IsTitle(Layer layer) => layer.Kind == LayerKind.Text;

    // The closest layer of the same stack above (or below) that matches.
    static Layer? Nearest(Document document, Layer from, bool above, Func<Layer, bool> match)
    {
        var siblings = document.Layers.Where(l => l.ParentId == from.ParentId).ToList(); int index = siblings.IndexOf(from);
        return (above ? siblings.Skip(index + 1) : siblings.Take(index).Reverse()).FirstOrDefault(match);
    }

    /// <summary>
    /// Adds the cut-out copy of the photo directly above the title (after the layers clipped to it),
    /// makes it the active layer and returns it. The subject mask is one byte per photo pixel; an
    /// existing photo mask is kept inside it. The photo and the title are not changed.
    /// </summary>
    public static Layer Apply(Document document, Guid photoId, Guid titleId, byte[] subject, string name)
    {
        var photo = document.Layers.SingleOrDefault(l => l.Id == photoId) ?? throw new InvalidOperationException("사진 레이어가 없습니다.");
        var title = document.Layers.SingleOrDefault(l => l.Id == titleId) ?? throw new InvalidOperationException("텍스트 레이어가 없습니다.");
        if (!IsTitle(title)) throw new InvalidOperationException(Usage);
        var copy = CutOut(photo, subject, name, title.ParentId);
        var siblings = document.Layers.Where(l => l.ParentId == title.ParentId).ToList();
        int top = siblings.IndexOf(title); while (top + 1 < siblings.Count && siblings[top + 1].Clipped) top++;
        var anchor = siblings[top];
        document.Add(copy); document.Layers.Remove(copy);
        document.Layers.Insert(document.Layers.IndexOf(anchor) + 1, copy);
        document.ActiveId = copy.Id;
        return copy;
    }

    /// <summary>
    /// The cut-out copy on its own (not added to a document): the photo layer, sharing its pixels, with the
    /// subject mask (an existing photo mask kept inside it) in folder <paramref name="parentId"/>. Design
    /// styles place it above their own title.
    /// </summary>
    public static Layer CutOut(Layer photo, byte[] subject, string name, Guid? parentId)
    {
        if (!IsPhoto(photo)) throw new InvalidOperationException(Usage);
        if (subject.Length != photo.Pixels.Width * photo.Pixels.Height) throw new InvalidDataException("피사체 마스크의 크기가 사진과 다릅니다.");
        var copy = photo.Snapshot();
        copy.Id = Guid.NewGuid(); copy.Name = name; copy.ParentId = parentId;
        copy.Clipped = false; copy.Locked = false; copy.Shadow = null; copy.SourceLayerName = null;
        copy.Mask = BackgroundRemoval.CombineMasks(subject, photo.Mask);
        return copy;
    }
}
