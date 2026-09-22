using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    LayerCategory layerCategory = LayerCategory.Photo;
    readonly Dictionary<LayerCategory, Button> layerCategoryButtons = [];
    Guid[]? sourceLayerSelection;

    FrameworkElement BuildLayerCategoryTabs()
    {
        var tabs = new UniformGrid { Columns = 2, Margin = new Thickness(7, 2, 7, 0) };
        foreach (var (category, caption) in new[] { (LayerCategory.Drawing, "도면 레이어"), (LayerCategory.Photo, "포토샵 레이어") })
        {
            var button = Theme.Button(caption, () => { CommitFocusedInspectorField(); layerCategory = category; BuildLayers(); });
            button.SetResourceReference(StyleProperty, "PanelTab"); button.MinHeight = 34;
            System.Windows.Automation.AutomationProperties.SetName(button, caption);
            layerCategoryButtons[category] = button; tabs.Children.Add(button);
        }
        return tabs;
    }

    LayerListEntry[] DrawingLayerEntries(Dictionary<Guid, LayerCategory> categories)
    {
        var children = doc.Layers.ToLookup(l => l.ParentId);
        var lookup = doc.Layers.ToDictionary(l => l.Id);
        var order = doc.Layers.Select((l, index) => (l.Id, index)).ToDictionary(p => p.Id, p => p.index);
        var selected = new HashSet<Guid>(selectedLayers); if (doc.ActiveId != Guid.Empty) selected.Add(doc.ActiveId);
        foreach (var id in selected.ToArray())
        {
            if (!lookup.TryGetValue(id, out var item)) continue;
            for (int depth = 0; item.ParentId is { } parent && depth < 16 && lookup.TryGetValue(parent, out item); depth++) selected.Add(parent);
        }
        var counts = new Dictionary<Guid, int>();
        int Count(Layer layer) => counts.TryGetValue(layer.Id, out var count) ? count
            : counts[layer.Id] = layer.Kind == LayerKind.Group ? children[layer.Id].Sum(Count) : 1;
        var entries = new List<LayerListEntry>();
        void Walk(IEnumerable<Layer> siblings, int depth)
        {
            var stack = siblings.OrderByDescending(l => order[l.Id]).ToArray();
            var sourceGroups = stack.Where(l => l.Kind == LayerKind.Group && l.SourceLayerName != null)
                .GroupBy(l => l.SourceLayerName!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var shown = new HashSet<Guid>();
            foreach (var layer in stack)
            {
                if (!shown.Add(layer.Id)) continue;
                if (layer.Kind == LayerKind.Group && layer.SourceLayerName is { } source)
                {
                    var runs = sourceGroups[source]; foreach (var run in runs) shown.Add(run.Id);
                    var ids = runs.Select(l => l.Id).ToArray();
                    var row = layer.Snapshot(); row.Name = source;
                    row.Visible = runs.Any(l => l.Visible); row.Locked = runs.All(l => l.Locked);
                    bool expanded = runs.Any(l => !collapsedGroups.Contains(l.Id));
                    entries.Add(new(row, depth, ids.Any(selected.Contains), expanded, ids, $"도면 그룹 · 객체 {runs.Sum(Count):N0}개"));
                    if (expanded) Walk(runs.SelectMany(l => children[l.Id]), depth + 1);
                }
                else
                {
                    bool expanded = !collapsedGroups.Contains(layer.Id);
                    string? description = layer.Kind == LayerKind.Group && categories[layer.Id] == LayerCategory.Drawing ? $"도면 레이어 · 객체 {Count(layer):N0}개" : null;
                    entries.Add(new(layer, depth, selected.Contains(layer.Id), expanded, Description: description));
                    if (layer.Kind == LayerKind.Group && expanded) Walk(children[layer.Id], depth + 1);
                }
            }
        }
        Walk(children[null].Where(l => categories[l.Id] == layerCategory), 0);
        return entries.ToArray();
    }

    void SelectSourceLayer(Guid[] members)
    {
        CommitFocusedInspectorField(); CancelGesture(); maskEditing = false;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) selectedLayers.Clear();
        foreach (var id in members) selectedLayers.Add(id);
        sourceLayerSelection = members; doc.ActiveId = members[0]; Refresh(false); canvas.Focus();
    }
    Layer[] EditingLayerBundle()
    {
        if (sourceLayerSelection is { Length: > 1 } ids && ids.Contains(doc.ActiveId) && ids.All(selectedLayers.Contains))
            return doc.Layers.Where(l => ids.Contains(l.Id)).ToArray();
        return doc.Active is { } active ? [active] : [];
    }
    void ToggleSourceLayer(Guid[] members, bool? visible = null)
    {
        var set = members.ToHashSet();
        bool locked = doc.Layers.Where(l => set.Contains(l.Id)).All(l => l.Locked);
        Edit(visible == null ? "도면 그룹 잠금" : "도면 그룹 표시", () =>
        {
            foreach (var layer in doc.Layers.Where(l => set.Contains(l.Id)))
                if (visible is { } show) layer.Visible = show; else layer.Locked = !locked;
        });
    }
    void ReorderSourceLayer(int delta)
    {
        var members = EditingLayerBundle(); if (members.Length == 0 || members.Any(IsLockedWithParents)) return;
        var ids = members.Select(l => l.Id).ToHashSet();
        var siblings = doc.Layers.Where(l => l.ParentId == members[0].ParentId).ToArray();
        int edge = delta > 0 ? Array.FindLastIndex(siblings, l => ids.Contains(l.Id)) : Array.FindIndex(siblings, l => ids.Contains(l.Id));
        int next = edge + Math.Sign(delta); if (next < 0 || next >= siblings.Length) return;
        var neighbor = siblings[next];
        // A logical source row may span several paint runs. Moving it explicitly
        // moves every run across the next source row while keeping its internal order.
        var neighbors = siblings.Where(l => neighbor.SourceLayerName != null ? l.SourceLayerName == neighbor.SourceLayerName : l.Id == neighbor.Id).ToArray();
        var anchor = delta > 0 ? neighbors[^1] : neighbors[0];
        Edit("도면 그룹 순서", () =>
        {
            doc.Layers.RemoveAll(l => ids.Contains(l.Id));
            int position = doc.Layers.IndexOf(anchor) + (delta > 0 ? 1 : 0);
            doc.Layers.InsertRange(position, members);
        });
    }
}
