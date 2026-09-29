using System.Windows.Input;

namespace Compositor.Windows;

// Visibility eyes in the layer list. A plain click toggles one row and makes it the
// reference. Shift+click gives every listed row from the reference to the clicked row
// the reference row's current visibility, as one undo step; without a reference in the
// list, the topmost row is the reference. Alt (also with Shift) keeps priority and
// cycles isolation. Row selection clicks (Shift range, Ctrl toggle) are separate.
public sealed partial class MainWindow
{
    Guid visibilityAnchor;

    internal void ClickLayerEye(LayerListEntry entry, ModifierKeys modifiers)
    {
        var ids = EntryIds(entry);
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Releasing Alt must not then open the menu bar.
            suppressAltMenu = true; IsolateLayers(ids); return;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift) && ApplyVisibilityRange(entry)) return;
        visibilityAnchor = ids[0];
        bool visible = !entry.Layer.Visible;
        if (entry.GroupMembers is { } members) ToggleSourceLayer(members, visible);
        else Edit("레이어 표시", () => doc.Layers.Single(item => item.Id == ids[0]).Visible = visible);
    }

    // False when the clicked row is the reference itself; the click then toggles as usual.
    bool ApplyVisibilityRange(LayerListEntry target)
    {
        var entries = layerList.ItemsSource as IReadOnlyList<LayerListEntry> ?? [];
        int to = IndexOfEntry(entries, e => e.Layer.Id == target.Layer.Id);
        if (to < 0) return false;
        int from = visibilityAnchor == Guid.Empty ? -1 : IndexOfEntry(entries, e => EntryIds(e).Contains(visibilityAnchor));
        if (from < 0) from = 0;
        if (from == to) return false;
        bool visible = entries[from].Layer.Visible;
        var ids = new HashSet<Guid>();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) foreach (var id in EntryIds(entries[i])) ids.Add(id);
        int rows = Math.Abs(to - from) + 1;
        if (doc.Layers.Any(l => ids.Contains(l.Id) && l.Visible != visible))
            Edit(visible ? "레이어 범위 표시" : "레이어 범위 숨기기", () => { foreach (var layer in doc.Layers) if (ids.Contains(layer.Id)) layer.Visible = visible; });
        status.Text = visible ? $"레이어 {rows}개를 기준 레이어처럼 표시했습니다" : $"레이어 {rows}개를 기준 레이어처럼 숨겼습니다";
        return true;
    }
}
