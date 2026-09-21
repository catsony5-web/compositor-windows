using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunMixedWorkspaceTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static MainWindow Create()
        {
            var w = new MainWindow(null) { headlessTesting = true };
            w.doc = new Document { Width = 128, Height = 128 };
            w.doc.Add(new Layer { Name = "원본", Pixels = Raster.Solid(128, 128, Colors.White) });
            w.history.Reset(w.doc); w.InitializeWorkspace(); w.Refresh(); return w;
        }
        static Button[] Toolbar(MainWindow window) => window.workspaceTools!.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().SelectMany(grid => grid.Children.OfType<Button>()).ToArray();
        test("workspace switches preserve mixed document, undo redo and proof independently", () =>
        {
            var w = Create(); w.AddShape(new Rect(10, 10, 30, 30), false); w.Undo();
            var before = w.doc.Snapshot(); var colors = (w.foreground, w.backgroundColor); w.cmykProof = true;
            w.SetWorkspaceMode(true);
            Check(SameDocument(before, w.doc) && w.history.CanRedo && !w.history.Dirty(w.doc), "Mode changed document history");
            Check(w.cmykProof && colors == (w.foreground, w.backgroundColor), "Mode changed colors or proof");
            Check(Toolbar(w).Length == w.toolButtons.Count && ReferenceEquals(Toolbar(w)[1], w.toolButtons[Tool.Text]), "Design tool ordering lost tools");
            w.SetWorkspaceMode(false);
            Check(Toolbar(w).SequenceEqual(w.photoToolOrder.Select(t => w.toolButtons[t])), "Photo tools did not restore");
            Check(w.cmykProof && SameDocument(before, w.doc), "Returning to photo changed document");
            w.Redo(); Check(w.doc.Active!.Shape != null, "Redo lost retained shape");
        });
        test("both workspaces create editable geometry beside raster and text", () =>
        {
            var w = Create(); w.SetWorkspaceMode(true); w.AddShape(new Rect(12, 15, 18, 20), false);
            var shapeId = w.doc.ActiveId; w.SetWorkspaceMode(false); w.OpenTextProperties(null, new Point(20, 20));
            Check(w.doc.Layers.Any(l => l.Kind == LayerKind.Raster) && w.doc.Layers.Any(l => l.Kind == LayerKind.Text) && w.doc.Layers.Single(l => l.Id == shapeId).Shape != null, "Mixed content lost after switching");
            w.doc.Validate();
        });
        test("shape inspector dimension edit preserves editability and undo", () =>
        {
            var w = Create(); w.AddShape(new Rect(10, 10, 20, 24), false);
            var group = w.properties.Children.OfType<StackPanel>().Single();
            var width = group.Children.OfType<Grid>().First().Children.OfType<TextBox>().First();
            width.Text = "44"; width.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(w.doc.Active!.Shape!.Width == 44 && w.doc.Active.Pixels.Width == 44, "Width did not update retained geometry");
            w.Undo(); Check(w.doc.Active!.Shape!.Width == 20, "Undo failed for shape property");
        });
        test("export follows newly created active layer and explicit multiple selection", () =>
        {
            var w = Create(); var original = w.doc.ActiveId;
            w.AddShape(new Rect(10, 10, 24, 18), false); var shape = w.doc.ActiveId;
            Check(w.ExportSelectionIds().SequenceEqual(new[] { shape }), "Export used stale selection after creating shape");
            var image = SelectedLayerExport.Render(w.doc, w.ExportSelectionIds());
            Check(image.Image.Width == 24 && image.Image.Height == 18, "New shape export included original image");
            w.selectedLayers.Clear(); w.selectedLayers.Add(original); w.selectedLayers.Add(shape);
            Check(w.ExportSelectionIds().ToHashSet().SetEquals(new[] { original, shape }), "Explicit multiple selection was lost");
            w.Undo(); Check(w.ExportSelectionIds().SequenceEqual(new[] { original }), "Undo left stale export ids");
        });
        test("workspace profiles retain every tool and docking while prioritizing relevant panels", () =>
        {
            var w = Create();
            Check(Toolbar(w).Distinct().Count() == Enum.GetValues<Tool>().Length, "Photo profile omitted or duplicated tools");
            Check(Array.IndexOf(Toolbar(w), w.toolButtons[Tool.Heal]) < Array.IndexOf(Toolbar(w), w.toolButtons[Tool.Text]), "Photo retouch priority missing");
            w.MovePane(w.studioPanes[2], "left");
            w.SetWorkspaceMode(true);
            Check(w.studioPanes[2].Location == "left" && w.leftPanels.Children.Contains(w.studioPanes[2]), "Mode reset a user-positioned pane");
            Check(w.histogramCard!.Visibility == Visibility.Collapsed && w.studioPage == 0 && (string)w.studioTabs[0].Content == "디자인", "Design shortcuts not opened");
            Check(Toolbar(w).Distinct().Count() == Enum.GetValues<Tool>().Length, "Design profile omitted or duplicated tools");
            w.SetWorkspaceMode(false);
            Check(w.histogramCard.Visibility == Visibility.Visible && w.studioPanes[2].Location == "left", "Photo profile did not restore histogram or preserve dock");
        });
        test("design quick actions add editable text with one undo and keep shape tools available", () =>
        {
            var w = Create(); w.SetWorkspaceMode(true);
            Button Action(string caption) => w.studioContents[0].Children.OfType<System.Windows.Controls.Primitives.UniformGrid>()
                .SelectMany(grid => grid.Children.OfType<Button>()).Single(button => button.Content is TextBlock text && text.Text == caption);
            Action("새 텍스트").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(w.doc.Active?.Text?.Content == "새 텍스트" && w.studioPage == 1 && w.doc.Layers.Count == 2, "Text quick action failed to open editable inspector");
            w.Undo(); Check(w.doc.Layers.Count == 1 && w.history.CanRedo, "Text action did not undo once");
            Action("타원").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(w.tool == Tool.Ellipse && w.history.CanRedo && !w.history.Dirty(w.doc), "Shape tool action edited history");
        });
        test("design alignment handles multiple transformed layers and affine parent in one undo", () =>
        {
            var w = Create(); w.AddShape(new Rect(17, 22, 20, 30), false); var first = w.doc.Active!; first.Rotation = 33;
            w.AddShape(new Rect(31, 12, 18, 26), true); var second = w.doc.Active!;
            var parent = DocumentFeatures.CreateGroup(w.doc); parent.X = 9; parent.Y = 11; parent.Rotation = 19; parent.Scale = 1.3;
            w.doc.Add(parent); second.ParentId = parent.Id;
            w.doc.ActiveId = second.Id; w.selectedLayers.Clear(); w.selectedLayers.Add(first.Id); w.selectedLayers.Add(second.Id);
            w.history.Reset(w.doc); var before = w.doc.Snapshot(); var pixels = first.Pixels.Data;
            w.AlignWorkspaceLayers("right");
            foreach (var id in new[] { first.Id, second.Id })
            {
                var layer = w.doc.Layers.Single(item => item.Id == id);
                var corners = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) };
                double right = corners.Max(point => DocumentFeatures.ToDocumentSpace(w.doc, layer, point).X);
                Check(Math.Abs(right - w.doc.Width) < .00001, "Alignment ignored transformed document bounds");
            }
            Check(ReferenceEquals(pixels, first.Pixels.Data) && first.Shape != null, "Alignment rasterized or mutated source pixels");
            w.Undo(); Check(SameDocument(before, w.doc) && !w.history.CanUndo && w.history.CanRedo, "Alignment was not one undo operation");
            var locked = w.doc.Layers.Single(layer => layer.Id == second.Id); locked.Locked = true;
            var guarded = w.doc.Snapshot(); w.AlignWorkspaceLayers("left");
            Check(SameDocument(guarded, w.doc) && w.history.CanRedo, "Locked multiselection was partially aligned or lost redo");
        });
        test("already aligned design command preserves redo and clean document", () =>
        {
            var w = Create(); w.AddShape(new Rect(10, 12, 20, 30), false); w.Undo();
            var before = w.doc.Snapshot(); w.AlignWorkspaceLayers("left"); w.AlignWorkspaceLayers("top");
            Check(SameDocument(before, w.doc) && w.history.CanRedo && !w.history.Dirty(w.doc), "No-op canvas alignment dirtied document or discarded redo");
        });
        test("design arrangement uses current active target and group ignores stale selection", () =>
        {
            var w = Create(); var original = w.doc.Active!;
            w.AddShape(new Rect(10, 10, 22, 18), false); var shape = w.doc.Active!;
            w.selectedLayers.Clear(); w.selectedLayers.Add(original.Id);
            w.WorkspaceArrange(w.GroupSelected, true);
            Check(shape.ParentId == w.doc.ActiveId && original.ParentId == null && w.doc.Active?.Kind == LayerKind.Group, "Design group used old selection after creating a shape");
            w.Undo(); original = w.doc.Layers.Single(layer => layer.Id == original.Id); shape = w.doc.Layers.Single(layer => layer.Id == shape.Id);
            original.Locked = true; w.doc.ActiveId = shape.Id; w.selectedLayers.Clear(); w.selectedLayers.Add(original.Id); w.selectedLayers.Add(shape.Id);
            w.WorkspaceArrange(() => w.Reorder(-1));
            Check(w.doc.Layers[0].Id == shape.Id && original.Locked, "Active-layer arrange was blocked by an unrelated selected locked layer");
        });
    }
}
