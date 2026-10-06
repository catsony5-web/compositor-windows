using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Compositor.Windows;

// Dragging across the visibility eyes (with or without Shift). A press on an eye only arms the
// sweep; a click that stays on its row keeps the plain, Shift (range) and Alt (isolate) meanings.
// Once the pointer reaches another row, the first eye's toggled state covers every row from the
// first row to the row under the pointer, so a fast move that skips rows still fills them. Rows
// that leave that span on the way back get their original state again. The canvas previews the
// change through the coalesced gesture render; release commits one undo step, Esc restores.
// Locked layers change like they do on an eye click. The sweep captures the list's scroll host,
// not a row, because rows are recycled; the eye column is separate from the reorder handle.
public sealed partial class MainWindow
{
    sealed class EyeSweep
    {
        public required Document Document { get; init; }
        public required Document Before { get; init; }
        public required IReadOnlyList<LayerListEntry> Entries { get; init; }
        public required Dictionary<Guid, Layer> Layers { get; init; }
        public required int Start { get; init; }
        public required bool Visible { get; init; }
        public Dictionary<Guid, bool> Original { get; } = [];
        public int Low, High;
    }

    LayerListEntry? eyePress;
    EyeSweep? eyeSweep;
    bool eyeSweepHooked;
    DispatcherTimer? eyeSweepScroll;

    internal bool EyeSweepActive => eyeSweep != null;

    void HookEyeSweep()
    {
        if (eyeSweepHooked) return;
        eyeSweepHooked = true;
        layerList.PreviewMouseMove += (_, e) =>
        {
            if (eyePress == null && eyeSweep == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { ReleaseLayerEye(); return; }
            if (EntryUnderPointer() is { } over && DragLayerEye(over)) e.Handled = true;
        };
        layerList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            bool sweeping = eyeSweep != null;
            ReleaseLayerEye();
            if (sweeping) e.Handled = true;
        };
    }

    // Armed from the eye's own press, so presses on the row (selection, reorder) never start a sweep.
    internal void PressLayerEye(LayerListEntry entry, ModifierKeys modifiers)
    {
        if (eyeSweep != null) return;
        eyePress = modifiers.HasFlag(ModifierKeys.Alt) || !HasDocument ? null : entry;
    }

    // True while a sweep runs. The first move onto another row starts it.
    internal bool DragLayerEye(LayerListEntry over)
    {
        if (eyeSweep == null)
        {
            if (eyePress is not { } press || press.Layer.Id == over.Layer.Id) return false;
            eyePress = null;
            if (!BeginEyeSweep(press)) return false;
        }
        var sweep = eyeSweep!;
        int index = IndexOfEntry(sweep.Entries, e => e.Layer.Id == over.Layer.Id);
        if (index >= 0) UpdateEyeSweep(sweep, index);
        return true;
    }

    // Ends a press or sweep: the sweep becomes one undo step, or is undone on cancel.
    internal void ReleaseLayerEye(bool cancel = false)
    {
        eyePress = null;
        if (eyeSweep is not { } sweep) return;
        eyeSweep = null;
        StopEyeSweepCapture();
        // Another tab or an undo replaced the document mid-sweep: put its rows back, record nothing.
        if (!ReferenceEquals(doc, sweep.Document)) { foreach (var (id, visible) in sweep.Original) sweep.Layers[id].Visible = visible; return; }
        if (cancel)
        {
            foreach (var (id, visible) in sweep.Original) sweep.Layers[id].Visible = visible;
            Refresh();
            status.Text = "레이어 표시 드래그를 취소했습니다";
            return;
        }
        if (!sweep.Original.Any(pair => sweep.Layers[pair.Key].Visible != pair.Value)) { Refresh(); return; }
        doc.Validate();
        history.Commit(sweep.Visible ? "레이어 범위 표시" : "레이어 범위 숨기기", sweep.Before, doc);
        Refresh();
        int rows = sweep.High - sweep.Low + 1;
        status.Text = sweep.Visible ? $"드래그로 레이어 {rows}개를 표시했습니다" : $"드래그로 레이어 {rows}개를 숨겼습니다";
    }

