using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunObjectLayerPanelTests(Action<string, Action> test)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static (Document Document, Layer File, Layer Group, Layer Child) GroupedDocument()
        {
            var document = new Document { Width = 16, Height = 16 };
            var file = new Layer { Name = "도면", Kind = LayerKind.Group, Pixels = new Raster(16, 16) };
            var group = new Layer { Name = "벽", Kind = LayerKind.Group, Pixels = new Raster(16, 16), ParentId = file.Id };
            var child = new Layer { Name = "선 1", Pixels = Raster.Solid(2, 2, Colors.Red), ParentId = group.Id };
            document.Add(file); document.Add(group); document.Add(child);
            return (document, file, group, child);
        }

        test("object layer panel initially collapses imported groups and reveals a selected child", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var (document, file, group, child) = GroupedDocument();
                window.AddTab(document, null);
                Check(window.collapsedGroups.Contains(file.Id) && window.collapsedGroups.Contains(group.Id), "New groups were not collapsed");
                Check(window.layerList.Items.Count == 1, "Hidden children created list entries");
                // This is already ActiveId after import, so merely checking for
                // an ActiveId change would miss the user's first click.
                window.SelectLayer(child.Id);
                Check(!window.collapsedGroups.Contains(file.Id) && !window.collapsedGroups.Contains(group.Id), "Selected child's ancestors stayed collapsed");
                var rows = window.layerList.Items.Cast<LayerListEntry>().ToArray();
                Check(rows.Length == 3 && rows.Single(row => row.Layer.Id == child.Id).Selected, "Selected child is missing from the layer list");
                Check(rows.Select(row => row.Depth).SequenceEqual(new[] { 0, 1, 2 }), "Hierarchy indentation changed");
                Check(!window.history.Dirty(document) && !window.history.CanUndo, "Revealing an object changed its history");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("object layer panel preserves manual collapse and tab-specific expanded groups", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var first = GroupedDocument(); window.AddTab(first.Document, null); window.SelectLayer(first.Child.Id);
                window.collapsedGroups.Add(first.Group.Id); window.BuildLayers();
                Check(window.collapsedGroups.Contains(first.Group.Id) && window.layerList.Items.Count == 2, "Refreshing reopened a manually collapsed group");
                var second = GroupedDocument(); window.AddTab(second.Document, null);
                Check(window.layerList.Items.Count == 1, "Second document did not start collapsed");
                window.SwitchTab(0);
                Check(!window.collapsedGroups.Contains(first.File.Id) && window.collapsedGroups.Contains(first.Group.Id), "Tab switch lost group expansion choices");
                Check(window.layerList.Items.Count == 2, "Tab switch displayed hidden children");
            }
            finally { window.StopRenderingForShutdown(); }
        });

        test("object layer panel realizes a bounded viewport and can select the final object among 2000", () =>
        {
            Guid selected = Guid.Empty;
            var control = new LayerList();
            control.CreateRow = entry => new LayerRow(entry.Layer, entry.Selected, () => selected = entry.Layer.Id, _ => { }, () => { });
            var pixels = Raster.Solid(2, 2, Colors.Blue);
            var entries = Enumerable.Range(0, 2000).Select(index => new LayerListEntry(
                new Layer { Name = $"선 {index + 1}", Pixels = pixels }, 1, false, true)).ToArray();
            void Layout()
            {
                control.Measure(new Size(340, 360)); control.Arrange(new Rect(0, 0, 340, 360)); control.UpdateLayout();
            }
            int Realized() => Enumerable.Range(0, control.Items.Count)
                .Count(index => control.ItemContainerGenerator.ContainerFromIndex(index) != null);
            control.SetEntries(entries); Layout();
            Check(control.Items.Count == 2000, "The object list was silently truncated");
            Check(Realized() > 0 && Realized() < 40, $"Initial viewport realized {Realized()} controls");
            control.ScrollIntoView(entries[^1]); Layout();
            Check(Realized() < 40, $"Scrolled viewport realized {Realized()} controls");
            var container = control.ItemContainerGenerator.ContainerFromIndex(entries.Length - 1) as ListBoxItem;
            Check(container?.Content is LayerRow, "The final object cannot be brought into view");
            var row = (LayerRow)container!.Content;
            row.DragHandle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(selected == entries[^1].Layer.Id, "A recycled row selected a previous object");
        });

        test("object layer panel lazily generated rows retain visibility lock and reorder actions", () =>
        {
            var window = new MainWindow(null) { headlessTesting = true };
            try
            {
                var (document, _, group, child) = GroupedDocument();
                var other = new Layer { Name = "선 2", Pixels = Raster.Solid(2, 2, Colors.Blue), ParentId = group.Id };
                document.Add(other); window.AddTab(document, null); window.SelectLayer(child.Id);
                var row = window.CreateLayerRow(window.layerList.Items.Cast<LayerListEntry>().Single(entry => entry.Layer.Id == child.Id));
                row.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 0 && button != row.DragHandle)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!document.Layers.Single(layer => layer.Id == child.Id).Visible, "Visibility action did not target the child");
                window.Undo();
                var active = window.doc.Layers.Single(layer => layer.Id == child.Id);
                row = window.CreateLayerRow(new LayerListEntry(active, 2, true, true));
                row.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 4)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.doc.Layers.Single(layer => layer.Id == child.Id).Locked, "Lock action did not target the child");
                window.Undo();
                window.ReorderDrop(child.Id, other.Id, true, false);
                var siblings = window.doc.Layers.Where(layer => layer.ParentId == group.Id).ToArray();
                Check(siblings[^1].Id == child.Id && siblings.Length == 2, "Reordering a child changed its group membership");
                window.Undo();
                Check(window.doc.Layers.Where(layer => layer.ParentId == group.Id).First().Id == child.Id, "Child reorder undo did not restore order");
            }
            finally { window.StopRenderingForShutdown(); }
        });
    }
}
