using System.Windows.Input;

namespace Compositor.Windows;

// Layer-list clicks: plain click selects, Shift+click selects the range from the last
// plain click, Ctrl+click adds or removes one row. Alt+click on a visibility icon cycles
// isolation: only that layer → every layer except it → the original visibility.
public sealed partial class MainWindow
{
    Guid layerAnchor;
    sealed record IsolationState(Document Document, Guid[] Targets, int Stage, Dictionary<Guid, bool> Before, Dictionary<Guid, bool> Expected);
    IsolationState? isolation;

    static Guid[] EntryIds(LayerListEntry entry) => entry.GroupMembers ?? [entry.Layer.Id];

    internal void ClickLayerRow(LayerListEntry entry, ModifierKeys modifiers)
    {
        var ids = EntryIds(entry);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift), ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var entries = layerList.ItemsSource as IReadOnlyList<LayerListEntry> ?? [];
        int anchor = layerAnchor == Guid.Empty ? -1 : IndexOfEntry(entries, e => EntryIds(e).Contains(layerAnchor));
        int target = IndexOfEntry(entries, e => e.Layer.Id == entry.Layer.Id);
        if (shift && anchor >= 0 && target >= 0)
        {
            BeginRowSelection();
            if (!ctrl) selectedLayers.Clear();
            for (int i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++) foreach (var id in EntryIds(entries[i])) selectedLayers.Add(id);
            doc.ActiveId = ids[0]; status.Text = $"레이어 {Math.Abs(target - anchor) + 1}개 범위 선택";
            Refresh(false); canvas.Focus(); return;
        }
        layerAnchor = ids[0];
        if (ctrl)
        {
            BeginRowSelection();
            if (doc.ActiveId != Guid.Empty) selectedLayers.Add(doc.ActiveId);
            if (ids.All(selectedLayers.Contains) && selectedLayers.Count > ids.Length)
            {
                foreach (var id in ids) selectedLayers.Remove(id);
                if (ids.Contains(doc.ActiveId)) doc.ActiveId = selectedLayers.Last();
            }
            else { foreach (var id in ids) selectedLayers.Add(id); doc.ActiveId = ids[0]; }
            Refresh(false); canvas.Focus(); return;
        }
        if (entry.GroupMembers is { } members) SelectSourceLayer(members); else SelectLayer(entry.Layer.Id);
    }

    void BeginRowSelection() { CommitFocusedInspectorField(); CancelGesture(); sourceLayerSelection = null; maskEditing = false; }

    static int IndexOfEntry(IReadOnlyList<LayerListEntry> entries, Func<LayerListEntry, bool> match)
    {
        for (int i = 0; i < entries.Count; i++) if (match(entries[i])) return i;
        return -1;
    }

    // Returns the stage now shown: 1 only the targets, 2 all but the targets, 0 restored.
    internal int IsolateLayers(Guid[] targets)
    {
        if (!HasDocument || targets.Length == 0) return 0;
        var set = targets.ToHashSet();
        var lookup = doc.Layers.ToDictionary(l => l.Id);
        // A changed document, other targets or visibility edited since the last step start a fresh cycle.
        bool continuing = isolation is { } state && ReferenceEquals(state.Document, doc) && state.Targets.ToHashSet().SetEquals(set)
            && state.Expected.All(p => !lookup.TryGetValue(p.Key, out var layer) || layer.Visible == p.Value);
        var before = continuing ? isolation!.Before : doc.Layers.ToDictionary(l => l.Id, l => l.Visible);
        int stage = continuing ? (isolation!.Stage + 1) % 3 : 1;
        var descendants = new HashSet<Guid>(); var children = doc.Layers.ToLookup(l => l.ParentId);
        void Down(Guid id) { foreach (var child in children[id]) if (descendants.Add(child.Id)) Down(child.Id); }
        foreach (var id in set) Down(id);
        var ancestors = new HashSet<Guid>();
        foreach (var id in set) for (var parent = lookup.GetValueOrDefault(id)?.ParentId; parent is { } p && ancestors.Add(p);) parent = lookup.GetValueOrDefault(p)?.ParentId;
        string label = stage switch { 1 => "레이어 단독 표시", 2 => "선택 레이어만 숨기기", _ => "레이어 표시 복원" };
        Edit(label, () =>
        {
            foreach (var layer in doc.Layers)
            {
                if (!before.TryGetValue(layer.Id, out bool original)) continue;
                layer.Visible = stage switch
                {
                    1 => set.Contains(layer.Id) || ancestors.Contains(layer.Id) || (descendants.Contains(layer.Id) && original),
                    2 => !set.Contains(layer.Id) && (ancestors.Contains(layer.Id) || original),
                    _ => original
                };
            }
        });
        isolation = stage == 0 ? null : new IsolationState(doc, targets, stage, before, doc.Layers.ToDictionary(l => l.Id, l => l.Visible));
        status.Text = stage switch
        {
            1 => "선택한 레이어만 표시 · Alt+클릭: 선택 레이어만 숨기기",
            2 => "선택한 레이어만 숨김 · Alt+클릭: 원래대로",
            _ => "레이어 표시를 원래대로 돌렸습니다"
        };
        return stage;
    }
}
