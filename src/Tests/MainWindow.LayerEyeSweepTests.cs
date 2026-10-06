using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

// Dragging across the layer eyes: press arms, moving onto other rows sweeps, release commits one step.
// The tests drive the same press / move / release handlers the layer list calls.
public sealed partial class MainWindow
{
    internal static void RunLayerEyeSweepTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IReadOnlyList<LayerListEntry> Rows(MainWindow w) => (IReadOnlyList<LayerListEntry>)w.layerList.ItemsSource;
        static MainWindow Photo(int count)
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var document = new Document { Width = 8, Height = 8, Name = "눈 드래그" };
            for (int i = 0; i < count; i++) document.Add(new Layer { Name = "레이어 " + i, Pixels = Raster.Solid(8, 8, Colors.Red) });
            w.AddTab(document, null);
            return w;
        }
        static bool Shown(MainWindow w, int row) { var id = Rows(w)[row].Layer.Id; return w.doc.Layers.Single(l => l.Id == id).Visible; }
        static string States(MainWindow w) => string.Concat(Enumerable.Range(0, Rows(w).Count).Select(i => Shown(w, i) ? 'o' : '-'));
        static void Sweep(MainWindow w, ModifierKeys modifiers, params int[] path)
        {
            w.PressLayerEye(Rows(w)[path[0]], modifiers);
            foreach (var row in path.Skip(1)) w.DragLayerEye(Rows(w)[row]);
        }

        test("dragging across layer eyes hides every row it passes as one undo step with a coalesced preview", () =>
        {
            var w = Photo(6);
            try
            {
                Check(Rows(w).Count == 6, "The layer list did not show six rows");
                w.doc.Layers.Single(l => l.Id == Rows(w)[2].Layer.Id).Locked = true;
                var selected = w.selectedLayers.ToHashSet(); var active = w.doc.ActiveId;
                string label = w.history.UndoLabel; int renders = w.FullRenderRequests;
                Sweep(w, ModifierKeys.None, 0, 1, 2, 3);
                Check(w.EyeSweepActive, "Moving onto another row did not start a sweep");
                Check(States(w) == "----oo", "The sweep did not hide the rows it passed (locked rows included, as on a click): " + States(w));
                Check(w.history.UndoLabel == label, "The sweep wrote history before release");
                Check(w.FullRenderRequests == renders, "Each swept row asked for a full render instead of the coalesced preview");
                w.ReleaseLayerEye();
                Check(!w.EyeSweepActive && w.history.UndoLabel == "레이어 범위 숨기기", "Release did not commit one named step: " + w.history.UndoLabel);
                Check(w.FullRenderRequests == renders + 1, "Release did not ask for one full render");
                Check(w.selectedLayers.SetEquals(selected) && w.doc.ActiveId == active, "The sweep changed the row selection");
                Check(w.status.Text.Contains('4'), "The status did not report four rows: " + w.status.Text);
                w.Undo(); Check(States(w) == "oooooo", "One undo did not restore every swept row: " + States(w));
                w.Redo(); Check(States(w) == "----oo", "Redo did not repeat the sweep");

                // Starting on a hidden eye shows rows, and a fast move that skips rows still fills them.
                Sweep(w, ModifierKeys.None, 1, 5); w.ReleaseLayerEye();
                Check(States(w) == "-ooooo" && w.history.UndoLabel == "레이어 범위 표시", "A skipping sweep from a hidden eye did not show the whole span: " + States(w));
                // The swept start becomes the Shift+click reference.
                w.ClickLayerEye(Rows(w)[3], ModifierKeys.None);
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.Shift);
                Check(States(w) == "----oo", "Shift+click no longer copies the reference row's state: " + States(w));
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("sweeping back restores rows that leave the span, and Esc restores everything", () =>
        {
            var w = Photo(6);
            try
            {
                w.Edit("준비", () => w.doc.Layers.Single(l => l.Id == Rows(w)[3].Layer.Id).Visible = false);
                Check(States(w) == "ooo-oo", "Setup failed: " + States(w));
                Sweep(w, ModifierKeys.None, 1, 4);
                Check(States(w) == "o----o", "Sweep down failed: " + States(w));
                w.DragLayerEye(Rows(w)[2]);
                Check(States(w) == "o---oo", "Sweeping back did not restore rows 3 and 4 to their own states: " + States(w));
                w.DragLayerEye(Rows(w)[0]);
                Check(States(w) == "--o-oo", "Crossing above the start did not restore the rows below it: " + States(w));
                w.ReleaseLayerEye();
                w.Undo(); Check(States(w) == "ooo-oo", "Undo did not return to the state before the sweep: " + States(w));

                string label = w.history.UndoLabel;
                Sweep(w, ModifierKeys.None, 0, 2, 5);
                Check(States(w) == "------", "Sweep before Esc failed: " + States(w));
                w.ReleaseLayerEye(true);
                Check(States(w) == "ooo-oo" && w.history.UndoLabel == label && !w.EyeSweepActive, "Esc did not restore the rows without a history step: " + States(w));

                // Switching documents mid-sweep leaves the first one as it was.
                var first = w.doc; var original = first.Layers.ToDictionary(l => l.Id, l => l.Visible);
                Sweep(w, ModifierKeys.None, 0, 5);
                var other = new Document { Width = 8, Height = 8, Name = "다른 문서" }; other.Add(new Layer { Name = "하나", Pixels = Raster.Solid(8, 8, Colors.Red) });
                w.AddTab(other, null); w.ReleaseLayerEye();
                Check(first.Layers.All(l => original[l.Id] == l.Visible) && !w.EyeSweepActive, "A document switch mid-sweep left the first document changed");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("Shift+drag sweeps, while Shift+click and Alt+click on eyes keep their meanings", () =>
        {
            var w = Photo(5);
            try
            {
                Sweep(w, ModifierKeys.Shift, 4, 2);
                Check(States(w) == "oo---", "Shift+drag did not sweep: " + States(w));
                w.ReleaseLayerEye(); w.Undo();
                // A Shift press released on its own row is a click: range from the reference.
                w.ClickLayerEye(Rows(w)[0], ModifierKeys.None);
                w.PressLayerEye(Rows(w)[3], ModifierKeys.Shift);
                Check(!w.DragLayerEye(Rows(w)[3]), "Moving within the pressed row started a sweep");
                w.ReleaseLayerEye(); w.ClickLayerEye(Rows(w)[3], ModifierKeys.Shift);
                Check(States(w) == "----o" && w.history.UndoLabel == "레이어 범위 숨기기", "Shift+click range broke: " + States(w));
                w.Undo(); w.Undo();
                // Alt never arms a sweep and still isolates on click.
                w.PressLayerEye(Rows(w)[1], ModifierKeys.Alt);
                Check(!w.DragLayerEye(Rows(w)[3]) && States(w) == "ooooo", "An Alt press started a sweep");
                w.ReleaseLayerEye(); w.suppressAltMenu = false;
                w.ClickLayerEye(Rows(w)[1], ModifierKeys.Alt);
                Check(States(w) == "-o---" && w.history.UndoLabel == "레이어 단독 표시", "Alt+click no longer isolates: " + States(w));
                // A move with no armed press does nothing.
                Check(!w.DragLayerEye(Rows(w)[4]), "A move without a press started a sweep");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("a press on a layer eye arms the sweep and never reaches the reorder handle", () =>
        {
            var w = Photo(3);
            try
            {
                var row = w.CreateLayerRow(Rows(w)[0]);
                Check(!ReferenceEquals(row.Eye, row.DragHandle) && !IsAncestor(row.DragHandle, row.Eye), "The eye sits inside the reorder handle");
                int handlePresses = 0;
                row.DragHandle.PreviewMouseLeftButtonDown += (_, _) => handlePresses++;
                row.Eye.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Check(handlePresses == 0, "A press on the eye reached the reorder handle");
                Check(w.DragLayerEye(Rows(w)[2]) && States(w) == "---", "A press on the eye did not arm the sweep: " + States(w));
                w.ReleaseLayerEye();
                Check(row.Eye.ToolTip is string tip && tip.Contains("드래그") && tip.Contains("Shift+클릭") && tip.Contains("Alt+클릭"), "The eye tooltip does not explain dragging");
                // The row shows a swept state without being rebuilt.
                row.ShowVisible(false);
                Check(row.Eye.ToolTip is string shown && shown.StartsWith("레이어 표시", StringComparison.Ordinal), "ShowVisible did not update the eye");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("eye sweeps cover open folders and grouped drawing layers", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var document = new Document { Width = 8, Height = 8, Name = "폴더 눈" };
                var folder = new Layer { Name = "폴더", Kind = LayerKind.Group, Pixels = new Raster(8, 8) };
                document.Add(new Layer { Name = "아래", Pixels = Raster.Solid(8, 8, Colors.Blue) });
                document.Add(folder);
                document.Add(new Layer { Name = "안 1", ParentId = folder.Id, Pixels = Raster.Solid(8, 8, Colors.Red) });
                document.Add(new Layer { Name = "안 2", ParentId = folder.Id, Pixels = Raster.Solid(8, 8, Colors.Green) });
                document.Validate();
                w.AddTab(document, null);
                w.collapsedGroups.Remove(folder.Id); w.BuildLayers();
                Check(Rows(w).Count == 4 && Rows(w)[0].Layer.Id == folder.Id, "The open folder did not list its children: " + string.Join(", ", Rows(w).Select(r => r.Layer.Name)));
                Sweep(w, ModifierKeys.None, 0, 2); w.ReleaseLayerEye();
                Check(States(w) == "---o", "The sweep did not cover the folder and its children: " + States(w));
                w.Undo(); Check(States(w) == "oooo", "Undo did not restore the folder sweep");

                var drawing = new Document { Width = 8, Height = 8, Name = "도면 드래그" };
                Layer Group(string source)
                {
                    var group = new Layer { Name = source, SourceLayerName = source, Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = new Raster(8, 8) };
                    drawing.Layers.Add(group);
                    drawing.Layers.Add(new Layer { Name = source + " 객체", ParentId = group.Id, Category = LayerCategory.Drawing, Pixels = Raster.Solid(8, 8, Colors.Black) });
                    return group;
                }
                var wallLow = Group("A-WALL"); var door = Group("A-DOOR"); var wallHigh = Group("A-WALL"); var notes = Group("A-ANNO");
                drawing.ActiveId = notes.Id; drawing.Validate();
                w.AddTab(drawing, null);
                Check(Rows(w).Select(r => r.Layer.Name).SequenceEqual(["A-ANNO", "A-WALL", "A-DOOR"]), "Drawing rows were not grouped: " + string.Join(", ", Rows(w).Select(r => r.Layer.Name)));
                bool On(Layer layer) => w.doc.Layers.Single(l => l.Id == layer.Id).Visible;
                Sweep(w, ModifierKeys.None, 2, 1);
                Check(!On(door) && !On(wallLow) && !On(wallHigh) && On(notes), "The drawing sweep did not hide every run of the source layer");
                w.ReleaseLayerEye(); w.Undo();
                Check(On(door) && On(wallLow) && On(wallHigh), "Undo did not restore the drawing sweep");
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }

    static bool IsAncestor(DependencyObject ancestor, DependencyObject node)
    {
        for (var item = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node); item != null; item = VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item))
            if (ReferenceEquals(item, ancestor)) return true;
        return false;
    }
}
