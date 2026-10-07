using System.Diagnostics;

namespace Compositor.Windows;

/// <summary>What to apply: a style, its parameters (missing ones use defaults), the layers to read (null: the whole document) and an existing style folder to replace.</summary>
public sealed record StyleRequest(string StyleId, IReadOnlyDictionary<string, double>? Parameters = null, IReadOnlyList<Guid>? Targets = null, Guid? GroupId = null);

/// <summary>The style folder made (a replaced folder keeps its id), the values used, the layers inside it and notes for the user.</summary>
public sealed record StyleOutcome(Guid GroupId, string StyleId, IReadOnlyDictionary<string, double> Values, int LayerCount, IReadOnlyList<string> Notes, TimeSpan Elapsed);

// Applies, re-applies and removes design styles on a document. Callers pass a candidate copy and
// commit it as one undo step; nothing here touches history. The folder is a pass-through group of
// editable layers placed above what it reads; the original layers are never changed except the
// documented, reversible edits a recipe records (StyleKit.Hide), which removing the style undoes.
// WPF drawing happens here (text, patterns, renders), so callers run it on an STA thread.
public static class DesignStyleEngine
{
    public static StyleOutcome Apply(Document doc, StyleRequest request, StyleServices? services = null, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        var style = DesignStyles.Find(request.StyleId) ?? throw new ArgumentException("알 수 없는 디자인 스타일입니다.");
        var values = style.Values(request.Parameters);
        services ??= StyleServices.Default;
        Layer? replaced = null; Guid? parent = null; int insertion = -1; IReadOnlyList<Guid>? previousTargets = null;
        bool visible = true; double opacity = 1; string? keptName = null;
        if (request.GroupId is { } groupId)
        {
            replaced = doc.Layers.FirstOrDefault(l => l.Id == groupId) ?? throw new ArgumentException("다시 적용할 스타일 그룹이 없습니다.");
            if (!DesignStyles.IsStyleGroup(replaced)) throw new ArgumentException("디자인 스타일 그룹이 아닙니다.");
            if (Locked(doc, replaced)) throw new InvalidOperationException("잠긴 스타일 그룹입니다. 그룹과 부모 그룹의 잠금을 먼저 해제하세요.");
            parent = replaced.ParentId; visible = replaced.Visible; opacity = replaced.Opacity;
            // A name the user gave stays; the automatic name follows the style.
            if (!replaced.Name.StartsWith(DesignStyles.GroupPrefix, StringComparison.Ordinal) && !replaced.Name.StartsWith(Loc.T(DesignStyles.GroupPrefix), StringComparison.Ordinal)) keptName = replaced.Name;
            previousTargets = replaced.Style!.Targets;
            insertion = doc.Layers.IndexOf(replaced);
            int removedBefore = 0; var subtree = Subtree(doc, replaced.Id);
            for (int i = 0; i < insertion; i++) if (subtree.Contains(doc.Layers[i].Id)) removedBefore++;
            // Removing the folder also restores what the style changed on other layers.
            DocumentFeatures.Remove(doc, replaced.Id); insertion -= removedBefore;
        }
        var targets = ResolveTargets(doc, request.Targets ?? previousTargets);
        token.ThrowIfCancellationRequested();
        var analysis = AnalysisDocument(doc, targets);
        bool drawing = services.Drawing ?? IsDrawing(doc, targets);
        var context = new StyleContext(analysis, drawing, values, services, token);
        var group = new Layer
        {
            Id = replaced?.Id ?? Guid.NewGuid(), Kind = LayerKind.Group, Name = keptName ?? Loc.T(DesignStyles.GroupName(style)), PassThrough = true,
            Category = LayerCategory.Photo, Pixels = new Raster(doc.Width, doc.Height), Visible = visible, Opacity = opacity, ParentId = parent
        };
        string seedKey = style.Id + "@" + style.Version + "/" + string.Join(",", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var kit = new StyleKit(doc, group.Id, group.Pixels, context, seedKey);
        StyleRecipes.Build(style.Id, kit);
        token.ThrowIfCancellationRequested();
        group.Style = new StyleTag(style.Id, style.Version, values.Select(p => new StyleValue(p.Key, p.Value)).ToArray(), targets ?? [], kit.Edits.ToArray());
        // Validate capacity with Add, then place the folder (and its layers, bottom to top) in one block.
        var block = new List<Layer> { group }; block.AddRange(kit.Layers);
        foreach (var layer in block) doc.Add(layer);
        foreach (var layer in block) doc.Layers.Remove(layer);
        if (insertion < 0) insertion = PlaceAbove(doc, targets);
        doc.Layers.InsertRange(Math.Clamp(insertion, 0, doc.Layers.Count), block);
        doc.ActiveId = group.Id;
        doc.Validate();
        return new StyleOutcome(group.Id, style.Id, values, kit.Layers.Count, context.Notes.Distinct().ToArray(), watch.Elapsed);
    }

    /// <summary>Removes a style folder and restores what the style changed on other layers.</summary>
    public static void Remove(Document doc, Guid groupId)
    {
        var group = doc.Layers.FirstOrDefault(l => l.Id == groupId) ?? throw new ArgumentException("스타일 그룹이 없습니다.");
        if (!DesignStyles.IsStyleGroup(group)) throw new ArgumentException("디자인 스타일 그룹이 아닙니다.");
        if (Locked(doc, group)) throw new InvalidOperationException("잠긴 스타일 그룹입니다. 그룹과 부모 그룹의 잠금을 먼저 해제하세요.");
        DocumentFeatures.Remove(doc, groupId);
        doc.Validate();
    }

    static bool Locked(Document doc, Layer layer)
    {
        var index = doc.Layers.ToDictionary(l => l.Id);
        for (Layer? current = layer; current != null; current = current.ParentId is { } p && index.TryGetValue(p, out var next) ? next : null)
            if (current.Locked) return true;
        return false;
    }

    static HashSet<Guid> Subtree(Document doc, Guid root)
    {
        var ids = new HashSet<Guid> { root }; bool changed;
        do { changed = false; foreach (var l in doc.Layers) if (l.ParentId is { } p && ids.Contains(p)) changed |= ids.Add(l.Id); } while (changed);
        return ids;
    }

    /// <summary>Explicit targets that still exist and are not style folders or inside one; null when the whole document is read.</summary>
    public static Guid[]? ResolveTargets(Document doc, IReadOnlyList<Guid>? requested)
    {
        if (requested is not { Count: > 0 }) return null;
        var index = doc.Layers.ToDictionary(l => l.Id); var styled = DesignStyles.StyledLayers(doc);
        var kept = requested.Distinct().Where(id => index.ContainsKey(id) && !styled.Contains(id)).ToArray();
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>
    /// The document as a style folder sees it: style folders hidden and, for explicit targets,
    /// only the targets (with their ancestors and descendants) shown.
    /// </summary>
    public static Document AnalysisDocument(Document doc, IReadOnlyCollection<Guid>? targets)
    {
        var copy = doc.Snapshot();
        var styled = copy.Layers.Where(DesignStyles.IsStyleGroup).Select(l => l.Id).ToHashSet();
        HashSet<Guid>? keep = null;
        if (targets is { Count: > 0 })
        {
            var index = copy.Layers.ToDictionary(l => l.Id); keep = [];
            foreach (var id in targets) foreach (var member in Subtree(copy, id)) keep.Add(member);
            foreach (var id in targets) for (var p = index.GetValueOrDefault(id)?.ParentId; p is { } pid && index.TryGetValue(pid, out var up); p = up.ParentId) keep.Add(pid);
        }
        foreach (var layer in copy.Layers)
            if (styled.Contains(layer.Id) || keep != null && !keep.Contains(layer.Id)) layer.Visible = false;
        return copy;
    }

    /// <summary>Whether the targets are mostly line drawing (vector, shape, drawing folders) rather than photos.</summary>
    public static bool IsDrawing(Document doc, IReadOnlyCollection<Guid>? targets)
    {
        var categories = DrawingLayers.Categories(doc); var scope = targets is { Count: > 0 } ? targets.SelectMany(id => Subtree(doc, id)).ToHashSet() : null;
        var styled = DesignStyles.StyledLayers(doc);
        long drawing = 0, photo = 0;
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible || layer.Kind == LayerKind.Group || styled.Contains(layer.Id) || scope != null && !scope.Contains(layer.Id)) continue;
            if (layer.Kind is LayerKind.Adjustment or LayerKind.Material) continue;
            // Line work counts per object; photos by their pixels; text and shapes (labels, frames) barely.
            long weight = layer.Kind switch { LayerKind.Raster => (long)layer.Pixels.Width * layer.Pixels.Height, LayerKind.Vector => 1_000_000, LayerKind.Shape => 100_000, _ => 20_000 };
            if (categories.GetValueOrDefault(layer.Id) == LayerCategory.Drawing || layer.Kind is LayerKind.Vector) drawing += weight; else photo += weight;
        }
        return drawing > photo;
    }

    // Root position just above the highest root ancestor of the targets; the top of the stack for the whole document.
    static int PlaceAbove(Document doc, IReadOnlyCollection<Guid>? targets)
    {
        if (targets is not { Count: > 0 }) return doc.Layers.Count;
        var index = doc.Layers.ToDictionary(l => l.Id); int highest = -1;
        foreach (var id in targets)
        {
            var root = index[id]; while (root.ParentId is { } p && index.TryGetValue(p, out var up)) root = up;
            highest = Math.Max(highest, doc.Layers.IndexOf(root));
        }
        // After the root and all of its descendants.
        var subtree = Subtree(doc, doc.Layers[highest].Id); int last = highest;
        for (int i = 0; i < doc.Layers.Count; i++) if (subtree.Contains(doc.Layers[i].Id)) last = Math.Max(last, i);
        return last + 1;
    }
}