    bool BeginEyeSweep(LayerListEntry press)
    {
        // Like Edit: finish pending slider or gesture work before the "before" state is taken.
        CancelGesture();
        var entries = layerList.ItemsSource as IReadOnlyList<LayerListEntry> ?? [];
        int start = IndexOfEntry(entries, e => e.Layer.Id == press.Layer.Id);
        if (start < 0 || !HasDocument) return false;
        var layers = doc.Layers.ToDictionary(l => l.Id);
        bool shown = EntryIds(entries[start]).Any(id => layers.TryGetValue(id, out var layer) && layer.Visible);
        eyeSweep = new EyeSweep { Document = doc, Before = doc.Snapshot(), Entries = entries, Layers = layers, Start = start, Visible = !shown, Low = start, High = start - 1 };
        visibilityAnchor = EntryIds(entries[start])[0];
        StartEyeSweepCapture();
        return true;
    }

    void UpdateEyeSweep(EyeSweep sweep, int current)
    {
        int low = Math.Min(sweep.Start, current), high = Math.Max(sweep.Start, current);
        if (low == sweep.Low && high == sweep.High) return;
        int from = Math.Min(low, sweep.Low), to = Math.Max(high, sweep.High);
        for (int i = from; i <= to; i++)
        {
            bool inside = i >= low && i <= high, was = i >= sweep.Low && i <= sweep.High;
            if (inside == was) continue;
            bool rowVisible = false;
            foreach (var id in EntryIds(sweep.Entries[i]))
            {
                if (!sweep.Layers.TryGetValue(id, out var layer)) continue;
                if (!sweep.Original.ContainsKey(id)) sweep.Original[id] = layer.Visible;
                layer.Visible = inside ? sweep.Visible : sweep.Original[id];
                rowVisible |= layer.Visible;
            }
            ShowRowVisibility(i, rowVisible);
        }
        sweep.Low = low; sweep.High = high;
        // The coalesced gesture frame, not a full composite per row.
        QueueRender(true); canvas.InvalidateVisual();
    }

    void ShowRowVisibility(int index, bool visible)
    {
        // By item, not index: a list rebuilt meanwhile has no container for the old entry.
        if (eyeSweep is { } sweep && layerList.ItemContainerGenerator.ContainerFromItem(sweep.Entries[index]) is ListBoxItem { Content: LayerRow row }) row.ShowVisible(visible);
    }

    void StartEyeSweepCapture()
    {
        if (headlessTesting || layerList.ScrollHost() is not { } host) return;
        host.CaptureMouse();
        host.LostMouseCapture += OnEyeSweepCaptureLost;
        eyeSweepScroll ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        eyeSweepScroll.Tick -= OnEyeSweepScroll; eyeSweepScroll.Tick += OnEyeSweepScroll;
        eyeSweepScroll.Start();
    }

    void StopEyeSweepCapture()
    {
        eyeSweepScroll?.Stop();
        if (layerList.ScrollHost() is not { } host) return;
        host.LostMouseCapture -= OnEyeSweepCaptureLost;
        if (host.IsMouseCaptured) host.ReleaseMouseCapture();
    }

    // Losing capture (another window, a dialog) keeps what was swept, as a release would.
    void OnEyeSweepCaptureLost(object sender, MouseEventArgs e) { if (eyeSweep != null) ReleaseLayerEye(); }

    // Near the top or bottom edge the list scrolls, faster the further past the edge.
    void OnEyeSweepScroll(object? sender, EventArgs e)
    {
        if (eyeSweep == null || layerList.ScrollHost() is not { } host) { eyeSweepScroll?.Stop(); return; }
        double y = Mouse.GetPosition(host).Y, edge = 24, step = 0;
        if (y < edge) step = -Math.Min(40, 6 + (edge - y) / 2);
        else if (y > host.ActualHeight - edge) step = Math.Min(40, 6 + (y - host.ActualHeight + edge) / 2);
        if (step == 0) return;
        host.ScrollToVerticalOffset(host.VerticalOffset + step);
        host.UpdateLayout();
        if (EntryUnderPointer() is { } over) DragLayerEye(over);
    }

    // The row at the pointer's height; above or below the list means its first or last shown row.
    LayerListEntry? EntryUnderPointer()
    {
        if (layerList.ScrollHost() is not { } host || host.ActualHeight <= 2) return null;
        double y = Math.Clamp(Mouse.GetPosition(host).Y, 1, host.ActualHeight - 1);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var point = host.TranslatePoint(new Point(host.ActualWidth / 2, y), layerList);
            DependencyObject? hit = layerList.InputHitTest(point) as DependencyObject;
            while (hit != null && hit is not ListBoxItem && !ReferenceEquals(hit, layerList))
                hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            if (hit is ListBoxItem item && layerList.ItemContainerGenerator.ItemFromContainer(item) is LayerListEntry entry) return entry;
            // The 1 px gap between rows: look a little lower.
            y = Math.Min(host.ActualHeight - 1, y + 2);
        }
        return null;
    }
}
