using System.Windows;
using System.Windows.Input;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    System.Windows.Controls.TextBlock? moveSelectionHint;
    bool objectMarquee;
    void BeginObjectMarquee(Point point, Point screen)
    {
        beforeGesture = null; start = point; screenStart = screen; dragging = true; objectMarquee = true;
        selectionMode = CurrentSelectionMode(); canvas.ObjectMarquee = new Rect(point, point); canvas.CaptureMouse();
    }
    void MoveObjectMarquee(Point point)
    {
        canvas.ObjectMarquee = Between(start, point); canvas.CrossingSelection = point.X < start.X;
        status.Text = canvas.CrossingSelection ? "닿는 객체 선택 ←" : "완전히 포함된 객체 선택 →";
        canvas.InvalidateVisual();
    }
    async void EndObjectMarquee(Point point)
    {
        var bounds = Between(start, point); bool crossing = point.X < start.X;
        objectMarquee = false; dragging = false; canvas.ObjectMarquee = null; canvas.ReleaseMouseCapture();
        if (bounds.Width * canvas.Zoom < 4 && bounds.Height * canvas.Zoom < 4)
        {
            if (selectionMode == SelectionCombine.Replace) ApplyObjectSelection([], selectionMode);
            canvas.InvalidateVisual(); return;
        }
        await SelectObjectsAsync(bounds, crossing, selectionMode);
    }
    internal async Task<bool> SelectObjectsAsync(Rect bounds, bool crossing, SelectionCombine mode)
    {
        var document = doc; var revision = doc.Revision; int tab = activeTab; var previous = selectedLayers.ToHashSet(); var active = doc.ActiveId;
        jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource(); var snapshot = doc.Snapshot();
        status.Text = "객체 선택 중… Esc: 취소";
        try
        {
            var result = await CompatibilityImport.OnSta(() => ObjectSelection.Find(snapshot, bounds, crossing, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(document, doc) || doc.Revision != revision || activeTab != tab || doc.ActiveId != active || !previous.SetEquals(selectedLayers)) return false;
            ApplyObjectSelection(result, mode); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { if (headlessTesting) throw; status.Text = "객체를 선택하지 못했습니다: " + error.Message; return false; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }
    void ApplyObjectSelection(IEnumerable<Guid> incoming, SelectionCombine mode)
    {
        var ids = incoming.ToHashSet(); sourceLayerSelection = null;
        switch (mode)
        {
            case SelectionCombine.Replace: selectedLayers.Clear(); selectedLayers.UnionWith(ids); break;
            case SelectionCombine.Add: selectedLayers.UnionWith(ids); break;
            case SelectionCombine.Subtract: selectedLayers.ExceptWith(ids); break;
            case SelectionCombine.Intersect: selectedLayers.IntersectWith(ids); break;
        }
        doc.ActiveId = selectedLayers.Contains(doc.ActiveId) ? doc.ActiveId : doc.Layers.LastOrDefault(l => selectedLayers.Contains(l.Id))?.Id ?? Guid.Empty;
        maskEditing = false; Refresh(false);
        status.Text = selectedLayers.Count == 0 ? "선택된 객체 없음" : $"객체 {selectedLayers.Count:N0}개 선택";
    }
    async void SelectAllObjects() => await SelectObjectsAsync(new Rect(-100_000, -100_000, 300_000, 300_000), true, SelectionCombine.Replace);
    void DeleteSelectedObjects()
    {
        CancelGesture(); var ids = MovableSelectedLayers().Select(l => l.Id).ToArray(); if (ids.Length == 0) return;
        Edit("객체 삭제", () =>
        {
            var removed = ids.ToHashSet(); var children = doc.Layers.ToLookup(l => l.ParentId);
            void Include(Guid id) { foreach (var child in children[id]) { removed.Add(child.Id); Include(child.Id); } }
            foreach (var id in ids) Include(id);
            doc.Layers.RemoveAll(l => removed.Contains(l.Id)); doc.ActiveId = Guid.Empty;
            selectedLayers.Clear(); sourceLayerSelection = null;
        });
    }
}
