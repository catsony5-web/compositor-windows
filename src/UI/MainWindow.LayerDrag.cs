using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    sealed record LayerDrag(Document Document, Guid Layer);
    void EnableLayerDrag(LayerRow row, Guid id)
    {
        Point? start = null;
        var marker = new Border { Height = 3, Background = Theme.Accent, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Grid.SetColumnSpan(marker, 5); row.Children.Add(marker);
        row.DragHandle.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(row);
        row.DragHandle.PreviewMouseLeftButtonUp += (_, _) => start = null;
        row.DragHandle.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(row);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null; CommitFocusedInspectorField();
            if (IsLockedWithParents(doc.Layers.Single(l => l.Id == id))) { status.Text = "잠긴 레이어는 이동할 수 없습니다."; return; }
            try { DragDrop.DoDragDrop(row.DragHandle, new DataObject(typeof(LayerDrag), new LayerDrag(doc, id)), DragDropEffects.Move); }
            finally { marker.Visibility = Visibility.Collapsed; }
            e.Handled = true;
        };
        (bool Above, bool Into) Position(DragEventArgs e)
        {
            double y = e.GetPosition(row).Y;
            return (y < row.ActualHeight / 2, doc.Layers.Single(l => l.Id == id).Kind == LayerKind.Group && y > row.ActualHeight / 3 && y < row.ActualHeight * 2 / 3);
        }
        bool Valid(DragEventArgs e) => e.Data.GetData(typeof(LayerDrag)) is LayerDrag payload && ReferenceEquals(payload.Document, doc) && payload.Layer != id;
        row.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(LayerDrag))) return;
            e.Effects = Valid(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
            if (!Valid(e)) return;
            var position = Position(e); marker.Visibility = Visibility.Visible;
            marker.Height = position.Into ? row.ActualHeight : 3; marker.Opacity = position.Into ? .2 : 1;
            marker.VerticalAlignment = position.Above ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            DependencyObject? ancestor = row;
            while (ancestor != null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is ScrollViewer scroll)
            { double y = e.GetPosition(scroll).Y; if (y < 28) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12); else if (y > scroll.ActualHeight - 28) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12); }
        };
        row.DragLeave += (_, _) => marker.Visibility = Visibility.Collapsed;
        row.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(LayerDrag))) return;
            marker.Visibility = Visibility.Collapsed;
            if (Valid(e) && e.Data.GetData(typeof(LayerDrag)) is LayerDrag payload)
            { var position = Position(e); Guard(() => ReorderDrop(payload.Layer, id, position.Above, position.Into)); }
            e.Handled = true;
        };
    }
}
